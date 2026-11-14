using EveConsole.Localization;

namespace EveConsole.Services.Connector;

/// <summary>
/// What a connector can be used for: the capabilities this build knows, by the names <c>/v1/me</c> lists.
/// A connector's other capabilities are kept as it sent them, but never shown or ticked.
///
/// <para>⚠️ Only <see cref="LinkedCharacters"/> is used today. The others are choices the user saves for
/// features built later; whoever builds one asks <see cref="ConnectorRouter"/> which connector to use.</para>
/// </summary>
public static class ConnectorFunctions
{
    public const string LinkedCharacters   = "linked-characters";
    public const string Groups             = "groups";
    public const string CharacterData      = "character-data";
    public const string CorporationData    = "corporation-data";
    public const string FleetParticipation = "fleet-participation";

    /// <summary>Every function this build knows, in the order the settings show them.</summary>
    public static readonly IReadOnlyList<string> All = [LinkedCharacters, Groups, CharacterData, CorporationData, FleetParticipation];

    public static bool IsKnown(string function) => All.Contains(function);

    /// <summary>The function in the user's words.</summary>
    public static string Label(string function) => function switch
    {
        LinkedCharacters   => SettingsText.ConnectorFnLinkedCharacters,
        Groups             => SettingsText.ConnectorFnGroups,
        CharacterData      => SettingsText.ConnectorFnCharacterData,
        CorporationData    => SettingsText.ConnectorFnCorporationData,
        FleetParticipation => SettingsText.ConnectorFnFleetParticipation,
        _                  => function,
    };
}
