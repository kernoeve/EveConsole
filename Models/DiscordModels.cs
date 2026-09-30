namespace EveConsole.Models;

/// <summary>
/// A Discord channel webhook the user has named.
///
/// <para>Webhooks are the only way EVE Console reaches Discord: no bot, no sign-in. Whoever
/// manages a server makes one under a channel's Integrations and hands out its link, which is
/// all a post needs.</para>
///
/// <para>⚠️ The URL is a secret. Anyone holding it can post to that channel as the webhook, so
/// it is kept here and nowhere else — never in a log line, an error, a status line or an
/// exception message. Everything a person reads names the webhook by <see cref="Name"/>.</para>
/// </summary>
public class DiscordWebhook
{
    public int    Id   { get; set; }

    /// <summary>What the user calls it. Shown in every destination list, prefixed so a Discord
    /// webhook is never mistaken for a Slack channel or webhook of the same name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The https://discord.com/api/webhooks/{id}/{token} link. Bound by Discord to one
    /// channel, which is why the name matters: the link says nothing readable about where it
    /// lands.</summary>
    public string Url  { get; set; } = "";
}
