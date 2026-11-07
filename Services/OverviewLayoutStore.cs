using System.Text.Json;
using EveConsole.Localization;
using EveConsole.Models;

namespace EveConsole.Services;

/// <summary>A saved Overview layout to choose: <see cref="OverviewLayoutStore.DefaultKey"/> for the Default one.</summary>
public sealed record OverviewLayoutChoice(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What the layout editor changed, applied in one go when it's closed with Apply.</summary>
/// <param name="Layouts">Every layout kept, in order: the key it had (null for a new one), its name now, and its layout.</param>
/// <param name="Shown">Which of <paramref name="Layouts"/> the Overview the editor was opened from should show.</param>
public sealed record OverviewLayoutEdits(IReadOnlyList<(string? Was, string Key, OverviewLayout Layout)> Layouts, int Shown);

/// <summary>
/// The Overview's layouts on this machine: "Default", and any others made in the layout editor.
///
/// <para>⚠️ Default stays where the single layout always was (config.json, see
/// <see cref="AppConfig.GetOverviewLayout"/>), so an older build on this machine still shows and
/// edits it. The others are in a file of their own, because every build reads config.json whole and
/// writes back only the fields it knows: kept there, one run of an older build would delete them.
/// Nothing but this class touches that file.</para>
///
/// <para>Each Overview shows one of them. The main window's choice is kept here too, so it comes
/// back after a restart; an Overview in a Tool Container starts with the layout its container sets.
/// Used on the UI thread only.</para>
/// </summary>
public sealed class OverviewLayoutStore(string filePath, Func<string?> readDefault, Action<string?> writeDefault)
{
    public const string DefaultKey = "";
    private const string BackupSuffix = ".bak";

    public static OverviewLayoutStore Shared { get; } = new(
        Path.Combine(AppConfig.AppDataDir, "overview-layouts.json"), AppConfig.GetOverviewLayout, AppConfig.SetOverviewLayout);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// The layouts changed. Carries how keys were renamed, old to new, so an Overview showing a
    /// renamed layout keeps showing it; one whose layout is gone goes back to Default.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, string>>? Changed;

    private sealed class Named
    {
        public string Name { get; set; } = "";
        public OverviewLayout Layout { get; set; } = new();
    }

    private sealed class Saved
    {
        public List<Named> Layouts { get; set; } = [];
        /// <summary>The layout the main window's Overview shows.</summary>
        public string? MainLayout { get; set; }
    }

    /// <summary>Default first, then the others in the order they were made.</summary>
    public IReadOnlyList<OverviewLayoutChoice> Choices =>
        [new(DefaultKey, OverviewText.LayoutDefault), .. Read().Layouts.Select(l => new OverviewLayoutChoice(l.Name, l.Name))];

    public static bool IsDefault(string? key) => string.IsNullOrEmpty(key);

    public bool Exists(string? key) => IsDefault(key) || Read().Layouts.Any(l => Same(l.Name, key));

    /// <summary>A layout by key, with any section added since it was saved. One that's gone is Default.</summary>
    public OverviewLayout Get(string? key)
    {
        if (!IsDefault(key) && Read().Layouts.FirstOrDefault(l => Same(l.Name, key)) is { } named)
            return OverviewLayout.FromJsonOrDefault(named.Layout.ToJson());
        return OverviewLayout.FromJsonOrDefault(readDefault());
    }

    /// <summary>The key itself if it's a layout here, otherwise Default's.</summary>
    public string KeyOf(string? key) =>
        Read().Layouts.FirstOrDefault(l => Same(l.Name, key))?.Name ?? DefaultKey;

    /// <summary>Saves one layout's arrangement under its key.</summary>
    public void Save(string? key, OverviewLayout layout)
    {
        if (IsDefault(key)) writeDefault(layout.ToJson());
        else
        {
            var saved = Read();
            if (saved.Layouts.FirstOrDefault(l => Same(l.Name, key)) is not { } named) return;
            named.Layout = layout;
            Write(saved);
        }
        Changed?.Invoke(new Dictionary<string, string>());
    }

    /// <summary>The layout the main window shows, kept across restarts.</summary>
    public string MainLayout
    {
        get => KeyOf(Read().MainLayout);
        set
        {
            var saved = Read();
            var key = IsDefault(value) ? null : value;
            if (saved.MainLayout == key) return;
            saved.MainLayout = key;
            Write(saved);
        }
    }

    /// <summary>
    /// Everything the layout editor did: layouts arranged, added, renamed and deleted. A deleted
    /// layout is one the edits leave out. Names are made unique here, so the editor can't save two
    /// alike. Returns the key the edited Overview should show.
    /// </summary>
    public string Apply(OverviewLayoutEdits edits)
    {
        var saved = Read();
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<Named>();
        var shown = DefaultKey;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OverviewText.LayoutDefault };

        for (var i = 0; i < edits.Layouts.Count; i++)
        {
            var (was, key, layout) = edits.Layouts[i];
            // Default is the one that was Default: a new layout never is, whatever it's called.
            if (was is not null && IsDefault(was))
            {
                writeDefault(layout.ToJson());
                continue;
            }
            var name = Unique(string.IsNullOrWhiteSpace(key) ? OverviewText.LayoutNewName : key.Trim(), taken);
            taken.Add(name);
            kept.Add(new Named { Name = name, Layout = layout });
            if (was != null && !Same(was, name)) renames[was] = name;
            if (i == edits.Shown) shown = name;
        }

        saved.Layouts = kept;
        if (saved.MainLayout is { } main)
            saved.MainLayout = renames.TryGetValue(main, out var renamed) ? renamed : kept.Any(k => Same(k.Name, main)) ? main : null;
        Write(saved);
        Changed?.Invoke(renames);
        return shown;
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file, or its backup if the file can't be read; nothing saved yet is an empty list.</summary>
    private Saved Read()
    {
        foreach (var path in new[] { filePath, filePath + BackupSuffix })
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (JsonSerializer.Deserialize<Saved>(stream, JsonOpts) is { } saved) return saved;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return new Saved();
    }

    /// <summary>Swapped in whole, leaving the previous contents as the backup.</summary>
    private void Write(Saved saved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(saved, JsonOpts));
        if (File.Exists(filePath)) File.Replace(tmp, filePath, filePath + BackupSuffix, ignoreMetadataErrors: true);
        else File.Move(tmp, filePath);
    }
}
