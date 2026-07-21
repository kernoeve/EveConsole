namespace EveConsole.Models;

// ── DB entities ───────────────────────────────────────────────────────────────

public class EveMailHeader
{
    public int            MailId      { get; set; }
    public long           CharacterId { get; set; }
    public long           FromId      { get; set; }
    public string         FromName    { get; set; } = "";
    public string         Subject     { get; set; } = "";
    public DateTimeOffset Timestamp   { get; set; }
    public bool           IsRead      { get; set; }
    public string         Labels      { get; set; } = ""; // comma-separated label IDs
    public bool           BodyFetched { get; set; }
}

public class EveMailBody
{
    public int    MailId { get; set; }
    public string Body   { get; set; } = "";
}

public class EveMailRecipientEntry
{
    public int    Id            { get; set; }
    public int    MailId        { get; set; }
    public long   RecipientId   { get; set; }
    public string RecipientType { get; set; } = ""; // character/corporation/alliance/mailing_list
    public string RecipientName { get; set; } = "";
}

public class EveMailLabelEntry
{
    public long   CharacterId { get; set; }
    public int    LabelId     { get; set; }
    public string Name        { get; set; } = "";
    public string Color       { get; set; } = "";
    public int    UnreadCount { get; set; }
}
