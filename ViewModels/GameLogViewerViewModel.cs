using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

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
            ? $"{e.Amount:N0} × {e.TargetName} (residue {r:N0})"
            : $"{e.Amount:N0} × {e.TargetName}",
        "combat.bounty"     => $"{e.Amount:N0} ISK bounty",
        _ => string.Join("  ", new[] { e.Weapon, e.Quality }.Where(s => !string.IsNullOrWhiteSpace(s))!),
    };

    private static string BuildDetail(GameLogEvent e)
    {
        var lines = new List<string> { $"{e.OccurredAt}   {e.Kind}" };

        void Add(string label, string? v)
        { if (!string.IsNullOrWhiteSpace(v)) lines.Add($"{label,-16}{v}"); }

        Add("Character",  e.CharacterName ?? e.CharacterId?.ToString());
        Add("Amount",     e.Amount?.ToString("N0"));
        Add("Secondary",  e.SecondaryAmount?.ToString("N0"));
        Add("Source",     e.SourceName);
        Add("Source ship", e.SourceShip);
        Add("Source corp", e.SourceCorp);
        Add("Source alli", e.SourceAlliance);
        Add("Target",     e.TargetName);
        Add("Target ship", e.TargetShip);
        Add("Target corp", e.TargetCorp);
        Add("Target alli", e.TargetAlliance);
        Add("Weapon",     e.Weapon);
        Add("Quality",    e.Quality);
        Add("From system", e.FromSystem);
        Add("To system",  e.ToSystem);
        Add("Location",   e.LocationName);
        Add("Source file", Path.GetFileName(e.SourceFile));
        Add("Line",       e.LineNumber.ToString());

        if (!string.IsNullOrWhiteSpace(e.RawText))
            lines.Add($"\nRaw:\n{e.RawText}");

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
    public const string AllTypes = "(all types)";
    private const int RowLimit   = 5000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private bool _isLoading;

    public ObservableCollection<GameLogRowVm> Rows  { get; } = [];
    public ObservableCollection<string>       Kinds { get; } = [];

    public GameLogViewerViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;

        // Default window is the last 90 days.
        _dateFrom     = DateTime.Now.AddDays(-90).ToString("yyyy-MM-dd");
        _selectedKind = AllTypes;
        Kinds.Add(AllTypes);

        RefreshCommand = ReactiveCommand.Create(() => { _ = LoadAsync(); });
        _ = LoadAsync();
    }

    private string _dateFrom;
    public string DateFrom { get => _dateFrom; set { this.RaiseAndSetIfChanged(ref _dateFrom, value); _ = LoadAsync(); } }

    private string _dateThru = "";
    public string DateThru { get => _dateThru; set { this.RaiseAndSetIfChanged(ref _dateThru, value); _ = LoadAsync(); } }

    private string _selectedKind;
    public string SelectedKind { get => _selectedKind; set { this.RaiseAndSetIfChanged(ref _selectedKind, value); _ = LoadAsync(); } }

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
        StatusText = "Loading…";

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

            var previous = _selectedKind;
            Kinds.Clear();
            Kinds.Add(AllTypes);
            foreach (var k in kinds) Kinds.Add(k);

            if (previous != AllTypes && !kinds.Contains(previous))
            {
                _selectedKind = AllTypes;
                this.RaisePropertyChanged(nameof(SelectedKind));
            }

            if (_selectedKind != AllTypes)
                q = q.Where(e => e.Kind == _selectedKind);

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
                ? "No entries in range."
                : list.Count >= RowLimit
                    ? $"{list.Count:N0} entries (capped — narrow the range)"
                    : $"{list.Count:N0} entr{(list.Count == 1 ? "y" : "ies")}";
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(GameLogViewerViewModel), "Load", ex);
            StatusText = "Error loading game log.";
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
