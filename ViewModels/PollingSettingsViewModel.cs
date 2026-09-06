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
    public bool ServiceSupported => WindowsServiceControl.IsSupported;

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
                TrayStatus = value
                    ? TrayIconController.LaunchNow() is { } error
                        ? $"Registered for next logon, but could not start it now — {error}"
                        : "Running, and will start with Windows."
                    : "Will not start with Windows. Any icon already showing stays until you choose "
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
    /// Whether Windows starts the service by itself at boot.
    ///
    /// <para>⚠️ Setting it asks for administrator approval, because changing a service's
    /// configuration is a right this account is deliberately not granted at install time — it
    /// would allow rewriting what Windows runs as LocalSystem at boot.</para>
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

    /// <summary>Stopped, but Windows will start it again anyway — the case worth saying out loud.</summary>
    public bool ServiceReturnsAtBoot => ServiceInstalled && !ServiceRunning && _startsWithWindows;

    private async Task SetStartsWithWindowsAsync(bool automatic)
    {
        if (!OperatingSystem.IsWindows()) return;

        ServiceBusy   = true;
        ServiceStatus = "Changing startup — approve the Windows prompt…";

        var error = await Task.Run(() => WindowsServiceControl.SetStartsWithWindows(automatic));

        ServiceBusy = false;
        RefreshServiceState();

        if (error is not null && error != "Cancelled.")
            ServiceStatus = $"Could not change startup — {error}";
    }

    /// <summary>Points the existing service at this copy and restarts it.</summary>
    public async Task RepointServiceAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        ServiceBusy   = true;
        ServiceStatus = "Repointing — approve the Windows prompt…";

        var error = await Task.Run(WindowsServiceControl.Repoint);

        ServiceBusy = false;
        RefreshServiceState();

        if (error is not null && error != "Cancelled.")
            ServiceStatus = $"Repoint failed — {error}";
    }

    /// <summary>
    /// Re-reads the service's state.
    ///
    /// <para>⚠️ Asked rather than remembered. The service can be started, stopped or removed from
    /// services.msc, sc.exe or another copy of this window entirely, so a cached answer is one that
    /// goes quietly wrong.</para>
    /// </summary>
    public void RefreshServiceState()
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

    /// <summary>Installs the service. Raises one UAC prompt; start and stop afterwards do not.</summary>
    public async Task InstallServiceAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        ServiceBusy   = true;
        ServiceStatus = "Installing — approve the Windows prompt…";

        var error = await Task.Run(WindowsServiceControl.Install);

        // Started for them: installing it and leaving it stopped is a switch that did half of what
        // it said.
        if (error is null) await Task.Run(() => WindowsServiceControl.StartService());

        ServiceBusy = false;
        RefreshServiceState();

        if (error is not null)
            ServiceStatus = error == "Cancelled." ? "Not installed" : $"Install failed — {error}";
    }

    public async Task UninstallServiceAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        ServiceBusy   = true;
        ServiceStatus = "Removing — approve the Windows prompt…";

        var error = await Task.Run(WindowsServiceControl.Uninstall);

        ServiceBusy = false;
        RefreshServiceState();

        if (error is not null && error != "Cancelled.")
            ServiceStatus = $"Remove failed — {error}";
    }

    public async Task SetServiceRunningAsync(bool run)
    {
        if (!OperatingSystem.IsWindows()) return;

        ServiceBusy   = true;
        ServiceStatus = run ? "Starting…" : "Stopping…";

        var error = await Task.Run(() => run
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
