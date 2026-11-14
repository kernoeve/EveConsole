using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.Localization;
using EveConsole.Services.ClientLayouts;
using EveConsole.Services.Mumble;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// A Mumble control on a panel, for one chosen server. Both modes show the toolbar's own bar for the
/// server and its channel. The single line mode shows who is talking in a window beside it, using
/// the always-on-top list's own talkers, so it follows the same rules, including how long someone
/// stays; the list mode lists the whole channel under the bar instead, talkers floating to the top.
/// </summary>
public partial class PanelMumbleControl : UserControl
{
    private readonly MumbleService? _service;
    private readonly MumbleViewModel? _vm;
    private readonly int? _serverId;
    private readonly MumbleControlMode _mode;
    private MumbleOverlayColumn? _column;
    private PanelTalkPopup? _popup;
    private PanelMumbleListVm? _list;
    private MumbleRowMenu? _menu;

    public PanelMumbleControl() => InitializeComponent();

    public PanelMumbleControl(MainWindowViewModel? main, ControlOptions? options) : this(main?.MumbleService, main?.MumbleVm, options) { }

    /// <summary>The Mumble parts alone: all this needs, and what a test puts it on a screen with.</summary>
    public PanelMumbleControl(MumbleService? service, MumbleViewModel? vm, ControlOptions? options) : this()
    {
        _service = service;
        _vm = vm;
        _serverId = options?.MumbleServerId;
        _mode = PanelControls.MumbleModeOf(options);
        ListMode.IsVisible = _mode == MumbleControlMode.List;
        if (_mode == MumbleControlMode.List)
        {
            // The line sits at the top, above the channel.
            Line.Margin = new Thickness(0, 4, 0, 0);
        }
        else
        {
            // Alone, the line sits halfway down whatever it was docked in.
            Grid.SetRowSpan(Line, 2);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_service == null || _vm == null || _serverId is not { } serverId)
        {
            LineState.Text = ClientLayoutsText.MumbleChooseServer;
            return;
        }

        if (_mode == MumbleControlMode.List)
        {
            _list = new PanelMumbleListVm(_service, serverId);
            ListUsers.ItemsSource = _list.Users;
            _list.Start();
        }

        _vm.OverlayColumns.CollectionChanged += OnColumnsChanged;
        _service.SessionChanged += OnSessionChanged;
        FindColumn();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _list?.Dispose();
        _list = null;
        if (_service != null && _vm != null)
        {
            _vm.OverlayColumns.CollectionChanged -= OnColumnsChanged;
            _service.SessionChanged -= OnSessionChanged;
        }
        UseColumn(null);
        _menu?.Close();
        _popup?.Close();
        _popup = null;
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => FindColumn();

    private void OnSessionChanged(MumbleSession session)
    {
        if (session.Server.Id == _serverId) Dispatcher.UIThread.Post(FindColumn);
    }

    /// <summary>A server has a column only while connected, and a new one after every reconnect.</summary>
    private void FindColumn()
    {
        if (_service == null || _vm == null) return;
        UseColumn(_vm.OverlayColumns.FirstOrDefault(c => c.ServerId == _serverId));

        var session = _serverId is { } id ? _service.SessionOf(id) : null;
        ShowPower(session);
        if (_column != null) return;

        LineState.Text = session == null
            ? ClientLayoutsText.MumbleNoServer
            : string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.MumbleServerState,
                session.Server.Name.Length > 0 ? session.Server.Name : session.Server.Host, MumbleViewModel.StateText(session));
    }

    /// <summary>Green while connected or trying to, so the button says it will disconnect; grey when it will connect.</summary>
    private void ShowPower(MumbleSession? session)
    {
        Power.IsVisible = session != null;
        var on = session is { State: not (MumbleState.Offline or MumbleState.Failed) };
        PowerIcon[!Avalonia.Controls.Shapes.Shape.StrokeProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(on ? "GoodBrush" : "TextMutedBrush");
        ToolTip.SetTip(Power, on ? MumbleText.Disconnect : MumbleText.Connect);
    }

    private void OnPower(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_service == null || _serverId is not { } id || _service.SessionOf(id) is not { } session) return;
        if (session.State is MumbleState.Offline or MumbleState.Failed) _service.Connect(session);
        else _ = _service.DisconnectAsync(session);
    }

    private void UseColumn(MumbleOverlayColumn? column)
    {
        if (ReferenceEquals(_column, column)) return;
        if (_column != null) _column.Talking.CollectionChanged -= OnTalkingChanged;
        _column = column;
        if (_column != null) _column.Talking.CollectionChanged += OnTalkingChanged;

        Bar.DataContext = column;
        Bar.IsVisible = column != null;
        LineState.IsVisible = column == null;
        OnTalkingChanged(null, null);
    }

    private void OnTalkingChanged(object? sender, NotifyCollectionChangedEventArgs? e)
    {
        // The list mode has no talk window: its talkers float to the top of the list instead.
        if (_mode == MumbleControlMode.List) return;
        if (_column == null || _column.Talking.Count == 0)
        {
            // Not under an open menu: what it was opened on goes on being shown until it closes.
            if (_popup is { MenuOpen: false }) _popup.Hide();
            return;
        }
        if (_popup == null)
        {
            _popup = new PanelTalkPopup();
            _popup.PersonMenuRequested += line => OpenPersonMenu(line.Session, _popup.MenuAnchor, _popup);
        }
        _popup.Show(_column.Talking, this);
    }

    private void OnPersonMenu(object? sender, Avalonia.Controls.ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: MumbleOverlayLine line }) return;
        e.Handled = true;
        OpenPersonMenu(line.Session, ListMode, null);
    }

    /// <summary>
    /// The channel tree's person menu, for someone in this control's list or talk window. ⚠️ Who it is for is taken now, as
    /// the right-click happens: a talker drops out of the window a few seconds after they stop, and the list reorders as
    /// people talk, so by the time something is chosen the line may be gone or be someone else. So the menu hangs from
    /// what stays (the list, the window), never from the line, and acts on the person it was opened for.
    /// </summary>
    private void OpenPersonMenu(uint user, Control anchor, PanelTalkPopup? popup)
    {
        if (_vm is not { } vm || _serverId is not { } serverId || vm.PersonRow(serverId, user) is not { } row) return;

        vm.SelectedRow = row;

        // ⚠️ A new one each time: one that has opened in the talk window will not open again in the list's, which is
        // another window.
        _menu?.Close();
        var menu = _menu = new MumbleRowMenu { DataContext = vm, Host = TopLevel.GetTopLevel(this) as Window };

        // The talk window takes no focus by itself. Given it now, a click anywhere else takes it away again, which is what
        // puts the menu away: otherwise it would stay over the game.
        var window = TopLevel.GetTopLevel(anchor) as Window;
        if (popup != null)
        {
            popup.MenuOpen = true;
            popup.Activate();
        }

        anchor.ContextMenu = menu;
        menu.Closed += Closed;
        if (window != null) window.Deactivated += Deactivated;
        menu.Open(anchor);

        void Deactivated(object? s, EventArgs a) => menu.Close();
        void Closed(object? s, Avalonia.Interactivity.RoutedEventArgs a)
        {
            menu.Closed -= Closed;
            if (window != null) window.Deactivated -= Deactivated;
            if (ReferenceEquals(anchor.ContextMenu, menu)) anchor.ContextMenu = null;
            if (ReferenceEquals(_menu, menu)) _menu = null;
            if (popup == null) return;
            popup.MenuOpen = false;
            OnTalkingChanged(null, null);
        }
    }
}
