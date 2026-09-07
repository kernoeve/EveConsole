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
        new("dark",   "Dark",   ThemeVariant.Dark),
        new("light",  "Light",  ThemeVariant.Light),
        new("system", "Follow the desktop", ThemeVariant.Default),
    ];

    /// <summary>
    /// ⚠️ Dark, not "follow the desktop". This application has only ever been dark, so defaulting
    /// to the desktop's setting would turn it white for everyone whose desktop is light — an
    /// upgrade that changes how the app looks without being asked to.
    /// </summary>
    public const string DefaultKey = "dark";

    public static string Current { get; private set; } = DefaultKey;

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

        UiState.Set(UiState.Theme, choice.Key);
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
