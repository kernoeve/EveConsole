using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace EveConsole.Services;

/// <summary>One theme the user made: a name, the ground it sits on, and its colours.</summary>
public sealed class CustomTheme
{
    /// <summary>"custom-" and a short id. ⚠️ Stable, not the name: the name can be changed in the
    /// editor, and the saved choice of theme must not be lost when it is.</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// Light ground or dark. ⚠️ More than a label: it is the variant the theme inherits, so it
    /// decides everything the palette does not reach — Fluent's own controls, and the chart series
    /// and security colours still written for one ground or the other.
    /// </summary>
    public bool IsLight { get; set; }

    /// <summary>Token → "#rrggbb", for every token in <see cref="ThemeTokens.Editable"/>.</summary>
    public Dictionary<string, string> Colours { get; set; } = [];
}

/// <summary>The palette tokens a theme of the user's own sets, in the editor's order.</summary>
public static class ThemeTokens
{
    public sealed record Group(string Key, IReadOnlyList<string> Tokens);

    public static IReadOnlyList<Group> Groups { get; } =
    [
        new("Surfaces", ["SurfaceBase", "SurfacePanel", "SurfacePanelAlt", "SurfaceHeader",
                         "SurfaceRaised", "SurfaceInput", "SurfaceHover", "SurfaceSelected"]),
        new("Lines",    ["BorderSubtle", "BorderDefault", "BorderStrong"]),
        new("Text",     ["TextFaint", "TextDim", "TextMuted", "TextSecondary", "TextPrimary", "TextBright"]),
        new("Accent",   ["Accent", "AccentHover", "AccentPressed", "AccentSurface", "AccentDeep", "AccentPale"]),
        new("Status",   ["Good", "Bad", "Warn", "Info", "GoodSurface", "BadSurface", "WarnSurface", "InfoSurface"]),
    ];

    public static IReadOnlyList<string> Editable { get; } = Groups.SelectMany(g => g.Tokens).ToList();

    /// <summary>
    /// The rest of the palette, worked out from the editable tokens rather than offered as more
    /// pickers. ⚠️ All of it has to be there all the same: a custom variant that leaves a key out
    /// borrows its parent's, so a missing accent ramp would put Dark's amber on every tab underline
    /// and checkbox of a blue theme.
    /// </summary>
    public static Dictionary<string, Color> Derived(IReadOnlyDictionary<string, Color> c, bool light)
    {
        var d = new Dictionary<string, Color>
        {
            // Fluent's accent ramp, which it reads by name for tab underlines, focus rings and
            // checkbox fills — the same steps the built-in themes give it.
            ["SystemAccentColor"]       = c["Accent"],
            ["SystemAccentColorDark1"]  = c["AccentPressed"],
            ["SystemAccentColorDark2"]  = Colours.Mix(c["AccentPressed"], c["AccentDeep"], 0.5),
            ["SystemAccentColorDark3"]  = c["AccentDeep"],
            ["SystemAccentColorLight1"] = c["AccentHover"],
            ["SystemAccentColorLight2"] = Colours.Mix(c["AccentHover"], c["AccentPale"], 0.5),
            ["SystemAccentColorLight3"] = c["AccentPale"],

            // ⚠️ Translucent: the map label plates and tooltips let the starfield through.
            ["SurfaceOverlay"]       = Colours.WithAlpha(c["SurfacePanel"], 0xcc),
            ["SurfaceOverlayStrong"] = Colours.WithAlpha(light ? c["SurfaceRaised"] : c["SurfaceBase"], light ? (byte)0xf2 : (byte)0xf0),
        };

        // The grid's column washes are a hue each and faint; they are tuned for a ground, not a
        // tint, so they come from the parent unchanged.
        foreach (var k in new[] { "ColumnGroupA", "ColumnGroupB", "ColumnGroupC" })
            d[k] = BuiltIn(light ? ThemeVariant.Light : ThemeVariant.Dark, k);

        return d;
    }

    /// <summary>A token as a built-in variant defines it, through its parent where it says nothing.</summary>
    public static Color BuiltIn(ThemeVariant variant, string token) =>
        Application.Current is { } app && app.TryGetResource(token, variant, out var v) && v is Color c
            ? c : Colors.Magenta;
}

/// <summary>
/// The user's own themes: kept, registered with the application as theme variants, and built.
///
/// <para>⚠️ Local, like the choice of theme itself (<see cref="ThemeService"/>): a theme is how one
/// screen is dressed, and the shared database is for the data.</para>
///
/// <para>Each one is a real <see cref="ThemeVariant"/> inheriting Dark or Light, with its colours in
/// a theme dictionary added to the application's resources at run time — exactly what
/// <c>Themes/Palette.axaml</c> declares for the built-in ones. So every brush and every
/// DynamicResource in the application follows it with no change anywhere else.</para>
/// </summary>
public static class CustomThemes
{
    private static readonly List<CustomTheme> Themes = [];
    private static readonly Dictionary<string, (ThemeVariant Variant, ResourceDictionary Dict)> Registered = [];

    public static IReadOnlyList<CustomTheme> All => Themes;

    public static CustomTheme? Find(string key) =>
        Themes.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    public static ThemeVariant? VariantOf(string key) =>
        Registered.TryGetValue(key, out var r) ? r.Variant : null;

    /// <summary>Reads the saved themes and registers each. Once, before the saved choice is applied.</summary>
    public static void Load()
    {
        Themes.Clear();
        try
        {
            var json = UiState.Get(UiState.CustomThemes);
            if (!string.IsNullOrWhiteSpace(json))
                Themes.AddRange(JsonSerializer.Deserialize<List<CustomTheme>>(json) ?? []);
        }
        catch { /* a file edited by hand into nonsense loses the themes, not the application */ }

        // ⚠️ Only themes that can be worn: one missing a colour (an older file, or one edited by
        // hand) is completed from its ground rather than left to borrow it token by token.
        foreach (var t in Themes)
        {
            Complete(t);
            Register(t);
        }
    }

    /// <summary>Adds a theme, or takes new values for one already kept; registers and saves it.</summary>
    public static void Save(CustomTheme theme)
    {
        Complete(theme);
        var at = Themes.FindIndex(t => t.Key == theme.Key);
        if (at < 0) Themes.Add(theme); else Themes[at] = theme;
        Register(theme);
        Persist();
    }

    public static void Delete(string key)
    {
        Themes.RemoveAll(t => t.Key == key);
        if (Registered.Remove(key, out var r) && Application.Current is { } app)
            app.Resources.ThemeDictionaries.Remove(r.Variant);
        Persist();
    }

    public static string NewKey() => "custom-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Every editable colour of any theme, built-in or the user's own — what the editor shows and
    /// what a copy starts from.
    /// </summary>
    public static Dictionary<string, Color> ColoursOf(string key)
    {
        if (Find(key) is { } own)
            return ThemeTokens.Editable.ToDictionary(t => t, t => Colours.Parse(own.Colours.GetValueOrDefault(t)) ?? Colors.Magenta);

        var variant = ThemeService.All.FirstOrDefault(c => c.Key == key)?.Variant ?? ThemeVariant.Dark;
        return ThemeTokens.Editable.ToDictionary(t => t, t => ThemeTokens.BuiltIn(variant, t));
    }

    /// <summary>Whether a built-in theme is on a light ground.</summary>
    public static bool IsLightTheme(string key)
    {
        if (Find(key) is { } own) return own.IsLight;
        var v = ThemeService.All.FirstOrDefault(c => c.Key == key)?.Variant;
        while (v?.InheritVariant is not null) v = v.InheritVariant;
        return v == ThemeVariant.Light;
    }

    // ── Starting from three colours ───────────────────────────────────────────

    /// <summary>
    /// A whole palette from a ground, a text colour and an accent: every surface, line and text
    /// step mixed between them, in the proportions the built-in Dark and Light use. The status
    /// colours are the ground's own and are left to the caller.
    ///
    /// <para>⚠️ The two grounds step in opposite directions, as the built-in ones do. On dark, a
    /// raised thing is nearer the text colour; on light it is nearer white, and the page is darker
    /// than its panels — keeping dark's direction gives grey cards on a white page, which reads as
    /// disabled.</para>
    /// </summary>
    public static Dictionary<string, Color> FromThree(bool light, Color ground, Color text, Color accent)
    {
        var white = Color.FromRgb(255, 255, 255);
        var black = Color.FromRgb(0, 0, 0);
        Color Up(double f)   => Colours.Mix(ground, light ? white : text, f);   // raised
        Color Down(double f) => Colours.Mix(ground, text, f);                   // toward the ink

        var c = new Dictionary<string, Color>
        {
            ["SurfaceBase"]     = ground,
            ["SurfacePanel"]    = light ? Up(0.25) : Up(0.04),
            ["SurfacePanelAlt"] = light ? Up(0.13) : Up(0.06),
            ["SurfaceHeader"]   = light ? Down(0.06) : Up(0.09),
            ["SurfaceRaised"]   = light ? Up(0.43) : Up(0.13),
            ["SurfaceInput"]    = light ? Up(0.47) : Up(0.03),
            ["SurfaceHover"]    = light ? Down(0.04) : Up(0.11),

            ["BorderSubtle"]    = light ? Down(0.03) : Down(0.11),
            ["BorderDefault"]   = light ? Down(0.17) : Down(0.18),
            ["BorderStrong"]    = light ? Down(0.36) : Down(0.26),

            ["TextFaint"]       = Down(light ? 0.53 : 0.46),
            ["TextDim"]         = Down(light ? 0.62 : 0.55),
            ["TextMuted"]       = Down(light ? 0.68 : 0.70),
            ["TextSecondary"]   = Down(0.85),
            ["TextPrimary"]     = text,
            ["TextBright"]      = Colours.Mix(text, light ? black : white, 0.5),

            ["Accent"]          = accent,
            ["AccentHover"]     = Colours.Mix(accent, white, light ? 0.15 : 0.2),
            ["AccentPressed"]   = Colours.Mix(accent, black, 0.18),
            ["AccentDeep"]      = Colours.Mix(accent, black, 0.4),
            ["AccentPale"]      = Colours.Mix(accent, white, light ? 0.3 : 0.45),
        };
        c["SurfaceSelected"] = Colours.Mix(c["SurfacePanel"], accent, light ? 0.18 : 0.15);
        c["AccentSurface"]   = Colours.Mix(c["SurfacePanel"], accent, light ? 0.18 : 0.15);
        return c;
    }

    // ── Registration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Puts a theme's colours where the application looks them up.
    ///
    /// <para>⚠️ Into the dictionary already registered when there is one, value by value, rather
    /// than a new dictionary in its place: a value set in a dictionary the application holds is
    /// announced, and every DynamicResource on it re-resolves — which is how a theme being worn
    /// takes its new colours. A new variant only when the ground changed, since the parent is part
    /// of the variant and cannot be changed on one.</para>
    /// </summary>
    private static void Register(CustomTheme theme)
    {
        if (Application.Current is not { } app) return;

        var parent = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        if (Registered.TryGetValue(theme.Key, out var r) && r.Variant.InheritVariant != parent)
        {
            app.Resources.ThemeDictionaries.Remove(r.Variant);
            Registered.Remove(theme.Key);
        }

        if (!Registered.TryGetValue(theme.Key, out r))
        {
            r = (new ThemeVariant(theme.Key, parent), new ResourceDictionary());
            Fill(r.Dict, theme);
            app.Resources.ThemeDictionaries[r.Variant] = r.Dict;
            Registered[theme.Key] = r;
            return;
        }

        Fill(r.Dict, theme);
    }

    private static void Fill(ResourceDictionary dict, CustomTheme theme)
    {
        var colours = ThemeTokens.Editable.ToDictionary(t => t, t => Colours.Parse(theme.Colours[t])!.Value);
        foreach (var (k, v) in colours.Concat(ThemeTokens.Derived(colours, theme.IsLight)))
            if (!dict.TryGetValue(k, out var old) || old is not Color o || o != v)
                dict[k] = v;
    }

    /// <summary>Fills any colour a theme lacks from its ground, so it never borrows one silently.</summary>
    private static void Complete(CustomTheme theme)
    {
        var ground = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        foreach (var t in ThemeTokens.Editable)
            if (Colours.Parse(theme.Colours.GetValueOrDefault(t)) is null)
                theme.Colours[t] = Colours.Hex(ThemeTokens.BuiltIn(ground, t));
    }

    private static void Persist() =>
        UiState.Set(UiState.CustomThemes, JsonSerializer.Serialize(Themes));
}

/// <summary>The colour arithmetic the themes need.</summary>
public static class Colours
{
    /// <summary>a, moved <paramref name="f"/> of the way to b, channel by channel.</summary>
    public static Color Mix(Color a, Color b, double f)
    {
        byte M(byte x, byte y) => (byte)Math.Clamp(Math.Round(x + (y - x) * f), 0, 255);
        return Color.FromArgb(M(a.A, b.A), M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Six digits, lower case, as Palette.axaml writes them.</summary>
    public static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>"#rrggbb", "rrggbb" or "#rgb"; null for anything else, including a value half typed.</summary>
    public static Color? Parse(string? hex)
    {
        var s = (hex ?? "").Trim();
        if (s.Length == 0) return null;
        if (!s.StartsWith('#')) s = "#" + s;
        if (s.Length is not (4 or 7)) return null;
        return Color.TryParse(s, out var c) ? c : null;
    }
}
