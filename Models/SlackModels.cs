namespace EveConsole.Models;

/// <summary>
/// A Slack incoming webhook the user has named.
///
/// <para>Webhooks exist for workspaces you cannot connect to as yourself — an alliance Slack,
/// typically, which will hand out a webhook where it would never hand out a token for its own
/// workspace.</para>
///
/// <para>⚠️ Named and kept, rather than pasted wherever one is needed. The URL is a long opaque
/// secret; retyping it per area meant three copies of the same string with no way to tell whether
/// they were the same webhook, and no way to change it in one place when it was rotated.</para>
/// </summary>
public class SlackWebhook
{
    public int    Id   { get; set; }

    /// <summary>What the user calls it. Shown in every destination dropdown, prefixed so a
    /// webhook is never mistaken for a channel in the same list.</summary>
    public string Name { get; set; } = "";

    /// <summary>The https://hooks.slack.com/services/… URL. Bound by Slack to one channel, which
    /// is why the name matters: the URL says nothing about where it lands.</summary>
    public string Url  { get; set; } = "";
}
