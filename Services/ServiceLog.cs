namespace EveConsole.Services;

/// <summary>
/// A plain text log for a process that has nowhere else to write.
///
/// <para>⚠️ Exists because a Windows service is otherwise a black box. It has no console — the
/// banner it prints goes to a handle nobody owns — and the application's own error log is a table
/// in the database, which is unreachable in exactly the case worth reporting. A service that is
/// alive, connected and doing nothing looks identical to one that is working perfectly, and there
/// was no way to tell them apart from outside.</para>
///
/// <para>⚠️ Written by every worker, not only the Windows service. A systemd unit has the journal
/// and a terminal has stdout, but both belong to whoever started the process — and somebody working
/// out why a worker is doing nothing is usually looking hours later, from a different session. A
/// file the worker always writes is the one account of itself that is still there.</para>
///
/// <para>Silent for a desktop client, which has a window to say things in.</para>
/// </summary>
public static class ServiceLog
{
    /// <summary>
    /// ⚠️ Machine-wide on Windows and per-user everywhere else, because that is what the two
    /// workers are. The Windows service runs as LocalSystem and has no user profile to write into;
    /// a systemd user unit runs as the user and cannot write to the machine-wide directory —
    /// <c>CommonApplicationData</c> resolves to /usr/share there, which belongs to root.
    /// </summary>
    public static string FilePath => Path.Combine(Folder, "service.log");

    private static string Folder =>
        OperatingSystem.IsWindows() ? MachineConfig.Folder : AppConfig.AppDataDir;

    /// <summary>
    /// ⚠️ Trimmed, not rotated. It records a handful of lines per start and one per lease change,
    /// so it grows slowly — but a worker left running for months would grow it forever, and an
    /// unbounded log on a system drive is a fault of its own.
    /// </summary>
    private const long MaxBytes = 512 * 1024;

    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        if (!AppRuntime.IsHeadless) return;   // implied by IsService

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);

                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    // Keep the tail: what happened most recently is what anybody is looking for.
                    var kept = File.ReadLines(FilePath).TakeLast(200).ToList();
                    File.WriteAllLines(FilePath, kept);
                }

                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* a log that cannot be written must not take the worker down with it */ }
    }
}
