using System.Collections.ObjectModel;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

// ── Display model types ───────────────────────────────────────────────────────

/// <summary>
/// One row on the Summary tab. Formatted text for display alongside the raw value each column
/// sorts on, because a grid that sorts "9.9B" above "10.1B" is worse than one showing raw digits.
/// </summary>
public sealed class CharacterSummaryRowVm(EveConsole.Services.CharacterSummaryRow r)
{
    public long   CharacterId => r.CharacterId;
    public string Name        => r.Name;
    public string CorpName    => r.CorpName;
    public string Alliance    => r.AllianceName;

    public bool   Online      => r.Online;
    public string OnlineText  => r.Online ? "Online" : "Offline";
    public string OnlineColor => r.Online ? "#5aa469" : "#666677";

    public string Location    => r.Location;
    public string Ship        => r.Ship;
    public string HomeStation => r.HomeStation;

    public double PodValueRaw => r.PodValue;
    public string PodValue    => Isk(r.PodValue);

    public long   TotalSpRaw  => r.TotalSp;
    public string TotalSp     => r.TotalSp.ToString("N0");

    public decimal IskRaw     => r.Isk;
    public string  Isk_       => Isk((double)r.Isk);

    public double AssetValueRaw => r.AssetValue;
    public string AssetValue    => Isk(r.AssetValue);

    /// <summary>Sorted on time remaining, which is what "is this queue about to run dry" asks.
    /// An empty queue sorts as if it ended long ago, so it lands at the urgent end.</summary>
    public DateTimeOffset QueueEndsRaw => r.QueueEnds ?? DateTimeOffset.MinValue;
    public int    QueueLength => r.QueueLength;
    public string QueueText   => r.QueueLength == 0
        ? "empty"
        : r.QueueEnds is { } end
            ? $"{r.QueueLength} · {Span(end - DateTimeOffset.UtcNow)}"
            : $"{r.QueueLength} · paused";

    /// <summary>Amber once the queue is inside a day, red when it has run dry.</summary>
    public string QueueColor => r.QueueLength == 0 ? "#c85a5a"
        : r.QueueEnds is { } e && e - DateTimeOffset.UtcNow < TimeSpan.FromDays(1) ? "#c8a84b"
        : "#c8c8d8";

    // A dash where the worklist is not allowed to use that pool. The character may well have
    // eleven slots, but none of them are available to this tool, and printing the capacity would
    // invite planning against slots it will never schedule.
    public string Manufacturing => r.UsesManufacturing ? $"{r.ManufacturingFree} / {r.ManufacturingTotal}" : "—";
    public string Reaction      => r.UsesReaction      ? $"{r.ReactionFree} / {r.ReactionTotal}"           : "—";
    public string Science       => r.UsesScience       ? $"{r.ScienceFree} / {r.ScienceTotal}"             : "—";

    // Sorted below every real figure rather than as a zero, which would rank an unused pool
    // alongside a genuinely full one.
    public int ManufacturingFreeRaw => r.UsesManufacturing ? r.ManufacturingFree : -1;
    public int ReactionFreeRaw      => r.UsesReaction      ? r.ReactionFree      : -1;
    public int ScienceFreeRaw       => r.UsesScience       ? r.ScienceFree       : -1;

    public string ManufacturingColor => r.UsesManufacturing ? SlotColor(r.ManufacturingFree, r.ManufacturingTotal) : Unused;
    public string ReactionColor      => r.UsesReaction      ? SlotColor(r.ReactionFree,      r.ReactionTotal)      : Unused;
    public string ScienceColor       => r.UsesScience       ? SlotColor(r.ScienceFree,       r.ScienceTotal)       : Unused;

    /// <summary>Muted, so a dash reads as "not applicable" rather than as a state to act on.</summary>
    private const string Unused = "#555566";

    /// <summary>
    /// Red when there is nothing free, amber below half, green above.
    ///
    /// <para>Judged as a share of the character's own capacity rather than an absolute count: two
    /// free slots is comfortable for an alt with three and nearly nothing for one with eleven, and
    /// the column is scanned to find who has room.</para>
    /// </summary>
    private static string SlotColor(int free, int total) =>
        free <= 0                ? "#c85a5a"
        : total > 0 && free * 2 > total ? "#5aa469"
        : "#e0902e";

    private static string Isk(double v) => v switch
    {
        >= 1_000_000_000_000 => $"{v / 1_000_000_000_000:N2}T",
        >= 1_000_000_000     => $"{v / 1_000_000_000:N2}B",
        >= 1_000_000         => $"{v / 1_000_000:N1}M",
        >= 1_000             => $"{v / 1_000:N0}k",
        _                    => v.ToString("N0"),
    };

    private static string Span(TimeSpan t) => t.TotalSeconds <= 0 ? "done"
        : t.TotalDays >= 1 ? $"{t.TotalDays:F0}d"
        : t.TotalHours >= 1 ? $"{t.TotalHours:F0}h"
        : $"{t.TotalMinutes:F0}m";
}

public record SkillGroupVm(int GroupId, string Name, long TotalSp)
{
    public string SpText => TotalSp >= 1_000_000
        ? $"{TotalSp / 1_000_000.0:F2}M"
        : TotalSp >= 1_000
            ? $"{TotalSp / 1_000.0:F0}k"
            : $"{TotalSp}";
}

public class SkillGroupHeader
{
    public string Name   { get; }
    public string SpText { get; }
    public SkillGroupHeader(string name, long sp)
    {
        Name   = name;
        SpText = sp >= 1_000_000 ? $"{sp / 1_000_000.0:F2}M SP"
               : sp >= 1_000     ? $"{sp / 1_000.0:F0}k SP"
               : $"{sp} SP";
    }
}

public class SkillItem
{
    public int    TypeId       { get; }
    public string Name         { get; }
    public int    TrainedLevel { get; }
    public int    ActiveLevel  { get; }
    public long   Sp           { get; }
    public string SpText       => $"{Sp:N0}";
    public bool   D1           => TrainedLevel >= 1;
    public bool   D2           => TrainedLevel >= 2;
    public bool   D3           => TrainedLevel >= 3;
    public bool   D4           => TrainedLevel >= 4;
    public bool   D5           => TrainedLevel >= 5;

    public SkillItem(int typeId, string name, int trainedLevel, int activeLevel, long sp)
    {
        TypeId = typeId; Name = name; TrainedLevel = trainedLevel; ActiveLevel = activeLevel; Sp = sp;
    }
}

public class QueueItemVm
{
    public int             TypeId      { get; }
    public int             Position    { get; }
    public int             DisplayPos  => Position + 1;
    public string          SkillName   { get; }
    public int             TargetLevel { get; }
    public DateTimeOffset? FinishDate  { get; }
    public double          Progress    { get; }
    public string          EtaText     { get; }
    public string          DurationText { get; }
    public bool            IsTraining  { get; }

    public QueueItemVm(int typeId, int position, string skillName, int targetLevel,
        DateTimeOffset? startDate, DateTimeOffset? finishDate)
    {
        TypeId      = typeId;
        Position    = position;
        SkillName   = skillName;
        TargetLevel = targetLevel;
        FinishDate  = finishDate;
        IsTraining  = position == 0 && startDate.HasValue && finishDate.HasValue;

        if (IsTraining)
        {
            var total   = (finishDate!.Value - startDate!.Value).TotalSeconds;
            var elapsed = (DateTimeOffset.UtcNow - startDate.Value).TotalSeconds;
            Progress = total > 0 ? Math.Clamp(elapsed / total, 0, 1) : 0;
        }

        EtaText = finishDate.HasValue
            ? FormatRemaining(finishDate.Value - DateTimeOffset.UtcNow)
            : "Paused";

        // Remaining time to train just this one skill level — not cumulative with the rest of
        // the queue. For a skill that hasn't started yet this equals its full duration; for one
        // already partway through (only ever the currently-training entry), it's what's left.
        if (startDate.HasValue && finishDate.HasValue)
        {
            var effectiveStart = startDate.Value > DateTimeOffset.UtcNow ? startDate.Value : DateTimeOffset.UtcNow;
            DurationText = FormatDuration(finishDate.Value - effectiveStart);
        }
        else
            DurationText = "—";
    }

    // "4 days, 2 hours, 3 minutes" style — used for both per-item and whole-queue ETAs.
    public static string FormatRemaining(TimeSpan remaining)
        => remaining <= TimeSpan.Zero ? "Complete" : FormatDuration(remaining);

    public static string FormatDuration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "—";

        int days    = (int)span.TotalDays;
        int hours   = span.Hours;
        int minutes = span.Minutes;

        var parts = new List<string>();
        if (days    > 0) parts.Add($"{days} day{(days == 1 ? "" : "s")}");
        if (hours   > 0 || days > 0) parts.Add($"{hours} hour{(hours == 1 ? "" : "s")}");
        if (minutes > 0 || parts.Count == 0) parts.Add($"{minutes} minute{(minutes == 1 ? "" : "s")}");
        return string.Join(", ", parts);
    }
}

public record ActiveImplantVm(string Name);
public record JumpCloneVm(string Location, string? CloneName, IReadOnlyList<string> ImplantNames);
public record MedalDisplayVm(string Title, DateTimeOffset Date, string Status, string Reason);
public record TitleVm(string Name);
public record StandingVm(string EntityName, string EntityType, float Standing)
{
    public string StandingText => Standing >= 0 ? $"+{Standing:F1}" : $"{Standing:F1}";
    public string TypeLabel => EntityType switch
    {
        "faction"  => "Faction",
        "npc_corp" => "Corp",
        "agent"    => "Agent",
        _          => EntityType
    };
}

// ── ViewModel ─────────────────────────────────────────────────────────────────

public class CharacterViewerViewModel : ReactiveObject
{
    private readonly AppDbContext _db;
    private const int SkillCategoryId = 16;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private record SkillGroupData(int GroupId, string GroupName, long TotalSp, List<SkillItem> Skills);
    private List<SkillGroupData> _allSkillData = [];
    private CancellationTokenSource _cts = new();

    // ── Characters ────────────────────────────────────────────────────────────
    public ObservableCollection<Character> Characters { get; }

    private Character? _selectedCharacter;
    public Character? SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (ReferenceEquals(_selectedCharacter, value)) return;
            this.RaiseAndSetIfChanged(ref _selectedCharacter, value);
            if (value is not null) _ = LoadCharacterDataAsync(value);
            else ClearAll();
        }
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }

    // ── Summary tab ───────────────────────────────────────────────────────────
    //
    // Every character on one grid. The detail tab answers "what about this one"; this answers
    // "which one", which is the question actually asked when deciding who to log in.

    public ObservableCollection<CharacterSummaryRowVm> SummaryRows { get; } = [];

    private int _outerTabIndex;
    public int OuterTabIndex
    {
        get => _outerTabIndex;
        set => this.RaiseAndSetIfChanged(ref _outerTabIndex, value);
    }

    private bool _summaryLoading;
    public bool SummaryLoading { get => _summaryLoading; private set => this.RaiseAndSetIfChanged(ref _summaryLoading, value); }

    private string _summaryStatus = "";
    public string SummaryStatus { get => _summaryStatus; private set => this.RaiseAndSetIfChanged(ref _summaryStatus, value); }

    public ReactiveCommand<Unit, Unit> RefreshSummaryCommand { get; }

    /// <summary>
    /// Opens one character on the detail tab. Bound to a double-click on a summary row; the
    /// dropdown there still works, so this is a shortcut rather than the only way in.
    /// </summary>
    public void ShowDetailFor(long characterId)
    {
        var match = Characters.FirstOrDefault(c => c.Id == characterId);
        if (match is null) return;
        SelectedCharacter = match;
        OuterTabIndex     = 1;
    }

    private async Task LoadSummaryAsync()
    {
        if (_summary is null) return;

        SummaryLoading = true;
        try
        {
            var rows = await _summary.LoadAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SummaryRows.Clear();
                foreach (var r in rows) SummaryRows.Add(new CharacterSummaryRowVm(r));
                SummaryStatus = $"{rows.Count} character(s). Figures come from polled data — "
                              + "the detail tab's Refresh updates one character, polling updates all.";
            });
        }
        catch (Exception ex)
        {
            SummaryStatus = $"Could not load the summary: {ex.Message}";
        }
        finally { SummaryLoading = false; }
    }

    // Selects a character by name and jumps to the Skills tab (index 0) — used when
    // navigating here from an Overview skill-queue alert.
    public void ShowSkillsFor(string characterName)
    {
        var match = Characters.FirstOrDefault(c => c.Name == characterName);
        if (match is not null) SelectedCharacter = match;
        SelectedTabIndex = 0;
    }

    // ── Character info ────────────────────────────────────────────────────────
    private string _corpName = "";
    public string CorpName { get => _corpName; private set => this.RaiseAndSetIfChanged(ref _corpName, value); }

    private Bitmap? _portrait;
    public Bitmap? Portrait { get => _portrait; private set => this.RaiseAndSetIfChanged(ref _portrait, value); }

    // ── Skills tab ────────────────────────────────────────────────────────────
    private IReadOnlyList<SkillGroupVm> _skillGroups = [];
    public IReadOnlyList<SkillGroupVm> SkillGroups
    { get => _skillGroups; private set => this.RaiseAndSetIfChanged(ref _skillGroups, value); }

    private SkillGroupVm? _selectedSkillGroup;
    public SkillGroupVm? SelectedSkillGroup
    {
        get => _selectedSkillGroup;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSkillGroup, value);
            RebuildSkillDisplay();
        }
    }

    public ObservableCollection<object> SkillDisplayItems { get; } = [];
    public ObservableCollection<QueueItemVm> Queue { get; } = [];

    private string _queueText = "";
    public string QueueText { get => _queueText; private set => this.RaiseAndSetIfChanged(ref _queueText, value); }

    private string _queueEtaText = "";
    public string QueueEtaText { get => _queueEtaText; private set => this.RaiseAndSetIfChanged(ref _queueEtaText, value); }

    // ── Attributes tab ────────────────────────────────────────────────────────
    private StoredCharacterAttributes? _attributes;
    public StoredCharacterAttributes? Attributes
    { get => _attributes; private set => this.RaiseAndSetIfChanged(ref _attributes, value); }

    private string _remapInfo = "";
    public string RemapInfo { get => _remapInfo; private set => this.RaiseAndSetIfChanged(ref _remapInfo, value); }

    // ── Clones tab ────────────────────────────────────────────────────────────
    private CharacterCloneState? _cloneState;
    public CharacterCloneState? CloneState
    { get => _cloneState; private set => this.RaiseAndSetIfChanged(ref _cloneState, value); }

    private IReadOnlyList<ActiveImplantVm> _activeImplants = [];
    public IReadOnlyList<ActiveImplantVm> ActiveImplants
    {
        get => _activeImplants;
        private set
        {
            this.RaiseAndSetIfChanged(ref _activeImplants, value);
            this.RaisePropertyChanged(nameof(NoActiveImplants));
        }
    }
    public bool NoActiveImplants => _activeImplants.Count == 0;

    private IReadOnlyList<JumpCloneVm> _jumpClones = [];
    public IReadOnlyList<JumpCloneVm> JumpClones
    {
        get => _jumpClones;
        private set
        {
            this.RaiseAndSetIfChanged(ref _jumpClones, value);
            this.RaisePropertyChanged(nameof(NoJumpClones));
        }
    }
    public bool NoJumpClones => _jumpClones.Count == 0;

    // ── Medals tab ────────────────────────────────────────────────────────────
    private IReadOnlyList<MedalDisplayVm> _medals = [];
    public IReadOnlyList<MedalDisplayVm> Medals
    {
        get => _medals;
        private set
        {
            this.RaiseAndSetIfChanged(ref _medals, value);
            this.RaisePropertyChanged(nameof(NoMedals));
            this.RaisePropertyChanged(nameof(MedalCountText));
        }
    }
    public bool   NoMedals      => _medals.Count == 0;
    public string MedalCountText => _medals.Count == 0 ? "No medals on record for this character."
                                  : $"{_medals.Count} medal(s)";

    // ── Titles tab ────────────────────────────────────────────────────────────
    private IReadOnlyList<TitleVm> _titles = [];
    public IReadOnlyList<TitleVm> Titles
    {
        get => _titles;
        private set
        {
            this.RaiseAndSetIfChanged(ref _titles, value);
            this.RaisePropertyChanged(nameof(NoTitles));
        }
    }
    public bool NoTitles => _titles.Count == 0;

    // ── Standings tab ─────────────────────────────────────────────────────────
    private IReadOnlyList<StandingVm> _standings = [];
    public IReadOnlyList<StandingVm> Standings
    {
        get => _standings;
        private set
        {
            this.RaiseAndSetIfChanged(ref _standings, value);
            this.RaisePropertyChanged(nameof(NoStandings));
        }
    }
    public bool NoStandings => _standings.Count == 0;

    // ── Status ────────────────────────────────────────────────────────────────
    private string _statusText = "Select a character to view their data.";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    // Wired by MainWindowViewModel — opens the Item Browser for a clicked skill name.
    public Action<int>? NavigateToItemAction { get; set; }
    public ReactiveCommand<int, Unit> OpenInItemBrowserCommand { get; }

    private readonly EveConsole.Services.CharacterSummaryService? _summary;

    public CharacterViewerViewModel(AppDbContext db, ObservableCollection<Character> characters,
                                    EveConsole.Services.CharacterSummaryService? summary = null)
    {
        _db        = db;
        Characters = characters;
        _summary   = summary;

        RefreshCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedCharacter is not null)
                await LoadCharacterDataAsync(SelectedCharacter);
        });

        RefreshSummaryCommand = ReactiveCommand.CreateFromTask(LoadSummaryAsync);
        _ = LoadSummaryAsync();

        // Keep the summary grid current without anyone pressing Refresh. Online state, location
        // and ship all change while the window sits open, and the grid is the one place they are
        // all visible at once.
        //
        // ⚠️ Gated and labelled, like every other timer here. This view model lives for the whole
        // session whether or not the tool is open, so without the tab check it would query on
        // behalf of a grid nobody is looking at, once a minute, forever.
        //
        // Cheap enough to do on a clock: CharacterSummaryService reads local tables, and its only
        // ESI calls resolve alliance and corporation names for ids it has not seen before. In
        // steady state that is none.
        Observable.Interval(TimeSpan.FromSeconds(60))
            .ObserveOnUi("CharacterViewer.SummaryRefresh")
            .Where(_ => OuterTabIndex == 0 && !SummaryLoading)
            .Subscribe(_ => { var t = LoadSummaryAsync(); });

        OpenInItemBrowserCommand = ReactiveCommand.Create<int>(typeId => NavigateToItemAction?.Invoke(typeId));

        if (characters.Count > 0)
            SelectedCharacter = characters[0];
    }

    private async Task LoadCharacterDataAsync(Character character)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy     = true;
        StatusText = $"Loading {character.Name}…";
        ClearData();

        try
        {
            // Start portrait download concurrently while DB loads
            var portraitTask = LoadPortraitAsync(character, ct);

            await LoadCorpNameAsync(character, ct);
            await LoadSkillsAsync(character, ct);
            await LoadAttributesAsync(character, ct);
            await LoadClonesAsync(character, ct);
            await LoadMedalsAsync(character, ct);
            await LoadTitlesAsync(character, ct);
            await LoadStandingsAsync(character, ct);

            await portraitTask;

            StatusText = $"{character.Name} — {character.TotalSp:N0} SP";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Error loading {character.Name}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadPortraitAsync(Character character, CancellationToken ct)
    {
        try
        {
            var url   = $"https://images.evetech.net/characters/{character.Id}/portrait?size=128";
            var bytes = await _http.GetByteArrayAsync(url, ct);
            using var ms  = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ct.IsCancellationRequested) Portrait = bitmap;
            });
        }
        catch { /* portrait is optional — silently ignore network errors */ }
    }

    private async Task LoadCorpNameAsync(Character character, CancellationToken ct)
    {
        var corp = await _db.Corporations
            .Where(c => c.Id == character.CorporationId)
            .FirstOrDefaultAsync(ct);
        if (corp is not null) { CorpName = $"[{corp.Ticker}] {corp.Name}"; return; }

        var npc = await _db.SdeNpcCorporations
            .Where(n => n.CorporationId == character.CorporationId)
            .FirstOrDefaultAsync(ct);
        CorpName = npc is not null ? npc.Name : $"Corp #{character.CorporationId}";
    }

    private async Task LoadSkillsAsync(Character character, CancellationToken ct)
    {
        var groupMap = await _db.SdeGroups
            .Where(g => g.CategoryId == SkillCategoryId)
            .ToDictionaryAsync(g => g.GroupId, g => g.Name, ct);

        var groupIds = groupMap.Keys.ToList();
        var typeMap = await _db.SdeTypes
            .Where(t => groupIds.Contains(t.GroupId))
            .Select(t => new { t.TypeId, t.Name, t.GroupId })
            .ToDictionaryAsync(t => t.TypeId, t => (t.Name, t.GroupId), ct);

        var skills = await _db.EsiSkills
            .Where(s => s.CharacterId == character.Id)
            .ToListAsync(ct);

        var queue = await _db.EsiSkillQueue
            .Where(q => q.CharacterId == character.Id)
            .OrderBy(q => q.QueuePosition)
            .ToListAsync(ct);

        ct.ThrowIfCancellationRequested();

        var grouped = skills
            .GroupBy(s => typeMap.TryGetValue(s.SkillId, out var t) ? t.GroupId : 0)
            .Select(g =>
            {
                var gId   = g.Key;
                var gName = gId > 0 && groupMap.TryGetValue(gId, out var n) ? n : "Unknown";
                var items = g.Select(s =>
                    {
                        var name = typeMap.TryGetValue(s.SkillId, out var t2) ? t2.Name : $"Skill #{s.SkillId}";
                        return new SkillItem(s.SkillId, name, s.TrainedSkillLevel, s.ActiveSkillLevel, s.SkillpointsInSkill);
                    })
                    .OrderBy(s => s.Name)
                    .ToList();
                return new SkillGroupData(gId, gName, g.Sum(s => s.SkillpointsInSkill), items);
            })
            .OrderBy(g => g.GroupName)
            .ToList();

        var totalSp  = grouped.Sum(g => g.TotalSp);
        var groupVms = grouped.Select(g => new SkillGroupVm(g.GroupId, g.GroupName, g.TotalSp)).ToList();
        var allSkills = new SkillGroupVm(0, "All Skills", totalSp);

        // Drop entries that already finished per stale local data (poll hasn't caught up with
        // real-world completion yet) — an already-complete skill has no business looking like
        // it's still queued. Renumber the rest so display position starts at 1 again.
        var activeQueue = queue
            .Where(q => !q.FinishDate.HasValue || q.FinishDate.Value > DateTimeOffset.UtcNow)
            .ToList();

        var queueVms = activeQueue.Select((q, idx) =>
        {
            var skillName = typeMap.TryGetValue(q.SkillId, out var t3) ? t3.Name : $"Skill #{q.SkillId}";
            return new QueueItemVm(q.SkillId, idx, skillName, q.FinishedLevel, q.StartDate, q.FinishDate);
        }).ToList();

        var queueFinish = activeQueue.LastOrDefault(q => q.FinishDate.HasValue)?.FinishDate;
        var queueText   = activeQueue.Count == 0 ? "Queue empty"
            : queueFinish.HasValue
                ? $"{activeQueue.Count} skills — finishes {queueFinish.Value.UtcDateTime:dd MMM yyyy}"
                : $"{activeQueue.Count} skills (paused)";
        var queueEtaText = activeQueue.Count == 0 ? ""
            : queueFinish.HasValue
                ? QueueItemVm.FormatRemaining(queueFinish.Value - DateTimeOffset.UtcNow)
                : "Paused";

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;

            _allSkillData = grouped;
            SkillGroups   = [allSkills, ..groupVms];

            _selectedSkillGroup = SkillGroups.Count > 0 ? SkillGroups[0] : null;
            this.RaisePropertyChanged(nameof(SelectedSkillGroup));
            RebuildSkillDisplay();

            Queue.Clear();
            foreach (var q in queueVms) Queue.Add(q);
            QueueText    = queueText;
            QueueEtaText = queueEtaText;
        });
    }

    private void RebuildSkillDisplay()
    {
        SkillDisplayItems.Clear();
        var source = (_selectedSkillGroup is null || _selectedSkillGroup.GroupId == 0)
            ? _allSkillData
            : _allSkillData.Where(g => g.GroupId == _selectedSkillGroup.GroupId).ToList();

        foreach (var g in source)
        {
            SkillDisplayItems.Add(new SkillGroupHeader(g.GroupName, g.TotalSp));
            foreach (var s in g.Skills)
                SkillDisplayItems.Add(s);
        }
    }

    private async Task LoadAttributesAsync(Character character, CancellationToken ct)
    {
        var attrs = await _db.EsiCharacterAttributes
            .Where(a => a.CharacterId == character.Id)
            .FirstOrDefaultAsync(ct);

        string remapInfo;
        if (attrs is null)
            remapInfo = "No attribute data available";
        else if (attrs.BonusRemaps > 0)
            remapInfo = $"{attrs.BonusRemaps} bonus remap(s) available";
        else if (attrs.AccruingRemapCooldownDate.HasValue && attrs.AccruingRemapCooldownDate > DateTimeOffset.UtcNow)
            remapInfo = $"Remap available: {attrs.AccruingRemapCooldownDate.Value.UtcDateTime:dd MMM yyyy}";
        else
            remapInfo = attrs.LastRemapDate.HasValue
                ? $"Last remap: {attrs.LastRemapDate.Value.UtcDateTime:dd MMM yyyy} — Remap available now"
                : "Remap available";

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;
            Attributes = attrs;
            RemapInfo  = remapInfo;
        });
    }

    private async Task LoadClonesAsync(Character character, CancellationToken ct)
    {
        var cloneState = await _db.EsiCloneStates
            .Where(c => c.CharacterId == character.Id)
            .FirstOrDefaultAsync(ct);

        var implantTypeIds = await _db.EsiImplants
            .Where(i => i.CharacterId == character.Id)
            .Select(i => i.TypeId)
            .ToListAsync(ct);

        var implantNameMap = await _db.SdeTypes
            .Where(t => implantTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var activeImplants = implantTypeIds
            .Select(id => new ActiveImplantVm(implantNameMap.GetValueOrDefault(id, $"Implant #{id}")))
            .OrderBy(i => i.Name)
            .ToList();

        var jClones = await _db.EsiJumpClones
            .Where(j => j.CharacterId == character.Id)
            .ToListAsync(ct);

        var jCloneIds = jClones.Select(j => j.JumpCloneId).ToList();
        var jImplants = await _db.EsiJumpCloneImplants
            .Where(i => jCloneIds.Contains(i.JumpCloneId))
            .ToListAsync(ct);

        var jTypeIds = jImplants.Select(i => i.TypeId).Distinct().ToList();
        var jNameMap = await _db.SdeTypes
            .Where(t => jTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var jumpClones = jClones.Select(jc =>
        {
            var implants = jImplants
                .Where(i => i.JumpCloneId == jc.JumpCloneId)
                .Select(i => jNameMap.GetValueOrDefault(i.TypeId, $"Implant #{i.TypeId}"))
                .OrderBy(n => n)
                .ToList();
            var location = $"Location {jc.LocationId} ({jc.LocationType})";
            return new JumpCloneVm(location, jc.Name, implants);
        }).ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;
            CloneState     = cloneState;
            ActiveImplants = activeImplants;
            JumpClones     = jumpClones;
        });
    }

    private async Task LoadMedalsAsync(Character character, CancellationToken ct)
    {
        // SQLite doesn't support DateTimeOffset ORDER BY via EF — sort in memory
        var charMedals = await _db.EsiMedals
            .Where(m => m.CharacterId == character.Id)
            .ToListAsync(ct);
        charMedals.Sort((a, b) => b.Date.CompareTo(a.Date));

        // Try to enrich with corp medal titles — only available when corp data has been polled
        var corpIds = charMedals.Select(m => (long)m.CorporationId).Distinct().ToList();
        var corpMedalTitles = new Dictionary<(long, int), string>();
        if (corpIds.Count > 0)
        {
            corpMedalTitles = await _db.EsiCorpMedals
                .Where(cm => corpIds.Contains(cm.CorporationId))
                .ToDictionaryAsync(cm => (cm.CorporationId, cm.MedalId), cm => cm.Title, ct);
        }

        var medals = charMedals.Select(m =>
        {
            var title = corpMedalTitles.TryGetValue(((long)m.CorporationId, m.MedalId), out var t)
                ? t : $"Medal #{m.MedalId}";
            return new MedalDisplayVm(title, m.Date, m.Status, m.Reason);
        }).ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;
            Medals = medals;
        });
    }

    private async Task LoadTitlesAsync(Character character, CancellationToken ct)
    {
        var rows = await _db.EsiTitles
            .Where(t => t.CharacterId == character.Id)
            .ToListAsync(ct);

        var titles = rows
            .OrderBy(t => t.Name)
            .Select(t => new TitleVm(t.Name))
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ct.IsCancellationRequested) Titles = titles;
        });
    }

    private async Task LoadStandingsAsync(Character character, CancellationToken ct)
    {
        var rows = await _db.EsiStandings
            .Where(s => s.OwnerId == character.Id && s.OwnerType == "character")
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ct.IsCancellationRequested) Standings = [];
            });
            return;
        }

        var allIds = rows.Select(s => (int)s.FromId).Distinct().ToList();

        // SDE lookups — may return empty names if SDE was imported before the nameID fix
        var factionMap = await _db.SdeFactions
            .Where(f => allIds.Contains(f.FactionId))
            .ToDictionaryAsync(f => (long)f.FactionId, f => f.Name, ct);

        var npcCorpMap = await _db.SdeNpcCorporations
            .Where(c => allIds.Contains(c.CorporationId))
            .ToDictionaryAsync(c => (long)c.CorporationId, c => c.Name, ct);

        // ESI /universe/names/ resolves ALL entity types (faction, corporation, character)
        // in a single batch — used as fallback when SDE names are absent or empty,
        // and as the only source for agent character names which are never in the SDE.
        var esiNameMap = await ResolveEsiNamesAsync(rows.Select(s => s.FromId).Distinct(), ct);

        var standings = rows.Select(s =>
        {
            var sdeMap = s.FromType == "faction" ? factionMap : npcCorpMap;
            var name = (sdeMap.TryGetValue(s.FromId, out var sn) && sn.Length > 0) ? sn
                     : esiNameMap.GetValueOrDefault(s.FromId)
                    ?? s.FromType switch
                     {
                         "faction"  => $"Faction #{s.FromId}",
                         "npc_corp" => $"Corp #{s.FromId}",
                         "agent"    => $"Agent #{s.FromId}",
                         _          => $"Entity #{s.FromId}"
                     };
            return new StandingVm(name, s.FromType, s.Standing);
        })
        .OrderByDescending(s => s.Standing)
        .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ct.IsCancellationRequested) Standings = standings;
        });
    }

    private record EsiUniverseName(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")]   long   Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name
    );

    private async Task<Dictionary<long, string>> ResolveEsiNamesAsync(
        IEnumerable<long> ids, CancellationToken ct)
    {
        try
        {
            var idList = ids.Take(1000).ToList();
            var json   = System.Text.Json.JsonSerializer.Serialize(idList);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(
                "https://esi.evetech.net/latest/universe/names/?datasource=tranquility",
                content, ct);
            if (!resp.IsSuccessStatusCode) return [];
            var results = await System.Text.Json.JsonSerializer.DeserializeAsync<List<EsiUniverseName>>(
                await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return results?.ToDictionary(r => r.Id, r => r.Name) ?? [];
        }
        catch { return []; }
    }

    private void ClearData()
    {
        CorpName = "";
        Portrait = null;
        SkillGroups = [];
        _allSkillData = [];
        SkillDisplayItems.Clear();
        Queue.Clear();
        QueueText    = "";
        QueueEtaText = "";
        Attributes = null;
        RemapInfo = "";
        CloneState = null;
        ActiveImplants = [];
        JumpClones = [];
        Medals = [];
        Titles = [];
        Standings = [];
    }

    private void ClearAll()
    {
        ClearData();
        StatusText = "Select a character.";
    }
}
