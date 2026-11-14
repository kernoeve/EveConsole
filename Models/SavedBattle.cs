namespace EveConsole.Models;

/// <summary>
/// A battle the user defined and kept: its name, and as JSON its systems and times, how its sides
/// were placed and whether to watch it live (see Services.Battle.BattleDefinition). Its kills are
/// not here: they are read from the kill tables each time it is opened.
///
/// <para>A table rather than a remembered control: this is the user's own work, and with one
/// PostgreSQL server it should be there on whichever machine they open it. Mirrored by hand in
/// App.axaml.cs (SQLite) and PostgresSchema.</para>
/// </summary>
public class SavedBattle
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Definition { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
