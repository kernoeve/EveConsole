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
    private readonly MainWindowViewModel? _main;
    private readonly int? _serverId;
    private readonly MumbleControlMode _mode;
    private MumbleOverlayColumn? _column;
    private PanelTalkPopup? _popup;
    private PanelMumbleListVm? _list;

    public PanelMumbleControl() => InitializeComponent();

    public PanelMumbleControl(MainWindowViewModel? main, ControlOptions? options) : this()
    {
        _main = main;
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
        if (_main == null || _serverId is not { } serverId)
        {
            LineState.Text = ClientLayoutsText.MumbleChooseServer;
            return;
        }

        if (_mode == MumbleControlMode.List)
        {
            _list = new PanelMumbleListVm(_main.MumbleService, serverId);
            ListUsers.ItemsSource = _list.Users;
            _list.Start();
        }

        _main.MumbleVm.OverlayColumns.CollectionChanged += OnColumnsChanged;
        _main.MumbleService.SessionChanged += OnSessionChanged;
        FindColumn();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _list?.Dispose();
        _list = null;
        if (_main != null)
        {
            _main.MumbleVm.OverlayColumns.CollectionChanged -= OnColumnsChanged;
            _main.MumbleService.SessionChanged -= OnSessionChanged;
        }
        UseColumn(null);
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
        if (_main == null) return;
        UseColumn(_main.MumbleVm.OverlayColumns.FirstOrDefault(c => c.ServerId == _serverId));
        if (_column != null) return;

        var session = _serverId is { } id ? _main.MumbleService.SessionOf(id) : null;
        LineState.Text = session == null
            ? ClientLayoutsText.MumbleNoServer
            : string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.MumbleServerState,
                session.Server.Name.Length > 0 ? session.Server.Name : session.Server.Host, MumbleViewModel.StateText(session));
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
            _popup?.Hide();
            return;
        }
        _popup ??= new PanelTalkPopup();
        _popup.Show(_column.Talking, this);
    }
}
