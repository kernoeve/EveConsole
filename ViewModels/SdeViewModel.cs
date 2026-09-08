using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Data;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

public class SdeViewModel : ReactiveObject
{
    private readonly SdeImportService  _sde;
    private readonly HoboImportService _hobo;
    /// <summary>
    /// ⚠️ Long-lived: created once for the window and never replaced, so its change tracker
    /// outlives every import. Read through <c>AsNoTracking()</c> only.
    ///
    /// <para>FindAsync answers from the tracker BEFORE it touches the database, so the first read
    /// of SdeBuildInfos pinned the values this screen showed for the life of the process. An
    /// import writes through its own scope and context, which this one never hears about — so the
    /// loaded build and Hoboleaks revision went on reporting whatever was true at startup, and
    /// only a restart appeared to fix it.</para>
    /// </summary>
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

    private string _hoboLatest = "checking…";
    private bool   _hoboUpdateAvailable;
    private long   _loadedHoboRevision;

    public string HoboLatest          { get => _hoboLatest;          private set => this.RaiseAndSetIfChanged(ref _hoboLatest,          value); }
    public bool   HoboUpdateAvailable { get => _hoboUpdateAvailable; private set => this.RaiseAndSetIfChanged(ref _hoboUpdateAvailable, value); }

    /// <summary>
    /// The loaded build, short enough for the title bar: "SDE 2831234".
    ///
    /// <para>The Settings tab shows the release date beside the number, which earns its room
    /// there and does not here — the bar answers "which SDE am I on", not "when was it cut".</para>
    /// </summary>
    private int _loadedBuildNumber;
    public string SdeShortText =>
        _loadedBuildNumber > 0 ? $"SDE {_loadedBuildNumber}" : "SDE not imported";

    /// <summary>
    /// Whether the comparison against CCP's feed actually completed.
    ///
    /// <para>⚠️ Not the same as "no update available", and the difference matters. When the feed
    /// cannot be reached, LatestBuild becomes "unavailable" and UpdateAvailable stays false — so
    /// a failed check and a current SDE look identical from the outside. Saying "up to date" off
    /// that would be claiming something nobody verified, which is what IntelService used to do
    /// when it answered "Intel: up to date" from its catch block. With nothing checked the bar
    /// shows neither state.</para>
    /// </summary>
    private bool _sdeChecked;
    public bool SdeUpToDate => _sdeChecked && !UpdateAvailable && _loadedBuildNumber > 0;

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

            await CheckLatestAsync();
            await CheckHoboLatestAsync();

            // ⚠️ And again, hourly. The title bar's "update available" link is how somebody learns
            // a new SDE exists, and this application is left running for days at a time — a
            // startup-only check would go on saying "up to date" about a build superseded on
            // Tuesday. The app's own update badge re-checks hourly for exactly this reason; this is
            // the same argument about the indicator sitting next to it.
            //
            // Hourly to match that neighbour rather than invent a second cadence. An SDE lands
            // about weekly and the feed is a small JSON, so this asks far more often than it needs
            // to — the right way round for something whose failure is staying quiet.
            Observable.Interval(TimeSpan.FromHours(1))
                .ObserveOnUi("Sde.AutoCheck")
                .Subscribe(tick => { _ = CheckLatestAsync(); _ = CheckHoboLatestAsync(); });
        }
        catch (Exception ex)
        {
            LatestBuild = $"error: {ex.Message}";
        }
    }

    private async Task CheckLatestAsync()
    {
        try
        {
            var latest = await _sde.GetLatestBuildInfoAsync();
            if (latest is null) { LatestBuild = "unavailable"; return; }

            LatestBuild = FormatBuild(latest.BuildNumber, latest.ReleaseDate);

            var stored = await _db.SdeBuildInfos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1);
            UpdateAvailable = stored is null || stored.BuildNumber != latest.BuildNumber;

            // Only here, where a real build number came back and was compared against ours.
            _sdeChecked = true;
            this.RaisePropertyChanged(nameof(SdeUpToDate));
        }
        catch (Exception ex)
        {
            // ⚠️ Leaves _sdeChecked alone. A re-check that could not reach the feed must not undo a
            // comparison that already succeeded, and must not let the bar claim "up to date" about
            // something nobody managed to look at.
            LatestBuild = $"error: {ex.Message.Split('\n')[0]}";
        }
    }

    /// <summary>
    /// Whether Hoboleaks has published anything newer than what was imported.
    /// </summary>
    /// <remarks>
    /// ⚠️ Answers "unknown" rather than "up to date" in the two cases where it cannot tell: the
    /// manifest was unreachable, or the stored data predates the app recording a revision at all.
    /// Same argument as SdeUpToDate above — a failed check and a current import must not look
    /// identical from the outside.
    /// </remarks>
    private async Task CheckHoboLatestAsync()
    {
        try
        {
            var meta = await _hobo.GetLatestMetaAsync();
            if (meta is null || meta.Revision == 0) { HoboLatest = "unavailable"; return; }

            HoboLatest = $"revision {meta.Revision:N0}";

            var stored = await _db.HoboBuildInfos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1);
            HoboUpdateAvailable = stored is null
                || (stored.Revision > 0 && stored.Revision != meta.Revision);
        }
        catch (Exception ex)
        {
            // Leaves HoboUpdateAvailable alone: a re-check that could not reach the manifest must
            // not undo a comparison that already succeeded.
            HoboLatest = $"error: {ex.Message.Split('\n')[0]}";
        }
    }

    private async Task LoadStoredBuildAsync()
    {
        var info = await _db.SdeBuildInfos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1);
        LoadedBuild = info is null
            ? "not imported"
            : FormatBuild(info.BuildNumber, info.ReleaseDate);

        _loadedBuildNumber = info?.BuildNumber ?? 0;
        this.RaisePropertyChanged(nameof(SdeShortText));
    }

    private async Task LoadHoboInfoAsync()
    {
        var info = await _db.HoboBuildInfos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1);

        // ⚠️ Revision 0 means "imported before the app recorded one", not "revision zero". Saying
        // that is better than printing a number nobody wrote.
        HoboImportedAt = info is null
            ? "not imported"
            : info.Revision > 0
                ? $"revision {info.Revision:N0}, imported {info.ImportedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
                : $"last imported {info.ImportedAt.ToLocalTime():yyyy-MM-dd HH:mm} (revision not recorded)";

        _loadedHoboRevision = info?.Revision ?? 0;
        HoboStatusText = HoboImportedAt;
    }

    // ── First-run automatic import ────────────────────────────────────────

    // True once the SDE has been imported at least once.
    public async Task<bool> IsSdeImportedAsync()
        => await _db.SdeBuildInfos.AsNoTracking().AnyAsync(x => x.Id == 1);

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
            var warnings = await Task.Run(async () => await _sde.ImportAsync(progress, _cts.Token), _cts.Token);

            // A clean import says so. One that landed but looks odd says that instead, because
            // "complete" over a table that came back empty is how a silent failure stays silent.
            StatusText      = warnings.Count == 0
                ? "SDE import complete."
                : $"SDE import complete — {warnings.Count} table(s) worth a look, see Errors.";
            Fraction        = 1;
            UpdateAvailable = false;
            await LoadStoredBuildAsync();
        }
        catch (SdeCompatibilityException ex)
        {
            // SDE format changed in a way this version of EVE Console can't handle.
            // Existing data is intact — just surface the message and leave the progress bar where it is.
            StatusText = $"⚠ Update required — {ex.Message}";
            Fraction   = 0;
        }
        catch (ImportVerificationException ex)
        {
            // The archive read cleanly but lost a table, so the import was rolled back. Same
            // shape of outcome as above: nothing was changed, and saying so is the point.
            StatusText = $"⚠ Import rolled back — {ex.Message}";
            Fraction   = 0;
        }
        catch (Exception ex)
        {
            // The import is one transaction, so whatever went wrong, the previous SDE is still
            // there. Before that was true this message left people guessing.
            StatusText = $"Error: {RootMessage(ex)} — your previous SDE data has been kept.";
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
            var warnings = await Task.Run(async () => await _hobo.ImportAsync(progress, _hoboCts.Token), _hoboCts.Token);

            HoboStatusText = warnings.Count == 0
                ? "Hoboleaks import complete."
                : $"Hoboleaks import complete — {warnings.Count} table(s) worth a look, see Errors.";
            HoboFraction   = 1;
            await LoadHoboInfoAsync();
            await CheckHoboLatestAsync();
        }
        catch (HoboCompatibilityException ex)
        {
            // A file this version reads is no longer published. Nothing was cleared.
            HoboStatusText = $"⚠ Update required — {ex.Message}";
        }
        catch (ImportVerificationException ex)
        {
            // Read cleanly but lost a table, so it was rolled back.
            HoboStatusText = $"⚠ Import rolled back — {ex.Message}";
        }
        catch (Exception ex)
        {
            // The import is undoable as one unit, so whatever went wrong the previous data is
            // still there.
            HoboStatusText = $"Error: {RootMessage(ex)} — your previous Hoboleaks data has been kept.";
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
