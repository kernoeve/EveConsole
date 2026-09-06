using System.Collections.ObjectModel;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

public class CharacterOption(long id, string name)
{
    public long   Id   { get; } = id;
    public string Name { get; } = name;

    public override string ToString() => Name;
}

public class PollingSettingsViewModel : ReactiveObject
{

    private readonly AppPreferencesService _prefs;
    private bool _loading;

    // ── Running the background work as a Windows service ──────────────────────

    /// <summary>Hides the whole section anywhere it could not mean anything.</summary>
    public bool ServiceSupported => WindowsServiceControl.IsSupported || SystemdServiceControl.IsSupported;

    /// <summary>
    /// ⚠️ The two platforms differ in more than wording. Windows installs a machine-wide service
    /// running as LocalSystem, which needs elevation and a separate copy of the database
    /// credential; Linux installs a systemd USER unit, which needs neither — it runs as the user,
    /// inside their session, and reads the same settings this window does. So the Windows-only
    /// controls are gated on this rather than shown everywhere and failing.
    /// </summary>
    public bool ServiceIsWindows => WindowsServiceControl.IsSupported;
    public bool ServiceIsSystemd => !WindowsServiceControl.IsSupported && SystemdServiceControl.IsSupported;

    // ── Wording ───────────────────────────────────────────────────────────────
    //
    // Kept here rather than duplicated as two hidden blocks of XAML. The differences are real —
    // when it starts, what it costs, what it can reach — and a settings page that says "starts
    // automatically with Windows" on a machine that will not start it until the user logs in is
    // worse than one that says nothing.

    public string ServiceSectionTitle => ServiceIsSystemd
        ? "Background Service (systemd)"
        : "Background Service (Windows)";

    public string ServiceIntro => ServiceIsSystemd
        ? "Runs the background processing — ESI polling, pricing, contracts, alarms, the scheduler and backups — without EVE Console being open. Exactly one client does this work: if this app is open and already doing it, the unit waits and takes over when you close it."
        : "Runs the background processing — ESI polling, pricing, contracts, alarms, the scheduler and backups — without EVE Console being open, starting automatically with Windows. Exactly one client does this work: if this app is open and already doing it, the service waits and takes over when you close it.";

    /// <summary>
    /// ⚠️ Said plainly, because it is the one thing about the Linux unit somebody could get wrong
    /// without noticing. It is a systemd USER unit, which is what lets it reach the keyring for the
    /// saved database password without an environment variable holding a credential — and the price
    /// of that is that it starts when you log in, not when the machine boots.
    /// </summary>
    public string ServiceInstallNote => ServiceIsSystemd
        ? "Installed as a systemd user unit in ~/.config/systemd/user, so no root access and no password prompt. It runs as you, inside your login session, which is how it reaches your keyring for the saved database password — and why it starts when you log in rather than when the machine boots. For a machine that boots unattended, see the system unit in the project's packaging directory."
        : "Installing and removing ask for administrator approval once; starting and stopping afterwards do not. The service runs as LocalSystem, so a copy of the database connection is written to C:\\ProgramData\\EveConsole for it to read — that copy can be decrypted by anything running on this computer, unlike the one saved for your account. Removing the service deletes it.";

    public string ServiceUpdateNote => ServiceIsSystemd
        ? "Updating rewrites the unit to run this copy and restarts it."
        : "Updating rewrites both what it runs and what it connects to, then restarts it. Changing the database here cannot do that on its own — writing the service's copy needs administrator approval.";

    public string ServiceInstallButtonText => ServiceIsSystemd ? "Install unit"   : "Install service";
    public string ServiceRemoveButtonText  => ServiceIsSystemd ? "Remove unit"    : "Remove service";
    public string ServiceRepointButtonText => ServiceIsSystemd ? "Update unit"    : "Update service";

    /// <summary>"Runs:" needs no explaining on Windows; on Linux it is the line that matters most.</summary>
    public string ServiceRunsLabel => ServiceIsSystemd ? "ExecStart:" : "Runs:";

    public string TrayCheckboxText => OperatingSystem.IsWindows()
        ? "Show a notification-area icon, starting with Windows"
        : "Show a notification-area icon, starting with your desktop session";

    private bool _serviceInstalled;
    public bool ServiceInstalled
    {
        get => _serviceInstalled;
        private set => this.RaiseAndSetIfChanged(ref _serviceInstalled, value);
    }

    private bool _serviceRunning;
    public bool ServiceRunning
    {
        get => _serviceRunning;
        private set => this.RaiseAndSetIfChanged(ref _serviceRunning, value);
    }

    private string _serviceStatus = "";
    public string ServiceStatus
    {
        get => _serviceStatus;
        private set => this.RaiseAndSetIfChanged(ref _serviceStatus, value);
    }

    private bool _serviceBusy;
    public bool ServiceBusy
    {
        get => _serviceBusy;
        private set { this.RaiseAndSetIfChanged(ref _serviceBusy, value); this.RaisePropertyChanged(nameof(ServiceIdle)); }
    }

    public bool ServiceIdle => !_serviceBusy;

    private string _serviceExePath = "";
    /// <summary>The executable the service control manager will launch.</summary>
    public string ServiceExePath
    {
        get => _serviceExePath;
        private set => this.RaiseAndSetIfChanged(ref _serviceExePath, value);
    }

    private bool _serviceIsThisCopy;
    public bool ServiceIsThisCopy
    {
        get => _serviceIsThisCopy;
        private set => this.RaiseAndSetIfChanged(ref _serviceIsThisCopy, value);
    }

    private bool _serviceOtherCopy;
    /// <summary>Installed, but running a different copy of EVE Console than this one.</summary>
    public bool ServiceOtherCopy
    {
        get => _serviceOtherCopy;
        private set => this.RaiseAndSetIfChanged(ref _serviceOtherCopy, value);
    }

    private string _serviceDatabase = "";
    /// <summary>Which database the service will connect to. Host and name only.</summary>
    public string ServiceDatabase
    {
        get => _serviceDatabase;
        private set => this.RaiseAndSetIfChanged(ref _serviceDatabase, value);
    }

    private bool _serviceOtherDatabase;
    /// <summary>Installed, but connecting somewhere other than this client does.</summary>
    public bool ServiceOtherDatabase
    {
        get => _serviceOtherDatabase;
        private set => this.RaiseAndSetIfChanged(ref _serviceOtherDatabase, value);
    }

    private bool _serviceNeedsUpdate;
    /// <summary>Either mismatch. One button fixes both, because one action rewrites both.</summary>
    public bool ServiceNeedsUpdate
    {
        get => _serviceNeedsUpdate;
        private set => this.RaiseAndSetIfChanged(ref _serviceNeedsUpdate, value);
    }

    /// <summary>
    /// What is out of step, in the order somebody would want to read it.
    ///
    /// <para>⚠️ The database first when both are wrong. A service running the wrong executable is
    /// confusing; a service polling the wrong database is doing real work in a place nobody is
    /// watching, and is the reason to act now rather than later.</para>
    /// </summary>
    public string ServiceUpdateReason =>
        _serviceOtherDatabase && _serviceOtherCopy
            ? "This service is connected to a different database than this client, and it runs a different copy of EVE Console. It is doing background work somewhere you are not looking."
      : _serviceOtherDatabase
            ? "This service is connected to a different database than this client. Your database settings changed after it was installed; it is still working against the old one."
      : ServiceIsSystemd
            ? "This unit starts a different copy of EVE Console, not the one you are using now — the file was moved, renamed, or replaced by a newer download. If that copy is an older version it will still run, and every client will then fail its version check against the database."
      :       "This service runs a different copy of EVE Console, not the one you are using now. It is doing the background work with that copy's settings.";

    // ── The notification-area icon ────────────────────────────────────────────

    private bool _trayAtLogon = TrayIconController.StartsAtLogon();

    /// <summary>
    /// Whether a tray icon starts with this user's session.
    ///
    /// <para>⚠️ A separate process, not part of this window. The client doing the background work
    /// may be a service, which cannot draw anything at all — and putting the icon in this
    /// application would mean it disappeared exactly when somebody wanted it, which is while the
    /// application is closed.</para>
    /// </summary>
    public bool TrayAtLogon
    {
        get => _trayAtLogon;
        set
        {
            if (_trayAtLogon == value) return;
            this.RaiseAndSetIfChanged(ref _trayAtLogon, value);

            try
            {
                TrayIconController.SetStartsAtLogon(value);

                // Ticking a box should do something today, not next time they log in.
                var session = OperatingSystem.IsWindows() ? "Windows" : "your desktop session";

                TrayStatus = value
                    ? TrayIconController.LaunchNow() is { } error
                        ? $"Registered for next logon, but could not start it now — {error}"
                        : $"Running, and will start with {session}."
                    : $"Will not start with {session}. Any icon already showing stays until you choose "
                    + "Exit on it, or log off.";
            }
            catch (Exception ex) { TrayStatus = $"Could not change it — {ex.Message.Split('\n')[0]}"; }
        }
    }

    private string _trayStatus = "";
    public string TrayStatus
    {
        get => _trayStatus;
        private set => this.RaiseAndSetIfChanged(ref _trayStatus, value);
    }

    private bool _startsWithWindows;
    /// <summary>
    /// Whether the system starts the service by itself — at boot on Windows, at login under systemd.
    ///
    /// <para>⚠️ On Windows, setting it asks for administrator approval, because changing a service's
    /// configuration is a right this account is deliberately not granted at install time — it
    /// would allow rewriting what Windows runs as LocalSystem at boot. Under systemd there is
    /// nothing to approve: the unit is the user's own file.</para>
    /// </summary>
    public bool StartsWithWindows
    {
        get => _startsWithWindows;
        set
        {
            if (_startsWithWindows == value) return;
            _ = SetStartsWithWindowsAsync(value);
        }
    }

    /// <summary>Stopped, but it will be started again anyway — the case worth saying out loud.</summary>
    public bool ServiceReturnsAtBoot => ServiceInstalled && !ServiceRunning && _startsWithWindows;

    private async Task SetStartsWithWindowsAsync(bool automatic)
    {
        ServiceBusy = true;

        string? error;

        if (OperatingSystem.IsLinux() && SystemdServiceControl.IsSupported)
        {
            ServiceStatus = "Changing startup…";
            error = await Task.Run(() => SystemdServiceControl.SetStartsAtLogin(automatic));
        }
        else if (OperatingSystem.IsWindows())
        {
            ServiceStatus = "Changing startup — approve the Windows prompt…";
            error = await Task.Run(() => WindowsServiceControl.SetStartsWithWindows(automatic));
        }
        else { ServiceBusy = false; return; }

        ServiceBusy = false;
        RefreshServiceState();

        if (error is not null && error != "Cancelled.")
            ServiceStatus = $"Could not change startup — {error}";
    }

    /// <summary>Points the existing service at this copy and restarts it.</summary>
    public async Task RepointServiceAsync()
    {
        ServiceBusy = true;

        string? error;

        if (OperatingSystem.IsLinux() && SystemdServiceControl.IsSupported)
        {
            ServiceStatus = "Repointing…";
            error = await Task.Run(SystemdServiceControl.Repoint);
        }
        else if (OperatingSystem.IsWindows())
        {
            ServiceStatus = "Repointing — approve the Windows prompt…";
            error = await Task.Run(WindowsServiceControl.Repoint);
        }
        else { ServiceBusy = false; return; }

        ServiceBusy = false;
        RefreshServiceState();

        if (error is not null && error != "Cancelled.")
            ServiceStatus = $"Repoint failed — {error}";
    }

    /// <summary>
    /// Re-reads the service's state, on whichever platform this is.
    ///
    /// <para>⚠️ Asked rather than remembered. The service can be started, stopped or removed from
    /// services.msc, systemctl or another copy of this window entirely, so a cached answer is one
    /// that goes quietly wrong.</para>
    /// </summary>
    public void RefreshServiceState()
    {
        if (OperatingSystem.IsLinux() && SystemdServiceControl.IsSupported) { RefreshSystemd(); return; }
        if (OperatingSystem.IsWindows()) RefreshWindows();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void RefreshSystemd()
    {
        var installed = SystemdServiceControl.IsInstalled();

        ServiceInstalled = installed;
        ServiceRunning   = installed && SystemdServiceControl.IsRunning();

        // ⚠️ No database drift to check for, unlike Windows. A user unit runs as this user and
        // reads this same config file, so it cannot be pointed somewhere else — there is no
        // second copy of the connection to fall out of step.
        ServiceDatabase      = "";
        ServiceOtherDatabase = false;

        // ⚠️ The path in the unit, not the unit's own path, and compared rather than assumed. Both
        // Linux builds can drift out from under it: a tarball that gets moved leaves the unit
        // pointing at nothing, and an AppImage downloaded again under a new version-stamped
        // filename leaves it pointing at the OLD file — which still exists and still starts, so the
        // worker quietly comes up on the previous version and every client then fails its version
        // check against a database nobody has knowingly touched.
        ServiceExePath     = installed ? SystemdServiceControl.InstalledExePath() ?? "unknown" : "";
        ServiceIsThisCopy  = installed && SystemdServiceControl.PointsAtThisCopy();
        ServiceOtherCopy   = installed && !ServiceIsThisCopy;
        ServiceNeedsUpdate = ServiceOtherCopy;

        _startsWithWindows = installed && SystemdServiceControl.StartsAtLogin();
        this.RaisePropertyChanged(nameof(StartsWithWindows));
        this.RaisePropertyChanged(nameof(ServiceReturnsAtBoot));
        this.RaisePropertyChanged(nameof(ServiceUpdateReason));

        ServiceStatus = !installed         ? "Not installed"
                      : ServiceRunning      ? "Running"
                      : _startsWithWindows  ? "Installed, stopped — will start at your next login"
                      :                       "Installed, stopped";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void RefreshWindows()

    {
        if (!OperatingSystem.IsWindows()) return;

        var status = WindowsServiceControl.Status();

        ServiceInstalled = status is not null;
        ServiceRunning   = status == System.ServiceProcess.ServiceControllerStatus.Running;

        // ⚠️ Which executable, not merely whether one is installed. A service installed from a
        // development build keeps running that build after a release copy is installed beside it,
        // and the release copy would otherwise report the background work as its own. It is not:
        // different executable, and — since the machine config is one file per computer — quite
        // possibly a different database.
        ServiceExePath      = status is null ? "" : WindowsServiceControl.InstalledExePath() ?? "unknown";
        ServiceIsThisCopy   = status is not null && WindowsServiceControl.PointsAtThisCopy();
        ServiceOtherCopy    = status is not null && !ServiceIsThisCopy;

        // ⚠️ And which DATABASE, which is the one that goes wrong quietly. The service reads a copy
        // of the connection taken when it was installed; changing the database here does not
        // rewrite it, because that file needs elevation to touch. So a client repointed at a new
        // server leaves the service polling the old one — background work continuing against a
        // database nobody is looking at, while the new one has no worker at all.
        // ⚠️ Whether Windows brings it back by itself. Stopping does not change this, so a service
        // stopped from here returns at the next reboot — which is a surprise unless it is said,
        // and cannot be changed by Stop because reconfiguring a service needs elevation.
        _startsWithWindows  = status is not null && WindowsServiceControl.StartsWithWindows() == true;
        this.RaisePropertyChanged(nameof(StartsWithWindows));
        this.RaisePropertyChanged(nameof(ServiceReturnsAtBoot));

        ServiceDatabase       = status is null ? "" : MachineConfig.DescribeConnection() ?? "not configured";
        ServiceOtherDatabase  = status is not null && !MachineConfig.MatchesConnection(AppConfig.GetPostgresConnection());
        ServiceNeedsUpdate    = ServiceOtherCopy || ServiceOtherDatabase;
        this.RaisePropertyChanged(nameof(ServiceUpdateReason));
        ServiceStatus    = status switch
        {
            null                                                  => "Not installed",
            System.ServiceProcess.ServiceControllerStatus.Running      => "Running",
            System.ServiceProcess.ServiceControllerStatus.Stopped      => "Installed, stopped",
            System.ServiceProcess.ServiceControllerStatus.StartPending => "Starting…",
            System.ServiceProcess.ServiceControllerStatus.StopPending  => "Stopping…",
            _                                                     => status.ToString()!,
        };
    }

    /// <summary>
    /// Installs the worker as a service, and starts it.
    ///
    /// <para>⚠️ Started as part of installing, on both platforms. A button that installs a service
    /// and leaves it stopped has done half of what it said.</para>
    ///
    /// <para>Windows raises one elevation prompt here; Linux raises none at all, because a systemd
    /// user unit lives in the user's own configuration.</para>
    /// </summary>
    public async Task InstallServiceAsync()
    {
        ServiceBusy   = true;
        ServiceStatus = ServiceIsSystemd ? "Installing…" : "Installing — approve the Windows prompt…";

        string? error;

        if (OperatingSystem.IsLinux() && SystemdServiceControl.IsSupported)
        {
            // `systemctl enable --now` installs and starts in one step, so there is nothing to
            // start afterwards.
            error = await Task.Run(SystemdServiceControl.Install);
        }
        else if (OperatingSystem.IsWindows())
        {
            error = await Task.Run(WindowsServiceControl.Install);
            if (error is null) await Task.Run(() => WindowsServiceControl.StartService());
        }
        else { ServiceBusy = false; return; }

        ServiceBusy = false;
        RefreshServiceState();

        if (error is not null)
            ServiceStatus = error == "Cancelled." ? "Not installed" : $"Install failed — {error}";
    }

    public async Task UninstallServiceAsync()
    {
        ServiceBusy   = true;
        ServiceStatus = ServiceIsSystemd ? "Removing…" : "Removing — approve the Windows prompt…";

        string? error;

        if (OperatingSystem.IsLinux() && SystemdServiceControl.IsSupported)
            error = await Task.Run(SystemdServiceControl.Uninstall);
        else if (OperatingSystem.IsWindows())
            error = await Task.Run(WindowsServiceControl.Uninstall);
        else { ServiceBusy = false; return; }

        ServiceBusy = false;
        RefreshServiceState();

        if (error is not null && error != "Cancelled.")
            ServiceStatus = $"Remove failed — {error}";
    }

    public async Task SetServiceRunningAsync(bool run)
    {
        ServiceBusy   = true;
        ServiceStatus = run ? "Starting…" : "Stopping…";

        string? error;

        if (OperatingSystem.IsLinux() && SystemdServiceControl.IsSupported)
        {
            error = await Task.Run(() => run ? SystemdServiceControl.Start() : SystemdServiceControl.Stop());

            ServiceBusy = false;
            RefreshServiceState();

            // ⚠️ The journal, not just the exit status. systemctl reports that starting failed and
            // says nothing about why; the reason is always one command away and never in front of
            // the person who needs it.
            if (error is not null)
                ServiceStatus = $"{(run ? "Start" : "Stop")} failed — {error}\n\n{SystemdServiceControl.RecentLog()}";

            return;
        }

        if (!OperatingSystem.IsWindows()) { ServiceBusy = false; return; }

        error = await Task.Run(() => run
            ? WindowsServiceControl.StartService()
            : WindowsServiceControl.StopService());

        ServiceBusy = false;
        RefreshServiceState();

        // ⚠️ "Access denied" here means the start/stop grant did not take during installation. That
        // is survivable and has a specific remedy, so it gets said rather than being folded into a
        // generic failure somebody would read as a broken service.
        if (error is not null)
            ServiceStatus = error.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? "Windows refused — this account was not granted permission to start or stop it. Remove and re-add the service."
                : $"{(run ? "Start" : "Stop")} failed — {error}";
    }

    public ObservableCollection<CharacterOption> StructureNameChars { get; } = [];

    private CharacterOption? _selectedStructureNameChar;
    public CharacterOption? SelectedStructureNameChar
    {
        get => _selectedStructureNameChar;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedStructureNameChar, value);
            if (!_loading) _ = SaveStructureNameCharAsync();
        }
    }

    public PollingSettingsViewModel(AppPreferencesService prefs)
    {
        _prefs = prefs;
    }

    public Task LoadAsync(IEnumerable<Character> characters)
    {
        _loading = true;
        try
        {
            StructureNameChars.Clear();
            StructureNameChars.Add(new CharacterOption(0, "(none — try all)"));
            foreach (var ch in characters)
                StructureNameChars.Add(new CharacterOption(ch.Id, ch.Name));

            var savedId = _prefs.GetLong(AppPreferencesService.StructureNameCharKey, 0);
            _selectedStructureNameChar = StructureNameChars.FirstOrDefault(c => c.Id == savedId)
                                         ?? StructureNameChars[0];
            this.RaisePropertyChanged(nameof(SelectedStructureNameChar));
        }
        finally
        {
            _loading = false;
        }
        return Task.CompletedTask;
    }

    private Task SaveStructureNameCharAsync()
    {
        var charId = _selectedStructureNameChar?.Id ?? 0;
        return _prefs.SetLongAsync(AppPreferencesService.StructureNameCharKey, charId == 0 ? null : charId);
    }
}
