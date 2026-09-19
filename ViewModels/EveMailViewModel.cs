using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;
using Avalonia.Media;

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
    private IBrush _fromColor;
    private IBrush _subjectColor;
    public bool   IsRead       { get => _isRead;       private set => this.RaiseAndSetIfChanged(ref _isRead,       value); }
    public bool   IsUnread     { get => _isUnread;     private set => this.RaiseAndSetIfChanged(ref _isUnread,     value); }
    public IBrush FromColor    { get => _fromColor;    private set => this.RaiseAndSetIfChanged(ref _fromColor,    value); }
    public IBrush SubjectColor { get => _subjectColor; private set => this.RaiseAndSetIfChanged(ref _subjectColor, value); }

    /// <summary>The name the row leads with, and whose image it shows: the sender, except in the
    /// Sent folder, where every mail is from the same person and the one worth seeing is who it
    /// went to — the first recipient, when there are several.</summary>
    public string PartyText  { get; }
    public long   PartyId    { get; }
    public string PartyType  { get; }

    /// <summary>The image server path for the party, or null for a mailing list, which has none.</summary>
    public string? PartyImageUrl => PartyType switch
    {
        "character"   => $"https://images.evetech.net/characters/{PartyId}/portrait?size=32",
        "corporation" => $"https://images.evetech.net/corporations/{PartyId}/logo?size=32",
        "alliance"    => $"https://images.evetech.net/alliances/{PartyId}/logo?size=32",
        _             => null,
    };

    /// <param name="showRecipient">True in the Sent folder: lead with who the mail went to.</param>
    public EveMailRowVm(EveMailRow r, string charName, bool showRecipient = false)
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
        _fromColor    = r.IsRead ? Palette.TextMuted : Palette.TextBright;
        _subjectColor = r.IsRead ? Palette.TextFaint : Palette.TextPrimary;

        var to = showRecipient ? r.Recipients.FirstOrDefault() : null;
        if (to is not null)
        {
            PartyText = string.IsNullOrEmpty(to.Name) ? $"#{to.Id}" : to.Name;
            PartyId   = to.Id;
            PartyType = to.Type;
        }
        else
        {
            PartyText = FromText;
            PartyId   = FromId;
            PartyType = "character";
        }
    }

    public void MarkAsRead()
    {
        IsRead       = true;
        IsUnread     = false;
        FromColor    = Palette.TextMuted;
        SubjectColor = Palette.TextFaint;
    }

    /// <summary>The row's identity in the list: one mail, in one character's mailbox.</summary>
    public (int, long) Key => (MailId, CharId);

    /// <summary>
    /// Takes whatever the poll has changed since this row was built — read in the game, say —
    /// without replacing the row. Replacing it is what lost the selection on every refresh.
    /// </summary>
    public void Update(EveMailRow r)
    {
        if (r.IsRead && !IsRead) MarkAsRead();
    }

    public Task LoadPortraitAsync()
    {
        if (PartyImageUrl is not { } url) return Task.CompletedTask;
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

    /// <summary>Recipients already known — a reply's original sender — added without a search.</summary>
    public IReadOnlyList<EveMailResolvedRecipient> InitialRecipients { get; set; } = [];

    /// <summary>True for a reply: the body is prefilled with the quote, so the dialog opens with
    /// the caret on the first line and focus in the body, ready to type above it.</summary>
    public bool StartInBody { get; set; }
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

    /// <summary>In Sent, a row leads with who the mail went to rather than who sent it.</summary>
    private bool InSentFolder => _selectedFolder?.LabelId == EveMailService.SentLabel;

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

    // ⚠️ One body load in flight at a time. Selecting a mail while the previous one is still
    // fetching used to leave both running, and whichever finished LAST owned the pane: on a
    // slow link the first fetch would land after the second and quietly replace the mail the
    // user was now looking at with the one they had left — or clear IsLoading while the real
    // load was still going. Each selection now cancels the one before it.
    private CancellationTokenSource? _bodyCts;

    private Bitmap? _selectedFromPortrait;
    public Bitmap? SelectedFromPortrait
    {
        get => _selectedFromPortrait;
        private set => this.RaiseAndSetIfChanged(ref _selectedFromPortrait, value);
    }

    // The body as EVE wrote it, markup included; the view renders it. Status text — "Loading…",
    // an error — goes through the same property and simply has no markup to render.
    private string _bodyMarkup = "";
    public string BodyMarkup
    {
        get => _bodyMarkup;
        private set => this.RaiseAndSetIfChanged(ref _bodyMarkup, value);
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
    public ReactiveCommand<Unit, Unit> ReplyCommand   { get; }
    public ReactiveCommand<Unit, Unit> ForwardCommand { get; }

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
        ReplyCommand   = ReactiveCommand.CreateFromTask(OpenReplyAsync,
                             this.WhenAnyValue(vm => vm.SelectedMail).Select(m => m is not null));
        ForwardCommand = ReactiveCommand.CreateFromTask(OpenForwardAsync,
                             this.WhenAnyValue(vm => vm.SelectedMail).Select(m => m is not null));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => _ = LoadMailsAsync(quiet: true);
        timer.Start();

        // Populate character list — include items already in the collection. "All Characters"
        // stays first; everyone else is kept in alphabetical order, however they arrive.
        Characters.Add(EveMailCharacterOption.All);
        foreach (var c in characters)
            InsertSorted(new EveMailCharacterOption(c));

        // Observe future adds/removes (fires when LoadFromDatabaseAsync populates the list)
        characters.CollectionChanged += OnSourceCharsChanged;

        // Default selections
        _selectedChar   = Characters[0];   // "All Characters"
        _selectedFolder = Folders[0];      // "All Mail"
    }

    /// <summary>Places a character after "All Characters" and before the first name that sorts
    /// after it, so the list reads alphabetically whatever order the characters loaded in.</summary>
    private void InsertSorted(EveMailCharacterOption option)
    {
        var at = 1;   // index 0 is always "All Characters"
        while (at < Characters.Count
               && string.Compare(Characters[at].Name, option.Name, StringComparison.OrdinalIgnoreCase) < 0)
            at++;
        Characters.Insert(at, option);
    }

    private void OnSourceCharsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (Character c in e.NewItems)
                InsertSorted(new EveMailCharacterOption(c));

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

    /// <param name="quiet">A background refresh: no spinner, no "Loading…", and nothing on
    /// screen moves unless a mail has actually arrived or gone.</param>
    public async Task LoadMailsAsync(CancellationToken ct = default, bool quiet = false)
    {
        if (_selectedChar is null) return;
        if (!quiet) { IsLoading = true; StatusText = "Loading…"; }
        try
        {
            List<long>? charIds = _selectedChar.IsAllCharacters
                ? _sourceChars.Select(c => c.Id).ToList()
                : null;
            long? singleCharId = _selectedChar.IsAllCharacters ? null : _selectedChar.Id;

            var rows = await _svc.GetMailsAsync(singleCharId, charIds, _selectedFolder?.LabelId, ct);

            // ⚠️ Two different operations, and which one depends on why we are here.
            //
            // A background refresh of the SAME list is reconciled in place. Clearing and
            // refilling made the ListBox drop its selection, which nulled SelectedMail and
            // blanked the message being read — every sixty seconds. Rows still present keep
            // their identity (and so the selection); a new mail is inserted where it belongs,
            // the newest at the top; a mail that has gone is removed. Nothing new, nothing moves.
            //
            // A folder or character change is a DIFFERENT list, and is replaced. Reconciling
            // one list into another moves and trims rows under the ListBox, and it was left
            // scrolled to the bottom of the shorter list — the old offset, clamped to the new
            // extent. Replacing resets the scroll to the top, which is where a new folder opens.
            var added = quiet ? Reconcile(rows) : Replace(rows);

            // Portraits for the new rows only — throttled to 4 concurrent HTTP requests.
            if (added.Count > 0)
                _ = Task.Run(async () =>
                {
                    using var sem = new SemaphoreSlim(4, 4);
                    await Task.WhenAll(added.Select(async vm =>
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
            if (!quiet) IsLoading = false;
        }
    }

    /// <summary>A new list: everything replaced, scroll back at the top, no selection.</summary>
    private List<EveMailRowVm> Replace(List<EveMailRow> rows)
    {
        Mails.Clear();
        foreach (var r in rows)
        {
            var charName = _sourceChars.FirstOrDefault(c => c.Id == r.CharacterId)?.Name
                           ?? _selectedChar?.Name ?? "";
            Mails.Add(new EveMailRowVm(r, charName, InSentFolder));
        }
        return Mails.ToList();
    }

    /// <summary>
    /// Brings <see cref="Mails"/> to exactly <paramref name="rows"/>, in that order, touching
    /// only what differs. Returns the rows that were new.
    /// </summary>
    private List<EveMailRowVm> Reconcile(List<EveMailRow> rows)
    {
        var added   = new List<EveMailRowVm>();
        var existing = new Dictionary<(int, long), EveMailRowVm>();
        foreach (var vm in Mails) existing[vm.Key] = vm;

        for (var i = 0; i < rows.Count; i++)
        {
            var r   = rows[i];
            var key = (r.MailId, r.CharacterId);

            if (existing.TryGetValue(key, out var vm))
            {
                vm.Update(r);
                var at = Mails.IndexOf(vm);
                if (at != i) Mails.Move(at, i);
                continue;
            }

            var charName = _sourceChars.FirstOrDefault(c => c.Id == r.CharacterId)?.Name
                           ?? _selectedChar?.Name ?? "";
            vm = new EveMailRowVm(r, charName, InSentFolder);
            Mails.Insert(i, vm);
            existing[key] = vm;
            added.Add(vm);
        }

        // Anything past the end is a mail the rows no longer contain.
        while (Mails.Count > rows.Count) Mails.RemoveAt(Mails.Count - 1);

        return added;
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
        if (vm.PartyImageUrl is not { } url) return;
        var bmp   = await EveImageCache.GetAsync(url);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_selectedMail == vm)
                SelectedFromPortrait = bmp;
        });
    }

    private async Task LoadBodyAsync()
    {
        _bodyCts?.Cancel();
        _bodyCts?.Dispose();
        var cts  = _bodyCts = new CancellationTokenSource();
        var ct   = cts.Token;
        var mail = _selectedMail;

        if (mail is null) { BodyMarkup = ""; return; }
        IsLoading = true;
        BodyMarkup = "Loading…";
        try
        {
            var body = await _svc.GetRawBodyAsync(mail.CharId, mail.MailId, ct);
            if (ct.IsCancellationRequested) return;   // superseded; the newer load owns the pane
            BodyMarkup = body;

            if (!mail.IsRead)
            {
                await _svc.MarkReadAsync(mail.CharId, mail.MailId);
                mail.MarkAsRead();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection, which has already taken over the pane.
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) BodyMarkup = $"(Error loading body: {ex.Message})";
        }
        finally
        {
            // Only the load that still owns the pane may clear the spinner.
            if (ReferenceEquals(cts, _bodyCts)) IsLoading = false;
        }
    }

    private async Task OpenComposeAsync()
    {
        if (ShowComposeDialog is null) return;

        if (_sourceChars.Count == 0) { StatusText = "No characters available to compose mail."; return; }

        var defaultFrom = _selectedChar?.IsAllCharacters == false
            ? _selectedChar.Id
            : _sourceChars[0].Id;

        await ComposeAndSendAsync(new ComposeMailArgs
        {
            Characters = _sourceChars.ToList(),
            FromCharId = defaultFrom,
        });
    }

    /// <summary>
    /// A reply to the selected mail: from the character it was sent to, to whoever sent it,
    /// "RE:" on the subject unless it already carries one, and the original quoted below three
    /// blank lines with the caret on the first of them.
    /// </summary>
    private Task OpenReplyAsync()   => QuoteSelectedAsync(forward: false);

    /// <summary>
    /// A forward of the selected mail: the same quote, "FW:" on the subject in place of any
    /// "RE:", and nobody in the To line — that is the whole point of a forward.
    /// </summary>
    private Task OpenForwardAsync() => QuoteSelectedAsync(forward: true);

    /// <summary>What a subject becomes on reply or forward.</summary>
    public static string QuotedSubject(string subject, bool forward)
    {
        var s = subject.TrimStart();
        var prefix = forward ? "FW:" : "RE:";

        // Already this kind of message: leave it as it is, whatever the case.
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return s;

        // Forwarding a reply: the RE: goes, since what is sent on is not an answer to anyone.
        if (forward && s.StartsWith("RE:", StringComparison.OrdinalIgnoreCase)) s = s[3..].TrimStart();

        return prefix + " " + s;
    }

    private async Task QuoteSelectedAsync(bool forward)
    {
        if (ShowComposeDialog is null || _selectedMail is not { } mail) return;

        // The character the mail was delivered to is the one sending. That is the row's
        // mailbox owner, not whichever character the list happens to be filtered on.
        if (_sourceChars.All(c => c.Id != mail.CharId))
        {
            StatusText = "The character this mail was sent to is no longer available to send from.";
            return;
        }

        // The stored body is EVE's markup; the compose box is plain text.
        var original = EveMailMarkup.ToPlainText(await _svc.GetRawBodyAsync(mail.CharId, mail.MailId));

        var quote = "\n\n\n"
                  + "--------------------------------\n"
                  + $"{mail.FromText} wrote on {mail.TimeText}:\n\n"
                  + original;

        await ComposeAndSendAsync(new ComposeMailArgs
        {
            Characters        = _sourceChars.ToList(),
            FromCharId        = mail.CharId,
            InitialRecipients = !forward && mail.FromId > 0
                                    ? [new EveMailResolvedRecipient(mail.FromId, mail.FromText, "character")]
                                    : [],
            InitialSubject    = QuotedSubject(mail.Subject, forward),
            InitialBody       = quote,
            StartInBody       = true,
        });
    }


    private async Task ComposeAndSendAsync(ComposeMailArgs args)
    {
        if (ShowComposeDialog is null) return;
        var result = await ShowComposeDialog(args);
        if (result is null) return;

        IsLoading  = true;
        StatusText = "Sending…";
        try
        {
            var (ok, err) = await _svc.SendMailAsync(result.FromCharId, result.Subject, result.Body, result.Recipients);
            StatusText = ok ? "Mail sent." : $"Send failed: {err}";
            if (ok) _ = LoadMailsAsync(quiet: true);   // the same list, with the sent mail in it
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
