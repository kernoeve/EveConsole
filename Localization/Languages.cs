using System.Globalization;
using Avalonia.Media;
using EveConsole.Services;
using SkiaSharp;

namespace EveConsole.Localization;

/// <summary>A language the interface can be shown in.</summary>
/// <param name="Code">The culture its files are named for: "zh-Hans" is read from
/// ShellText.zh-Hans.resx and every file like it.</param>
/// <param name="NativeName">What the language calls itself, which is what a speaker looks for in
/// a list.</param>
/// <param name="Preview">Offered, but not finished. Chosen by hand only — never picked for somebody
/// because it is their system's language, which would drop them into a half-translated app they
/// did not ask for.</param>
/// <param name="Fonts">Families to try first for characters the interface font lacks. See
/// <see cref="Languages.FontFallbacks"/>.</param>
public sealed record UiLanguage(
    string Code, string NativeName, string EnglishName, bool Preview, IReadOnlyList<string> Fonts)
{
    /// <summary>The culture the interface text is looked up under.</summary>
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Code);
}

/// <summary>One entry in the Language list: a language, or "System default" (an empty code).</summary>
public sealed record LanguageChoice(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Which language the interface is in, chosen once at startup.
///
/// <para>⚠️ Once per run, and a change waits for the next start. Every label is read from its
/// resource when its window is built — {x:Static loc:ShellText.NavOverview} is a value, not a
/// binding — which is what lets the compiler reject a mistyped name, and what keeps a screen from
/// ever showing half one language and half another. A language picked in Settings is stored at
/// once and offers a restart.</para>
///
/// <para>⚠️ Numbers and dates follow the machine's regional format (CultureInfo.CurrentCulture)
/// while that format is in the interface's language — English on a UK region keeps the UK's own
/// dates — and the interface language's own formats when it is not (decided 2026-09-28): a
/// Chinese interface on an English-region PC put "Sep 28" among the Chinese. See
/// <see cref="ApplyAtStartup"/>.</para>
/// </summary>
public static class Languages
{
    /// <summary><c>--pseudo-loc</c>: every string accented, bracketed and a third longer, to find
    /// text that has not been moved into the resources, and labels that clip when a language runs
    /// long. A developer's switch; see <see cref="PseudoLocalization"/>.</summary>
    public const string PseudoArgument = "--pseudo-loc";

    public static readonly UiLanguage English = new("en", "English", "English", Preview: false, []);

    /// <summary>
    /// Every language the app has text for. EVE's own eight are the candidates — English, Chinese,
    /// French, German, Japanese, Korean, Russian and Spanish — each added here once its files
    /// exist, so the list never offers a language with nothing behind it.
    ///
    /// <para>⚠️ The font list is the fix for Han unification: Chinese, Japanese and Korean share
    /// code points but not glyph shapes, and left to the system's fallback a Chinese label can come
    /// out in a Japanese font. The bundled Inter has no CJK glyphs at all, so for these languages
    /// every character of the interface text goes through this list. Windows first, then macOS,
    /// then the Linux families — Linux needs Noto CJK installed, which most desktops ship.</para>
    /// </summary>
    public static IReadOnlyList<UiLanguage> All { get; } =
    [
        English,
        new("zh-Hans", "简体中文", "Chinese (Simplified)", Preview: true,
        [
            "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Hiragino Sans GB",
            "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC", "WenQuanYi Micro Hei",
        ]),
        // Latin, which the bundled Inter covers: no fallback list needed.
        new("de", "Deutsch", "German", Preview: true, []),
        new("es", "Español", "Spanish", Preview: true, []),
        new("fr", "Français", "French", Preview: true, []),
        new("ja", "日本語", "Japanese", Preview: true,
        [
            "Yu Gothic UI", "Meiryo UI", "Meiryo", "Hiragino Sans", "Hiragino Kaku Gothic ProN",
            "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP", "IPAexGothic",
        ]),
        new("ko", "한국어", "Korean", Preview: true,
        [
            "Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Noto Sans KR",
            "Source Han Sans KR", "NanumGothic",
        ]),
        // Cyrillic, which the bundled Inter covers.
        new("ru", "Русский", "Russian", Preview: true, []),
    ];

    /// <summary>The language in force for this run.</summary>
    public static UiLanguage Active { get; private set; } = English;

    /// <summary>The system's own interface language, as it was before this run chose one.</summary>
    public static CultureInfo SystemCulture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>True when this run shows pseudo-localised text.</summary>
    public static bool Pseudo { get; private set; }

    /// <summary>Whether the interface language counts large numbers in 10,000s — 万 and 亿 in
    /// Chinese, 万/億 in Japanese, 만/억 in Korean — rather than in thousands. "12.4 十亿" is how a
    /// thousands-based amount reads in Chinese; "124 亿" is how anyone would say it.</summary>
    public static bool CountsInMyriads =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "zh" or "ja" or "ko";

    /// <summary>What the user chose, as stored for this machine: a language's code, or empty for
    /// the system's language.</summary>
    public static string Chosen => UiState.Get(UiState.Language) ?? "";

    /// <summary>Remembers a choice for the next start. Empty goes back to the system's language.</summary>
    public static void Choose(string code) =>
        AppConfig.SetUiState(UiState.Language, code.Length > 0 ? code : null);

    /// <summary>A different language is chosen than the one this run is showing.</summary>
    public static bool RestartNeeded =>
        !Pseudo && Resolve(Chosen, SystemCulture).Code != Active.Code;

    /// <summary>
    /// The language a choice comes to on this machine: the one chosen, if it is here; otherwise
    /// the system's language, when a finished translation of it exists; otherwise English.
    /// </summary>
    public static UiLanguage Resolve(string chosen, CultureInfo system)
    {
        if (chosen.Length > 0 && Find(chosen) is { } picked) return picked;

        // Up the culture's parents, so zh-CN is served by zh-Hans and de-AT by de. A language
        // still in preview is never picked on the system's say-so.
        for (var c = system; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            if (Find(c.Name) is { Preview: false } match) return match;

        return English;
    }

    private static UiLanguage? Find(string code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Settles this run's language. Called first thing in Main, once the profile is known — the
    /// choice lives in the profile's config — and before anything reads a string.
    ///
    /// <para>⚠️ Never fails. An unreadable config is English, not a client that will not start.</para>
    /// </summary>
    public static void ApplyAtStartup(IReadOnlyCollection<string> args)
    {
        SystemCulture = CultureInfo.CurrentUICulture;
        Pseudo = args.Any(a => string.Equals(a, PseudoArgument, StringComparison.OrdinalIgnoreCase));

        try   { Active = Pseudo ? English : Resolve(Chosen, SystemCulture); }
        catch { Active = English; }

        // The default for every thread, and this one's too: a thread that has set nothing reads
        // the default, and async code carries the caller's forward.
        var culture = Active.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // Dates and numbers follow Windows' regional format — unless that format is in another
        // language than the interface: a Chinese interface on an English-region PC put "Sep 28"
        // and "27 Sep 2026" among the Chinese. Then they follow the interface language (decided
        // 2026-09-28). An English interface on a UK region keeps the UK's own format.
        var region = CultureInfo.CurrentCulture;
        if (!Pseudo && !string.Equals(region.TwoLetterISOLanguageName, culture.TwoLetterISOLanguageName,
                                      StringComparison.OrdinalIgnoreCase))
        {
            var formats = CultureInfo.CreateSpecificCulture(culture.Name);
            CultureInfo.DefaultThreadCurrentCulture = formats;
            CultureInfo.CurrentCulture = formats;
        }
        Formats = CultureInfo.CurrentCulture;

        if (Pseudo) PseudoLocalization.Install();
    }

    /// <summary>The app's own formats for dates and numbers, as decided at startup: what
    /// <see cref="LanguageScope.App"/> goes back to from inside a store's language.</summary>
    public static CultureInfo Formats { get; private set; } = CultureInfo.CurrentCulture;

    /// <summary>
    /// The families the interface falls back to, in order, for characters its own font lacks —
    /// the active language's list, tried before the system's own choice. Empty for a language
    /// the interface font covers, which leaves the system's fallback exactly as it was.
    /// </summary>
    public static IReadOnlyList<FontFallback> FontFallbacks() =>
        Active.Fonts.Select(f => new FontFallback { FontFamily = new FontFamily(f) }).ToList();

    /// <summary>
    /// The face charts draw their text in, for a language the charts' default cannot show; null
    /// for any other. Charts draw with Skia directly, outside Avalonia's fallback above, so a
    /// Chinese axis title needs a face that has Chinese in it: the first of the language's own
    /// families that does, else whatever the system matches for it.
    /// </summary>
    public static SKTypeface? ChartTypeface()
    {
        if (Active.Fonts.Count == 0) return null;

        // A character only this language's script has, to test a face against. Skia hands back
        // its default face for a family it cannot find, and that face has none of these.
        int sample = Active.Code switch { "ja" => 'あ', "ko" => '한', _ => '中' };
        foreach (var family in Active.Fonts)
            if (SKTypeface.FromFamilyName(family) is { } face && face.GetGlyph(sample) != 0) return face;

        return SKFontManager.Default.MatchCharacter(null, SKFontStyle.Normal, [Active.Code], sample);
    }

    /// <summary>The Language list: "System default", then every language by its own name.</summary>
    public static IReadOnlyList<LanguageChoice> Choices()
    {
        var system = Resolve("", SystemCulture);
        return
        [
            new("", string.Format(SettingsText.LanguageSystemDefault, system.NativeName)),
            .. All.Select(l => new LanguageChoice(
                l.Code, l.Preview ? string.Format(SettingsText.LanguagePreview, l.NativeName) : l.NativeName)),
        ];
    }
}
