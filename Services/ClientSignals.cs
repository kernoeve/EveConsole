using System.Net.Sockets;
using EveConsole.Data;
using Npgsql;

namespace EveConsole.Services;

/// <summary>
/// A push channel from the client doing the background work to every client watching.
///
/// <para>Some things the worker discovers have to happen <em>on a person's machine</em> — a sound,
/// a dialog, the agent saying something out loud. The worker cannot do those: it may be headless,
/// or on a server in another room. It can only say what happened and let each client decide what
/// it is willing to do about it.</para>
///
/// <para>⚠️ Push, not poll, and that is the whole point. The obvious implementation is a table the
/// clients read every few seconds, and for intel it is useless — ten seconds is long enough to be
/// dead before the warning arrives. PostgreSQL's LISTEN/NOTIFY delivers in about a millisecond,
/// which is the difference between a warning and a post-mortem.</para>
///
/// <para><b>What it does not do:</b> replay. A client that is disconnected when a signal is sent
/// never learns of it. That is deliberate rather than merely easier — the payload is "act now",
/// and an intel warning replayed on reconnect four minutes later is worse than silence, because
/// it reads exactly like a live one. Anything that must survive a disconnection belongs in a
/// table, which is where the Alert action already writes.</para>
/// </summary>
public sealed class ClientSignals(AppErrorLogger errors)
{
    /// <summary>
    /// Arbitrary but fixed. Channel names are per-database, so two EVE Consoles on different
    /// databases cannot hear each other, and two on the same one always can.
    /// </summary>
    private const string Channel = "eveconsole_signal";

    /// <summary>
    /// ⚠️ pg_notify's payload limit, and a hard one — the server raises an error rather than
    /// truncating. Callers keep well under it by sending identifiers and short strings; this is
    /// the backstop that turns a would-be exception into a dropped signal and a log line.
    /// </summary>
    private const int MaxPayloadBytes = 7900;

    /// <summary>
    /// Whether a payload is small enough to send.
    ///
    /// <para>⚠️ Exposed so callers can trim to fit rather than discovering the limit by being
    /// refused. A publisher that builds a batch, is rejected, and drops the whole thing has lost
    /// exactly the data the batch existed to carry.</para>
    /// </summary>
    public static bool Fits(string payload) =>
        System.Text.Encoding.UTF8.GetByteCount(payload) <= MaxPayloadBytes;

    private NpgsqlConnection?        _listener;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Raised on every signal this client hears, including ones it sent itself.
    ///
    /// <para>⚠️ Self-delivery is deliberate and is what keeps the handling uniform. PostgreSQL
    /// delivers a notification to the sending session too, so a worker that also has a window
    /// receives its own signal and runs the client half exactly as any other client would. No
    /// branch anywhere asks "was this mine".</para>
    ///
    /// <para>Raised on a background thread. Handlers that touch the UI must marshal.</para>
    /// </summary>
    public event Action<string>? Received;

    public void Start()
    {
        _cts = new CancellationTokenSource();

        // ⚠️ On SQLite there is one client, and Publish raises Received in it directly — but the
        // tray is a second PROCESS, and heard nothing at all: its Background Processes window sat
        // empty for as long as it was open. So the client serves a local socket and the tray
        // listens on it. See LocalSocketPath.
        if (!DbEngine.IsPostgres)
        {
            if (AppRuntime.IsTray) _ = LocalListenLoopAsync(_cts.Token);
            else                   StartLocalServer(_cts.Token);
            return;
        }

        _ = ListenLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try { _listener?.Dispose(); } catch { }
        _listener = null;

        // The socket FILE is left: a client replacing this one (a restart) may already have bound
        // a new one at that path, and deleting it would cut the tray off from the newcomer. A
        // stale file is cleared by whoever binds next.
        try { _server?.Dispose(); } catch { }
        _server = null;
        lock (_peers)
        {
            foreach (var p in _peers) try { p.Dispose(); } catch { }
            _peers.Clear();
        }
    }

    // ── SQLite: a local socket from the client to the tray ────────────────────

    private Socket?               _server;
    private readonly List<Socket> _peers = [];

    /// <summary>
    /// Raised on the client when a tray connects, on a background thread. A signal has no replay,
    /// so whoever publishes state sends it again whole for the newcomer.
    /// </summary>
    public event Action? PeerConnected;

    /// <summary>
    /// Beside the profile's data, so a profile's client and tray find each other and not anyone
    /// else's. ⚠️ A Unix socket path is capped at about 104 bytes on macOS, which a profile folder
    /// can exceed; past that it goes to the temp folder under a name derived from the data folder.
    /// </summary>
    private static string LocalSocketPath
    {
        get
        {
            var path = Path.Combine(AppConfig.AppDataDir, "EveConsole.signals.sock");
            if (System.Text.Encoding.UTF8.GetByteCount(path) <= 100) return path;

            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(AppConfig.AppDataDir)))[..16];
            return Path.Combine(Path.GetTempPath(), $"eveconsole-{hash}.sock");
        }
    }

    private void StartLocalServer(CancellationToken ct)
    {
        try
        {
            var path = LocalSocketPath;
            if (File.Exists(path)) File.Delete(path);   // left by a client that died; only one can run

            _server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _server.Bind(new UnixDomainSocketEndPoint(path));
            _server.Listen(4);

            // Yours alone. The calls it carries name your characters, and a socket file takes the
            // process's umask, which on most systems lets other users on the machine connect.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            _ = AcceptLoopAsync(_server, ct);
        }
        catch (Exception ex)
        {
            // No tray will hear anything, which is all this costs: the client itself is unaffected.
            errors.Warn(nameof(ClientSignals), "serving the tray", ex.Message);
        }
    }

    private async Task AcceptLoopAsync(Socket server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var peer = await server.AcceptAsync(ct);
                // A tray that stops reading must not stall the client that is publishing to it.
                peer.SendTimeout = 2000;
                lock (_peers) _peers.Add(peer);

                try { PeerConnected?.Invoke(); }
                catch (Exception ex) { errors.Log(nameof(ClientSignals), "a tray connected", ex); }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException)    { return; }
            catch (Exception ex)
            {
                errors.Log(nameof(ClientSignals), "accepting the tray", ex);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Sends to every connected tray. ⚠️ Length-prefixed rather than one per line: a payload is
    /// whatever its publisher wrote, and a newline inside one would split it in two.
    /// </summary>
    private void SendToPeers(string payload)
    {
        List<Socket> peers;
        lock (_peers)
        {
            if (_peers.Count == 0) return;
            peers = [.. _peers];
        }

        var body  = System.Text.Encoding.UTF8.GetBytes(payload);
        var frame = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);

        foreach (var p in peers)
        {
            try { p.Send(frame); }
            catch
            {
                // Gone, or not reading: dropped. A tray that comes back connects again.
                lock (_peers) _peers.Remove(p);
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// The tray's side: connects to the client's socket and raises Received for each payload.
    /// Retried for as long as the tray runs — the client is usually started after it, at logon,
    /// and is closed and opened again many times while the icon stays up.
    /// </summary>
    private async Task LocalListenLoopAsync(CancellationToken ct)
    {
        var header = new byte[4];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(LocalSocketPath), ct);
                await using var stream = new NetworkStream(socket, ownsSocket: false);

                while (!ct.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, ct);
                    var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length is < 0 or > 1 << 20) break;   // not ours; reconnect rather than trust it

                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, ct);

                    try { Received?.Invoke(System.Text.Encoding.UTF8.GetString(body)); }
                    catch (Exception ex) { errors.Log(nameof(ClientSignals), "handling a signal", ex); }
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // No client running yet, or it has just closed: the normal state of a tray icon,
                // not worth a line in the log every few seconds.
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Sends a signal to every listening client.
    ///
    /// <para>⚠️ Delivered at COMMIT, not at the moment this is called. That is a guarantee worth
    /// leaning on: publish inside the transaction that writes the row a client will need, and the
    /// row is visible to that client by the time the signal reaches it. Published outside one, as
    /// here, it goes immediately.</para>
    /// </summary>
    public async Task PublishAsync(string payload, CancellationToken ct = default)
    {
        if (!DbEngine.IsPostgres)
        {
            Received?.Invoke(payload);
            SendToPeers(payload);
            return;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            errors.Log(nameof(ClientSignals), "signal too large to send", new InvalidOperationException(
                $"payload is {System.Text.Encoding.UTF8.GetByteCount(payload)} bytes, limit is {MaxPayloadBytes}"));
            return;
        }

        try
        {
            var cs = AppConfig.GetPostgresConnection();
            if (string.IsNullOrWhiteSpace(cs)) return;

            // Pooled, unlike the listener: this is one short statement and holding a connection
            // open for it would be waste.
            await using var conn = new NpgsqlConnection(AppDb.PostgresConnectionString(cs));
            await conn.OpenAsync(ct);

            // ⚠️ pg_notify rather than NOTIFY. NOTIFY takes a string literal, not a parameter, so
            // the payload would have to be pasted into the statement text and escaped by hand.
            await using var cmd = new NpgsqlCommand("SELECT pg_notify(@ch, @payload)", conn);
            cmd.Parameters.AddWithValue("ch", Channel);
            cmd.Parameters.AddWithValue("payload", payload);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Log(nameof(ClientSignals), "publishing a signal", ex);
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cs = AppConfig.GetPostgresConnection();
                if (string.IsNullOrWhiteSpace(cs)) return;

                // ⚠️ Unpooled and held open. A listening session IS the subscription — returning
                // this connection to the pool would end the session and silently unsubscribe,
                // leaving a client that looks connected and hears nothing.
                var b = new NpgsqlConnectionStringBuilder(AppDb.PostgresConnectionString(cs))
                        { Pooling = false, KeepAlive = 30 };

                await using var conn = new NpgsqlConnection(b.ConnectionString);
                conn.Notification += (_, e) =>
                {
                    // Guarded: a handler that throws must not take down the connection that every
                    // later signal arrives on.
                    try { Received?.Invoke(e.Payload); }
                    catch (Exception ex) { errors.Log(nameof(ClientSignals), "handling a signal", ex); }
                };

                await conn.OpenAsync(ct);
                _listener = conn;

                await using (var cmd = new NpgsqlCommand($"LISTEN {Channel}", conn))
                    await cmd.ExecuteNonQueryAsync(ct);

                // Blocks until a notification arrives, then returns to be called again. KeepAlive
                // above is what keeps a silent connection from being dropped by a firewall that
                // sees no traffic for an hour.
                while (!ct.IsCancellationRequested)
                    await conn.WaitAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                errors.Log(nameof(ClientSignals), "listening for signals", ex);
            }
            finally { _listener = null; }

            // Reconnect. A client that stops listening stops hearing intel warnings, and would
            // do so silently — so the loop comes back rather than ending on the first bad night
            // the network has.
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
