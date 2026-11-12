using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Reactive.Threading.Tasks;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Jabber;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>Group Chat. Tells the view model whether it can be seen, because that decides what counts as read.</summary>
public partial class GroupChatView : UserControl
{
    private GroupChatViewModel? _vm;
    private Window? _window;

    public GroupChatView()
    {
        InitializeComponent();

        DraftBox.AddHandler(KeyDownEvent, OnDraftKey, RoutingStrategies.Tunnel);
        EmoteBox.Target = DraftBox;

        DataContextChanged += (_, _) => Adopt(DataContext as GroupChatViewModel);
    }

    private void Adopt(GroupChatViewModel? vm)
    {
        if (_vm is not null) _vm.ScrollToEndRequested -= ScrollToEnd;

        _vm = vm;
        if (vm is null) return;

        vm.ScrollToEndRequested += ScrollToEnd;
        if (this.IsAttachedToVisualTree()) Arrive();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Dragged out to another window, it arrives here again with that one.
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.Activated   += OnWindowActivated;
            _window.Deactivated += OnWindowDeactivated;
        }
        Arrive();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_window is not null)
        {
            _window.Activated   -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
        }
        _window = null;
        if (_vm is not null) _vm.IsBeingLookedAt = false;
    }

    private void Arrive()
    {
        if (_vm is { } vm) vm.IsBeingLookedAt = _window?.IsActive ?? true;
        ScrollToEnd();
    }

    // ⚠️ By which event, not by asking the window: it raises Activated before its IsActive turns true.
    private void OnWindowActivated(object? sender, EventArgs e) { if (_vm is not null) _vm.IsBeingLookedAt = true; }
    private void OnWindowDeactivated(object? sender, EventArgs e) { if (_vm is not null) _vm.IsBeingLookedAt = false; }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm is { Lines.Count: > 0 } vm) LineList.ScrollIntoView(vm.Lines[^1]);
    }, DispatcherPriority.Background);

    /// <summary>Enter sends and Shift+Enter is a new line, as in every chat client.</summary>
    private void OnDraftKey(object? sender, KeyEventArgs e)
    {
        // Choosing from the emote list comes first: Enter there picks one.
        if (EmoteBox.HandleKey(e)) return;

        // Up in an empty box corrects this account's latest message; Escape stops replying or correcting.
        if (e.Key == Key.Up && string.IsNullOrEmpty(DraftBox.Text) && _vm?.BeginEditLast() == true)
        {
            e.Handled = true;
            DraftBox.CaretIndex = DraftBox.Text?.Length ?? 0;
            return;
        }
        if (e.Key == Key.Escape && _vm is { HasDraftBar: true } vm2) { e.Handled = true; vm2.CancelDraftMode(); return; }

        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (_vm is { CanSend: true } vm && !string.IsNullOrWhiteSpace(vm.Draft))
            vm.SendCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count == 0) return;

        var file = files[0];
        var size = (await file.GetBasicPropertiesAsync()).Size ?? 0;
        await using var content = await file.OpenReadAsync();
        await vm.SendFileAsync(content, file.Name, (long)size);
    }

    // ── Right-click: someone in the room, or the room ─────────────────────────

    /// <summary>
    /// Direct Message for anyone else, then whatever the room lets the person do to them. ⚠️ Only lines the room allows
    /// are there at all, worked out when the menu opens, so they are always for the person and room as they are now.
    /// </summary>
    private void OnOccupantMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: JabberOccupantVm person } at || _vm is not { } vm) return;
        e.Handled = true;
        if (person.IrcMember is not null) { ShowIrcPersonMenu(at, vm, person); return; }

        var rights = vm.RightsOver(person);
        var room = vm.RoomName;
        var lines = new List<Control>();
        if (!person.IsSelf) lines.Add(Line(GroupChatText.MenuDirectMessage, () => { vm.MessageOccupant(person); return Task.CompletedTask; }));

        var admin = new List<Control>();
        if (rights.GiveVoice) admin.Add(Line(GroupChatText.MenuGiveVoice, () => vm.SetRoleAsync(person, "participant")));
        if (rights.TakeVoice) admin.Add(Line(GroupChatText.MenuTakeVoice, () => vm.SetRoleAsync(person, "visitor")));
        if (rights.MakeModerator) admin.Add(Line(GroupChatText.MenuMakeModerator, () => vm.SetRoleAsync(person, "moderator")));
        if (rights.RemoveModerator) admin.Add(Line(GroupChatText.MenuRemoveModerator, () => vm.SetRoleAsync(person, "participant")));
        if (rights.MakeMember) admin.Add(Line(GroupChatText.MenuMakeMember, () => vm.SetAffiliationAsync(person, "member")));
        if (rights.RemoveMember) admin.Add(Line(GroupChatText.MenuRemoveMember, () => vm.SetAffiliationAsync(person, "none")));
        if (rights.MakeAdmin) admin.Add(Line(GroupChatText.MenuMakeAdmin, () => vm.SetAffiliationAsync(person, "admin")));
        if (rights.RemoveAdmin) admin.Add(Line(GroupChatText.MenuRemoveAdmin, () => vm.SetAffiliationAsync(person, "member")));
        if (rights.Kick) admin.Add(Line(GroupChatText.MenuKick, async () =>
        {
            if (await AskAsync(Format(GroupChatText.KickTitle, person.Nick), "", GroupChatText.LabelReason, "", null) is { } reason)
                await vm.SetRoleAsync(person, "none", reason);
        }));
        if (rights.Ban) admin.Add(Line(GroupChatText.MenuBan, async () =>
        {
            if (await AskAsync(Format(GroupChatText.BanTitle, person.Nick), "", GroupChatText.LabelReason, "", null) is not { } reason) return;
            if (await ConfirmAsync(Format(GroupChatText.ConfirmBan, person.Nick, person.Person?.RealJid ?? "", room)))
                await vm.SetAffiliationAsync(person, "outcast", reason);
        }));

        if (lines.Count > 0 && admin.Count > 0) lines.Add(new Separator());
        lines.AddRange(admin);
        Show(at, lines);
    }

    /// <summary>A room's own menu: its topic, inviting, bans, leaving. The room is opened first, as a click would.</summary>
    private void OnRowMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: GroupChatRowVm { IsRoom: true } row } at || _vm is not { } vm) return;
        e.Handled = true;
        vm.SelectedRow = row;
        if (row.IsIrc) { ShowIrcChannelMenu(at, vm, row); return; }

        var rights = vm.RoomRights();
        var room = row.ShownText;
        var lines = new List<Control>();
        if (rights.ChangeTopic) lines.Add(Line(GroupChatText.MenuChangeTopic, async () =>
        {
            if (await AskAsync(Format(GroupChatText.TopicTitle, room), "", GroupChatText.LabelTopic, "", null) is { Length: > 0 } topic)
                await vm.SetSubjectAsync(topic);
        }));
        if (rights.Invite) lines.Add(Line(GroupChatText.MenuInvite, async () =>
        {
            if (await AskAsync(Format(GroupChatText.InviteTitle, room), "", GroupChatText.LabelInvitee, "name@server", vm.KnownAddresses()) is { Length: > 0 } who)
                await vm.InviteAsync(who, "");
        }));
        if (rights.LiftBan) lines.Add(Line(GroupChatText.MenuLiftBan, async () =>
        {
            var bans = await vm.BansAsync();
            if (bans.Count == 0) { if (!vm.HasStatus) vm.SetStatus(Format(GroupChatText.NoBans, room)); return; }

            var shown = bans.Select(b => b.Reason.Length > 0 ? $"{b.Jid}  ({b.Reason})" : b.Jid).ToList();
            if (await AskAsync(Format(GroupChatText.LiftBanTitle, room), "", GroupChatText.LabelBanned, "", shown) is not { Length: > 0 } picked) return;

            var jid = picked.Split("  (")[0].Trim();
            if (bans.Any(b => b.Jid == jid)) await vm.LiftBanAsync(jid);
            else vm.SetStatus(GroupChatText.PickBanned);
        }));
        if (lines.Count > 0) lines.Add(new Separator());
        lines.Add(Line(JabberText.LeaveRoom, () => vm.LeaveRoomCommand.Execute().ToTask()));
        Show(at, lines);
    }

    /// <summary>
    /// Someone in an IRC channel: Direct Message, then what this person's rank there lets them do. ⚠️ Ranks on IRC are
    /// modes; a server may still refuse, and says so in the status line.
    /// </summary>
    private void ShowIrcPersonMenu(Control at, GroupChatViewModel vm, JabberOccupantVm person)
    {
        var member = person.IrcMember!;
        var lines = new List<Control>();
        if (!person.IsSelf) lines.Add(Line(GroupChatText.MenuDirectMessage, () => { vm.MessageOccupant(person); return Task.CompletedTask; }));

        var admin = new List<Control>();
        foreach (var (mode, give, take) in vm.IrcRankChoices())
            admin.Add(member.Ranks.Contains(mode)
                ? Line(take, () => vm.IrcSetRankAsync(person, mode, false))
                : Line(give, () => vm.IrcSetRankAsync(person, mode, true)));
        if (vm.IrcCanModerate() && !person.IsSelf)
        {
            admin.Add(Line(GroupChatText.MenuKick, async () =>
            {
                if (await AskAsync(Format(GroupChatText.KickTitle, member.Nick), "", GroupChatText.LabelReason, "", null) is { } reason)
                    await vm.IrcKickAsync(person, reason);
            }));
            admin.Add(Line(GroupChatText.MenuBan, async () =>
            {
                if (await AskAsync(Format(GroupChatText.BanTitle, member.Nick), "", GroupChatText.LabelReason, "", null) is not { } reason) return;
                if (await ConfirmAsync(Format(IrcText.ConfirmBan, member.Nick, member.UserHost.Length > 0 ? member.UserHost : member.Nick, vm.RoomName)))
                    await vm.IrcBanAsync(person, reason);
            }));
        }

        if (lines.Count > 0 && admin.Count > 0) lines.Add(new Separator());
        lines.AddRange(admin);
        Show(at, lines);
    }

    /// <summary>An IRC channel's own menu: its topic, inviting someone, leaving.</summary>
    private void ShowIrcChannelMenu(Control at, GroupChatViewModel vm, GroupChatRowVm row)
    {
        var room = row.ShownText;
        var lines = new List<Control>
        {
            Line(GroupChatText.MenuChangeTopic, async () =>
            {
                if (await AskAsync(Format(GroupChatText.TopicTitle, room), "", GroupChatText.LabelTopic, "", null) is { Length: > 0 } topic)
                    await vm.IrcSetTopicAsync(topic);
            }),
            Line(GroupChatText.MenuInvite, async () =>
            {
                if (await AskAsync(Format(GroupChatText.InviteTitle, room), "", IrcText.LabelInviteNick, "", vm.IrcKnownNicks()) is { Length: > 0 } who)
                    await vm.IrcInviteAsync(who);
            }),
        };
        if (vm.IrcCanModerate()) lines.Add(Line(GroupChatText.MenuLiftBan, async () =>
        {
            var bans = await vm.IrcBansAsync();
            if (bans.Count == 0) { if (!vm.HasStatus) vm.SetStatus(Format(GroupChatText.NoBans, room)); return; }

            var shown = bans.Select(b => b.SetBy.Length > 0 ? $"{b.Mask}  ({b.SetBy})" : b.Mask).ToList();
            if (await AskAsync(Format(GroupChatText.LiftBanTitle, room), "", GroupChatText.LabelBanned, "", shown) is not { Length: > 0 } picked) return;

            var mask = picked.Split("  (")[0].Trim();
            if (bans.Any(b => b.Mask == mask)) await vm.IrcLiftBanAsync(mask);
            else vm.SetStatus(GroupChatText.PickBanned);
        }));
        // Its settings and lists, for an operator: a window of their own, as there are many.
        if (vm.IrcChannelSettings() is { } settings && TopLevel.GetTopLevel(this) is Window host)
            lines.Add(Line(IrcText.MenuChannelSettings, () => { IrcChannelSettingsWindow.ShowFor(settings, host); return Task.CompletedTask; }));
        lines.AddRange(new List<Control>
        {
            new Separator(),
            Line(JabberText.LeaveRoom, () => vm.LeaveRoomCommand.Execute().ToTask()),
        });
        Show(at, lines);
    }

    private static string Format(string text, params object[] values) => string.Format(System.Globalization.CultureInfo.CurrentCulture, text, values);

    private static MenuItem Line(string header, Func<Task> act)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await act();
        return item;
    }

    /// <summary>
    /// Opens the menu on what was right-clicked, and only while it is open. ⚠️ Closed when the window stops being the one
    /// in use: a menu is a topmost window of its own and would otherwise sit over the next program.
    /// </summary>
    private void Show(Control at, List<Control> lines)
    {
        if (lines.Count == 0) return;

        var menu = new ContextMenu { ItemsSource = lines };
        var window = TopLevel.GetTopLevel(this) as Window;
        void Leave(object? s, EventArgs a) => menu.Close();
        if (window is not null) window.Deactivated += Leave;
        menu.Closed += (_, _) =>
        {
            at.ContextMenu = null;
            if (window is not null) window.Deactivated -= Leave;
        };
        at.ContextMenu = menu;
        menu.Open(at);
    }

    private async Task<string?> AskAsync(string title, string message, string label, string hint, IEnumerable<string>? choices) =>
        TopLevel.GetTopLevel(this) is Window owner ? await new AskDialog(title, message, label, hint, choices).ShowDialog<string?>(owner) : null;

    private async Task<bool> ConfirmAsync(string message) =>
        // ⚠️ No window means nobody to ask, so nothing that cannot be undone goes ahead.
        TopLevel.GetTopLevel(this) is Window owner && await new ConfirmDialog(message).ShowDialog<bool>(owner);


    // ── Reactions ─────────────────────────────────────────────────────────────

    private Flyout? _reactFlyout;
    private ReactionPicker? _reactPicker;
    private Func<string, Task>? _reacting;

    /// <summary>The message a control under it belongs to.</summary>
    private static T? LineOf<T>(object? sender) where T : class =>
        (sender as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<T>().FirstOrDefault();

    /// <summary>
    /// The picker, at the message's own button. ⚠️ Put away when the window stops being the one in use: a flyout is a
    /// topmost window of its own and would otherwise sit over the next program.
    /// </summary>
    private void OpenReactionPicker(Control at, Func<string, Task> react)
    {
        if (_reactFlyout is null)
        {
            _reactPicker = new ReactionPicker();
            _reactFlyout = new Flyout { Content = _reactPicker, Placement = PlacementMode.TopEdgeAlignedRight };
            _reactPicker.Picked += emoji =>
            {
                _reactFlyout.Hide();
                if (_reacting is { } chosen) _ = chosen(emoji);
            };
            if (TopLevel.GetTopLevel(this) is Window window) window.Deactivated += (_, _) => _reactFlyout.Hide();
        }

        _reacting = react;
        _reactPicker!.Reset();
        _reactFlyout.ShowAt(at);
    }

    private void OnReply(object? sender, RoutedEventArgs e)
    {
        if (LineOf<GroupLineVm>(sender) is { } line && _vm is { } vm) { vm.BeginReply(line); DraftBox.Focus(); }
    }

    private void OnEditOwn(object? sender, RoutedEventArgs e)
    {
        if (LineOf<GroupLineVm>(sender) is { } line && _vm is { } vm) { vm.BeginEdit(line); DraftBox.Focus(); DraftBox.CaretIndex = DraftBox.Text?.Length ?? 0; }
    }

    private async void OnDeleteOwn(object? sender, RoutedEventArgs e)
    {
        if (LineOf<GroupLineVm>(sender) is not { } line || _vm is not { } vm) return;
        if (await ConfirmAsync(EveConsole.Localization.JabberText.ConfirmDeleteOwn)) await vm.DeleteOwnAsync(line);
    }

    private void OnCancelDraftMode(object? sender, RoutedEventArgs e) => _vm?.CancelDraftMode();

    private void OnAddReaction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control at && LineOf<GroupLineVm>(sender) is { } line && _vm is { } vm)
            OpenReactionPicker(at, emoji => vm.ToggleReactionAsync(line, emoji));
    }

    private void OnReactionChip(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ReactionChipVm chip } && LineOf<GroupLineVm>(sender) is { } line && _vm is { } vm)
            _ = vm.ToggleReactionAsync(line, chip.Emoji);
    }

    /// <summary>Opens a link from a message in the browser. Only web addresses ever become links.</summary>
    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            ExternalLinks.Open(url);
    }
}
