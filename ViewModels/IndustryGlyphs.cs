namespace EveConsole.ViewModels;

/// <summary>
/// The industry activities as drawn marks, on a 16-unit grid, stroked rather than filled.
///
/// <para>⚠️ Drawn rather than fetched, as the worklist's kind marks are: EVE's image server has no
/// pictures for activities, and the client's own activity icons are CCP's art, not ours to
/// redistribute.</para>
/// </summary>
public static class IndustryGlyphs
{
    /// <summary>Factory roofline.</summary>
    public const string Manufacturing = "M2,13 V6 L6,8.5 V6 L10,8.5 V6 L14,8.5 V13 Z";

    /// <summary>
    /// Atom. ⚠️ Not a flask: the flask is what EVE draws for science, and a flask on a reaction
    /// read as copying.
    /// <para>⚠️ Each orbit's rotation runs along its own chord (y grows downward, so the chord up
    /// to the right is -45°). With them swapped the arcs were stretched to more than twice their
    /// size to reach their ends, and the atom spilled out of its 16 units.</para>
    /// </summary>
    public const string Reaction =
        "M8,7 A1,1 0 1,0 8,9 A1,1 0 1,0 8,7 " +
        "M3,13 A7.1,2.5 -45 1,1 13,3 A7.1,2.5 -45 1,1 3,13 " +
        "M3,3 A7.1,2.5 45 1,1 13,13 A7.1,2.5 45 1,1 3,3";

    /// <summary>Flask: the science slots as a whole, where the activity itself isn't known.</summary>
    public const string Science =
        "M6.5,2 V6 L3,12.5 A1,1 0 0,0 4,14 H12 A1,1 0 0,0 13,12.5 L9.5,6 V2 Z M5.5,2 H10.5";

    /// <summary>Stopwatch: researching time efficiency.</summary>
    public const string ResearchTime =
        "M8,4.5 A4.75,4.75 0 1,0 8,14 A4.75,4.75 0 1,0 8,4.5 M8,9.25 V6.75 M8,9.25 L9.75,10.5 M6.5,1.5 H9.5 M8,1.5 V4.5";

    /// <summary>A cut gem: researching material efficiency.</summary>
    public const string ResearchMaterial =
        "M5,2.5 H11 L14,6 L8,14 L2,6 Z M2,6 H14 M5,2.5 L6.5,6 L8,14 M11,2.5 L9.5,6 L8,14";

    /// <summary>A page over a page: copying.</summary>
    public const string Copying = "M5.5,4.5 H13 V14 H5.5 Z M3,11.5 V2 H10.5";

    /// <summary>A light bulb: invention.</summary>
    public const string Invention =
        "M6,11 C6,9.2 3.75,8.2 3.75,5.75 A4.25,4.25 0 0,1 12.25,5.75 C12.25,8.2 10,9.2 10,11 Z M6.5,13 H9.5 M7,14.75 H9";

    /// <summary>Gear, for an activity this doesn't know.</summary>
    public const string Unknown =
        "M8,5.5 A2.5,2.5 0 1,0 8,10.5 A2.5,2.5 0 1,0 8,5.5 M8,1.5 V3.5 M8,12.5 V14.5 " +
        "M1.5,8 H3.5 M12.5,8 H14.5 M3.5,3.5 L5,5 M11,11 L12.5,12.5 M12.5,3.5 L11,5 M5,11 L3.5,12.5";

    /// <summary>An ESI activity id's mark: 1 manufacturing, 3 time research, 4 material research,
    /// 5 copying, 8 invention, 9 and 11 reactions.</summary>
    public static string ForActivity(long activityId) => activityId switch
    {
        1      => Manufacturing,
        3      => ResearchTime,
        4      => ResearchMaterial,
        5      => Copying,
        8      => Invention,
        9 or 11 => Reaction,
        _      => Unknown,
    };
}
