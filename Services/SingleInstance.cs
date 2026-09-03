using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EveConsole.Services;

/// <summary>
/// Ensures only one EVE Console runs at a time, and brings the existing one forward instead.
///
/// <para>⚠️ Two copies against one SQLite file is not merely untidy. Both would poll ESI and write
/// the results, doubling the API traffic and racing each other's inserts; and a database shrink
/// replaces the file wholesale, so a second instance opening it mid-swap would be reading a file
/// that is about to stop existing. The shrink is also the moment a second launch is most likely —
/// a rebuild of a large database on a slow disk takes minutes, and a user who thinks the app has
/// hung will start it again.</para>
///
/// <para>Per-user rather than machine-wide (<c>Local\</c>, not <c>Global\</c>): two people logged
/// into the same machine have separate profiles and therefore separate databases, so neither has
/// any reason to block the other.</para>
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;

    /// <summary>
    /// An exclusive handle on a file beside the database, held for the life of the process.
    ///
    /// <para>⚠️ This is the guarantee, not the mutex. A named Mutex does not hold across
    /// processes on Linux the way it does on Windows, and TryAcquire's blanket catch turns any
    /// such failure into a silent second start — which is two writers on one SQLite file,
    /// both polling ESI and racing each other's inserts. Measured on Arch: a second copy
    /// started.</para>
    ///
    /// <para>An open handle with FileShare.None is enforced by the kernel on both platforms
    /// (.NET maps it to flock on Unix), so there is no pid to go stale and nothing to clean up
    /// after a crash: the operating system drops the lock when the process dies, however it
    /// dies.</para>
    ///
    /// <para>Beside the DATABASE rather than in the app data folder, because the database is
    /// what is being protected. Two installs pointed at different databases have no reason to
    /// exclude each other, and two pointed at the same one must.</para>
    /// </summary>
    private static FileStream? _lockFile;

    /// <summary>
    /// The same guarantee on a server, where there is no file to hold.
    ///
    /// <para>A PostgreSQL advisory lock is the exact analogue: it is scoped to the database, it
    /// is held for the life of the session that took it, and the server drops it when that
    /// session ends however it ends. So it keeps the rule this class is built on — the lock
    /// belongs to the database being protected, not to the machine — which a lock file cannot
    /// do once the database is somewhere else entirely. Two clients on different machines
    /// pointed at one server exclude each other; two pointed at different servers do not.</para>
    ///
    /// <para>⚠️ Held on its own connection, kept open deliberately. Returning it to the pool
    /// would end the session and silently drop the lock.</para>
    ///
    /// <para>⚠️ This currently stops a second client outright, matching the SQLite behaviour.
    /// The intended end state is different: additional clients run and read, and only ONE of
    /// them polls. That is the lease work, and when it lands this becomes a lock on the poller
    /// rather than on the application.</para>
    /// </summary>
    private static Npgsql.NpgsqlConnection? _pgLock;

    /// <summary>
    /// Arbitrary, and only has to be stable and unlikely to collide with another application
    /// using advisory locks in the same database.
    /// </summary>
    private const long AdvisoryLockKey = 0x4556_4543_4F4E_5301;   // "EVECONS" + 1

    private static bool TryTakePostgresLock()
    {
        try
        {
            var cs = AppConfig.GetPostgresConnection();
            if (string.IsNullOrWhiteSpace(cs)) return true;   // App reports the misconfiguration

            var conn = new Npgsql.NpgsqlConnection(cs);
            conn.Open();

            using var cmd = new Npgsql.NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn);
            cmd.Parameters.AddWithValue("k", AdvisoryLockKey);

            if (cmd.ExecuteScalar() is true)
            {
                _pgLock = conn;       // kept open: closing it releases the lock
                return true;
            }

            conn.Dispose();
            return false;             // somebody else has it: exactly what this is for
        }
        catch
        {
            // ⚠️ A server we cannot reach at all lets the app start, on purpose. It will fail
            // a moment later at EnsureCreated with an error that says what is actually wrong,
            // which is far more use than exiting silently from the single-instance check.
            return true;
        }
    }

    private static bool TryTakeLockFile()
    {
        try
        {
            var dbPath = AppConfig.GetDbPath();
            var dir    = Path.GetDirectoryName(dbPath);
            if (string.IsNullOrWhiteSpace(dir)) return true;   // nowhere to put it; do not block

            Directory.CreateDirectory(dir);

            _lockFile = new FileStream(
                Path.Combine(dir, "EveConsole.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            // The pid is for a human reading the file, not for the locking — the handle is
            // what excludes. Best effort.
            try
            {
                var pid = System.Text.Encoding.UTF8.GetBytes(
                    $"{Environment.ProcessId}{Environment.NewLine}");
                _lockFile.SetLength(0);
                _lockFile.Write(pid);
                _lockFile.Flush();
            }
            catch { }

            return true;
        }
        catch (IOException)
        {
            return false;      // somebody else holds it: exactly what this is for
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            // ⚠️ Any OTHER failure lets the app start. A lock we cannot create at all — a
            // read-only volume, an exotic filesystem — must not make the app unusable. The
            // two cases above are the ones that mean "occupied", and they are named explicitly
            // rather than swept up with everything else.
            return true;
        }
    }

    /// <summary>
    /// Passed by an instance that is deliberately replacing itself. ⚠️ Without it the app cannot
    /// restart at all: RequestRestart launches the replacement BEFORE calling Environment.Exit, so
    /// for a moment both processes are alive, the new one finds the mutex held, politely focuses
    /// the old one and exits — and then the old one exits too, leaving nothing running. That is
    /// every restart path (shrink, move, rename, re-point), not just one.
    /// </summary>
    public const string RestartingArgument = "--restarting";

    /// <summary>How long a replacement instance waits for its predecessor to let go.</summary>
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// True when this process may continue. False when another instance already holds the lock —
    /// in which case that one has been brought to the front and this one should exit quietly.
    /// </summary>
    public static bool TryAcquire(string[] args)
    {
        var replacing = args.Any(a =>
            string.Equals(a, RestartingArgument, StringComparison.OrdinalIgnoreCase));

        // ⚠️ On a server the lock lives in the database and the local mutex is meaningless
        // — the other instance may not even be on this machine, which is also why there may
        // be no window here to focus.
        if (DbEngine.IsPostgres)
        {
            if (TryTakePostgresLock()) return true;
            FocusExistingWindow();
            return false;
        }

        // ⚠️ The file lock first, because it is the one that holds on every platform. The
        // mutex stays: it is proven on Windows and costs nothing, and two agreeing guards are
        // cheaper than deciding which single one to trust.
        if (!TryTakeLockFile())
        {
            if (replacing && WaitForLockFile()) { /* predecessor let go */ }
            else { FocusExistingWindow(); return false; }
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: true, @"Local\EveConsole.SingleInstance", out var isFirst);
            if (isFirst) return true;

            // A restart waits for the outgoing process rather than treating it as a rival. It
            // releases the mutex before spawning this one, so the wait is normally instant; the
            // timeout is only there so a predecessor that dies badly cannot lock us out forever.
            if (replacing && _mutex.WaitOne(HandoverTimeout)) return true;
        }
        catch (AbandonedMutexException)
        {
            // The previous owner exited without releasing. The lock is ours and the file it was
            // protecting is no longer in use.
            return true;
        }
        catch
        {
            // A mutex we cannot create is not a reason to refuse to start.
            return true;
        }

        FocusExistingWindow();
        return false;
    }

    /// <summary>
    /// Hands the lock over before spawning a replacement. Called only by the restart path — an
    /// ordinary exit releases it for free when the process ends.
    /// </summary>
    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { /* not the owning thread — the dispose still frees it */ }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;

        // The replacement is already running and waiting on this handle.
        try { _lockFile?.Dispose(); } catch { }
        _lockFile = null;

        // Closing the session is what frees an advisory lock; there is nothing to unlock
        // separately, and unlocking before closing would only widen the gap where neither
        // process holds it.
        try { _pgLock?.Dispose(); } catch { }
        _pgLock = null;
    }

    /// <summary>
    /// Waits for a predecessor to drop the lock file during a deliberate restart.
    ///
    /// <para>Polled rather than blocking: a file lock has no wait primitive, and the wait is
    /// normally over in a single pass because Release runs before the replacement is spawned.
    /// The timeout only stops a predecessor that died badly from locking us out forever.</para>
    /// </summary>
    private static bool WaitForLockFile()
    {
        var until = DateTime.UtcNow + HandoverTimeout;
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(150);
            if (TryTakeLockFile()) return true;
        }
        return false;
    }

    /// <summary>
    /// Restores and foregrounds the running instance, so a second launch behaves like clicking the
    /// taskbar rather than doing nothing at all — which would read as the app being broken.
    ///
    /// <para>Does nothing when the other instance has no window yet: during a shrink it is still
    /// on its splash, or has not drawn one. Silence is the right answer there — the alternative is
    /// telling the user something is wrong when it is merely busy.</para>
    /// </summary>
    private static void FocusExistingWindow()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(me.ProcessName))
            {
                if (other.Id == me.Id) continue;

                var handle = other.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;

                if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
                return;
            }
        }
        catch { /* best effort — never let this stop the process exiting cleanly */ }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
}
