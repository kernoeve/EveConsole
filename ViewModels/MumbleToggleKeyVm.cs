using System.Reactive;
using EveConsole.Localization;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>Something on the Servers tab a key is chosen for by pressing it: a channel target, mute, deafen or the selection.</summary>
public interface IMumbleKeySlot
{
    int Key { get; set; }

    /// <summary>Waiting for the next key or button pressed.</summary>
    bool Capturing { get; set; }

    /// <summary>What is wrong with the key: blank when nothing is.</summary>
    string KeyProblem { get; set; }

    /// <summary>The key pressed was refused, rather than the key kept being one another place has too.</summary>
    bool KeyRefused { get; set; }
}

/// <summary>One of the server's keys that isn't a channel target: mute and deafen, which switch, and the selection key, held.</summary>
public sealed class MumbleToggleKeyVm : ReactiveObject, IMumbleKeySlot
{
    private int _key;
    private bool _capturing;
    private string _keyProblem = "";
    private bool _keyRefused;

    public MumbleToggleKeyVm(MumbleViewModel owner)
    {
        CaptureKeyCommand = ReactiveCommand.Create(() => owner.CaptureKeyFor(this));
        ClearKeyCommand   = ReactiveCommand.Create(() => { owner.CancelKeyCapture(); Key = 0; owner.KeyCleared(); });
    }

    public ReactiveCommand<Unit, Unit> CaptureKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearKeyCommand { get; }

    public int Key
    {
        get => _key;
        set { this.RaiseAndSetIfChanged(ref _key, value); this.RaisePropertyChanged(nameof(KeyText)); }
    }

    public bool Capturing
    {
        get => _capturing;
        set { this.RaiseAndSetIfChanged(ref _capturing, value); this.RaisePropertyChanged(nameof(KeyText)); }
    }

    public string KeyText => Capturing ? MumbleText.KeyPressNow : Key == 0 ? MumbleText.KeyNone : GlobalHotkeyService.VkName(Key);

    public string KeyProblem
    {
        get => _keyProblem;
        set { this.RaiseAndSetIfChanged(ref _keyProblem, value); this.RaisePropertyChanged(nameof(HasKeyProblem)); }
    }

    public bool HasKeyProblem => _keyProblem.Length > 0;

    public bool KeyRefused
    {
        get => _keyRefused;
        set => this.RaiseAndSetIfChanged(ref _keyRefused, value);
    }
}
