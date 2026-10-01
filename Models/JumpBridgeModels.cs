namespace EveConsole.Models;

/// <summary>
/// A jump bridge the user entered by hand: one row per bridged pair of systems, both directions.
///
/// <para>The ones ESI can see — Ansiblexes owned by a corporation with a token — are never stored
/// here; they are read from the corporation's structures each time (see JumpBridgeService). This
/// holds only what ESI cannot tell: bridges of other corporations and alliances, or a pair whose
/// gates have not been named "A » B" in the game.</para>
///
/// <para>⚠️ Stored with <see cref="FromSystemId"/> the lower id, so a pair entered either way
/// round is the same row and the unique index can refuse the second.</para>
/// </summary>
public class ManualJumpBridge
{
    public int            Id           { get; set; }
    public int            FromSystemId { get; set; }
    public int            ToSystemId   { get; set; }
    public string         Note         { get; set; } = "";
    public DateTimeOffset CreatedAt    { get; set; }
}
