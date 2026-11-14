using EveConsole.Models;

namespace EveConsole.Services.ClientLayouts;

/// <summary>A layout file a new layout can start from.</summary>
public sealed record LayoutTemplate(string Name, LayoutArea Root);

/// <summary>
/// The layouts a new one can start from: layout files (see <see cref="ClientLayoutFile"/>) in a
/// folder of the person's data, beside the config and the database. A layout exported there is a
/// template from then on.
///
/// <para>The app ships some in <c>LayoutTemplates</c> beside the executable, and copies each into the
/// data folder once. The ones given are listed in a file there, so one the person deletes or changes
/// stays that way, and a template added by a later version still arrives. A template the app changed
/// later replaces the copy only while that copy is still exactly an earlier shipped version
/// (<c>superseded.txt</c> beside them): ⚠️ when a shipped template changes, add the old version's hash
/// there (<see cref="HashAsync"/>), or people who already have it keep the old one.</para>
/// </summary>
public static class LayoutTemplates
{
    /// <summary>The person's own folder of templates, which they can add to.</summary>
    public static string Folder => Path.Combine(AppConfig.AppDataDir, "Layout Templates");

    private static string ShippedFolder => Path.Combine(AppContext.BaseDirectory, "LayoutTemplates");

    /// <summary>The shipped templates already given, one file name a line.</summary>
    private const string GivenFile = ".shipped";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Copies in the shipped templates not given before. Never replaces a file there: a person's
    /// own template of the same name stays theirs.
    /// </summary>
    public static async Task CopyShippedAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Folder);
            if (!Directory.Exists(ShippedFolder)) return;
            var givenPath = Path.Combine(Folder, GivenFile);
            var given = File.Exists(givenPath)
                ? (await File.ReadAllLinesAsync(givenPath, ct).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var superseded = await SupersededAsync(ct).ConfigureAwait(false);
            var added = false;
            foreach (var shipped in Directory.EnumerateFiles(ShippedFolder, "*.json"))
            {
                var name = Path.GetFileName(shipped);
                var target = Path.Combine(Folder, name);
                if (!given.Add(name))
                {
                    // Given before: brought up to date only while it is still exactly a version the app shipped
                    // before. One the person changed, or saved over, stays theirs.
                    if (File.Exists(target) && superseded.Contains((name, await HashAsync(target, ct).ConfigureAwait(false))))
                        File.Copy(shipped, target, overwrite: true);
                    continue;
                }
                added = true;
                if (!File.Exists(target)) File.Copy(shipped, target);
            }
            if (added)
                await File.WriteAllLinesAsync(givenPath, given.Order(StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Every template in the folder, by name. A file that can't be read as a layout is left out,
    /// and so is one that won't open: one bad file mustn't hide the rest.
    /// </summary>
    public static async Task<List<LayoutTemplate>> ListAsync(IReadOnlyCollection<MumbleServer> servers, CancellationToken ct = default)
    {
        await CopyShippedAsync(ct).ConfigureAwait(false);
        var found = new List<LayoutTemplate>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var (name, root) = await ClientLayoutFile.ReadAsync(stream, servers, ct).ConfigureAwait(false);
                found.Add(new LayoutTemplate(name.Length > 0 ? name : Path.GetFileNameWithoutExtension(path), root));
            }
            catch (Exception e) when (e is LayoutFileException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return [.. found.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Earlier versions of the shipped templates, each a SHA-256 and the file's name on a line.</summary>
    private const string SupersededFile = "superseded.txt";

    /// <summary>
    /// Every earlier version of a shipped template, by name and SHA-256, from <see cref="SupersededFile"/> beside
    /// them: a copy still exactly one of these was never changed by the person, so a newer version replaces it.
    /// When a template changes, the hash of the version it replaces goes in that file.
    /// </summary>
    private static async Task<HashSet<(string Name, string Hash)>> SupersededAsync(CancellationToken ct)
    {
        var path = Path.Combine(ShippedFolder, SupersededFile);
        var found = new HashSet<(string, string)>();
        if (!File.Exists(path)) return found;
        foreach (var line in await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var space = text.IndexOf(' ');
            if (space > 0) found.Add((text[(space + 1)..].Trim(), text[..space].ToLowerInvariant()));
        }
        return found;
    }

    /// <summary>The SHA-256 of a template's text with its line endings as "\n": a release checked out on another
    /// system may have shipped the same template with the other line endings.</summary>
    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        var text = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}
