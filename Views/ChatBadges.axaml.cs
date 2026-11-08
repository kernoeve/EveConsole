using Avalonia.Controls;

namespace EveConsole.Views;

public enum ChatBadge { Messages, GroupChat, Mail }

/// <summary>The Eve Mail, direct messages or group chat icon, with its counts.</summary>
public partial class ChatBadges : UserControl
{
    private ChatBadge _badge;

    public ChatBadges()
    {
        InitializeComponent();
        Badge = ChatBadge.Messages;
    }

    public ChatBadge Badge
    {
        get => _badge;
        set
        {
            _badge = value;
            MessagesItem.IsVisible = value == ChatBadge.Messages;
            GroupChatItem.IsVisible = value == ChatBadge.GroupChat;
            MailItem.IsVisible = value == ChatBadge.Mail;
        }
    }
}
