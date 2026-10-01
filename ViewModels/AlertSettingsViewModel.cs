using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

/// <summary>
/// Which conditions the Overview raises alerts for. Every rule is saved the moment it is changed.
/// </summary>
public class AlertSettingsViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AutoSave                        _autoSave;

    private bool    _skillQueueEmpty       = true;
    private bool    _skillQueuePaused      = true;
    private bool    _skillQueueEmptyInDays = true;
    private decimal _skillQueueEmptyDays   = 30;
    private bool    _assetSafety                = true;
    private bool    _inactiveStandingProjects   = true;
    private bool    _standingBuyOrdersAttention = true;
    private bool    _unriggedIndustryJobs       = true;
    private string  _status                    = "";

    public bool SkillQueueEmpty
    {
        get => _skillQueueEmpty;
        set => this.RaiseAndSetIfChanged(ref _skillQueueEmpty, value);
    }

    public bool SkillQueuePaused
    {
        get => _skillQueuePaused;
        set => this.RaiseAndSetIfChanged(ref _skillQueuePaused, value);
    }

    public bool SkillQueueEmptyInDays
    {
        get => _skillQueueEmptyInDays;
        set => this.RaiseAndSetIfChanged(ref _skillQueueEmptyInDays, value);
    }

    // decimal so Avalonia NumericUpDown binds without a converter
    public decimal SkillQueueEmptyDays
    {
        get => _skillQueueEmptyDays;
        set => this.RaiseAndSetIfChanged(ref _skillQueueEmptyDays, value);
    }

    public bool AssetSafety
    {
        get => _assetSafety;
        set => this.RaiseAndSetIfChanged(ref _assetSafety, value);
    }

    public bool InactiveStandingProjects
    {
        get => _inactiveStandingProjects;
        set => this.RaiseAndSetIfChanged(ref _inactiveStandingProjects, value);
    }

    public bool StandingBuyOrdersAttention
    {
        get => _standingBuyOrdersAttention;
        set => this.RaiseAndSetIfChanged(ref _standingBuyOrdersAttention, value);
    }

    public bool UnriggedIndustryJobs
    {
        get => _unriggedIndustryJobs;
        set => this.RaiseAndSetIfChanged(ref _unriggedIndustryJobs, value);
    }

    private bool _industryJobsReady = true;
    public bool IndustryJobsReady
    {
        get => _industryJobsReady;
        set => this.RaiseAndSetIfChanged(ref _industryJobsReady, value);
    }

    private bool _outstandingContracts = true;
    public bool OutstandingContracts
    {
        get => _outstandingContracts;
        set => this.RaiseAndSetIfChanged(ref _outstandingContracts, value);
    }

    private bool _expiringContracts = true;
    public bool ExpiringContracts
    {
        get => _expiringContracts;
        set => this.RaiseAndSetIfChanged(ref _expiringContracts, value);
    }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <param name="dbFactory">⚠️ A context per load and per save, not one held: the Overview
    /// reloads these while the Settings tab may be saving them, and one context cannot run both.</param>
    public AlertSettingsViewModel(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        _autoSave  = new AutoSave(SaveAsync,
            ex => Status = string.Format(CommonText.ErrorWithMessage, ex.Message));

        // Every rule is a tick box or a number picker, so each change is saved at once.
        Changed.Subscribe(e => { if (e.PropertyName != nameof(Status)) _autoSave.Changed(); });
    }

    /// <summary>Saves a change still waiting — the Settings window, closing.</summary>
    public Task FlushAsync() => _autoSave.FlushAsync();

    public async Task LoadAsync()
    {
        var s = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            return await db.AlertSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1).ConfigureAwait(false);
        });
        if (s is null) return;

        // ⚠️ Not over a change still being saved: the tab is newer than what was just read, and
        // putting the old value back would have the save write the old value too.
        if (_autoSave.IsPending) return;

        using var loading = _autoSave.Suspend();
        SkillQueueEmpty       = s.SkillQueueEmpty;
        SkillQueuePaused      = s.SkillQueuePaused;
        SkillQueueEmptyInDays = s.SkillQueueEmptyInDays;
        SkillQueueEmptyDays   = s.SkillQueueEmptyDays;
        AssetSafety                = s.AssetSafety;
        InactiveStandingProjects   = s.InactiveStandingProjects;
        StandingBuyOrdersAttention = s.StandingBuyOrdersAttention;
        UnriggedIndustryJobs       = s.UnriggedIndustryJobs;
        IndustryJobsReady          = s.IndustryJobsReady;
        OutstandingContracts       = s.OutstandingContracts;
        ExpiringContracts          = s.ExpiringContracts;
    }

    private async Task SaveAsync()
    {
        int days      = (int)Math.Clamp(SkillQueueEmptyDays, 1, 365);
        int empty     = SkillQueueEmpty             ? 1 : 0;
        int paused    = SkillQueuePaused            ? 1 : 0;
        int emptyDay  = SkillQueueEmptyInDays       ? 1 : 0;
        int safety    = AssetSafety                 ? 1 : 0;
        int inactive  = InactiveStandingProjects    ? 1 : 0;
        int buyOrders = StandingBuyOrdersAttention  ? 1 : 0;
        int unrigged  = UnriggedIndustryJobs        ? 1 : 0;
        int ready     = IndustryJobsReady           ? 1 : 0;
        int outstanding = OutstandingContracts      ? 1 : 0;
        int expiring  = ExpiringContracts           ? 1 : 0;

        // Read above, on the UI thread; written off it.
        await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AlertSettings"
                    ("Id","SkillQueueEmpty","SkillQueuePaused","SkillQueueEmptyInDays","SkillQueueEmptyDays","AssetSafety","InactiveStandingProjects","StandingBuyOrdersAttention","UnriggedIndustryJobs","IndustryJobsReady","OutstandingContracts","ExpiringContracts")
                VALUES (1,{empty},{paused},{emptyDay},{days},{safety},{inactive},{buyOrders},{unrigged},{ready},{outstanding},{expiring})
                ON CONFLICT("Id") DO UPDATE SET
                    "SkillQueueEmpty"             = excluded."SkillQueueEmpty",
                    "SkillQueuePaused"            = excluded."SkillQueuePaused",
                    "SkillQueueEmptyInDays"       = excluded."SkillQueueEmptyInDays",
                    "SkillQueueEmptyDays"         = excluded."SkillQueueEmptyDays",
                    "AssetSafety"                 = excluded."AssetSafety",
                    "InactiveStandingProjects"    = excluded."InactiveStandingProjects",
                    "StandingBuyOrdersAttention"  = excluded."StandingBuyOrdersAttention",
                    "UnriggedIndustryJobs"        = excluded."UnriggedIndustryJobs",
                    "IndustryJobsReady"           = excluded."IndustryJobsReady",
                    "OutstandingContracts"        = excluded."OutstandingContracts",
                    "ExpiringContracts"           = excluded."ExpiringContracts"
                """).ConfigureAwait(false);
        });

        _autoSave.Flash(s => Status = s, SettingsText.Saved);
    }
}
