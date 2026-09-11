using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Formatting shared by every row on this tool, so a figure means the same thing in the summary,
/// the detail list and the totals strip.
/// </summary>
internal static class UsageFormat
{
    /// <summary>
    /// Money, at a precision that suits the magnitude.
    ///
    /// <para>⚠️ Two decimal places alone would render most of this tool as "$0.00". A single turn
    /// costs fractions of a cent, and a table of zeroes reads as "nothing is being recorded"
    /// rather than "these are small" — so anything below half a cent gets the places it needs.
    /// The threshold is half a cent rather than a fixed magnitude so that figures shown side by
    /// side agree: $17.31 next to $3.0000 looks like two different quantities.</para>
    ///
    /// <para>⚠️ Always "$", never the machine's currency symbol. These are USD list prices from
    /// American vendors; rendering them as £ or € because of a locale would be a wrong number,
    /// not a translated one.</para>
    /// </summary>
    public static string Money(decimal usd)
        => usd == 0m                 ? "$0"
         : Math.Abs(usd) >= 0.005m   ? usd.ToString("$#,##0.00",  CultureInfo.InvariantCulture)
         :                             usd.ToString("$0.######",  CultureInfo.InvariantCulture);

    /// <summary>The stored kind code as something a person would say.</summary>
    public static string Kind(string code) => code switch
    {
        "llm" => "Agent",
        "tts" => "Speech out",
        "stt" => "Speech in",
        _     => code,
    };

    public static string Units(long n) => n == 0 ? "" : n.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// What a service charges, at a scale the capsuleer actually reads prices in.
///
/// <para>⚠️ The table stores USD per single unit; nobody quotes prices that way. Anthropic and
/// OpenAI quote per million tokens, voices quote per million characters, transcription quotes per
/// minute — so the value is multiplied on the way out and divided on the way back in. Editing
/// "3" for Sonnet is possible; editing "0.000003" is a typo waiting to happen.</para>
/// </summary>
public class ServiceRateVm : ReactiveObject
{
    public ServiceRate Row { get; }

    /// <summary>Multiplier between the stored per-unit rate and the displayed one.</summary>
    private readonly decimal _scale;

    public ServiceRateVm(ServiceRate row)
    {
        Row = row;
        (_scale, ScaleLabel) = row.Kind switch
        {
            "llm" => (1_000_000m, "per 1M tokens"),
            "tts" => (1_000_000m, "per 1M characters"),
            "stt" => (60m,        "per minute"),
            _     => (1m,         "per unit"),
        };

        _input      = Show(row.InputPerUnit);
        _output     = Show(row.OutputPerUnit);
        _cacheRead  = Show(row.CacheReadPerUnit);
        _cacheWrite = Show(row.CacheWritePerUnit);
        _notes      = row.Notes;
    }

    public string ScaleLabel { get; }
    public string KindText   => UsageFormat.Kind(Row.Kind);
    public string Provider   => Row.Provider;
    public string ModelText  => Row.Model.Length == 0 ? "(any model)" : Row.Model;

    /// <summary>Only the LLM providers bill a cache separately; the columns are blank elsewhere.</summary>
    public bool HasCache => Row.Kind == "llm";

    public string UpdatedText => Row.UpdatedAt == default
        ? ""
        : Row.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

    /// <summary>Set by any edit; the parent writes only these rows back.</summary>
    public bool IsDirty { get; private set; }

    public void MarkSaved() { IsDirty = false; this.RaisePropertyChanged(nameof(UpdatedText)); }

    private string _input;
    public string Input { get => _input; set { this.RaiseAndSetIfChanged(ref _input, value); Apply(value, v => Row.InputPerUnit = v); } }

    private string _output;
    public string Output { get => _output; set { this.RaiseAndSetIfChanged(ref _output, value); Apply(value, v => Row.OutputPerUnit = v); } }

    private string _cacheRead;
    public string CacheRead { get => _cacheRead; set { this.RaiseAndSetIfChanged(ref _cacheRead, value); Apply(value, v => Row.CacheReadPerUnit = v); } }

    private string _cacheWrite;
    public string CacheWrite { get => _cacheWrite; set { this.RaiseAndSetIfChanged(ref _cacheWrite, value); Apply(value, v => Row.CacheWritePerUnit = v); } }

    private string _notes;
    public string Notes { get => _notes; set { this.RaiseAndSetIfChanged(ref _notes, value); Row.Notes = value ?? ""; IsDirty = true; } }

    private string Show(decimal perUnit)
    {
        var scaled = perUnit * _scale;
        return scaled == 0m ? "0" : scaled.ToString("0.######", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// ⚠️ An unparseable entry is ignored rather than zeroed. Mid-typing a box is briefly "" or
    /// "0.", and writing that through would silently reset a rate the capsuleer was editing.
    /// </summary>
    private void Apply(string text, Action<decimal> set)
    {
        if (!decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var scaled)) return;
        if (scaled < 0m) return;
        set(scaled / _scale);
        IsDirty = true;
    }
}

/// <summary>One line of the summary: a time bucket crossed with one service.</summary>
public class UsageSummaryRowVm
{
    public string PeriodText { get; init; } = "";
    public long   PeriodSort { get; init; }

    public string KindText   { get; init; } = "";
    public string Provider   { get; init; } = "";
    public string ModelText  { get; init; } = "";

    public int  Calls        { get; init; }
    public long InputUnits   { get; init; }
    public long OutputUnits  { get; init; }
    public long CacheReadUnits  { get; init; }
    public long CacheWriteUnits { get; init; }
    public string UnitKind   { get; init; } = "";

    public string InputText      => UsageFormat.Units(InputUnits);
    public string OutputText     => UsageFormat.Units(OutputUnits);
    public string CacheReadText  => UsageFormat.Units(CacheReadUnits);
    public string CacheWriteText => UsageFormat.Units(CacheWriteUnits);

    public int Errors      { get; init; }
    public string ErrorText => Errors == 0 ? "" : Errors.ToString("N0", CultureInfo.CurrentCulture);

    public long TotalMs    { get; init; }
    public string AvgText  => Calls == 0 ? "" : $"{TotalMs / (double)Calls / 1000.0:0.0}s";

    public decimal Cost    { get; init; }

    /// <summary>False when nothing in the rate table matched, which is not the same as free.</summary>
    public bool IsPriced   { get; init; }
    public bool IsLocal    { get; init; }

    /// <summary>
    /// ⚠️ Three states, not two. A local service is genuinely free; a paid service with no rate row
    /// is unknown. Showing "$0" for both would understate a bill and hide the missing rate.
    /// </summary>
    public string CostText => IsLocal   ? "local"
                            : !IsPriced ? "no rate"
                            :             UsageFormat.Money(Cost);
}

/// <summary>One recorded service call.</summary>
public class UsageDetailRowVm
{
    public string TimeText { get; init; } = "";
    public long   TimeSort { get; init; }

    public string KindText  { get; init; } = "";
    public string Provider  { get; init; } = "";
    public string ModelText { get; init; } = "";

    public long InputUnits      { get; init; }
    public long OutputUnits     { get; init; }
    public long CacheReadUnits  { get; init; }
    public long CacheWriteUnits { get; init; }
    public string UnitKind      { get; init; } = "";

    public string InputText      => UsageFormat.Units(InputUnits);
    public string OutputText     => UsageFormat.Units(OutputUnits);
    public string CacheReadText  => UsageFormat.Units(CacheReadUnits);
    public string CacheWriteText => UsageFormat.Units(CacheWriteUnits);

    public string DurationText { get; init; } = "";

    /// <summary>Marked when the counts were inferred rather than reported by the provider.</summary>
    public string EstimatedText { get; init; } = "";

    public decimal Cost  { get; init; }
    public bool IsPriced { get; init; }
    public bool IsLocal  { get; init; }
    public string CostText => IsLocal ? "local" : !IsPriced ? "no rate" : UsageFormat.Money(Cost);

    /// <summary>What the turn was doing, for the rows that belong to one. Blank for speech.</summary>
    public string Activity { get; init; } = "";
    public string Error    { get; init; } = "";
}

/// <summary>
/// What the agent, the voice and the transcriber have consumed, and what that would cost.
///
/// <para>Aggregated in memory from the rows in range rather than in SQL. Date bucketing is where
/// the two engines diverge hardest — <c>strftime</c> against <c>date_trunc</c>, over a column that
/// is TEXT on one and TIMESTAMPTZ on the other — and the volume here is a few thousand rows a
/// month, which is not worth two dialects of the same query.</para>
///
/// <para>⚠️ Bucketed by LOCAL date. Rows are stored in UTC, and a capsuleer asking what today cost
/// means their today.</para>
/// </summary>
public class AgentUsageViewModel : ReactiveObject
{
    /// <summary>
    /// ⚠️ A ceiling on the read, not a page size. A year-wide range on a busy install could
    /// otherwise pull every row ever written into memory to total it up; when this bites, the
    /// status line says so rather than quietly reporting a partial figure as the total.
    /// </summary>
    private const int MaxRows = 100_000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errors;

    /// <summary>Everything read for the current range, kept so re-bucketing needs no query.</summary>
    private List<ServiceUsage> _rows = [];
    private Dictionary<long, AgentInteraction> _interactions = [];
    private RateBook _rates = new([]);

    /// <summary>Set when the rate table could not be read at all, so the totals can say why.</summary>
    private string _ratesMissing = "";

    public ObservableCollection<UsageSummaryRowVm> Summary { get; } = [];
    public ObservableCollection<UsageDetailRowVm>  Detail  { get; } = [];
    public ObservableCollection<ServiceRateVm>     Rates   { get; } = [];

    public GridPager Pager { get; }

    public AgentUsageViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errors)
    {
        _dbFactory = dbFactory;
        _errors    = errors;
        _dateFrom  = DateTime.Now.Date.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        Pager = new GridPager(() => { FillDetailPage(); return Task.CompletedTask; });

        RefreshCommand    = ReactiveCommand.Create(() => { _ = LoadAsync(); });
        SaveRatesCommand  = ReactiveCommand.CreateFromTask(SaveRatesAsync);

        // Not loaded here — see ErrorLogViewModel. Constructing at startup would show a total read
        // when the application launched, which looks current and is not. MainWindowViewModel calls
        // Reload when the tab is opened.
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand   { get; }
    public ReactiveCommand<Unit, Unit> SaveRatesCommand { get; }

    // ── Filters ──────────────────────────────────────────────────────────────

    public string[] PeriodOptions { get; } = ["Day", "Week", "Month"];

    private string _period = "Day";
    public string Period
    {
        get => _period;
        set { this.RaiseAndSetIfChanged(ref _period, value); Rebuild(); }
    }

    public string[] KindOptions { get; } = ["All services", "Agent", "Speech out", "Speech in"];

    private string _kindFilter = "All services";
    public string KindFilter
    {
        get => _kindFilter;
        set { this.RaiseAndSetIfChanged(ref _kindFilter, value); Pager.Reset(); Rebuild(); }
    }

    private string _dateFrom;
    public string DateFrom { get => _dateFrom; set { this.RaiseAndSetIfChanged(ref _dateFrom, value); _ = LoadAsync(); } }

    private string _dateThru = "";
    public string DateThru { get => _dateThru; set { this.RaiseAndSetIfChanged(ref _dateThru, value); _ = LoadAsync(); } }

    // ── Totals strip ─────────────────────────────────────────────────────────

    private string _totalCostText = "";
    public string TotalCostText { get => _totalCostText; private set => this.RaiseAndSetIfChanged(ref _totalCostText, value); }

    private string _agentCostText = "";
    public string AgentCostText { get => _agentCostText; private set => this.RaiseAndSetIfChanged(ref _agentCostText, value); }

    private string _ttsCostText = "";
    public string TtsCostText { get => _ttsCostText; private set => this.RaiseAndSetIfChanged(ref _ttsCostText, value); }

    private string _sttCostText = "";
    public string SttCostText { get => _sttCostText; private set => this.RaiseAndSetIfChanged(ref _sttCostText, value); }

    private string _callsText = "";
    public string CallsText { get => _callsText; private set => this.RaiseAndSetIfChanged(ref _callsText, value); }

    /// <summary>
    /// Anything about the figures above that a reader would otherwise assume away — a service with
    /// no rate, counts the provider did not report, a range that hit the row ceiling.
    /// </summary>
    private string _caveatText = "";
    public string CaveatText { get => _caveatText; private set => this.RaiseAndSetIfChanged(ref _caveatText, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private string _ratesStatus = "";
    public string RatesStatus { get => _ratesStatus; private set => this.RaiseAndSetIfChanged(ref _ratesStatus, value); }

    // ── Load ─────────────────────────────────────────────────────────────────

    public void Reload() => _ = LoadAsync();

    private bool _isLoading;

    private async Task LoadAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        StatusText = "Loading…";
        try
        {
            var parts = new List<string>();
            var ps    = new List<object>();
            if (TryDate(_dateFrom, out var from))
            { int i = ps.Count; ps.Add(from); parts.Add($"\"OccurredAt\" >= {{{i}}}"); }
            if (TryDate(_dateThru, out var thru))
            { int i = ps.Count; ps.Add(thru.AddDays(1)); parts.Add($"\"OccurredAt\" < {{{i}}}"); }
            var where = parts.Count > 0 ? "WHERE " + string.Join(" AND ", parts) : "";

            await using var db = await _dbFactory.CreateDbContextAsync();
#pragma warning disable EF1002
            var usage = await db.ServiceUsage.FromSqlRaw(
                    $"SELECT * FROM \"ServiceUsage\" {where} ORDER BY \"OccurredAt\" DESC LIMIT {MaxRows}", ps.ToArray())
                .AsNoTracking().ToListAsync();
#pragma warning restore EF1002

            // The turns those rows served, for the detail list's activity column.
            //
            // ⚠️ Fetched by the same date range rather than by the ids just read. An id list is
            // the obvious join and does not scale — a wide range yields tens of thousands of
            // distinct ids, and both engines turn that into a parameter list that is slow at best
            // and rejected at worst. The range is one indexed scan and covers the same rows.
            var turnWhere = where.Replace("\"OccurredAt\"", "\"StartedAt\"");
#pragma warning disable EF1002
            var turns = await db.AgentInteractions.FromSqlRaw(
                    $"SELECT * FROM \"AgentInteractions\" {turnWhere} ORDER BY \"StartedAt\" DESC LIMIT {MaxRows}", ps.ToArray())
                .AsNoTracking().ToListAsync();
#pragma warning restore EF1002

            // ⚠️ Read on its own, because it is the one query here that can be missing entirely.
            // On PostgreSQL the schema is applied by whichever client holds the worker lease, so a
            // client that does not hold it can be running a build whose rate table has not been
            // created yet. Usage is still worth showing then — priced as unknown, and saying so —
            // rather than failing the whole tool over the price list.
            List<ServiceRate> rates;
            var ratesMissing = "";
            try
            {
                rates = await db.ServiceRates.AsNoTracking()
                                .OrderBy(r => r.Kind).ThenBy(r => r.Provider).ThenBy(r => r.Model)
                                .ToListAsync();
            }
            catch (Exception ex)
            {
                rates        = [];
                ratesMissing = AppErrorLogger.Line("Rate list unavailable", ex);
                _errors.Log("AgentUsageViewModel", "LoadRates", ex);
            }

            _rows         = usage;
            _interactions = turns.ToDictionary(t => t.Id);
            _rates        = new RateBook(rates);
            _ratesMissing = ratesMissing;

            Rates.Clear();
            foreach (var r in rates) Rates.Add(new ServiceRateVm(r));
            RatesStatus = ratesMissing;

            StatusText = usage.Count == 0
                ? "Nothing recorded in this range."
                : $"{usage.Count:N0} service call(s)";

            Pager.Reset();
            Rebuild();
        }
        catch (Exception ex)
        {
            _errors.Log("AgentUsageViewModel", "Load", ex);
            StatusText = AppErrorLogger.Line("Error loading usage", ex);
        }
        finally { _isLoading = false; }
    }

    // ── Aggregate ────────────────────────────────────────────────────────────

    /// <summary>Re-buckets what is already loaded. Called on every filter change but the dates.</summary>
    private void Rebuild()
    {
        var kind = _kindFilter switch
        {
            "Agent"      => "llm",
            "Speech out" => "tts",
            "Speech in"  => "stt",
            _            => "",
        };
        var rows = kind.Length == 0 ? _rows : _rows.Where(r => r.Kind == kind).ToList();

        BuildSummary(rows);
        BuildTotals(rows);

        Pager.TotalCount = rows.Count;
        Pager.ClampToRange();
        FillDetailPage();
    }

    private void BuildSummary(List<ServiceUsage> rows)
    {
        var groups = rows
            .GroupBy(r => (Bucket(r.OccurredAt), r.Kind, r.Provider, r.Model))
            .Select(g =>
            {
                var rate  = _rates.Find(g.Key.Kind, g.Key.Provider, g.Key.Model);
                var local = g.All(r => r.IsLocal);
                long inp  = g.Sum(r => r.InputUnits);
                long outp = g.Sum(r => r.OutputUnits);
                long cr   = g.Sum(r => r.CacheReadUnits);
                long cw   = g.Sum(r => r.CacheWriteUnits);

                return new UsageSummaryRowVm
                {
                    PeriodText      = g.Key.Item1.Label,
                    PeriodSort      = g.Key.Item1.Sort,
                    KindText        = UsageFormat.Kind(g.Key.Kind),
                    Provider        = g.Key.Provider,
                    ModelText       = g.Key.Model.Length == 0 ? "—" : g.Key.Model,
                    Calls           = g.Count(),
                    InputUnits      = inp,
                    OutputUnits     = outp,
                    CacheReadUnits  = cr,
                    CacheWriteUnits = cw,
                    UnitKind        = g.Key.Kind switch { "llm" => "tokens", "tts" => "characters", "stt" => "seconds", _ => "" },
                    Errors          = g.Count(r => r.Error.Length > 0),
                    TotalMs         = g.Sum(r => (long)r.DurationMs),
                    Cost            = RateBook.Cost(rate, inp, outp, cr, cw),
                    IsPriced        = rate is not null,
                    IsLocal         = local,
                };
            })
            .OrderByDescending(r => r.PeriodSort)
            .ThenByDescending(r => r.Cost)
            .ThenBy(r => r.KindText, StringComparer.Ordinal)
            .ThenBy(r => r.Provider, StringComparer.Ordinal)
            .ToList();

        Summary.Clear();
        foreach (var g in groups) Summary.Add(g);
    }

    private void BuildTotals(List<ServiceUsage> rows)
    {
        decimal total = 0m, llm = 0m, tts = 0m, stt = 0m;
        int unpriced = 0, estimated = 0;

        foreach (var r in rows)
        {
            var rate = _rates.Find(r.Kind, r.Provider, r.Model);
            if (rate is null && !r.IsLocal) unpriced++;
            if (r.UnitsAreEstimated) estimated++;

            var cost = RateBook.Cost(rate, r.InputUnits, r.OutputUnits, r.CacheReadUnits, r.CacheWriteUnits);
            total += cost;
            switch (r.Kind)
            {
                case "llm": llm += cost; break;
                case "tts": tts += cost; break;
                case "stt": stt += cost; break;
            }
        }

        TotalCostText = UsageFormat.Money(total);
        AgentCostText = UsageFormat.Money(llm);
        TtsCostText   = UsageFormat.Money(tts);
        SttCostText   = UsageFormat.Money(stt);
        CallsText     = $"{rows.Count:N0} call(s)";

        var caveats = new List<string>();
        if (_ratesMissing.Length > 0)
            caveats.Add($"no prices could be read, so every cost above is zero — {_ratesMissing}");
        if (_rows.Count >= MaxRows)
            caveats.Add($"only the most recent {MaxRows:N0} rows were read — narrow the range for a true total");
        // Only when the table was readable — if it was not, the line above already said so, and
        // repeating "nothing is priced" underneath it is noise.
        if (unpriced > 0 && _ratesMissing.Length == 0)
            caveats.Add($"{unpriced:N0} call(s) have no rate set and are NOT in the total");
        if (estimated > 0)
            caveats.Add($"{estimated:N0} call(s) have estimated rather than reported counts");
        CaveatText = caveats.Count == 0 ? "" : "⚠️ " + string.Join(" · ", caveats);
    }

    private void FillDetailPage()
    {
        var kind = _kindFilter switch
        {
            "Agent"      => "llm",
            "Speech out" => "tts",
            "Speech in"  => "stt",
            _            => "",
        };

        var page = (kind.Length == 0 ? _rows : _rows.Where(r => r.Kind == kind))
            .Skip(Pager.Offset)
            .Take(GridPager.PageSize)
            .Select(r =>
            {
                var rate = _rates.Find(r.Kind, r.Provider, r.Model);
                var turn = r.InteractionId.HasValue && _interactions.TryGetValue(r.InteractionId.Value, out var t) ? t : null;

                return new UsageDetailRowVm
                {
                    TimeText        = r.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                    TimeSort        = r.OccurredAt.UtcTicks,
                    KindText        = UsageFormat.Kind(r.Kind),
                    Provider        = r.Provider,
                    ModelText       = r.Model.Length == 0 ? "—" : r.Model,
                    InputUnits      = r.InputUnits,
                    OutputUnits     = r.OutputUnits,
                    CacheReadUnits  = r.CacheReadUnits,
                    CacheWriteUnits = r.CacheWriteUnits,
                    UnitKind        = r.UnitKind,
                    DurationText    = r.DurationMs <= 0 ? "" : $"{r.DurationMs / 1000.0:0.0}s",
                    EstimatedText   = r.UnitsAreEstimated ? "est." : "",
                    Cost            = RateBook.Cost(rate, r.InputUnits, r.OutputUnits, r.CacheReadUnits, r.CacheWriteUnits),
                    IsPriced        = rate is not null,
                    IsLocal         = r.IsLocal,
                    Activity        = Describe(turn),
                    Error           = r.Error,
                };
            })
            .ToList();

        Detail.Clear();
        foreach (var d in page) Detail.Add(d);
    }

    private static string Describe(AgentInteraction? t)
    {
        if (t is null) return "";

        // ⚠️ Describes the TURN, and every usage row belonging to that turn repeats it. A turn is
        // what a capsuleer asked for; the rows under it are provider round trips they never see.
        var bits = new List<string>();
        if (t.RoundTrips    > 1) bits.Add($"{t.RoundTrips} rounds");
        // Which tools, not just how many — the same words the chat shows under the reply. "No
        // tool calls" is said out loud: it is the one that matters when the answer looked real.
        bits.Add(Agent.ToolUseSummary.Describe(t.ToolsUsed));
        if (t.Error.Length  > 0) bits.Add("failed");
        return string.Join(" · ", bits);
    }

    // ── Rates ────────────────────────────────────────────────────────────────

    private async Task SaveRatesAsync()
    {
        var dirty = Rates.Where(r => r.IsDirty).ToList();
        if (dirty.Count == 0) { RatesStatus = "Nothing changed."; return; }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var ids  = dirty.Select(d => d.Row.Id).ToList();
            var live = await db.ServiceRates.Where(r => ids.Contains(r.Id)).ToListAsync();
            var byId = live.ToDictionary(r => r.Id);

            foreach (var d in dirty)
            {
                if (!byId.TryGetValue(d.Row.Id, out var row)) continue;
                row.InputPerUnit      = d.Row.InputPerUnit;
                row.OutputPerUnit     = d.Row.OutputPerUnit;
                row.CacheReadPerUnit  = d.Row.CacheReadPerUnit;
                row.CacheWritePerUnit = d.Row.CacheWritePerUnit;
                row.Notes             = d.Row.Notes;
                row.UpdatedAt         = DateTimeOffset.UtcNow;
                d.Row.UpdatedAt       = row.UpdatedAt;
            }

            await db.SaveChangesAsync();
            foreach (var d in dirty) d.MarkSaved();

            // Costs are derived, so a saved rate has to be re-applied to what is on screen —
            // otherwise the grid still shows figures at the old price and looks stale for no
            // visible reason.
            _rates = new RateBook(Rates.Select(r => r.Row));
            Rebuild();

            RatesStatus = $"Saved {dirty.Count} rate(s).";
        }
        catch (Exception ex)
        {
            _errors.Log("AgentUsageViewModel", "SaveRates", ex);
            RatesStatus = AppErrorLogger.Line("Error saving rates", ex);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (string Label, long Sort) Bucket(DateTimeOffset utc)
    {
        var d = utc.ToLocalTime().Date;
        switch (_period)
        {
            case "Month":
                var m = new DateTime(d.Year, d.Month, 1);
                return (m.ToString("MMMM yyyy", CultureInfo.CurrentCulture), m.Ticks);
            case "Week":
                // ISO-style: the week starts on Monday, whatever the machine's culture says.
                var w = d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
                return ($"Week of {w:yyyy-MM-dd}", w.Ticks);
            default:
                return (d.ToString("yyyy-MM-dd ddd", CultureInfo.CurrentCulture), d.Ticks);
        }
    }

    /// <summary>
    /// A plain date or date+time, read as local time and handed on as UTC.
    ///
    /// <para>⚠️ The conversion is not cosmetic. On SQLite the column is text, so the comparison is
    /// lexicographic — a parameter carrying a local offset would be compared character by character
    /// against rows written with "+00:00" and select the wrong window by exactly that offset.</para>
    /// </summary>
    private static bool TryDate(string s, out DateTimeOffset dt)
    {
        if (!string.IsNullOrWhiteSpace(s) &&
            DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var d))
        { dt = new DateTimeOffset(d).ToUniversalTime(); return true; }
        dt = default; return false;
    }

    /// <summary>
    /// The rate table, indexed for lookup.
    ///
    /// <para>An exact (kind, provider, model) row wins; failing that the provider's catch-all row
    /// with an empty model. A model nobody has priced falls back to the provider's general rate
    /// rather than to nothing — a new model is usually close in price to its predecessor, and a
    /// blank is a worse answer than an approximate one that says which rate it used.</para>
    /// </summary>
    private sealed class RateBook
    {
        private readonly Dictionary<(string, string, string), ServiceRate> _exact = new();
        private readonly Dictionary<(string, string), ServiceRate>         _anyModel = new();

        public RateBook(IEnumerable<ServiceRate> rates)
        {
            foreach (var r in rates)
            {
                _exact[(r.Kind, r.Provider, r.Model)] = r;
                if (r.Model.Length == 0) _anyModel[(r.Kind, r.Provider)] = r;
            }
        }

        public ServiceRate? Find(string kind, string provider, string model)
            => _exact.TryGetValue((kind, provider, model), out var exact) ? exact
             : _anyModel.TryGetValue((kind, provider), out var any)       ? any
             : null;

        public static decimal Cost(ServiceRate? r, long input, long output, long cacheRead, long cacheWrite)
            => r is null ? 0m
             : input * r.InputPerUnit
             + output * r.OutputPerUnit
             + cacheRead * r.CacheReadPerUnit
             + cacheWrite * r.CacheWritePerUnit;
    }
}
