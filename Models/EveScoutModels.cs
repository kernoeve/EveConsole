namespace EveConsole.Models;

/// <summary>
/// One wormhole between Thera or Turnur and the rest of New Eden, as EVE-Scout's public list has
/// it (api.eve-scout.com/v2/public/signatures). The whole list is replaced on every read, so a
/// row is never older than the last poll; turning the poll off deletes them all, since an old
/// connection is worse than none.
/// </summary>
public class EveScoutConnection
{
    /// <summary>EVE-Scout's own id for the signature.</summary>
    public string          Id             { get; set; } = "";
    /// <summary>Thera or Turnur.</summary>
    public int             HubSystemId    { get; set; }
    public string          HubSystemName  { get; set; } = "";
    public string          HubSignature   { get; set; } = "";
    public int             OtherSystemId  { get; set; }
    public string          OtherSystemName { get; set; } = "";
    public string          OtherSignature { get; set; } = "";
    public int?            OtherRegionId  { get; set; }
    public string          OtherRegionName { get; set; } = "";
    /// <summary>"hs", "ls", "ns", "c2" … as EVE-Scout writes it.</summary>
    public string          OtherClass     { get; set; } = "";
    public string          WormholeType   { get; set; } = "";
    /// <summary>"small", "medium", "large", "xlarge" or "capital".</summary>
    public string          MaxShipSize    { get; set; } = "";
    public DateTimeOffset? ExpiresAt      { get; set; }
    public DateTimeOffset  ReadAt         { get; set; }
}
