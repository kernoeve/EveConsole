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
