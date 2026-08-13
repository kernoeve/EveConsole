namespace EveConsole.Models;

/// <summary>Who last wrote a structure record. Stamped on every write so a hand-entered value is
/// distinguishable from one ESI supplied.</summary>
public static class StructureSource
{
    /// <summary>Written by the polling sync from what ESI returned.</summary>
    public const string Esi = "esi";

    /// <summary>Typed by the user in the Structure Browser. Overwritten by the next successful
    /// ESI refresh for structures we can read — which is expected, and is the whole reason the
    /// source is recorded rather than guessed at.</summary>
    public const string User = "user";
}

/// <summary>
/// The app's own record of a player structure, and the one the Structure Browser reads and writes.
///
/// <para>⚠️ Distinct from <see cref="StructureName"/> on purpose. That table is ESI's: the polling
/// service owns every row and rewrites it on each resolve, so editing it would mean the UI writing
/// into polled data and losing the edit without trace. This table is fed FROM it and is free to
/// hold values ESI never gave us — which is what lets a structure we have no access to be described
/// by hand, and what lets a row exist here with no ESI counterpart at all.</para>
///
/// <para><see cref="StructureId"/> is the in-game location id and is never editable: it is the
/// identity of the thing, and the only field that cannot be re-derived or corrected.</para>
/// </summary>
public class Structure
{
    public long   StructureId        { get; set; }
    public string Name               { get; set; } = "";
    public int    SolarSystemId      { get; set; }
    public int    TypeId             { get; set; }
    public long   OwnerId            { get; set; }
    public long   AllianceId         { get; set; }
    public double X                  { get; set; }
    public double Y                  { get; set; }
    public double Z                  { get; set; }
    public long   NearestCelestialId { get; set; }
    public string NearestCelestial   { get; set; } = "";

    /// <summary>Mirrors <see cref="StructureStatus"/> for rows that came from ESI. A row created
    /// by hand for a structure we cannot read keeps whatever the lookup last said, so the browser
    /// can still show why it is not refreshing.</summary>
    public int Status { get; set; }

    /// <summary>Free text the user can attach — access notes, who to ask for a docking invite,
    /// anything ESI will never carry. Never touched by the sync.</summary>
    public string Notes { get; set; } = "";

    /// <summary><see cref="StructureSource"/>. Which side wrote the row last.</summary>
    public string UpdatedBy { get; set; } = StructureSource.Esi;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One hand-entered fitted module, in any band.
///
/// <para>⚠️ One table for every band rather than one per band. The two earlier tables covered rigs
/// and service modules only, which left nowhere to record a high, mid or low — and the moment a
/// second shape existed, every read would have had to union them and every write pick between
/// them. Band is stored as the enum's name so the table reads plainly in SQL.</para>
///
/// <para>Only ever holds fittings for structures whose modules we CANNOT see in assets. When
/// assets arrive for a structure these rows are deleted, because the game is then the authority
/// and two answers would be worse than one.</para>
/// </summary>
public class StructureFitting
{
    public int    Id          { get; set; }
    public long   StructureId { get; set; }

    /// <summary><see cref="EveConsole.Controls.FittingBand"/> as a string.</summary>
    public string Band        { get; set; } = "";

    public int    SlotIndex   { get; set; }
    public int    TypeId      { get; set; }
}

/// <summary>A service module on an Indy Parks structure. Kept separate from
/// <see cref="StructureFitting"/> because a park entry describes a planned or hypothetical
/// structure that need not correspond to a real one — only those with a RealStructureId can be
/// pushed across.</summary>
public class IndyStructureService
{
    public int Id          { get; set; }
    public int StructureId { get; set; }   // IndyStructures.Id
    public int TypeId      { get; set; }
}
