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
        // One process, so a signal never leaves it: Publish raises Received directly and there is
        // nothing to listen to.
        if (!DbEngine.IsPostgres) return;

        _cts = new CancellationTokenSource();
        _ = ListenLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try { _listener?.Dispose(); } catch { }
        _listener = null;
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
