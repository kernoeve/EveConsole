using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

public class GameLogRowVm(GameLogEvent e)
{
    public string Time      { get; } = LogViewerDates.ToLocalDisplay(e.OccurredAt);
    public string Kind      { get; } = e.Kind;
    public string Character { get; } = e.CharacterName ?? (e.CharacterId?.ToString() ?? "");
    public string Amount    { get; } = e.Amount is { } a ? a.ToString("N0") : "";
    public string Source    { get; } = Combine(e.SourceName, e.SourceShip);
    public string Target    { get; } = Combine(e.TargetName, e.TargetShip);
    public string Detail    { get; } = BuildDetail(e);

    /// <summary>Summary column — for recognised rows the structured fields, for
    /// unmatched rows the raw text, which is the only thing they have.</summary>
    public string Summary { get; } = e.Kind == "unmatched"
        ? e.RawText ?? ""
        : Describe(e);

    private static string Combine(string? name, string? ship) =>
        (name, ship) switch
        {
            (null, _)      => "",
            (_, null)      => name!,
            _ when name == ship => name!,
            _              => $"{name} ({ship})",
        };

    private static string Describe(GameLogEvent e) => e.Kind switch
    {
        "movement.jumped"   => $"{e.FromSystem} → {e.ToSystem}",
        "movement.undocked" => $"{e.LocationName} → {e.ToSystem}",
        "industry.units_mined" => e.SecondaryAmount is { } r
            ? string.Format(DataText.GameLogMinedResidue, e.Amount, e.TargetName, r)
            : $"{e.Amount:N0} × {e.TargetName}",
        "combat.bounty"     => string.Format(DataText.GameLogBounty, e.Amount),
        _ => string.Join("  ", new[] { e.Weapon, e.Quality }.Where(s => !string.IsNullOrWhiteSpace(s))!),
    };

    /// <summary>The label padded to a column in the monospace detail pane. ⚠️ Not {label,-16}:
    /// that counts characters, and a Chinese, Japanese or Korean character takes two columns, so
    /// translated labels would push their values out of line.</summary>
    private static string PadToColumns(string label, int columns)
    {
        var width = 0;
        foreach (var c in label) width += IsWide(c) ? 2 : 1;
        return label + new string(' ', Math.Max(1, columns - width));
    }

    // The East Asian wide ranges these labels can hold: CJK ideographs and punctuation, kana,
    // Hangul, and full-width forms.
    private static bool IsWide(char c) =>
        c is >= 'ᄀ' and <= 'ᅟ'
          or >= '⺀' and <= '꓏'
          or >= '가' and <= '힣'
          or >= '豈' and <= '﫿'
          or >= '︰' and <= '﹏'
          or >= '＀' and <= '｠'
          or >= '￠' and <= '￦';

    private static string BuildDetail(GameLogEvent e)
    {
        var lines = new List<string> { $"{e.OccurredAt}   {e.Kind}" };

        void Add(string label, string? v)
        { if (!string.IsNullOrWhiteSpace(v)) lines.Add(PadToColumns(label, 16) + v); }

        Add(DataText.GameLogFieldCharacter,      e.CharacterName ?? e.CharacterId?.ToString());
        Add(DataText.GameLogFieldAmount,         e.Amount?.ToString("N0"));
        Add(DataText.GameLogFieldSecondary,      e.SecondaryAmount?.ToString("N0"));
        Add(DataText.GameLogFieldSource,         e.SourceName);
        Add(DataText.GameLogFieldSourceShip,     e.SourceShip);
        Add(DataText.GameLogFieldSourceCorp,     e.SourceCorp);
        Add(DataText.GameLogFieldSourceAlliance, e.SourceAlliance);
        Add(DataText.GameLogFieldTarget,         e.TargetName);
        Add(DataText.GameLogFieldTargetShip,     e.TargetShip);
        Add(DataText.GameLogFieldTargetCorp,     e.TargetCorp);
        Add(DataText.GameLogFieldTargetAlliance, e.TargetAlliance);
        Add(DataText.GameLogFieldWeapon,         e.Weapon);
        Add(DataText.GameLogFieldQuality,        e.Quality);
        Add(DataText.GameLogFieldFromSystem,     e.FromSystem);
        Add(DataText.GameLogFieldToSystem,       e.ToSystem);
        Add(DataText.GameLogFieldLocation,       e.LocationName);
        Add(DataText.GameLogFieldSourceFile,     Path.GetFileName(e.SourceFile));
        Add(DataText.GameLogFieldLine,           e.LineNumber.ToString());

        if (!string.IsNullOrWhiteSpace(e.RawText))
            lines.Add("\n" + DataText.GameLogFieldRaw + "\n" + e.RawText);

        return string.Join("\n", lines);
    }
}

/// <summary>
/// Viewer over the GameLogEvents table.
///
/// The type dropdown includes <c>unmatched</c> — lines the parser did not recognise,
/// stored verbatim. That entry is the point of keeping them: it's how a coverage gap
/// (a whole channel EVE logs that no rule handles yet) becomes visible.
/// </summary>
public class GameLogViewerViewModel : ReactiveObject
{
    // The value of a type entry is the kind as stored; "(all types)" has the empty value, which no
    // kind is, and a label of its own.
    private const string AllKinds = "";
    private static readonly Choice<string> AllTypes = new(AllKinds, DataText.GameLogAllTypes);
    private const int RowLimit   = 5000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private bool _isLoading;

    public ObservableCollection<GameLogRowVm>   Rows  { get; } = [];
    public ObservableCollection<Choice<string>> Kinds { get; } = [];

    public GameLogViewerViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;

        // Default window is the last 90 days.
        _dateFrom     = DateTime.Now.AddDays(-90).ToString("yyyy-MM-dd");
        _selectedKind = AllKinds;
        Kinds.Add(AllTypes);

        RefreshCommand = ReactiveCommand.Create(() => { _ = LoadAsync(); });
        _ = LoadAsync();
    }

    private string _dateFrom;
    public string DateFrom { get => _dateFrom; set { this.RaiseAndSetIfChanged(ref _dateFrom, value); _ = LoadAsync(); } }

    private string _dateThru = "";
    public string DateThru { get => _dateThru; set { this.RaiseAndSetIfChanged(ref _dateThru, value); _ = LoadAsync(); } }

    private string _selectedKind;
    public Choice<string> SelectedKind
    {
        get => Kinds.FirstOrDefault(o => o.Value == _selectedKind) ?? AllTypes;
        set
        {
            // A detaching ComboBox, or one whose list is being refilled, sets null; that is not a
            // choice. The list is re-selected once it is full again.
            if (value is null) return;
            _selectedKind = value.Value;
            this.RaisePropertyChanged();
            _ = LoadAsync();
        }
    }

    private string _search = "";
    public string Search { get => _search; set { this.RaiseAndSetIfChanged(ref _search, value); _ = LoadAsync(); } }

    private GameLogRowVm? _selected;
    public GameLogRowVm? Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    private async Task LoadAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        StatusText = CommonText.Loading;

        try
        {
            var fromIso = LogViewerDates.ToIso(_dateFrom);
            var thruIso = LogViewerDates.ToIso(_dateThru);

            await using var db = await _dbFactory.CreateDbContextAsync();

            // OccurredAt is an ISO-8601 string precisely so this comparison translates
            // to SQL — a DateTimeOffset column could not be filtered here at all.
            var q = db.GameLogEvents.AsNoTracking().AsQueryable();
            if (fromIso is not null) q = q.Where(e => string.Compare(e.OccurredAt, fromIso) >= 0);
            if (thruIso is not null) q = q.Where(e => string.Compare(e.OccurredAt, thruIso) < 0);

            // Type list reflects what's actually in the chosen window.
            var kinds = await q.Select(e => e.Kind).Distinct().OrderBy(k => k).ToListAsync();

            Kinds.Clear();
            Kinds.Add(AllTypes);
            foreach (var k in kinds) Kinds.Add(new Choice<string>(k, k));

            if (_selectedKind != AllKinds && !kinds.Contains(_selectedKind))
                _selectedKind = AllKinds;
            this.RaisePropertyChanged(nameof(SelectedKind));

            var kind = _selectedKind;
            if (kind != AllKinds)
                q = q.Where(e => e.Kind == kind);

            if (!string.IsNullOrWhiteSpace(_search))
            {
                var s = _search.Trim();
                q = q.Where(e => (e.RawText != null    && EF.Functions.Like(e.RawText,    $"%{s}%"))
                              || (e.SourceName != null && EF.Functions.Like(e.SourceName, $"%{s}%"))
                              || (e.TargetName != null && EF.Functions.Like(e.TargetName, $"%{s}%"))
                              || (e.CharacterName != null && EF.Functions.Like(e.CharacterName, $"%{s}%")));
            }

            var list = await q.OrderByDescending(e => e.OccurredAt).Take(RowLimit).ToListAsync();

            Rows.Clear();
            foreach (var e in list) Rows.Add(new GameLogRowVm(e));

            StatusText = list.Count == 0
                ? DataText.GameLogNoEntries
                : list.Count >= RowLimit
                    ? string.Format(DataText.GameLogEntriesCapped, list.Count)
                    : Plurals.Format(DataText.ResourceManager, nameof(DataText.GameLogEntriesOther), list.Count);
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(GameLogViewerViewModel), "Load", ex);
            StatusText = AppErrorLogger.Line("Error loading game log", ex);
        }
        finally { _isLoading = false; }
    }
}

/// <summary>Date conversion shared by the two log viewers. Stored timestamps are
/// ISO-8601 UTC strings; the filter boxes take local dates.</summary>
public static class LogViewerDates
{
    /// <summary>Local date (or date+time) → the stored ISO-8601 UTC form, so a plain
    /// string comparison filters correctly. Null when the box is empty or unparseable.</summary>
    public static string? ToIso(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
            return null;

        return new DateTimeOffset(d).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    /// <summary>Stored ISO-8601 UTC → local display. Falls back to the raw value if it
    /// somehow isn't parseable, rather than showing a blank cell.</summary>
    public static string ToLocalDisplay(string iso)
        => DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : iso;
}
