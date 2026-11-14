using System.Collections.ObjectModel;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A choice in "Standings from": one of the user's characters, or 0 for each client's own.</summary>
public sealed record IntelStandingsChoice(long CharacterId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Settings → Intel: how long each kind of news lasts, the follow-up window, what makes a gate camp,
/// whose standings colour the pilots, whether travel distances use our jump bridges, and the intel
/// alarms' two shared numbers.
///
/// <para>Every value is shared in the database and written as it is changed. The snapshot reads them
/// again at once on this PC, and within a minute on the others.</para>
/// </summary>
public sealed class IntelSettingsViewModel : ReactiveObject
{
    private readonly IntelSettings _settings;
    private readonly IntelSnapshotService _snapshots;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private bool _loading;

    public IntelSettingsViewModel(IntelSettings settings, IntelSnapshotService snapshots, IDbContextFactory<AppDbContext> dbFactory)
    {
        _settings  = settings;
        _snapshots = snapshots;
        _dbFactory = dbFactory;
        StandingsChoices.Add(new IntelStandingsChoice(0, IntelText.SettingsStandingsEachClient));
        Apply(settings.Cached);
    }

    public int MinLifetime => IntelSettings.MinLifetimeMinutes;
    public int MaxLifetime => IntelSettings.MaxLifetimeMinutes;
    public int MinFollowUp => IntelThreads.MinWindowSeconds;
    public int MaxFollowUp => IntelThreads.MaxWindowSeconds;
    public double MinGateKm => IntelSettings.MinCampGateKm;
    public double MaxGateKm => IntelSettings.MaxCampGateKm;
    public int MinCampWindow => IntelSettings.MinCampWindowMinutes;
    public int MaxCampWindow => IntelSettings.MaxCampWindowMinutes;
    public int MinCampVictims => IntelSettings.MinCampMinVictims;
    public int MaxCampVictims => IntelSettings.MaxCampMinVictims;
    public int MaxWakeCooldown => IntelSettings.MaxWakeCooldownSeconds;
    public int MinFresh => IntelSettings.MinFreshSeconds;
    public int MaxFresh => IntelSettings.MaxFreshSeconds;

    /// <summary>The values and the character list as the database holds them now. Read off the UI
    /// thread; called as the Settings window opens.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var (values, chars) = await Task.Run(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var v = await IntelSettings.ReadAsync(db);
                var c = await db.Characters.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync();
                return (v, c);
            });
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                while (StandingsChoices.Count > 1) StandingsChoices.RemoveAt(1);
                foreach (var c in chars.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                    StandingsChoices.Add(new IntelStandingsChoice(c.Id, c.Name));
                Apply(values);
            });
        }
        catch (Exception ex)
        {
            Status = string.Format(IntelText.SettingsSaveFailed, ex.Message);
        }
    }

    private void Apply(IntelSettingsValues v)
    {
        _loading = true;
        try
        {
            NamedMinutes        = (int)v.Named.TotalMinutes;
            UnnamedMinutes      = (int)v.Unnamed.TotalMinutes;
            ClearMinutes        = (int)v.Clear.TotalMinutes;
            CampMinutes         = (int)v.Camp.TotalMinutes;
            CynoMinutes         = (int)v.Cyno.TotalMinutes;
            KillAttackerMinutes = (int)v.KillAttacker.TotalMinutes;
            KillVictimMinutes   = (int)v.KillVictim.TotalMinutes;
            FollowUpSeconds     = (int)v.FollowUpWindow.TotalSeconds;
            CampGateKm          = v.CampGateKm;
            CampWindowMinutes   = (int)v.CampWindow.TotalMinutes;
            CampMinVictims      = v.CampMinVictims;
            UseBridges          = v.UseBridges;
            WakeCooldownSeconds = v.WakeCooldownSeconds;
            FreshSeconds        = v.FreshSeconds;
            SelectedStandings   = StandingsChoices.FirstOrDefault(c => c.CharacterId == v.StandingsCharacterId)
                                  ?? StandingsChoices[0];
        }
        finally { _loading = false; }
    }

    private void Save(Func<Task> write)
    {
        if (_loading) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await write();
                _snapshots.SettingsChanged();
                Dispatcher.UIThread.Post(() => Status = "");
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => Status = string.Format(IntelText.SettingsSaveFailed, ex.Message));
            }
        });
    }

    private int Minutes(ref int field, int value, string key, string name)
    {
        var clamped = Math.Clamp(value, IntelSettings.MinLifetimeMinutes, IntelSettings.MaxLifetimeMinutes);
        if (field == clamped) return clamped;
        field = clamped;
        this.RaisePropertyChanged(name);
        Save(() => _settings.SetMinutesAsync(key, clamped));
        return clamped;
    }

    // ── Lifetimes ────────────────────────────────────────────────────────────

    private int _named, _unnamed, _clear, _camp, _cyno, _killAttacker, _killVictim;

    public int NamedMinutes        { get => _named;        set => Minutes(ref _named,        value, IntelSettings.KeyNamed,        nameof(NamedMinutes)); }
    public int UnnamedMinutes      { get => _unnamed;      set => Minutes(ref _unnamed,      value, IntelSettings.KeyUnnamed,      nameof(UnnamedMinutes)); }
    public int ClearMinutes        { get => _clear;        set => Minutes(ref _clear,        value, IntelSettings.KeyClear,        nameof(ClearMinutes)); }
    public int CampMinutes         { get => _camp;         set => Minutes(ref _camp,         value, IntelSettings.KeyCamp,         nameof(CampMinutes)); }
    public int CynoMinutes         { get => _cyno;         set => Minutes(ref _cyno,         value, IntelSettings.KeyCyno,         nameof(CynoMinutes)); }
    public int KillAttackerMinutes { get => _killAttacker; set => Minutes(ref _killAttacker, value, IntelSettings.KeyKillAttacker, nameof(KillAttackerMinutes)); }
    public int KillVictimMinutes   { get => _killVictim;   set => Minutes(ref _killVictim,   value, IntelSettings.KeyKillVictim,   nameof(KillVictimMinutes)); }

    // ── Follow-ups and camps ─────────────────────────────────────────────────

    private int _followUp;
    public int FollowUpSeconds
    {
        get => _followUp;
        set
        {
            var v = Math.Clamp(value, IntelThreads.MinWindowSeconds, IntelThreads.MaxWindowSeconds);
            if (_followUp == v) return;
            this.RaiseAndSetIfChanged(ref _followUp, v);
            Save(() => _settings.SetFollowUpSecondsAsync(v));
        }
    }

    private double _gateKm;
    public double CampGateKm
    {
        get => _gateKm;
        set
        {
            var v = Math.Clamp(value, IntelSettings.MinCampGateKm, IntelSettings.MaxCampGateKm);
            if (_gateKm.Equals(v)) return;
            this.RaiseAndSetIfChanged(ref _gateKm, v);
            Save(() => _settings.SetCampGateKmAsync(v));
        }
    }

    private int _campWindow;
    public int CampWindowMinutes
    {
        get => _campWindow;
        set
        {
            var v = Math.Clamp(value, IntelSettings.MinCampWindowMinutes, IntelSettings.MaxCampWindowMinutes);
            if (_campWindow == v) return;
            this.RaiseAndSetIfChanged(ref _campWindow, v);
            Save(() => _settings.SetCampWindowMinutesAsync(v));
        }
    }

    private int _campVictims;
    public int CampMinVictims
    {
        get => _campVictims;
        set
        {
            var v = Math.Clamp(value, IntelSettings.MinCampMinVictims, IntelSettings.MaxCampMinVictims);
            if (_campVictims == v) return;
            this.RaiseAndSetIfChanged(ref _campVictims, v);
            Save(() => _settings.SetCampMinVictimsAsync(v));
        }
    }

    // ── Standings and distance ───────────────────────────────────────────────

    public ObservableCollection<IntelStandingsChoice> StandingsChoices { get; } = [];

    private IntelStandingsChoice? _standings;
    public IntelStandingsChoice? SelectedStandings
    {
        get => _standings;
        set
        {
            if (value is null || Equals(_standings, value)) return;
            this.RaiseAndSetIfChanged(ref _standings, value);
            Save(() => _settings.SetStandingsCharacterAsync(value.CharacterId));
        }
    }

    private bool _bridges;
    public bool UseBridges
    {
        get => _bridges;
        set
        {
            if (_bridges == value) return;
            this.RaiseAndSetIfChanged(ref _bridges, value);
            Save(() => _settings.SetUseBridgesAsync(value));
        }
    }

    // ── Alerts ───────────────────────────────────────────────────────────────

    private int _wakeCooldown;
    public int WakeCooldownSeconds
    {
        get => _wakeCooldown;
        set
        {
            var v = Math.Clamp(value, 0, IntelSettings.MaxWakeCooldownSeconds);
            if (_wakeCooldown == v) return;
            this.RaiseAndSetIfChanged(ref _wakeCooldown, v);
            Save(() => _settings.SetWakeCooldownSecondsAsync(v));
        }
    }

    private int _fresh;
    public int FreshSeconds
    {
        get => _fresh;
        set
        {
            var v = Math.Clamp(value, IntelSettings.MinFreshSeconds, IntelSettings.MaxFreshSeconds);
            if (_fresh == v) return;
            this.RaiseAndSetIfChanged(ref _fresh, v);
            Save(() => _settings.SetFreshSecondsAsync(v));
        }
    }

    // ── The kill-feed warning ────────────────────────────────────────────────

    /// <summary>Shows the Intel tool's kill-feed warning again, on every PC.</summary>
    public async Task ShowFeedWarningAgainAsync()
    {
        try
        {
            await Task.Run(_snapshots.ResetFeedWarningAsync);
            Status = IntelText.SettingsWarningShownAgain;
        }
        catch (Exception ex)
        {
            Status = string.Format(IntelText.SettingsSaveFailed, ex.Message);
        }
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }
}
