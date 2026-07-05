using System.Reactive;
using EveCortex.Data;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

public class SdeViewModel : ReactiveObject
{
    private readonly SdeImportService  _sde;
    private readonly HoboImportService _hobo;
    private readonly AppDbContext      _db;

    // ── SDE state ─────────────────────────────────────────────────────────
    private string _statusText      = "SDE not loaded";
    private double _fraction        = 0;
    private bool   _isBusy          = false;
    private string _loadedBuild     = "—";
    private string _latestBuild     = "checking…";
    private bool   _updateAvailable = false;
    private CancellationTokenSource? _cts;

    public string StatusText      { get => _statusText;      private set => this.RaiseAndSetIfChanged(ref _statusText,      value); }
    public double Fraction        { get => _fraction;        private set => this.RaiseAndSetIfChanged(ref _fraction,        value); }
    public bool   IsBusy          { get => _isBusy;          private set => this.RaiseAndSetIfChanged(ref _isBusy,          value); }
    public string LoadedBuild     { get => _loadedBuild;     private set => this.RaiseAndSetIfChanged(ref _loadedBuild,     value); }
    public string LatestBuild     { get => _latestBuild;     private set => this.RaiseAndSetIfChanged(ref _latestBuild,     value); }
    public bool   UpdateAvailable { get => _updateAvailable; private set => this.RaiseAndSetIfChanged(ref _updateAvailable, value); }

    // ── Hoboleaks state ───────────────────────────────────────────────────
    private string _hoboStatusText  = "Not imported";
    private double _hoboFraction    = 0;
    private bool   _hoboIsBusy      = false;
    private string _hoboImportedAt  = "—";
    private CancellationTokenSource? _hoboCts;

    public string HoboStatusText { get => _hoboStatusText; private set => this.RaiseAndSetIfChanged(ref _hoboStatusText, value); }
    public double HoboFraction   { get => _hoboFraction;   private set => this.RaiseAndSetIfChanged(ref _hoboFraction,   value); }
    public bool   HoboIsBusy    { get => _hoboIsBusy;     private set => this.RaiseAndSetIfChanged(ref _hoboIsBusy,     value); }
    public string HoboImportedAt { get => _hoboImportedAt; private set => this.RaiseAndSetIfChanged(ref _hoboImportedAt, value); }

    public ReactiveCommand<Unit, Unit> RefreshSdeCommand  { get; }
    public ReactiveCommand<Unit, Unit> RefreshHoboCommand { get; }

    public SdeViewModel(SdeImportService sde, HoboImportService hobo, AppDbContext db)
    {
        _sde  = sde;
        _hobo = hobo;
        _db   = db;

        var canRunSde  = this.WhenAnyValue(x => x.IsBusy,     busy => !busy);
        var canRunHobo = this.WhenAnyValue(x => x.HoboIsBusy, busy => !busy);

        RefreshSdeCommand = ReactiveCommand.CreateFromTask(RunImportAsync, canRunSde);
        RefreshSdeCommand.ThrownExceptions.Subscribe(ex =>
        {
            IsBusy     = false;
            StatusText = $"Error: {RootMessage(ex)}";
        });

        RefreshHoboCommand = ReactiveCommand.CreateFromTask(RunHoboImportAsync, canRunHobo);
        RefreshHoboCommand.ThrownExceptions.Subscribe(ex =>
        {
            HoboIsBusy     = false;
            HoboStatusText = $"Error: {RootMessage(ex)}";
        });

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _hobo.EnsureSchemaAsync();
            await LoadStoredBuildAsync();
            await LoadHoboInfoAsync();

            var latest = await _sde.GetLatestBuildInfoAsync();
            if (latest is null) { LatestBuild = "unavailable"; return; }

            LatestBuild = FormatBuild(latest.BuildNumber, latest.ReleaseDate);

            var stored = await _db.SdeBuildInfos.FindAsync(1);
            UpdateAvailable = stored is null || stored.BuildNumber != latest.BuildNumber;
        }
        catch (Exception ex)
        {
            LatestBuild = $"error: {ex.Message}";
        }
    }

    private async Task LoadStoredBuildAsync()
    {
        var info = await _db.SdeBuildInfos.FindAsync(1);
        LoadedBuild = info is null
            ? "not imported"
            : FormatBuild(info.BuildNumber, info.ReleaseDate);
    }

    private async Task LoadHoboInfoAsync()
    {
        var info = await _db.HoboBuildInfos.FindAsync(1);
        HoboImportedAt = info is null
            ? "not imported"
            : $"last imported {info.ImportedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        HoboStatusText = HoboImportedAt;
    }

    // ── First-run automatic import ────────────────────────────────────────

    // True once the SDE has been imported at least once.
    public async Task<bool> IsSdeImportedAsync()
        => await _db.SdeBuildInfos.FindAsync(1) is not null;

    // Runs the SDE import followed by the Hoboleaks import, back to back. Used to
    // populate game data automatically the first time the application is launched.
    public async Task RunFirstTimeImportAsync()
    {
        await RunImportAsync();
        await RunHoboImportAsync();
    }

    // ── SDE import ────────────────────────────────────────────────────────

    private async Task RunImportAsync()
    {
        _cts    = new CancellationTokenSource();
        IsBusy  = true;
        Fraction = 0;
        StatusText = "Starting…";

        var progress = new Progress<SdeImportProgress>(rep =>
        {
            StatusText = $"{rep.Stage} — {rep.Detail}";
            if (rep.Fraction >= 0) Fraction = rep.Fraction;
        });

        try
        {
            await Task.Run(async () => await _sde.ImportAsync(progress, _cts.Token), _cts.Token);
            StatusText      = "SDE import complete.";
            Fraction        = 1;
            UpdateAvailable = false;
            await LoadStoredBuildAsync();
        }
        catch (SdeCompatibilityException ex)
        {
            // SDE format changed in a way this version of Eve Cortex can't handle.
            // Existing data is intact — just surface the message and leave the progress bar where it is.
            StatusText = $"⚠ Update required — {ex.Message}";
            Fraction   = 0;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {RootMessage(ex)}";
            Fraction   = 0;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    // ── Hoboleaks import ──────────────────────────────────────────────────

    private async Task RunHoboImportAsync()
    {
        _hoboCts      = new CancellationTokenSource();
        HoboIsBusy    = true;
        HoboFraction  = 0;
        HoboStatusText = "Starting…";

        var progress = new Progress<HoboImportProgress>(rep =>
        {
            HoboStatusText = $"{rep.Stage} — {rep.Detail}";
            if (rep.Fraction >= 0) HoboFraction = rep.Fraction;
        });

        try
        {
            await Task.Run(async () => await _hobo.ImportAsync(progress, _hoboCts.Token), _hoboCts.Token);
            HoboStatusText = "Hoboleaks import complete.";
            HoboFraction   = 1;
            await LoadHoboInfoAsync();
        }
        catch (Exception ex)
        {
            HoboStatusText = $"Error: {RootMessage(ex)}";
        }
        finally
        {
            HoboIsBusy = false;
            _hoboCts.Dispose();
            _hoboCts = null;
        }
    }

    private static string FormatBuild(int build, DateTimeOffset date)
        => $"build {build}  ({date.ToLocalTime():yyyy-MM-dd})";

    private static string RootMessage(Exception ex)
    {
        var e = ex;
        while (e.InnerException != null) e = e.InnerException;
        return e.Message;
    }
}
