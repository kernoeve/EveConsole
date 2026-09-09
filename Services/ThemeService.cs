using Avalonia;
using Avalonia.Styling;

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
    /// <summary>The themes on offer, in the order a menu should list them.</summary>
    public static IReadOnlyList<ThemeChoice> All { get; } =
    [
        new("dark",        "Dark",        ThemeVariant.Dark),
        new("light",       "Light",       ThemeVariant.Light),

        // ⚠️ Tints, not replacements. Each inherits Dark or Light and restates only the neutrals
        // and the accent, so everything that carries meaning — a chart line, a status word, a row
        // tint, EVE's own security ramp — reads exactly as it does on its parent. A theme changes
        // the room; it does not change what the signals mean.
        new("blue-dark",   "Blue (dark)",  AppThemes.BlueDark),
        new("blue-light",  "Blue (light)", AppThemes.BlueLight),
        new("pink-dark",   "Pink (dark)",  AppThemes.PinkDark),
        new("pink-light",  "Pink (light)", AppThemes.PinkLight),
        new("beige-dark",  "Beige (dark)", AppThemes.BeigeDark),
        new("beige-light", "Beige (light)",AppThemes.BeigeLight),
    ];

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

    /// <summary>Reads the saved choice and puts it on. Called once, before the first window.</summary>
    public static void ApplySaved()
    {
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

    private static ThemeChoice? Find(string key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One entry in the theme list. Key is what is stored; Name is what is shown.</summary>
/// <param name="Key">⚠️ Stable, and not the display name: renaming "Dark" to "Midnight" in the menu
/// must not silently reset everybody who had it selected.</param>
public sealed record ThemeChoice(string Key, string Name, ThemeVariant Variant)
{
    public override string ToString() => Name;
}
