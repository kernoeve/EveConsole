using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Services.Worklist;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One row on the worklist.</summary>
public class WorklistRowVm : ReactiveObject
{
    private readonly WorklistItem _item;

    public WorklistRowVm(WorklistItem item) => _item = item;

    public string Key           => _item.Key;
    public string Title         => _item.Title;
    public string Detail        => _item.Detail;
    public string SourceName    => _item.Source;
    public string CharacterName => _item.CharacterName.Length > 0 ? _item.CharacterName : "—";
    public string LocationName  => _item.LocationName;
    public int    TypeId        => _item.TypeId;
    public int    Priority      => _item.Priority;
    public bool   IsSnoozed     => _item.IsSnoozed;

    public string ReadinessText => _item.Readiness switch
    {
        WorklistReadiness.Ready   => "Ready",
        WorklistReadiness.Blocked => "Blocked",
        _                         => "Waiting",
    };

    public string ReadinessColor => _item.Readiness switch
    {
        WorklistReadiness.Ready   => "#5aa469",
        WorklistReadiness.Blocked => "#c85a5a",
        _                         => "#c8a84b",
    };

    /// <summary>Blocked items say what is in the way; the rest carry their own detail.</summary>
    public string Note => _item.BlockedBy.Length > 0 ? _item.BlockedBy : "";

    public bool HasNote => Note.Length > 0;

    /// <summary>How long this has been asking to be done — the thing a regenerated list would
    /// otherwise lose on every refresh.</summary>
    public string AgeText => _item.FirstSeenAt is { } f ? Ago(f) : "";

    /// <summary>How stale the data behind the suggestion is. Shown so the player does not act
    /// on an hour-old order book without knowing it.</summary>
    public string DataAgeText => _item.DataAsOf is { } d ? $"data {Ago(d)}" : "";

    public string SnoozeText => _item.SnoozedUntil is { } s && s > DateTimeOffset.UtcNow
        ? $"snoozed until {s.ToLocalTime():d MMM HH:mm}"
        : "";

    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalMinutes <  1) return "just now";
        if (d.TotalHours   <  1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays    <  1) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}

/// <summary>
/// The worklist: what to do next, and whether it can be done now.
///
/// Rows are rebuilt from live data on every refresh rather than tracked, so an item disappears
/// once the thing it asked for has happened. Detection is off polled ESI data, which means a
/// refresh only reflects the last poll — hence the per-row data age.
/// </summary>
public class WorklistViewModel : ReactiveObject
{
    private readonly WorklistService _service;

    public ObservableCollection<WorklistRowVm> Rows { get; } = [];

    /// <summary>Market alt configuration, hosted here because it exists only to serve this tool.</summary>
    public WorklistMarketAltsViewModel MarketAltsVm { get; }

    /// <summary>Inventory-level rules: thresholds, stations and fill targets.</summary>
    public WorklistInvRulesViewModel RulesVm { get; }

    /// <summary>Rules turning pending customer orders into buys.</summary>
    public WorklistOrderRulesViewModel OrderRulesVm { get; }

    /// <summary>Who maintains each corporation's standing projects.</summary>
    public WorklistCorpAltsViewModel CorpAltsVm { get; }

    /// <summary>Which characters run industry, and against which park.</summary>
    public WorklistIndustryViewModel IndustryVm { get; }

    /// <summary>Where each group's stock should live. Distribution, not demand.</summary>
    public WorklistStationLevelsViewModel StationLevelsVm { get; }

    /// <summary>Which sources run, and which conditions each one raises.</summary>
    public ObservableCollection<WorklistToggleVm> Sources    { get; } = [];
    public ObservableCollection<WorklistToggleVm> Conditions { get; } = [];

    public ReactiveCommand<Unit, Unit>   RefreshCommand { get; }
    public ReactiveCommand<string, Unit> SnoozeCommand  { get; }

    public WorklistViewModel(WorklistService service, WorklistMarketAltsViewModel marketAlts,
                             WorklistInvRulesViewModel rules,
                             WorklistOrderRulesViewModel orderRules,
                             WorklistCorpAltsViewModel corpAlts,
                             WorklistIndustryViewModel industry,
                             WorklistStationLevelsViewModel stationLevels)
    {
        _service = service;
        MarketAltsVm  = marketAlts;
        RulesVm  = rules;
        RulesVm.RulesChanged = RefreshAsync;
        OrderRulesVm = orderRules;
        OrderRulesVm.RulesChanged = RefreshAsync;
        CorpAltsVm = corpAlts;
        CorpAltsVm.CorpAltsChanged = RefreshAsync;
        IndustryVm = industry;
        IndustryVm.IndustryChanged = RefreshAsync;
        StationLevelsVm = stationLevels;
        StationLevelsVm.LevelsChanged = RefreshAsync;

        // Assigning a market alt unblocks items, so the list should reflect it without a manual
        // refresh — that gap is exactly what makes config feel like it did not take.
        MarketAltsVm.MarketAltsChanged = RefreshAsync;

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        SnoozeCommand  = ReactiveCommand.CreateFromTask<string>(async key =>
        {
            await _service.SnoozeAsync(key, DateTimeOffset.UtcNow.AddHours(SnoozeHours));
            await RefreshAsync();
        });

        BuildToggles();
        _ = RefreshAsync();
    }

    /// <summary>
    /// The Sources tab. Built from the registered generators rather than a hard-coded list, so
    /// a new generator appears here the moment it is registered.
    /// </summary>
    private void BuildToggles()
    {
        var s = _service.Settings;

        foreach (var g in _service.Generators)
            Sources.Add(new WorklistToggleVm(
                g.DisplayName,
                "Include this source in the worklist.",
                s.IsSourceEnabled(g.Id),
                on => s.SetSourceEnabledAsync(g.Id, on),
                RefreshAsync));

        // Standing-buy conditions are separate switches because they are separate jobs: an
        // outbid order needs a price change, a missing one needs creating, a low one topping up.
        Conditions.Add(new WorklistToggleVm("Missing orders",
            "A standing order with no live buy order at its station.",
            s.RaiseMissing,  on => s.SetConditionAsync("missing", on),  RefreshAsync));
        Conditions.Add(new WorklistToggleVm("Outbid",
            "Someone else is bidding higher, so the order is buying nothing.",
            s.RaiseOutbid,   on => s.SetConditionAsync("outbid", on),   RefreshAsync));
        Conditions.Add(new WorklistToggleVm("Running low",
            "Remaining volume has fallen below the top-up threshold.",
            s.RaiseLow,      on => s.SetConditionAsync("low", on),      RefreshAsync));
        Conditions.Add(new WorklistToggleVm("Expiring soon",
            "The order is close to the end of its duration.",
            s.RaiseExpiring, on => s.SetConditionAsync("expiring", on), RefreshAsync));
    }

    /// <summary>How long the snooze button hides an item for.</summary>
    public const int SnoozeHours = 8;

    private bool _showSnoozed;
    public bool ShowSnoozed
    {
        get => _showSnoozed;
        set { this.RaiseAndSetIfChanged(ref _showSnoozed, value); _ = RefreshAsync(); }
    }

    /// <summary>
    /// Blocked and waiting items are hidden unless asked for.
    ///
    /// The list is read to find something to do now, and an item that cannot be started is not
    /// that — it is context. Left in by default they crowd out the actionable rows, and a list
    /// mostly full of things you cannot do stops being read at all. They stay one click away
    /// because the reasons are worth seeing when planning rather than executing.
    /// </summary>
    private bool _showNotReady;
    public bool ShowNotReady
    {
        get => _showNotReady;
        set { this.RaiseAndSetIfChanged(ref _showNotReady, value); _ = RefreshAsync(); }
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _errors = "";
    public string Errors { get => _errors; private set => this.RaiseAndSetIfChanged(ref _errors, value); }
    public bool HasErrors => Errors.Length > 0;

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var run = await _service.BuildAsync();

            var unsnoozed = run.AllItems.Where(i => ShowSnoozed || !i.IsSnoozed).ToList();

            var visible = unsnoozed
                .Where(i => ShowNotReady || i.Readiness == WorklistReadiness.Ready)
                // Blocked last: the list is read top-down looking for something to do, and an
                // item that cannot be actioned does not belong at the top of that read.
                .OrderBy(i => i.Readiness == WorklistReadiness.Blocked ? 1 : 0)
                .ThenByDescending(i => i.Priority)
                .ThenBy(i => i.CharacterName)
                .ThenBy(i => i.Title)
                .ToList();

            var failed = run.Sections.Where(s => s.Error is not null).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Rows.Clear();
                foreach (var i in visible) Rows.Add(new WorklistRowVm(i));

                var snoozed  = run.AllItems.Count(i => i.IsSnoozed);
                var blocked  = unsnoozed.Count(i => i.Readiness == WorklistReadiness.Blocked);
                var waiting  = unsnoozed.Count(i => i.Readiness == WorklistReadiness.Waiting);

                // Hidden counts are always reported, even when nothing is actionable. "Nothing to
                // do" beside nine blocked items would be a lie of omission — there is plenty to
                // do, none of it right now.
                var hidden = new List<string>();
                if (!ShowNotReady && blocked > 0) hidden.Add($"{blocked} blocked");
                if (!ShowNotReady && waiting > 0) hidden.Add($"{waiting} waiting");
                if (!ShowSnoozed  && snoozed > 0) hidden.Add($"{snoozed} snoozed");

                var tail = hidden.Count > 0 ? $" ({string.Join(", ", hidden)} hidden)" : "";

                Status = visible.Count == 0
                    ? $"Nothing ready{tail}."
                    : $"{visible.Count:N0} ready{tail}";

                Errors = failed.Count == 0
                    ? ""
                    : string.Join("  ·  ", failed.Select(f => $"{f.DisplayName}: {f.Error}"));
                this.RaisePropertyChanged(nameof(HasErrors));
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"Error: {ex.Message}");
        }
        finally { IsLoading = false; }
    }
}

/// <summary>
/// One configurable switch on the Sources tab. Writes straight through to preferences and asks
/// the worklist to rebuild, because a setting that needs a separate refresh to take effect reads
/// as a setting that did not save.
/// </summary>
public class WorklistToggleVm : ReactiveObject
{
    private readonly Func<bool, Task> _save;
    private readonly Func<Task>       _refresh;
    private bool _isOn;

    public WorklistToggleVm(string label, string description, bool isOn,
                            Func<bool, Task> save, Func<Task> refresh)
    {
        Label       = label;
        Description = description;
        _isOn       = isOn;
        _save       = save;
        _refresh    = refresh;
    }

    public string Label       { get; }
    public string Description { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            this.RaiseAndSetIfChanged(ref _isOn, value);
            _ = Apply(value);
        }
    }

    private async Task Apply(bool value)
    {
        await _save(value);
        await _refresh();
    }
}
