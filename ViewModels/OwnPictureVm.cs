using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Localization;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// "Your picture" on a Jabber account or a Mumble server: from a file, an EVE character's portrait, or taken away. Each
/// is sent the moment it is chosen, and only then: nothing here ever changes a picture unasked.
/// </summary>
public sealed class OwnPictureVm : ReactiveObject
{
    private readonly Func<byte[]?, Task> _apply;
    private readonly Func<Task<List<(long Id, string Name)>>> _characters;
    private AvatarRef? _preview;
    private bool _canChange, _isWorking;
    private string _note = "", _status = "", _problem = "";
    private IrcPortraitChoice? _portrait;

    /// <param name="apply">Sends a picture made ready, or null to take it away. Throws with words for the person when refused.</param>
    /// <param name="characters">The EVE characters added to this app.</param>
    public OwnPictureVm(Func<byte[]?, Task> apply, Func<Task<List<(long Id, string Name)>>> characters)
    {
        _apply = apply;
        _characters = characters;
        RemoveCommand = ReactiveCommand.CreateFromTask(() => ApplyAsync(null, JabberText.PictureRemoved));
    }

    /// <summary>What is shown as this person's picture now.</summary>
    public AvatarRef? Preview { get => _preview; set => this.RaiseAndSetIfChanged(ref _preview, value); }

    /// <summary>Connected, so a change can be sent. Nothing is kept to send later.</summary>
    public bool CanChange { get => _canChange; set { this.RaiseAndSetIfChanged(ref _canChange, value); this.RaisePropertyChanged(nameof(CanUse)); } }

    public bool IsWorking { get => _isWorking; private set { this.RaiseAndSetIfChanged(ref _isWorking, value); this.RaisePropertyChanged(nameof(CanUse)); } }
    public bool CanUse => CanChange && !IsWorking;

    /// <summary>What this server does with a picture, where it is worth saying (not kept for someone not registered).</summary>
    public string Note { get => _note; set { this.RaiseAndSetIfChanged(ref _note, value); this.RaisePropertyChanged(nameof(HasNote)); } }
    public bool HasNote => Note.Length > 0;

    public string Status { get => _status; private set { this.RaiseAndSetIfChanged(ref _status, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => Status.Length > 0;
    public string Problem { get => _problem; private set { this.RaiseAndSetIfChanged(ref _problem, value); this.RaisePropertyChanged(nameof(HasProblem)); } }
    public bool HasProblem => Problem.Length > 0;

    public ObservableCollection<IrcPortraitChoice> Portraits { get; } = [];
    public bool HasPortraits => Portraits.Count > 0;

    /// <summary>A character chosen: their portrait is fetched and set at once.</summary>
    public IrcPortraitChoice? Portrait
    {
        get => _portrait;
        set
        {
            this.RaiseAndSetIfChanged(ref _portrait, value);
            if (value is not null) _ = UsePortraitAsync(value);
        }
    }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    /// <summary>Forgets what was said last, for another account or server.</summary>
    public void Reset()
    {
        Status = Problem = "";
        _portrait = null;
        this.RaisePropertyChanged(nameof(Portrait));
    }

    /// <summary>Reads the characters again: one added since is offered too.</summary>
    public async Task LoadPortraitsAsync()
    {
        try
        {
            var list = await _characters();
            Portraits.Clear();
            foreach (var (id, name) in list) Portraits.Add(new IrcPortraitChoice(id, name));
            this.RaisePropertyChanged(nameof(HasPortraits));
        }
        catch (Exception e) when (e is not OperationCanceledException) { Problem = e.Message; }
    }

    /// <summary>A picture file chosen: made ready and set.</summary>
    public async Task UseFileAsync(byte[] file)
    {
        if (await Task.Run(() => OwnPicture.Prepare(file)) is not { } jpeg) { Status = ""; Problem = JabberText.PictureNotAPicture; return; }
        await ApplyAsync(jpeg, JabberText.PictureSet);
    }

    private async Task UsePortraitAsync(IrcPortraitChoice who)
    {
        Status = Problem = "";
        IsWorking = true;
        byte[]? jpeg;
        try { jpeg = await Task.Run(() => OwnPicture.FromEvePortraitAsync(who.Id, CancellationToken.None)); }
        finally { IsWorking = false; }
        if (jpeg is null) { Problem = JabberText.PictureEveFailed; return; }
        await ApplyAsync(jpeg, JabberText.PictureSet);
    }

    private async Task ApplyAsync(byte[]? jpeg, string done)
    {
        Status = Problem = "";
        IsWorking = true;
        try
        {
            await _apply(jpeg);
            Status = done;
        }
        catch (Exception e) when (e is not OperationCanceledException) { Problem = e.Message; }
        finally { IsWorking = false; }
    }
}
