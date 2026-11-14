using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Mumble;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One channel a channel target reaches, as it stands in Settings.</summary>
public sealed class MumbleTargetChannelVm : ReactiveObject
{
    private readonly MumbleTargetVm _owner;
    private string _text;
    private bool _links, _subchannels;

    public MumbleTargetChannelVm(MumbleTargetVm owner, MumbleTargetChannel from)
    {
        _owner = owner;
        _text = from.Path switch
        {
            "" => MumbleText.TargetWhereIAm,
            MumbleTargets.RootPath => MumbleText.RootChannel,
            var path => path,
        };
        _links = from.Links;
        _subchannels = from.Subchannels;
        RemoveCommand = ReactiveCommand.Create(() => owner.RemoveChannel(this));
    }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ObservableCollection<string> Choices => _owner.ChannelChoices;

    /// <summary>The channel as picked: the words for the current channel or for the root, or its path.</summary>
    /// <remarks>⚠️ No pick (null) is ignored: the list changing under the box clears its selection for a moment.</remarks>
    public string Text
    {
        get => _text;
        set
        {
            if (value is null || value == _text) return;
            this.RaiseAndSetIfChanged(ref _text, value);
            _owner.Touched();
        }
    }

    /// <summary>Shown again after the list changed under it, so the box finds its pick in the new list.</summary>
    internal void Reshow() => this.RaisePropertyChanged(nameof(Text));

    public bool Links
    {
        get => _links;
        set { this.RaiseAndSetIfChanged(ref _links, value); _owner.Touched(); }
    }

    public bool Subchannels
    {
        get => _subchannels;
        set { this.RaiseAndSetIfChanged(ref _subchannels, value); _owner.Touched(); }
    }

    public MumbleTargetChannel ToModel()
    {
        var text = Text.Trim();
        return new MumbleTargetChannel
        {
            Path = text.Length == 0 || text == MumbleText.TargetWhereIAm ? ""
                 : text == MumbleText.RootChannel ? MumbleTargets.RootPath
                 : text,
            Links = Links,
            Subchannels = Subchannels,
        };
    }
}

/// <summary>One channel target, as it stands in Settings: what it's called, its key, and who it reaches.</summary>
public sealed class MumbleTargetVm : ReactiveObject, IMumbleKeySlot
{
    private readonly MumbleViewModel _owner;
    private string _name, _people, _group;
    private int _key;
    private bool _capturing;
    private string _keyProblem = "";
    private bool _keyRefused;

    public MumbleTargetVm(MumbleViewModel owner, MumbleTalkTarget from)
    {
        _owner = owner;
        _name = from.Name;
        _key = from.Key;
        _people = from.People;
        _group = from.Group;
        foreach (var channel in from.Channels) Channels.Add(new MumbleTargetChannelVm(this, channel));

        CaptureKeyCommand = ReactiveCommand.Create(() => owner.CaptureKeyFor(this));
        ClearKeyCommand   = ReactiveCommand.Create(() => { owner.CancelKeyCapture(); Key = 0; owner.KeyCleared(); });
        RemoveCommand     = ReactiveCommand.Create(() => owner.RemoveTarget(this));
        AddChannelCommand = ReactiveCommand.Create(() =>
        {
            Channels.Add(new MumbleTargetChannelVm(this, new MumbleTargetChannel()));
            Touched();
        });
    }

    public ObservableCollection<MumbleTargetChannelVm> Channels { get; } = [];
    public ObservableCollection<string> ChannelChoices => _owner.TargetChannelChoices;

    public ReactiveCommand<Unit, Unit> CaptureKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> AddChannelCommand { get; }

    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value ?? ""); Typed(); }
    }

    public string People
    {
        get => _people;
        set { this.RaiseAndSetIfChanged(ref _people, value ?? ""); Typed(); }
    }

    public string Group
    {
        get => _group;
        set { this.RaiseAndSetIfChanged(ref _group, value ?? ""); Typed(); }
    }

    public int Key
    {
        get => _key;
        set { this.RaiseAndSetIfChanged(ref _key, value); this.RaisePropertyChanged(nameof(KeyText)); }
    }

    /// <summary>Waiting for the next key or button pressed.</summary>
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

    internal void Typed() => _owner.TargetTyped();
    internal void Touched() => _owner.TargetChanged();

    internal void RemoveChannel(MumbleTargetChannelVm channel)
    {
        Channels.Remove(channel);
        Touched();
    }

    public MumbleTalkTarget ToModel() => new()
    {
        Name = Name.Trim(), Key = Key, People = People.Trim(), Group = Group.Trim(),
        Channels = [.. Channels.Select(c => c.ToModel())],
    };
}
