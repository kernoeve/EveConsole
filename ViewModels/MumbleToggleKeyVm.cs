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
}

/// <summary>One of the server's keys that isn't a channel target: mute and deafen, which switch, and the selection key, held.</summary>
public sealed class MumbleToggleKeyVm : ReactiveObject, IMumbleKeySlot
{
    private int _key;
    private bool _capturing;

    public MumbleToggleKeyVm(MumbleViewModel owner)
    {
        CaptureKeyCommand = ReactiveCommand.Create(() => owner.CaptureKeyFor(this));
        ClearKeyCommand   = ReactiveCommand.Create(() => { owner.CancelKeyCapture(); Key = 0; owner.TargetChanged(); });
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
}
