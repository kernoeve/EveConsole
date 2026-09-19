using System.Xml.Linq;

namespace EveConsole.Services.WebStore;

/// <summary>
/// The app's themes as colour tables, for a store's web site.
///
/// <para>⚠️ A copy of <c>Themes/Palette.axaml</c>, on purpose. The site is themed by the store's
/// own setting, resolved by key, and never by what the desktop happens to be showing — so the
/// live resource dictionary is the wrong source even when a window is open, and the client
/// holding the worker lease may be headless with no dictionary loaded at all. The copy is
/// checked against the XAML by <see cref="Differences"/>, which the CI tool runs, so the two
/// cannot drift without a build saying so.</para>
///
/// <para>Tints restate only the neutrals and the accent and inherit everything that carries
/// meaning from Dark or Light, exactly as the XAML does; <see cref="Resolve"/> applies the
/// inheritance so the site gets a complete set.</para>
/// </summary>
public static class WebThemes
{
    /// <summary>One theme the store can wear, in the order a picker should list them.</summary>
    public sealed record Choice(string Key, string Name, string Parent, string Pair);

    public static IReadOnlyList<Choice> All { get; } =
    [
        new("dark",        "Dark",          "dark",  "light"),
        new("light",       "Light",         "light", "dark"),
        new("blue-dark",   "Blue (dark)",   "dark",  "blue-light"),
        new("blue-light",  "Blue (light)",  "light", "blue-dark"),
        new("pink-dark",   "Pink (dark)",   "dark",  "pink-light"),
        new("pink-light",  "Pink (light)",  "light", "pink-dark"),
        new("beige-dark",  "Beige (dark)",  "dark",  "beige-light"),
        new("beige-light", "Beige (light)", "light", "beige-dark"),
    ];

    public const string DefaultKey = "dark";

    public static Choice ChoiceOf(string key) =>
        All.FirstOrDefault(c => c.Key == key) ?? All[0];

    /// <summary>The tokens a site needs, in the XAML's names. Everything else in the palette is
    /// Fluent plumbing the web has no use for.</summary>
    public static readonly string[] Tokens =
    [
        "SurfaceBase", "SurfacePanel", "SurfacePanelAlt", "SurfaceHeader", "SurfaceRaised",
        "SurfaceInput", "SurfaceHover", "SurfaceSelected",
        "BorderSubtle", "BorderDefault", "BorderStrong",
        "TextFaint", "TextDim", "TextMuted", "TextSecondary", "TextPrimary", "TextBright",
        "Accent", "AccentHover", "AccentPressed", "AccentSurface", "AccentDeep", "AccentPale",
        "Good", "Bad", "Warn", "Info", "GoodSurface", "BadSurface", "WarnSurface", "InfoSurface",
        "SurfaceOverlay", "SurfaceOverlayStrong",
    ];

    /// <summary>The token's name as a CSS custom property: SurfacePanelAlt → surface-panel-alt.</summary>
    public static string CssName(string token)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in token)
        {
            if (char.IsUpper(ch) && sb.Length > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>A theme's complete colour set, inheritance applied, keyed by CSS name.</summary>
    public static Dictionary<string, string> Resolve(string key)
    {
        var choice = ChoiceOf(key);
        var result = new Dictionary<string, string>();
        var parent = Raw[choice.Parent];
        Raw.TryGetValue(choice.Key, out var own);
        foreach (var token in Tokens)
        {
            var hex = own is not null && own.TryGetValue(token, out var o) ? o : parent[token];
            result[CssName(token)] = hex;
        }
        return result;
    }

    // ── The palette, as the XAML has it ───────────────────────────────────────
    //
    // ⚠️ Values, not meaning: this file says nothing about what a colour is for. The comments
    // beside the XAML do, and the checker below keeps these numbers equal to those.

    private static readonly Dictionary<string, Dictionary<string, string>> Raw = new()
    {
        ["dark"] = new()
        {
            ["SurfaceBase"] = "#14141c", ["SurfacePanel"] = "#1b1b25", ["SurfacePanelAlt"] = "#1f1f2b",
            ["SurfaceHeader"] = "#25252f", ["SurfaceRaised"] = "#2c2c3a", ["SurfaceInput"] = "#191922",
            ["SurfaceHover"] = "#2a2a38", ["SurfaceSelected"] = "#26304a",
            ["BorderSubtle"] = "#2a2a36", ["BorderDefault"] = "#36364a", ["BorderStrong"] = "#454560",
            ["TextFaint"] = "#6b6b80", ["TextDim"] = "#7c7c92", ["TextMuted"] = "#9a9ab0",
            ["TextSecondary"] = "#b6b6cc", ["TextPrimary"] = "#d2d2e2", ["TextBright"] = "#eeeef6",
            ["Accent"] = "#c8a84b", ["AccentHover"] = "#dcc06a", ["AccentPressed"] = "#a88a34",
            ["AccentSurface"] = "#3a3020", ["AccentDeep"] = "#886810", ["AccentPale"] = "#e8d898",
            ["Good"] = "#6aba90", ["Bad"] = "#c85555", ["Warn"] = "#d09050", ["Info"] = "#5fa8bc",
            ["GoodSurface"] = "#1c3028", ["BadSurface"] = "#3a1e1e", ["WarnSurface"] = "#33260f", ["InfoSurface"] = "#22323c",
            ["SurfaceOverlay"] = "#cc1b1b25", ["SurfaceOverlayStrong"] = "#f0161620",
        },
        ["light"] = new()
        {
            ["SurfaceBase"] = "#e2e0e8", ["SurfacePanel"] = "#eeecf2", ["SurfacePanelAlt"] = "#e8e6ee",
            ["SurfaceHeader"] = "#d8d5e0", ["SurfaceRaised"] = "#f6f5f8", ["SurfaceInput"] = "#f8f7fa",
            ["SurfaceHover"] = "#dcd9e6", ["SurfaceSelected"] = "#d8dcf0",
            ["BorderSubtle"] = "#dedce6", ["BorderDefault"] = "#c6c3d2", ["BorderStrong"] = "#a8a4b8",
            ["TextFaint"] = "#8a8698", ["TextDim"] = "#757182", ["TextMuted"] = "#5d596a",
            ["TextSecondary"] = "#454152", ["TextPrimary"] = "#2e2a3a", ["TextBright"] = "#17141f",
            ["Accent"] = "#8a6a12", ["AccentHover"] = "#a8842a", ["AccentPressed"] = "#6d520a",
            ["AccentSurface"] = "#f6ecd0", ["AccentDeep"] = "#503c06", ["AccentPale"] = "#c9a94e",
            ["Good"] = "#1f7a52", ["Bad"] = "#b03030", ["Warn"] = "#a86410", ["Info"] = "#1f6f88",
            ["GoodSurface"] = "#dff0e6", ["BadSurface"] = "#fadedd", ["WarnSurface"] = "#faecd4", ["InfoSurface"] = "#dcecf2",
            ["SurfaceOverlay"] = "#e0eeecf2", ["SurfaceOverlayStrong"] = "#f2f6f5f8",
        },
        ["blue-dark"] = new()
        {
            ["SurfaceBase"] = "#121a26", ["SurfacePanel"] = "#192434", ["SurfacePanelAlt"] = "#1e2b3e",
            ["SurfaceHeader"] = "#253449", ["SurfaceRaised"] = "#2e405a", ["SurfaceInput"] = "#161f2c",
            ["SurfaceHover"] = "#2b3c52", ["SurfaceSelected"] = "#24405f",
            ["BorderSubtle"] = "#2a3a4e", ["BorderDefault"] = "#364c66", ["BorderStrong"] = "#47617f",
            ["TextFaint"] = "#6b7f96", ["TextDim"] = "#7d92aa", ["TextMuted"] = "#9aaec5",
            ["TextSecondary"] = "#b6c8dc", ["TextPrimary"] = "#d2e0ee", ["TextBright"] = "#eef4fa",
            ["Accent"] = "#5fb0d8", ["AccentHover"] = "#7cc4e6", ["AccentPressed"] = "#4090b8",
            ["AccentSurface"] = "#1e3040", ["AccentDeep"] = "#2a5f7a", ["AccentPale"] = "#a8d8ee",
            ["SurfaceOverlay"] = "#cc192434",
        },
        ["blue-light"] = new()
        {
            ["SurfaceBase"] = "#dfe6ee", ["SurfacePanel"] = "#f0f4f9", ["SurfacePanelAlt"] = "#e8eef5",
            ["SurfaceHeader"] = "#d2dce8", ["SurfaceRaised"] = "#f8fafd", ["SurfaceInput"] = "#ffffff",
            ["SurfaceHover"] = "#d6e2ef", ["SurfaceSelected"] = "#c6d8ee",
            ["BorderSubtle"] = "#d2dce6", ["BorderDefault"] = "#b8c8d8", ["BorderStrong"] = "#97abc0",
            ["TextFaint"] = "#7d8fa2", ["TextDim"] = "#68798c", ["TextMuted"] = "#4f6072",
            ["TextSecondary"] = "#3a4959", ["TextPrimary"] = "#26333f", ["TextBright"] = "#141d26",
            ["Accent"] = "#1f5f8a", ["AccentHover"] = "#2b7aad", ["AccentPressed"] = "#164a6c",
            ["AccentSurface"] = "#dceaf4", ["AccentDeep"] = "#123a54", ["AccentPale"] = "#9cc6e0",
            ["SurfaceOverlay"] = "#ccf0f4f9",
        },
        ["pink-dark"] = new()
        {
            ["SurfaceBase"] = "#1d1520", ["SurfacePanel"] = "#271c2c", ["SurfacePanelAlt"] = "#2e2134",
            ["SurfaceHeader"] = "#3a2942", ["SurfaceRaised"] = "#46334f", ["SurfaceInput"] = "#221826",
            ["SurfaceHover"] = "#3d2c46", ["SurfaceSelected"] = "#472a4a",
            ["BorderSubtle"] = "#3a2c42", ["BorderDefault"] = "#4b3a55", ["BorderStrong"] = "#63506e",
            ["TextFaint"] = "#8a7a92", ["TextDim"] = "#9c8ba4", ["TextMuted"] = "#b6a3be",
            ["TextSecondary"] = "#cdbcd4", ["TextPrimary"] = "#e4d6e9", ["TextBright"] = "#f6eef8",
            ["Accent"] = "#e08aa8", ["AccentHover"] = "#eca0bc", ["AccentPressed"] = "#c06a88",
            ["AccentSurface"] = "#3a2230", ["AccentDeep"] = "#6a3a4e", ["AccentPale"] = "#f4c0d2",
            ["SurfaceOverlay"] = "#cc271c2c",
        },
        ["pink-light"] = new()
        {
            ["SurfaceBase"] = "#eee4e9", ["SurfacePanel"] = "#faf5f8", ["SurfacePanelAlt"] = "#f4ecf1",
            ["SurfaceHeader"] = "#e2d4dc", ["SurfaceRaised"] = "#fdfafc", ["SurfaceInput"] = "#ffffff",
            ["SurfaceHover"] = "#ecdde5", ["SurfaceSelected"] = "#edd6e2",
            ["BorderSubtle"] = "#e6d8e0", ["BorderDefault"] = "#d0bcc8", ["BorderStrong"] = "#b49caa",
            ["TextFaint"] = "#9a8a92", ["TextDim"] = "#85737d", ["TextMuted"] = "#6b5a64",
            ["TextSecondary"] = "#52434c", ["TextPrimary"] = "#3a2d35", ["TextBright"] = "#241a20",
            ["Accent"] = "#a03a5e", ["AccentHover"] = "#bc5077", ["AccentPressed"] = "#7e2846",
            ["AccentSurface"] = "#f8e6ee", ["AccentDeep"] = "#641c34", ["AccentPale"] = "#d894ac",
            ["SurfaceOverlay"] = "#ccfaf5f8",
        },
        ["beige-dark"] = new()
        {
            ["SurfaceBase"] = "#1e1a16", ["SurfacePanel"] = "#29241e", ["SurfacePanelAlt"] = "#302a23",
            ["SurfaceHeader"] = "#3b342b", ["SurfaceRaised"] = "#473e33", ["SurfaceInput"] = "#231f1a",
            ["SurfaceHover"] = "#3b342b", ["SurfaceSelected"] = "#4a3f2c",
            ["BorderSubtle"] = "#3a332a", ["BorderDefault"] = "#4b4235", ["BorderStrong"] = "#635745",
            ["TextFaint"] = "#8a8175", ["TextDim"] = "#9c9285", ["TextMuted"] = "#b6ab9c",
            ["TextSecondary"] = "#cdc3b4", ["TextPrimary"] = "#e4dbcd", ["TextBright"] = "#f7f2e9",
            ["Accent"] = "#d0a870", ["AccentHover"] = "#e0be8e", ["AccentPressed"] = "#a88450",
            ["AccentSurface"] = "#332a1c", ["AccentDeep"] = "#5c4a2e", ["AccentPale"] = "#ecd8b4",
            ["SurfaceOverlay"] = "#cc29241e",
        },
        ["beige-light"] = new()
        {
            ["SurfaceBase"] = "#eae3d7", ["SurfacePanel"] = "#f7f3ec", ["SurfacePanelAlt"] = "#f1ece2",
            ["SurfaceHeader"] = "#ded5c6", ["SurfaceRaised"] = "#fdfbf7", ["SurfaceInput"] = "#ffffff",
            ["SurfaceHover"] = "#e6ded0", ["SurfaceSelected"] = "#e2dcc8",
            ["BorderSubtle"] = "#e0d8c9", ["BorderDefault"] = "#c8bda9", ["BorderStrong"] = "#ab9e88",
            ["TextFaint"] = "#948a79", ["TextDim"] = "#7d7466", ["TextMuted"] = "#635b50",
            ["TextSecondary"] = "#4c453c", ["TextPrimary"] = "#35302a", ["TextBright"] = "#201c18",
            ["Accent"] = "#8a6a3a", ["AccentHover"] = "#a8834c", ["AccentPressed"] = "#6a5028",
            ["AccentSurface"] = "#f4e9d6", ["AccentDeep"] = "#4c3818", ["AccentPale"] = "#cfae7c",
            ["SurfaceOverlay"] = "#ccf7f3ec",
        },
    };

    // ── Keeping the copy honest ───────────────────────────────────────────────

    /// <summary>
    /// Every way this table disagrees with the palette XAML, as lines; empty when they agree.
    ///
    /// <para>Reads the XAML as XML — no Avalonia needed — so a CI tool can run it against the
    /// repository file. A token the XAML defines and this table lacks, or defines differently,
    /// is a difference; the Fluent accent plumbing the site does not use is ignored.</para>
    /// </summary>
    public static List<string> Differences(string paletteXaml)
    {
        var diffs = new List<string>();
        var doc   = XDocument.Parse(paletteXaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var keyOfDictionary = new Dictionary<string, string>
        {
            ["Dark"] = "dark", ["Light"] = "light",
            ["{x:Static svc:AppThemes.BlueDark}"]   = "blue-dark",
            ["{x:Static svc:AppThemes.BlueLight}"]  = "blue-light",
            ["{x:Static svc:AppThemes.PinkDark}"]   = "pink-dark",
            ["{x:Static svc:AppThemes.PinkLight}"]  = "pink-light",
            ["{x:Static svc:AppThemes.BeigeDark}"]  = "beige-dark",
            ["{x:Static svc:AppThemes.BeigeLight}"] = "beige-light",
        };

        var seen = new HashSet<string>();
        foreach (var dict in doc.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary"))
        {
            var key = dict.Attribute(x + "Key")?.Value;
            if (key is null || !keyOfDictionary.TryGetValue(key, out var themeKey)) continue;
            seen.Add(themeKey);

            var colours = dict.Elements().Where(e => e.Name.LocalName == "Color")
                .ToDictionary(e => e.Attribute(x + "Key")!.Value, e => e.Value.Trim().ToLowerInvariant());

            Raw.TryGetValue(themeKey, out var mine);
            foreach (var token in Tokens)
            {
                colours.TryGetValue(token, out var xaml);
                var ours = mine is not null && mine.TryGetValue(token, out var o) ? o : null;
                if (xaml is null && ours is null) continue;
                if (xaml is null) diffs.Add($"{themeKey}: {token} is in the table but not the XAML");
                else if (ours is null) diffs.Add($"{themeKey}: {token} is in the XAML ({xaml}) but not the table");
                else if (!string.Equals(xaml, ours, StringComparison.OrdinalIgnoreCase))
                    diffs.Add($"{themeKey}: {token} is {xaml} in the XAML and {ours} in the table");
            }
        }

        foreach (var themeKey in keyOfDictionary.Values)
            if (!seen.Contains(themeKey)) diffs.Add($"{themeKey}: not found in the XAML");

        return diffs;
    }
}
