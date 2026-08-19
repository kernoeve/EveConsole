using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

// ── Row view-models ───────────────────────────────────────────────────────────

public class EveMailRowVm : ReactiveObject
{
    private Bitmap? _portrait;
    public Bitmap? Portrait { get => _portrait; private set => this.RaiseAndSetIfChanged(ref _portrait, value); }

    public int            MailId       { get; }
    public long           CharId       { get; }
    public long           FromId       { get; }
    public string         FromText     { get; }
    public string         ToText       { get; }
    public string         Subject      { get; }
    public string         TimeText     { get; }
    public DateTimeOffset TimeRaw      { get; }
    public string         CharName     { get; }

    /// <summary>Each addressee separately, so every name in the To line is its own link. A
    /// mailing list has no entity page, so it renders plain.</summary>
    public IReadOnlyList<EveMailPartyVm> Recipients { get; }

    public bool HasFromLink => FromId > 0 && FromText.Length > 0;
    public void OpenFrom() => EntityNavigator.Instance.Entity(EntityLinks.KindOf(FromId), FromId);

    private bool   _isRead;
    private bool   _isUnread;
    private string _fromColor;
    private string _subjectColor;
    public bool   IsRead       { get => _isRead;       private set => this.RaiseAndSetIfChanged(ref _isRead,       value); }
    public bool   IsUnread     { get => _isUnread;     private set => this.RaiseAndSetIfChanged(ref _isUnread,     value); }
    public string FromColor    { get => _fromColor;    private set => this.RaiseAndSetIfChanged(ref _fromColor,    value); }
    public string SubjectColor { get => _subjectColor; private set => this.RaiseAndSetIfChanged(ref _subjectColor, value); }

    public EveMailRowVm(EveMailRow r, string charName)
    {
        MailId        = r.MailId;
        CharId        = r.CharacterId;
        FromId        = r.FromId;
        FromText      = string.IsNullOrEmpty(r.FromName) ? $"#{r.FromId}" : r.FromName;
        ToText        = r.RecipientSummary;
        Recipients    = r.Recipients.Select(x => new EveMailPartyVm(x)).ToList();
        Subject       = r.Subject;
        TimeText      = r.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        TimeRaw       = r.Timestamp;
        CharName      = charName;
        _isRead       = r.IsRead;
        _isUnread     = !r.IsRead;
        _fromColor    = r.IsRead ? "#888899" : "#e8e8f0";
        _subjectColor = r.IsRead ? "#555566" : "#c8c8d8";
    }

    public void MarkAsRead()
    {
        IsRead       = true;
        IsUnread     = false;
        FromColor    = "#888899";
        SubjectColor = "#555566";
    }

    public Task LoadPortraitAsync()
    {
        var url = $"https://images.evetech.net/characters/{FromId}/portrait?size=32";
        return EveImageCache.GetAsync(url)
            .ContinueWith(t => Dispatcher.UIThread.Post(() => Portrait = t.Result),
                TaskScheduler.Default);
    }
}


/// <summary>One name on a mail's To line.</summary>
public class EveMailPartyVm(EveMailRecipient r)
{
    public string Name    { get; } = r.Name;
    /// <summary>A mailing list is not an entity — nothing to open, so no link.</summary>
    public bool   HasLink => r.Id > 0 && r.Type != "mailing_list";
    public void   Open()  => EntityNavigator.Instance.Entity(EntityLinks.KindOf(r.Id, r.Type), r.Id);
}
public class EveMailFolderVm(string name, int? labelId)
{
    public string Name    { get; } = name;
    public int?   LabelId { get; } = labelId;
}

public class EveMailCharacterOption
{
    public long   Id              { get; }
    public string Name            { get; }
    public bool   IsAllCharacters => Id == 0;

    public EveMailCharacterOption(Character c) { Id = c.Id; Name = c.Name; }

    private EveMailCharacterOption() { Id = 0; Name = "All Characters"; }
    public static readonly EveMailCharacterOption All = new();

    public override string ToString() => Name;
}

// ── Compose args passed to the dialog ────────────────────────────────────────

public sealed class ComposeMailArgs
{
    public IReadOnlyList<Character> Characters    { get; set; } = [];
    public long                     FromCharId    { get; set; }   // pre-selected character
    public string                   InitialTo      { get; set; } = "";
    public string                   InitialSubject { get; set; } = "";
    public string                   InitialBody    { get; set; } = "";
}

public sealed class ComposeMailResult
{
    public long   FromCharId { get; set; }
    public string Subject    { get; set; } = "";
    public string Body       { get; set; } = "";
    public List<EsiMailRecipientItem> Recipients { get; set; } = [];
}

// ── Main view-model ───────────────────────────────────────────────────────────

public class EveMailViewModel : ReactiveObject
{
    private readonly EveMailService                  _svc;
    private readonly ObservableCollection<Character> _sourceChars;

    public ObservableCollection<EveMailCharacterOption> Characters { get; } = [];
    public ObservableCollection<EveMailFolderVm>        Folders    { get; } = [];
    public ObservableCollection<EveMailRowVm>           Mails      { get; } = [];

    private EveMailCharacterOption? _selectedChar;
    public EveMailCharacterOption? SelectedChar
    {
        get => _selectedChar;
        set { this.RaiseAndSetIfChanged(ref _selectedChar, value); _ = LoadMailsAsync(); }
    }

    private bool _suppressFolderLoad;
    private EveMailFolderVm? _selectedFolder;
    public EveMailFolderVm? SelectedFolder
    {
        get => _selectedFolder;
        set { this.RaiseAndSetIfChanged(ref _selectedFolder, value); if (!_suppressFolderLoad) _ = LoadMailsAsync(); }
    }

    private EveMailRowVm? _selectedMail;
    public EveMailRowVm? SelectedMail
    {
        get => _selectedMail;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMail, value);
            SelectedFromPortrait = null;
            if (value is not null) _ = LoadSelectedPortraitAsync(value);
            _ = LoadBodyAsync();
        }
    }

    private Bitmap? _selectedFromPortrait;
    public Bitmap? SelectedFromPortrait
    {
        get => _selectedFromPortrait;
        private set => this.RaiseAndSetIfChanged(ref _selectedFromPortrait, value);
    }

    private string _bodyText = "";
    public string BodyText
    {
        get => _bodyText;
        private set => this.RaiseAndSetIfChanged(ref _bodyText, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public ReactiveCommand<Unit, Unit> ComposeCommand { get; }

    public Func<ComposeMailArgs, Task<ComposeMailResult?>>? ShowComposeDialog { get; set; }

    public EveMailViewModel(EveMailService svc, ObservableCollection<Character> characters)
    {
        _svc         = svc;
        _sourceChars = characters;

        // Static folders — always present
        Folders.Add(new EveMailFolderVm("All Mail",  null));
        Folders.Add(new EveMailFolderVm("Inbox",     1));
        Folders.Add(new EveMailFolderVm("Sent",      2));
        Folders.Add(new EveMailFolderVm("Corp",      4));
        Folders.Add(new EveMailFolderVm("Alliance",  8));

        ComposeCommand = ReactiveCommand.CreateFromTask(OpenComposeAsync);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => _ = LoadMailsAsync();
        timer.Start();

        // Populate character list — include items already in the collection
        Characters.Add(EveMailCharacterOption.All);
        foreach (var c in characters)
            Characters.Add(new EveMailCharacterOption(c));

        // Observe future adds/removes (fires when LoadFromDatabaseAsync populates the list)
        characters.CollectionChanged += OnSourceCharsChanged;

        // Default selections
        _selectedChar   = Characters[0];   // "All Characters"
        _selectedFolder = Folders[0];      // "All Mail"
    }

    private void OnSourceCharsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (Character c in e.NewItems)
                Characters.Add(new EveMailCharacterOption(c));

        if (e.OldItems is not null)
            foreach (Character c in e.OldItems)
            {
                var opt = Characters.FirstOrDefault(o => o.Id == c.Id);
                if (opt is not null) Characters.Remove(opt);
            }

        // Auto-select All Characters once the first character arrives
        if (_selectedChar?.IsAllCharacters == true && Mails.Count == 0)
            _ = LoadMailsAsync();
    }

    public async Task LoadMailsAsync(CancellationToken ct = default)
    {
        if (_selectedChar is null) return;
        IsLoading  = true;
        StatusText = "Loading…";
        try
        {
            List<long>? charIds = _selectedChar.IsAllCharacters
                ? _sourceChars.Select(c => c.Id).ToList()
                : null;
            long? singleCharId = _selectedChar.IsAllCharacters ? null : _selectedChar.Id;

            var rows = await _svc.GetMailsAsync(singleCharId, charIds, _selectedFolder?.LabelId, ct);

            Mails.Clear();
            foreach (var r in rows)
            {
                var charName = _sourceChars.FirstOrDefault(c => c.Id == r.CharacterId)?.Name
                               ?? _selectedChar.Name;
                Mails.Add(new EveMailRowVm(r, charName));
            }

            // Load portraits in background — throttle to 4 concurrent HTTP requests.
            var snapshot = Mails.ToList();
            _ = Task.Run(async () =>
            {
                using var sem = new SemaphoreSlim(4, 4);
                await Task.WhenAll(snapshot.Select(async vm =>
                {
                    await sem.WaitAsync(ct);
                    try { await vm.LoadPortraitAsync(); }
                    finally { sem.Release(); }
                }));
            });

            // Rebuild folder list every load — clears stale custom labels when switching chars.
            var customLabels = !_selectedChar.IsAllCharacters
                ? await _svc.GetLabelsAsync(_selectedChar.Id, ct)
                : new List<EveMailLabelOption>();
            RebuildFolders(customLabels);

            StatusText = $"{Mails.Count} messages";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildFolders(List<EveMailLabelOption> customLabels)
    {
        // Never remove the 5 static folders — clearing+re-adding them causes the ListBox binding
        // to push SelectedFolder=null back, which triggers a reload loop.
        // Only manage the custom label entries at index 5+.
        _suppressFolderLoad = true;
        try
        {
            var savedLabelId = _selectedFolder?.LabelId;

            while (Folders.Count > 5)
                Folders.RemoveAt(5);

            foreach (var lbl in customLabels)
                Folders.Add(new EveMailFolderVm(lbl.Name, lbl.LabelId));

            var newSel = savedLabelId.HasValue
                ? Folders.FirstOrDefault(f => f.LabelId == savedLabelId)
                : _selectedFolder ?? Folders.FirstOrDefault();

            if (!ReferenceEquals(_selectedFolder, newSel))
            {
                _selectedFolder = newSel;
                this.RaisePropertyChanged(nameof(SelectedFolder));
            }
        }
        finally
        {
            _suppressFolderLoad = false;
        }
    }

    private async Task LoadSelectedPortraitAsync(EveMailRowVm vm)
    {
        // Use the 32px portrait already in-flight/cached from the list to avoid a second fetch.
        var url   = $"https://images.evetech.net/characters/{vm.FromId}/portrait?size=32";
        var bmp   = await EveImageCache.GetAsync(url);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_selectedMail == vm)
                SelectedFromPortrait = bmp;
        });
    }

    private async Task LoadBodyAsync()
    {
        if (_selectedMail is null) { BodyText = ""; return; }
        IsLoading = true;
        BodyText  = "Loading…";
        try
        {
            BodyText = await _svc.GetBodyAsync(_selectedMail.CharId, _selectedMail.MailId);
            if (!_selectedMail.IsRead)
            {
                await _svc.MarkReadAsync(_selectedMail.CharId, _selectedMail.MailId);
                _selectedMail.MarkAsRead();
            }
        }
        catch (Exception ex)
        {
            BodyText = $"(Error loading body: {ex.Message})";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task OpenComposeAsync()
    {
        if (ShowComposeDialog is null) return;

        if (_sourceChars.Count == 0) { StatusText = "No characters available to compose mail."; return; }

        var defaultFrom = _selectedChar?.IsAllCharacters == false
            ? _selectedChar.Id
            : _sourceChars[0].Id;

        var args = new ComposeMailArgs
        {
            Characters = _sourceChars.ToList(),
            FromCharId = defaultFrom,
        };
        var result = await ShowComposeDialog(args);
        if (result is null) return;

        IsLoading  = true;
        StatusText = "Sending…";
        try
        {
            var (ok, err) = await _svc.SendMailAsync(result.FromCharId, result.Subject, result.Body, result.Recipients);
            StatusText = ok ? "Mail sent." : $"Send failed: {err}";
            if (ok) _ = LoadMailsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Send failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
