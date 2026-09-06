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
/// <para>Silent for a desktop client and for <c>--headless</c> run from a terminal: those have
/// somewhere better to put it. This is the fallback for the one that does not.</para>
/// </summary>
public static class ServiceLog
{
    public static string FilePath => Path.Combine(MachineConfig.Folder, "service.log");

    /// <summary>
    /// ⚠️ Trimmed, not rotated. It records a handful of lines per start and one per lease change,
    /// so it grows slowly — but a worker left running for months would grow it forever, and an
    /// unbounded log on a system drive is a fault of its own.
    /// </summary>
    private const long MaxBytes = 512 * 1024;

    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        if (!AppRuntime.IsService) return;

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(MachineConfig.Folder);

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
