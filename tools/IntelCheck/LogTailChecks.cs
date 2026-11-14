using EveConsole.Monitoring;

namespace EveConsole.Tools.IntelCheck;

/// <summary>
/// The chat log tail sees a log the client still holds open grow. Intel comes from those lines, and
/// from 2026-11-13 the tail had stopped seeing them: a folder listing gives a file's size and write
/// time as the folder last recorded them, which stays behind while a writer keeps the file open.
/// </summary>
internal static class LogTailChecks
{
    public static void Run(Checks c, string scratchDir)
    {
        var dir = Path.Combine(scratchDir, "logtail");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"qzx.intel_{DateTime.UtcNow:yyyyMMdd}_000000_1.txt");
        var line = System.Text.Encoding.Unicode.GetBytes("[ 2026.11.13 00:00:00 ] Qzx Reporter > QZ-X77 clr\r\n");

        // Held open for writing, as the game client holds its logs all session.
        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.Write(line);
            writer.Flush();
            var first = LogFileCursor.RecentFiles(dir, DateTime.UtcNow.AddMinutes(-30)).SingleOrDefault();

            for (var i = 0; i < 20; i++) writer.Write(line);
            writer.Flush();
            var later = LogFileCursor.RecentFiles(dir, DateTime.UtcNow.AddMinutes(-30)).SingleOrDefault();

            c.Check("log tail: a log still held open by its writer is found with its real size, not the folder's stale one",
                first?.Length == line.Length && later?.Length == writer.Position,
                $"first {first?.Length} of {line.Length}, then {later?.Length} of {writer.Position}");
        }
        Directory.Delete(dir, true);
    }
}
