using System.Globalization;

namespace EveConsole.Localization;

/// <summary>
/// Text written for a reader other than the person at the screen, in that reader's language: a
/// shop's buyers, when the shop sells in another language than its owner's interface. Inside the
/// scope every resource (<c>StoreText.X</c>), <see cref="Plurals"/> and number format reads the
/// given language; outside it nothing changes.
///
/// <para>⚠️ The current async flow only. .NET keeps CurrentUICulture and CurrentCulture per async
/// flow, so a scope opened in a background loop never reaches the UI thread, and an await inside
/// it keeps it. Open it with <c>using</c> in the method that writes the text: set in a method that
/// returns without disposing it, it would stay on for the caller.</para>
///
/// <para>SDE names are not covered: <see cref="SdeNames.InLanguageAsync"/> gives them.</para>
/// </summary>
public readonly struct LanguageScope : IDisposable
{
    private readonly CultureInfo? _ui;
    private readonly CultureInfo? _formats;

    private LanguageScope(CultureInfo ui, CultureInfo formats) { _ui = ui; _formats = formats; }

    /// <summary>Writes in <paramref name="language"/>, an interface code ("de", "zh-Hans"). Null or
    /// empty, or the interface's own language, changes nothing.</summary>
    public static LanguageScope Use(string? language)
    {
        if (string.IsNullOrEmpty(language) || language == Languages.Active.Code) return default;

        var scope = new LanguageScope(CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentCulture   = CultureInfo.CreateSpecificCulture(language);
        return scope;
    }

    /// <summary>Back in the app's own language, inside a store's: for text the owner reads — a log
    /// line, a status — written while a buyer's text is being written.</summary>
    public static LanguageScope App()
    {
        var scope = new LanguageScope(CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = Languages.Active.Culture;
        CultureInfo.CurrentCulture   = Languages.Formats;
        return scope;
    }

    public void Dispose()
    {
        if (_ui is null || _formats is null) return;
        CultureInfo.CurrentUICulture = _ui;
        CultureInfo.CurrentCulture   = _formats;
    }
}
