using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using EveConsole.Localization;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>How a sovereignty campaign is said, wherever it is shown: the map's hover, the
/// campaigns tab and the system page.</summary>
public static class CampaignText
{
    /// <summary>The structure fought over, from ESI's event type.</summary>
    public static string Kind(string eventType) => eventType switch
    {
        "ihub_defense"     => MapText.CampaignKindIhub,
        "tcu_defense"      => MapText.CampaignKindTcu,
        "station_defense"  => MapText.CampaignKindStation,
        "station_freeport" => MapText.CampaignKindFreeport,
        _                  => eventType,
    };

    private static string Span(TimeSpan t)
    {
        t = t.Duration();
        return t.TotalHours >= 1
            ? string.Format(MapText.DurationHm, (int)t.TotalHours, t.Minutes)
            : string.Format(MapText.DurationM, Math.Max(0, (int)t.TotalMinutes));
    }

    /// <summary>"running for 1 h 05 m" or "starts in 3 h 12 m".</summary>
    public static string When(SovCampaign c, DateTimeOffset now) =>
        c.IsRunning(now) ? string.Format(MapText.CampaignRunning, Span(now - c.Start))
                         : string.Format(MapText.CampaignStartsIn, Span(c.Start - now));

    /// <summary>The start in EVE time and on this machine's clock.</summary>
    public static string Times(SovCampaign c) =>
        string.Format(CultureInfo.CurrentCulture, MapText.CampaignAt, c.Start.UtcDateTime, c.Start.ToLocalTime().DateTime);

    /// <summary>The defender's and the attackers' share; "" where ESI gives none (a freeport).</summary>
    public static string Scores(SovCampaign c) =>
        c.DefenderScore is { } d && c.AttackersScore is { } a
            ? string.Format(MapText.CampaignScores, d * 100, a * 100)
            : "";

    /// <summary>One campaign in a line: structure, when, defender, scores.</summary>
    public static string Line(SovCampaign c, DateTimeOffset now)
    {
        var line = string.Format(MapText.CampaignLine, Kind(c.EventType), When(c, now), c.DefenderName);
        return Scores(c) is { Length: > 0 } s ? $"{line} · {s}" : line;
    }
}

/// <summary>
/// The map tool's sovereignty campaigns tab: every campaign now and coming, soonest first, with
/// a region filter. Refreshed with the map's other live data; rows change in place, so the list
/// only moves where a campaign came, went or changed.
/// </summary>
public sealed class CampaignsTabViewModel : MapTabViewModel
{
    public CampaignsTabViewModel(MapToolViewModel tool) : base(tool)
    {
        Regions.Add(new Choice<int>(0, MapText.CampaignsAllRegions));
        _region = Regions[0];
    }

    public override string TabTitle => MapText.CampaignsTab;
    public override string TabGlyph => "⚔";

    public ObservableCollection<CampaignRowVm> Rows    { get; } = [];
    public ObservableCollection<Choice<int>>   Regions { get; } = [];

    private Choice<int> _region;
    public Choice<int> Region
    {
        get => _region;
        set
        {
            if (value is null) return;   // a detaching ComboBox sets null; that is not a choice
            this.RaiseAndSetIfChanged(ref _region, value);
            Apply();
        }
    }

    private string _summary = "";
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    public bool HasRows => Rows.Count > 0;

    private IReadOnlyList<SovCampaign> _all = [];

    /// <summary>Shows a fresh reading. UI thread.</summary>
    public void Show(IReadOnlyList<SovCampaign> campaigns)
    {
        _all = campaigns;
        // The region list changes entry by entry, never cleared: a ComboBox whose list is emptied
        // loses its selection.
        var regions = campaigns.Where(c => c.RegionId > 0)
            .GroupBy(c => c.RegionId)
            .Select(g => new Choice<int>(g.Key, SdeNames.Region(g.Key, g.First().RegionName)))
            .OrderBy(c => c.Label, StringComparer.CurrentCulture)
            .Prepend(Regions[0])
            .ToList();
        SystemPageViewModel.Merge(Regions, regions, c => c.Value, (a, b) => a == b);
        if (!Regions.Contains(_region)) Region = Regions[0];
        Apply();
    }

    private void Apply()
    {
        var now  = DateTimeOffset.UtcNow;
        var rows = _all.Where(c => _region.Value == 0 || c.RegionId == _region.Value)
                       .Select(c => new CampaignRowVm(c, Tool, now))
                       .ToList();
        SystemPageViewModel.Merge(Rows, rows, r => r.CampaignId, (a, b) => a.Signature == b.Signature);
        Summary = _all.Count == 0
            ? MapText.CampaignsNone
            : string.Format(MapText.CampaignsSummary, _all.Count, _all.Count(c => c.IsRunning(now)));
        this.RaisePropertyChanged(nameof(HasRows));
    }
}

/// <summary>One campaign in the tab.</summary>
public sealed class CampaignRowVm
{
    public CampaignRowVm(SovCampaign c, MapToolViewModel tool, DateTimeOffset now)
    {
        CampaignId  = c.CampaignId;
        SystemId    = c.SystemId;
        SystemLabel = SdeNames.SolarSystem(c.SystemId, c.SystemName);
        RegionLabel = SdeNames.Region(c.RegionId, c.RegionName);
        Kind        = CampaignText.Kind(c.EventType);
        Defender    = c.DefenderName;
        Scores      = CampaignText.Scores(c);
        When        = CampaignText.When(c, now);
        Start       = CampaignText.Times(c);
        Running     = c.IsRunning(now);
        State       = Running ? MapText.CampaignStateRunning : MapText.CampaignStateScheduled;
        Signature   = $"{Scores}|{When}|{Defender}";
        OpenCommand = ReactiveCommand.Create(() => tool.OpenSystem(c.SystemId));
    }

    public long   CampaignId  { get; }
    public int    SystemId    { get; }
    public string SystemLabel { get; }
    public string RegionLabel { get; }
    public string Kind        { get; }
    public string Defender    { get; }
    public string Scores      { get; }
    public string When        { get; }
    public string Start       { get; }
    public string State       { get; }
    public bool   Running     { get; }
    /// <summary>What a refresh compares: the countdown is part of it, so it ticks.</summary>
    public string Signature   { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
}
