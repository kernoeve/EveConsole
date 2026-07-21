using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using EveConsole.Services;
using ReactiveUI;
using Velopack;
using Velopack.Sources;

namespace EveConsole.ViewModels;

// Auto-update via Velopack against the GitHub releases. Checks on startup and hourly (when the
// auto-check preference is on), lets the user apply the update, and only prompts once per version
// (declining is remembered so we re-prompt only for the next version).
public class UpdateViewModel : ReactiveObject
{
    private const string RepoUrl        = "https://github.com/kernoeve/EveConsole";
    public  const string AutoCheckKey   = "update.auto_check";
    public  const string DeclinedKey    = "update.declined_version";

    private readonly AppPreferencesService _prefs;
    private readonly AppErrorLogger        _errorLogger;
    private readonly UpdateManager         _mgr;
    private UpdateInfo? _pending;

    public UpdateViewModel(AppPreferencesService prefs, AppErrorLogger errorLogger)
    {
        _prefs       = prefs;
        _errorLogger = errorLogger;
        _mgr         = new UpdateManager(new GithubSource(RepoUrl, null, false));

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        CurrentVersionText = ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "unknown";
        _autoCheck = _prefs.Get(AutoCheckKey) != "0";   // default on

        CheckNowCommand     = ReactiveCommand.CreateFromTask(() => CheckAsync(auto: false));
        InstallUpdateCommand = ReactiveCommand.CreateFromTask(InstallAsync);

        // Startup check + hourly re-check.
        _ = CheckAsync(auto: true);
        Observable.Interval(TimeSpan.FromHours(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(tick => { _ = CheckAsync(auto: true); });
    }

    private bool _autoCheck;
    public bool AutoCheck
    {
        get => _autoCheck;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoCheck, value);
            _ = _prefs.SetAsync(AutoCheckKey, value ? "1" : "0");
            if (value) _ = CheckAsync(auto: false);
        }
    }

    public string CurrentVersionText { get; }

    private string _latestVersionText = "—";
    public string LatestVersionText { get => _latestVersionText; private set => this.RaiseAndSetIfChanged(ref _latestVersionText, value); }

    // True when a newer release exists (drives the Settings "Update Now" button).
    private bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; private set => this.RaiseAndSetIfChanged(ref _updateAvailable, value); }

    // True only when we should surface the startup prompt (update available AND not already declined).
    private bool _shouldPrompt;
    public bool ShouldPrompt { get => _shouldPrompt; private set => this.RaiseAndSetIfChanged(ref _shouldPrompt, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> CheckNowCommand      { get; }
    public ReactiveCommand<Unit, Unit> InstallUpdateCommand { get; }

    private async Task CheckAsync(bool auto)
    {
        if (auto && !AutoCheck) return;
        if (!_mgr.IsInstalled)
        {
            LatestVersionText = "n/a — not an installed build";
            return;
        }

        try
        {
            StatusText = "Checking for updates…";
            var info = await _mgr.CheckForUpdatesAsync();
            if (info is null)
            {
                _pending = null;
                UpdateAvailable = false;
                ShouldPrompt = false;
                LatestVersionText = CurrentVersionText;
                StatusText = "Up to date.";
                return;
            }

            _pending = info;
            var latest = "v" + info.TargetFullRelease.Version;
            LatestVersionText = latest;
            UpdateAvailable = true;
            StatusText = "An update is available.";

            // Only nag once per version — respect a previous "Not now".
            var declined = _prefs.Get(DeclinedKey);
            ShouldPrompt = declined != latest;
        }
        catch (Exception ex)
        {
            _errorLogger.Log("UpdateViewModel", "Check", ex);
            StatusText = "Update check failed.";
        }
    }

    // Called when the user declines the startup prompt — remember this version so we don't ask again.
    public void DeclineCurrent()
    {
        ShouldPrompt = false;
        if (_pending is not null)
            _ = _prefs.SetAsync(DeclinedKey, "v" + _pending.TargetFullRelease.Version);
    }

    private async Task InstallAsync()
    {
        if (_pending is null || IsBusy) return;
        try
        {
            IsBusy = true;
            StatusText = "Downloading update…";
            await _mgr.DownloadUpdatesAsync(_pending);
            StatusText = "Restarting to apply…";
            _mgr.ApplyUpdatesAndRestart(_pending);   // exits the process
        }
        catch (Exception ex)
        {
            _errorLogger.Log("UpdateViewModel", "Install", ex);
            StatusText = "Update failed.";
            IsBusy = false;
        }
    }
}
