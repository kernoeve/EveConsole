using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using EveConsole.Localization;
using EveConsole.Services.Irc;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One of a channel's on-or-off settings (a mode letter without a value), as a box to tick.</summary>
public sealed class IrcModeFlagVm(char letter, string label, string tip, bool on) : ReactiveObject
{
    private bool _isOn = on;

    public char Letter { get; } = letter;
    public string Label { get; } = label;
    public string Tip { get; } = tip;
    public bool WasOn { get; } = on;
    public bool IsOn { get => _isOn; set => this.RaiseAndSetIfChanged(ref _isOn, value); }
}

/// <summary>A setting that holds a value when on (the most people, or a letter only this server has), as a box and a field.</summary>
public sealed class IrcModeValueVm(char letter, string label, string tip, bool on, string value, bool always) : ReactiveObject
{
    private bool _isOn = on;
    private string _value = value;

    public char Letter { get; } = letter;
    public string Label { get; } = label;
    public string Tip { get; } = tip;
    public bool WasOn { get; } = on;
    public string WasValue { get; } = value;

    /// <summary>Takes its value when taken off too (CHANMODES type B), as a key does.</summary>
    public bool ValueToRemove { get; } = always;

    public bool IsOn { get => _isOn; set => this.RaiseAndSetIfChanged(ref _isOn, value); }
    public string Value { get => _value; set => this.RaiseAndSetIfChanged(ref _value, value); }
}

/// <summary>One of a channel's lists (bans, ban exceptions, invite exceptions): read when its tab is first shown, changed a mask at a time.</summary>
public sealed class IrcModeListVm(IrcChannelSettingsVm owner, char letter, string header, string note) : ReactiveObject
{
    private string _newMask = "";
    private bool _loaded, _loading;

    public char Letter { get; } = letter;
    public string Header { get; } = header;
    public string Note { get; } = note;
    public ObservableCollection<IrcModeListEntryVm> Entries { get; } = [];

    public string NewMask { get => _newMask; set { this.RaiseAndSetIfChanged(ref _newMask, value); this.RaisePropertyChanged(nameof(CanAdd)); } }
    public bool CanAdd => NewMask.Trim().Length > 0 && !NewMask.Trim().Contains(' ');
    public bool IsLoading { get => _loading; private set { this.RaiseAndSetIfChanged(ref _loading, value); this.RaisePropertyChanged(nameof(IsEmpty)); } }
    public bool IsEmpty => !IsLoading && _loaded && Entries.Count == 0;

    public async Task LoadAsync(bool again = false)
    {
        if (_loaded && !again || IsLoading) return;
        IsLoading = true;
        try
        {
            var entries = await owner.ReadListAsync(Letter);
            Entries.Clear();
            foreach (var e in entries) Entries.Add(new IrcModeListEntryVm(this, e.Mask, e.SetBy));
            _loaded = true;
        }
        finally { IsLoading = false; }
    }

    public async Task AddAsync()
    {
        var mask = NewMask.Trim();
        if (!CanAdd) return;
        if (await owner.ChangeListAsync(Letter, mask, add: true))
        {
            NewMask = "";
            await Task.Delay(400);
            await LoadAsync(again: true);
        }
    }

    public async Task RemoveAsync(IrcModeListEntryVm entry)
    {
        if (await owner.ChangeListAsync(Letter, entry.Mask, add: false))
        {
            Entries.Remove(entry);
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }
}

public sealed class IrcModeListEntryVm(IrcModeListVm list, string mask, string setBy)
{
    public IrcModeListVm List { get; } = list;
    public string Mask { get; } = mask;
    public string SetBy { get; } = setBy.Length > 0 ? string.Format(CultureInfo.CurrentCulture, IrcText.ModeSetBy, setBy) : "";
}

/// <summary>
/// A channel's settings (its modes) and lists, for an operator to change. Only what the server says it has (CHANMODES)
/// is offered: the usual letters with words, and any other as its letter.
/// </summary>
/// <remarks>
/// ⚠️ A key is a password to join, and is never shown: whether there is one, and a field for a new one.
/// </remarks>
public sealed class IrcChannelSettingsVm : ReactiveObject
{
    private readonly IrcService _irc;
    private readonly IrcSession _session;
    private readonly bool _hadKey;
    private bool _keyOn, _busy;
    private string _newKey = "", _status = "";

    public IrcChannelSettingsVm(IrcService irc, IrcSession session, string channel, IrcSupport support, IReadOnlyDictionary<char, string> modes, bool known)
    {
        _irc = irc;
        _session = session;
        Channel = channel;
        Known = known;

        foreach (var letter in support.ModesD.Distinct())
        {
            var (label, tip) = Describe(letter);
            Flags.Add(new IrcModeFlagVm(letter, label, tip, modes.ContainsKey(letter)));
        }

        // Taking a value when set (type C), or always (type B): the most people (l), the key (k), and any the server adds.
        foreach (var letter in support.ModesC.Concat(support.ModesB).Distinct())
        {
            if (letter == 'k') continue;
            var (label, tip) = Describe(letter);
            Values.Add(new IrcModeValueVm(letter, label, tip, modes.ContainsKey(letter), modes.GetValueOrDefault(letter) ?? "", support.ModesB.Contains(letter)));
        }

        HasKeyMode = support.ModesB.Contains('k');
        _hadKey = _keyOn = modes.ContainsKey('k');

        foreach (var (letter, header, note) in new[]
                 {
                     ('b', IrcText.ModeListBans, IrcText.ModeListBansNote),
                     ('e', IrcText.ModeListExceptions, IrcText.ModeListExceptionsNote),
                     ('I', IrcText.ModeListInvites, IrcText.ModeListInvitesNote),
                 })
            if (support.ModesA.Contains(letter)) Lists.Add(new IrcModeListVm(this, letter, header, note));
    }

    public string Channel { get; }
    public string Title => string.Format(CultureInfo.CurrentCulture, IrcText.ChannelSettingsTitle, Channel);

    /// <summary>The settings were read from the server since joining. When not, what shows may be short of what is set.</summary>
    public bool Known { get; }
    public bool NotKnown => !Known;

    public ObservableCollection<IrcModeFlagVm> Flags { get; } = [];
    public ObservableCollection<IrcModeValueVm> Values { get; } = [];
    public ObservableCollection<IrcModeListVm> Lists { get; } = [];
    public bool HasValues => Values.Count > 0;

    public IrcModeListVm? Bans => Lists.FirstOrDefault(l => l.Letter == 'b');
    public IrcModeListVm? Exceptions => Lists.FirstOrDefault(l => l.Letter == 'e');
    public IrcModeListVm? InviteExceptions => Lists.FirstOrDefault(l => l.Letter == 'I');
    public bool HasBans => Bans is not null;
    public bool HasExceptions => Exceptions is not null;
    public bool HasInviteExceptions => InviteExceptions is not null;

    public bool HasKeyMode { get; }

    /// <summary>A password is needed to join. Ticked with a new one typed sets it; unticked takes it off.</summary>
    public bool KeyOn { get => _keyOn; set { this.RaiseAndSetIfChanged(ref _keyOn, value); this.RaisePropertyChanged(nameof(KeyNote)); } }
    public string NewKey { get => _newKey; set { this.RaiseAndSetIfChanged(ref _newKey, value); this.RaisePropertyChanged(nameof(KeyNote)); } }

    public string KeyNote =>
        !KeyOn ? (_hadKey ? IrcText.ModeKeyWillGo : "")
        : NewKey.Trim().Length > 0 ? IrcText.ModeKeyWillChange
        : _hadKey ? IrcText.ModeKeyKept : IrcText.ModeKeyNeeded;

    public string StatusText { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }
    public bool IsBusy { get => _busy; private set => this.RaiseAndSetIfChanged(ref _busy, value); }

    /// <summary>The usual letters in words; any other as itself. The same letter means different things on some servers: these are the common ones.</summary>
    public static (string Label, string Tip) Describe(char letter) => letter switch
    {
        'i' => (IrcText.ModeInviteOnly, IrcText.ModeInviteOnlyTip),
        'm' => (IrcText.ModeModerated, IrcText.ModeModeratedTip),
        'n' => (IrcText.ModeNoOutside, IrcText.ModeNoOutsideTip),
        't' => (IrcText.ModeTopicLock, IrcText.ModeTopicLockTip),
        's' => (IrcText.ModeSecret, IrcText.ModeSecretTip),
        'p' => (IrcText.ModePrivate, IrcText.ModePrivateTip),
        'R' => (IrcText.ModeRegisteredOnly, IrcText.ModeRegisteredOnlyTip),
        'M' => (IrcText.ModeRegisteredSpeak, IrcText.ModeRegisteredSpeakTip),
        'C' => (IrcText.ModeNoCtcp, IrcText.ModeNoCtcpTip),
        'c' => (IrcText.ModeNoColour, IrcText.ModeNoColourTip),
        'l' => (IrcText.ModeLimit, IrcText.ModeLimitTip),
        _ => (string.Format(CultureInfo.CurrentCulture, IrcText.ModeOther, letter), IrcText.ModeOtherTip),
    };

    /// <summary>
    /// What would change, as MODE wants it: the letters on and off with a value for each that takes one, in order.
    /// ⚠️ A setting with a value goes in a change of its own: a server takes only a few of those to a line (MODES).
    /// </summary>
    public IReadOnlyList<(string Changes, IReadOnlyList<string> Values)> Changes()
    {
        var list = new List<(string, IReadOnlyList<string>)>();

        var on = new StringBuilder();
        var off = new StringBuilder();
        foreach (var f in Flags.Where(f => f.IsOn != f.WasOn)) (f.IsOn ? on : off).Append(f.Letter);
        if (on.Length + off.Length > 0)
            list.Add(((on.Length > 0 ? "+" + on : "") + (off.Length > 0 ? "-" + off : ""), []));

        foreach (var v in Values)
        {
            var value = v.Value.Trim();
            if (v.IsOn && value.Length > 0 && (!v.WasOn || value != v.WasValue)) list.Add(("+" + v.Letter, [value]));
            else if (!v.IsOn && v.WasOn) list.Add(("-" + v.Letter, v.ValueToRemove ? [v.WasValue.Length > 0 ? v.WasValue : "*"] : []));
        }

        if (HasKeyMode)
        {
            var key = NewKey.Trim();
            if (KeyOn && key.Length > 0) list.Add(("+k", [key]));
            // ⚠️ Taking a key off takes a value, which servers do not check: the key itself is never held here.
            else if (!KeyOn && _hadKey) list.Add(("-k", ["*"]));
        }
        return list;
    }

    /// <summary>Whether what was ticked can be sent as it is: a value for each setting turned on that needs one.</summary>
    public string? Problem()
    {
        if (Values.FirstOrDefault(v => v.IsOn && v.Value.Trim().Length == 0) is { } empty)
            return string.Format(CultureInfo.CurrentCulture, IrcText.ModeNeedsValue, empty.Label);
        if (Values.FirstOrDefault(v => v is { Letter: 'l', IsOn: true } && !int.TryParse(v.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)) is { } limit)
            return string.Format(CultureInfo.CurrentCulture, IrcText.ModeNeedsNumber, limit.Label);
        if (HasKeyMode && KeyOn && !_hadKey && NewKey.Trim().Length == 0) return IrcText.ModeKeyNeeded;
        if (NewKey.Trim().Contains(' ')) return IrcText.ModeKeyNoSpaces;
        return null;
    }

    /// <summary>Sends what changed. True when it went (the server may still refuse a part, and says so where the channel is shown).</summary>
    public async Task<bool> SaveAsync()
    {
        if (Problem() is { } problem) { StatusText = problem; return false; }
        var changes = Changes();
        if (changes.Count == 0) return true;

        IsBusy = true;
        StatusText = "";
        try
        {
            foreach (var (letters, values) in changes)
                await Task.Run(() => _irc.SetModesAsync(_session, Channel, letters, values));
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; return false; }
        finally { IsBusy = false; }
    }

    internal async Task<IReadOnlyList<IrcBan>> ReadListAsync(char list)
    {
        StatusText = "";
        try { return await Task.Run(() => _irc.ModeListAsync(_session, Channel, list)); }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; return []; }
    }

    internal async Task<bool> ChangeListAsync(char list, string mask, bool add)
    {
        StatusText = "";
        try { await Task.Run(() => _irc.SetListEntryAsync(_session, Channel, list, mask, add)); return true; }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; return false; }
    }
}
