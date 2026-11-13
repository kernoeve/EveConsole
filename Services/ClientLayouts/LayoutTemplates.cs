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
/// stays that way, and a template added by a later version still arrives.</para>
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
            var added = false;
            foreach (var shipped in Directory.EnumerateFiles(ShippedFolder, "*.json"))
            {
                var name = Path.GetFileName(shipped);
                if (!given.Add(name)) continue;
                added = true;
                var target = Path.Combine(Folder, name);
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
}
