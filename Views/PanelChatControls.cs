using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// The icon for direct messages or for group chat, as on the main window's bar. Clicked, it drops
/// down what is new: the conversations, or the rooms, with something not yet read, each opening
/// where it is in its tool, and a link to the tool itself.
/// </summary>
public sealed class PanelChatLine : PanelDropLine
{
    private const double DropWidth = 340, DropHeight = 300;

    private readonly MainWindowViewModel _main;
    private readonly bool _messages;
    private readonly StackPanel _list;
    private readonly TextBlock _nothing;

    public static PanelChatLine Messages(MainWindowViewModel main) => new(main, true);
    public static PanelChatLine GroupChat(MainWindowViewModel main) => new(main, false);

    private PanelChatLine(MainWindowViewModel main, bool messages)
        : this(main, messages, new StackPanel { Spacing = 2 }, new TextBlock
        {
            Text = messages ? ClientLayoutsText.ChatNoNewMessages : ClientLayoutsText.ChatNoNewGroup,
            FontSize = 11, FontStyle = FontStyle.Italic, Margin = new Thickness(2, 4),
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextFaintBrush"),
        })
    {
    }

    private PanelChatLine(MainWindowViewModel main, bool messages, StackPanel list, TextBlock nothing)
        : base(Icon(main, messages), Dropped(main, messages, list, nothing), DropWidth, DropHeight)
    {
        _main = main;
        _messages = messages;
        _list = list;
        _nothing = nothing;
        DataContext = main;
        ToolTip.SetTip(this, messages ? main.MessagesVm.WindowTitle : main.GroupChatVm.IconTip);
    }

    private static Control Icon(MainWindowViewModel main, bool messages) => new Border
    {
        Padding = new Thickness(6, 0),
        Child = new ChatBadges
        {
            Badge = messages ? ChatBadge.Messages : ChatBadge.GroupChat,
            DataContext = main,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static Control Dropped(MainWindowViewModel main, bool messages, StackPanel list, TextBlock nothing)
    {
        var title = new TextBlock
        {
            Text = messages ? MessagesText.WindowTitle : ShellText.NavGroupChat,
            FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6),
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("AccentBrush"),
        };
        var open = PanelLinks.TextLink(messages ? ClientLayoutsText.ChatOpenMessages : ClientLayoutsText.ChatOpenGroupChat, () =>
        {
            if (messages) main.OpenMessagesCommand.Execute().Subscribe();
            else main.OpenGroupChatCommand.Execute().Subscribe();
            main.ShowTool(messages ? "messages" : "group_chat");
        });
        open.Margin = new Thickness(0, 6, 0, 0);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(10, 8) };
        Grid.SetRow(title, 0);
        var scroll = new ScrollViewer { Content = new StackPanel { Children = { nothing, list } } };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(open, 2);
        grid.Children.Add(title);
        grid.Children.Add(scroll);
        grid.Children.Add(open);
        return grid;
    }

    protected override void Shown()
    {
        if (_messages) _main.MessagesVm.PropertyChanged += OnCountsChanged;
        else _main.GroupChatVm.PropertyChanged += OnCountsChanged;
    }

    protected override void Hidden()
    {
        _main.MessagesVm.PropertyChanged -= OnCountsChanged;
        _main.GroupChatVm.PropertyChanged -= OnCountsChanged;
    }

    protected override void Dropped() => Fill();

    private void OnCountsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("TotalUnread" or "TotalMentions")) return;
        ToolTip.SetTip(this, _messages ? _main.MessagesVm.WindowTitle : _main.GroupChatVm.IconTip);
        if (IsDropped) Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        if (_messages)
            foreach (var c in _main.MessagesVm.UnreadConversations())
                _list.Children.Add(Row(c.Name, c.Where, c.LastText, c.LastAt, c.UnreadText, null, () =>
                {
                    _main.MessagesVm.Open(c.Summary.Key, c.Name);
                    _main.ShowTool("messages");
                }));
        else
        {
            var server = "";
            foreach (var row in _main.GroupChatVm.Rows)
            {
                if (row.IsServer && !row.IsMumble) { server = row.ShownText; continue; }
                if (!row.HasUnread && !row.HasMentions) continue;
                var where = row.IsMumble ? "Mumble" : server;
                _list.Children.Add(Row(row.ShownText, where, "", "", row.HasUnread ? row.UnreadText : "",
                    row.HasMentions ? row.MentionsText : null, () =>
                    {
                        _main.ShowTool("group_chat");
                        _main.GroupChatVm.SelectedRow = row;
                    }));
            }
        }
        _nothing.IsVisible = _list.Children.Count == 0;
    }

    /// <summary>One conversation or room: who or where, what was said last, and how much is new. Clicked, it opens.</summary>
    private Control Row(string name, string where, string last, string when, string unread, string? mentions, Action open)
    {
        var head = new DockPanel();
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (unread.Length > 0)
            counts.Children.Add(new TextBlock
            {
                Text = unread, FontSize = 11, FontWeight = FontWeight.SemiBold,
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension(_messages ? "WarnBrush" : "TextPrimaryBrush"),
            });
        if (mentions != null)
            counts.Children.Add(new TextBlock
            {
                Text = mentions, FontSize = 11, FontWeight = FontWeight.SemiBold,
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("WarnBrush"),
            });
        DockPanel.SetDock(counts, Dock.Right);
        head.Children.Add(counts);
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = name, FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                    [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextBrightBrush"),
                },
                new TextBlock
                {
                    Text = where, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextFaintBrush"),
                },
            },
        });

        var body = new StackPanel { Spacing = 1, Children = { head } };
        if (last.Length > 0)
        {
            var said = new DockPanel();
            if (when.Length > 0)
            {
                var at = new TextBlock
                {
                    Text = when, FontSize = 10, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextFaintBrush"),
                };
                DockPanel.SetDock(at, Dock.Right);
                said.Children.Add(at);
            }
            said.Children.Add(new TextBlock
            {
                Text = last, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush"),
            });
            body.Children.Add(said);
        }

        var button = new Button
        {
            Content = body,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) =>
        {
            PutAway();
            open();
        };
        return button;
    }
}
