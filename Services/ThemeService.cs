using Avalonia;
using Avalonia.Styling;
using EveConsole.Localization;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Which theme the application is wearing.
///
/// <para>⚠️ Local, per client, like every other piece of UI state — see <see cref="UiState"/>. A
/// theme is a fact about the screen somebody is sitting in front of, not about the data, and with
/// several clients sharing one PostgreSQL database putting it in the preference table would mean
/// switching to light on the desktop switched the laptop too.</para>
///
/// <para>Applied by setting <c>RequestedThemeVariant</c> on the application, which re-resolves
/// every <c>DynamicResource</c> in every open window. ⚠️ That only reaches colours bound through
/// the palette: anything still written as a literal in a view stays exactly as it was, which is
/// what a half-converted screen looks like rather than a broken switch.</para>
/// </summary>
public static class ThemeService
{
    /// <summary>The themes that come with the application, in the order a menu should list them.
    /// ⚠️ Never changed by the user: the theme editor copies them, it does not edit them.</summary>
    public static IReadOnlyList<ThemeChoice> BuiltIn { get; } =
    [
        new("dark",        ShellText.ThemeDark,       ThemeVariant.Dark),
        new("light",       ShellText.ThemeLight,      ThemeVariant.Light),

        // ⚠️ Tints, not replacements. Each inherits Dark or Light and restates only the neutrals
        // and the accent, so everything that carries meaning — a chart line, a status word, a row
        // tint, EVE's own security ramp — reads exactly as it does on its parent. A theme changes
        // the room; it does not change what the signals mean.
        new("blue-dark",   ShellText.ThemeBlueDark,   AppThemes.BlueDark),
        new("blue-light",  ShellText.ThemeBlueLight,  AppThemes.BlueLight),
        new("pink-dark",   ShellText.ThemePinkDark,   AppThemes.PinkDark),
        new("pink-light",  ShellText.ThemePinkLight,  AppThemes.PinkLight),
        new("beige-dark",  ShellText.ThemeBeigeDark,  AppThemes.BeigeDark),
        new("beige-light", ShellText.ThemeBeigeLight, AppThemes.BeigeLight),
    ];

    /// <summary>The user's own, after the built-in ones. One instance per theme for its life, so a
    /// rename shows in a combo that has it selected without the selection being replaced.</summary>
    private static readonly List<ThemeChoice> Own = [];

    /// <summary>Every theme on offer: the built-in ones, then the user's own.</summary>
    public static IReadOnlyList<ThemeChoice> All => [.. BuiltIn, .. Own];

    /// <summary>
    /// ⚠️ Dark. This application has only ever been dark, so anything else would change how it
    /// looks for everyone on upgrade, without being asked to.
    ///
    /// <para>⚠️ Also where an UNKNOWN key lands, which is what retires a theme safely. There was a
    /// "Follow the desktop" entry, and it could only ever have followed two of the eight: the
    /// desktop says light or dark and has no opinion about blue, pink or beige, so choosing it
    /// silently discarded the tint. Anyone still holding "system" arrives here.</para>
    /// </summary>
    public const string DefaultKey = "dark";

    public static string Current { get; private set; } = DefaultKey;

    /// <summary>
    /// Raised after a theme has been applied.
    ///
    /// <para>⚠️ There are two pickers now — the Settings window and the label on the title bar —
    /// and each has to follow the other. Without this, changing the theme from the bar left the
    /// Settings combo naming the theme that used to be on.</para>
    ///
    /// <para>Subscribers are static-rooted, so anything that lives shorter than the application
    /// must unsubscribe. Both current subscribers outlive it.</para>
    /// </summary>
    public static event Action? Changed;

    /// <summary>Raised when a theme of the user's own is added or deleted, for the lists of themes
    /// to follow. A rename needs nothing: the choice's Name changes in place.</summary>
    public static event Action? ListChanged;

    /// <summary>Reads the saved choice and puts it on. Called once, before the first window.</summary>
    public static void ApplySaved()
    {
        CustomThemes.Load();
        SyncOwn();

        var saved = UiState.Get(UiState.Theme) ?? DefaultKey;
        Apply(Find(saved) is null ? DefaultKey : saved);
    }

    /// <summary>Switches theme and remembers it.</summary>
    public static void Apply(string key)
    {
        var choice = Find(key);
        if (choice is null) return;

        Current = choice.Key;

        if (Application.Current is { } app) app.RequestedThemeVariant = choice.Variant;

        // ⚠️ Charts do not follow on their own. LiveCharts draws through Skia and takes a colour
        // value, so an axis keeps whatever it was built with until something replaces it — the
        // rest of the window would turn and the chart frames would not.
        ChartPaint.Restyle();

        UiState.Set(UiState.Theme, choice.Key);

        Changed?.Invoke();
    }

    /// <summary>
    /// Keeps a theme of the user's own: added, or its new name and colours taken. If it is the
    /// one being worn, the application takes the new colours at once.
    /// </summary>
    public static void SaveOwn(CustomTheme theme)
    {
        var added = CustomThemes.Find(theme.Key) is null;
        CustomThemes.Save(theme);
        SyncOwn();

        if (string.Equals(Current, theme.Key, StringComparison.OrdinalIgnoreCase))
        {
            // The variant is new when the ground changed; the same one otherwise, and its colours
            // have already been announced. Either way the charts are rebuilt by hand.
            // ⚠️ A new variant goes on by way of its ground. Variants are equal by key, so asking
            // for the new one straight over the old is no change at all, and the application kept
            // the old parent: a theme moved to light still drew dark Fluent controls.
            if (Application.Current is { } app && !ReferenceEquals(app.RequestedThemeVariant, Find(theme.Key)!.Variant))
            {
                var variant = Find(theme.Key)!.Variant;
                app.RequestedThemeVariant = variant.InheritVariant;
                app.RequestedThemeVariant = variant;
            }
            ChartPaint.Restyle();
            Changed?.Invoke();
        }

        if (added) ListChanged?.Invoke();
    }

    /// <summary>Deletes a theme of the user's own. Wearing it, the application goes to its ground.</summary>
    public static void DeleteOwn(string key)
    {
        var ground = CustomThemes.IsLightTheme(key) ? "light" : "dark";
        var wearing = string.Equals(Current, key, StringComparison.OrdinalIgnoreCase);

        // Off it first: a variant being worn is not removed from under the windows wearing it.
        if (wearing) Apply(ground);

        CustomThemes.Delete(key);
        SyncOwn();
        ListChanged?.Invoke();
    }

    public static bool IsOwn(string key) => CustomThemes.Find(key) is not null;

    private static void SyncOwn()
    {
        Own.RemoveAll(c => CustomThemes.Find(c.Key) is null);
        foreach (var t in CustomThemes.All)
        {
            var variant = CustomThemes.VariantOf(t.Key);
            if (variant is null) continue;
            var choice = Own.FirstOrDefault(c => c.Key == t.Key);
            if (choice is null) Own.Add(new ThemeChoice(t.Key, t.Name, variant, isOwn: true));
            else { choice.Name = t.Name; choice.Variant = variant; }
        }
    }

    private static ThemeChoice? Find(string key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One entry in the theme list. Key is what is stored; Name is what is shown.</summary>
public sealed class ThemeChoice : ReactiveObject
{
    /// <param name="key">⚠️ Stable, and not the display name: renaming "Dark" to "Midnight" in the
    /// menu must not silently reset everybody who had it selected.</param>
    public ThemeChoice(string key, string name, ThemeVariant variant, bool isOwn = false)
    {
        Key     = key;
        _name   = name;
        Variant = variant;
        IsOwn   = isOwn;
    }

    public string Key { get; }

    private string _name;
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    /// <summary>Settable for a theme of the user's own, whose ground can change.</summary>
    public ThemeVariant Variant { get; set; }

    /// <summary>The user's own, which can be edited and deleted; the built-in ones cannot.</summary>
    public bool IsOwn { get; }

    public override string ToString() => Name;
}
