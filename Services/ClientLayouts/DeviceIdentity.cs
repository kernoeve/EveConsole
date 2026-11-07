namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// Which desktop this is. The database can't say, because several desktops can share one, so the
/// answer is a generated id in a small file of its own in the app's data folder.
///
/// <para>⚠️ Not in config.json: a build that doesn't know a field drops it the next time it saves
/// that file, and a lost id would turn this desktop into a stranger with no layouts.</para>
/// </summary>
public static class DeviceIdentity
{
    private const string FileName = "client-layout-device.txt";
    private const string HolderFileName = "client-layout-holder.lock";

    private static readonly object Gate = new();
    private static string? _id;
    private static FileStream? _holder;

    public static string Id
    {
        get
        {
            lock (Gate)
            {
                if (_id != null) return _id;
                var path = Path.Combine(AppConfig.AppDataDir, FileName);
                if (File.Exists(path))
                {
                    var saved = File.ReadAllText(path).Trim();
                    if (saved.Length > 0) return _id = saved;
                }
                var created = Guid.NewGuid().ToString("N");
                Write(path, created);
                return _id = created;
            }
        }
    }

    /// <summary>This desktop becomes an existing device, as after a Windows reinstall.</summary>
    public static void TakeOver(string id)
    {
        lock (Gate)
        {
            Write(Path.Combine(AppConfig.AppDataDir, FileName), id);
            _id = id;
        }
    }

    /// <summary>
    /// Only one copy of the app on a desktop may move clients, or two would fight over them. An open
    /// file held exclusively decides it: the system releases it however the holder ends.
    /// </summary>
    public static bool TryHoldPlacement()
    {
        lock (Gate)
        {
            if (_holder != null) return true;
            try
            {
                _holder = new FileStream(Path.Combine(AppConfig.AppDataDir, HolderFileName),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                return false;   // another copy has it
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public static void ReleasePlacement()
    {
        lock (Gate)
        {
            _holder?.Dispose();
            _holder = null;
        }
    }

    private static void Write(string path, string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, id);
        File.Move(temp, path, overwrite: true);
    }
}
