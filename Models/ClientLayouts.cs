namespace EveConsole.Models;

/// <summary>
/// A saved layout: its name, and as JSON the shape it gives a monitor (see
/// Services.ClientLayouts.LayoutArea). It holds no monitors or characters, so one layout can be
/// used on several monitors, or on another desktop. Mirrored by hand in App.axaml.cs (SQLite) and
/// PostgresSchema.
/// </summary>
public class ClientLayout
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Definition { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One desktop that runs EVE Console. The id is generated once and kept in a small file on that
/// desktop (see DeviceIdentity), so renaming the machine or moving the database doesn't lose it.
/// The name starts as the machine name and the user can change it.
/// </summary>
public class ClientLayoutDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Off by default: until it's turned on, this desktop's clients are left alone.</summary>
    public bool PlacementOn { get; set; }

    /// <summary>What was last applied, as JSON (Services.ClientLayouts.AppliedDesktop).</summary>
    public string Applied { get; set; } = "";

    /// <summary>Names seen in client window titles, as JSON, which the editor offers for groups.</summary>
    public string SeenCharacters { get; set; } = "";
}

/// <summary>
/// A named set of what's on the Monitors tab for one desktop: each monitor's layout and settings,
/// and who runs where. One is applied at a time, so switching is picking another and pressing Apply.
/// </summary>
public class ClientLayoutProfile
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>A monitor on a desktop in one profile, known by its hardware identity rather than its Windows number.</summary>
public class ClientLayoutMonitor
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";

    /// <summary>0 only on a row from before profiles, until <c>ClientLayoutStore.ListProfilesAsync</c> claims it.</summary>
    public int ProfileId { get; set; }
    public string HardwareKey { get; set; } = "";

    /// <summary>The layout assigned to this monitor. None means its clients are left alone.</summary>
    public int? LayoutId { get; set; }

    /// <summary>Use the whole monitor rather than staying clear of the taskbar.</summary>
    public bool FullMonitor { get; set; }

    /// <summary>When panels appear (Services.ClientLayouts.PanelStart), by name.</summary>
    public string PanelStart { get; set; } = "WithConsole";
}

/// <summary>
/// A character in the group of one client position on a monitor. By name, because the name is what
/// a client's window title shows, and an alt doesn't have to be in the app.
/// </summary>
public class ClientLayoutCharacter
{
    public int Id { get; set; }
    public int MonitorId { get; set; }
    public string AreaId { get; set; } = "";
    public string CharacterName { get; set; } = "";
}
