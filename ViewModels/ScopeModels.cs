using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

public class ScopeItem : ReactiveObject
{
    private bool _isSelected = true;

    public string Scope        { get; }
    public string FriendlyName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public ScopeItem(string scope, bool stripCorporation = false)
    {
        Scope = scope;
        FriendlyName = ScopeLabels.Of(scope) ?? MakeFriendlyName(scope, stripCorporation);
    }

    /// <summary>The name of a scope <see cref="ScopeLabels"/> has no label for — one added to the
    /// lists since — made from its id, in English.</summary>
    private static string MakeFriendlyName(string scope, bool stripCorporation)
    {
        // "esi-skills.read_skills.v1" → "Read Skills"
        var parts = scope.Split('.');
        if (parts.Length < 2) return scope;
        var name = string.Join(' ',
            parts[1].Split('_')
                    .Select(w => w.Length == 0 ? w : char.ToUpper(w[0]) + w[1..]));
        return stripCorporation
            ? name.Replace("Corporation ", "").Replace(" Corporation", "").Trim()
            : name;
    }
}

// Read-only scope data bound to the per-character details panel in Settings.
public record ScopeDisplayItem(string FriendlyName, bool IsGranted);
public record ScopeDisplayGroup(string Category, IReadOnlyList<ScopeDisplayItem> Items);

public class ScopeGroup : ReactiveObject
{
    public string                   Category { get; }
    public IReadOnlyList<ScopeItem> Items    { get; }

    private bool _allSelected = true;
    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref _allSelected, value);
            foreach (var item in Items)
                item.IsSelected = value;
        }
    }

    public ScopeGroup(string category, IEnumerable<ScopeItem> items)
    {
        Category = category;
        Items    = [.. items];
    }
}

/// <summary>
/// What a person reads for an ESI scope, and for the heading it is listed under — in the scope
/// dialog when a character or corporation is added, and in Settings → ESI Tokens.
///
/// <para>⚠️ Words only. The scope ids stay exactly as ESI spells them, since they are what is
/// requested, stored and compared; nothing may be matched on a label. A scope with no entry here —
/// one added to <see cref="Auth.EsiAuthService.CharacterScopes"/> or
/// <see cref="Auth.EsiAuthService.CorporationScopes"/> since — is shown as its id made readable, in
/// English, as every scope used to be.</para>
///
/// <para>A corporation's scopes are listed for the corporation, so their labels leave the word out:
/// esi-assets.read_corporation_assets.v1 is "Read Assets", as it always read.</para>
/// </summary>
public static class ScopeLabels
{
    private static readonly Dictionary<string, string> ByScope = new(StringComparer.Ordinal)
    {
        // A character's
        ["esi-access.read_lists.v1"]                        = OverviewText.ScopeReadLists,
        ["esi-activities.read_character.v1"]                = OverviewText.ScopeReadCharacter,
        ["esi-alliances.read_contacts.v1"]                  = OverviewText.ScopeReadContacts,
        ["esi-assets.read_assets.v1"]                       = OverviewText.ScopeReadAssets,
        ["esi-calendar.read_calendar_events.v1"]            = OverviewText.ScopeReadCalendarEvents,
        ["esi-calendar.respond_calendar_events.v1"]         = OverviewText.ScopeRespondCalendarEvents,
        ["esi-characters.read_agents_research.v1"]          = OverviewText.ScopeReadAgentsResearch,
        ["esi-characters.read_blueprints.v1"]               = OverviewText.ScopeReadBlueprints,
        ["esi-characters.read_chat_channels.v1"]            = OverviewText.ScopeReadChatChannels,
        ["esi-characters.read_contacts.v1"]                 = OverviewText.ScopeReadContacts,
        ["esi-characters.read_corporation_roles.v1"]        = OverviewText.ScopeReadCorporationRoles,
        ["esi-characters.read_fatigue.v1"]                  = OverviewText.ScopeReadFatigue,
        ["esi-characters.read_freelance_jobs.v1"]           = OverviewText.ScopeReadFreelanceJobs,
        ["esi-characters.read_fw_stats.v1"]                 = OverviewText.ScopeReadFwStats,
        ["esi-characters.read_loyalty.v1"]                  = OverviewText.ScopeReadLoyalty,
        ["esi-characters.read_medals.v1"]                   = OverviewText.ScopeReadMedals,
        ["esi-characters.read_notifications.v1"]            = OverviewText.ScopeReadNotifications,
        ["esi-characters.read_standings.v1"]                = OverviewText.ScopeReadStandings,
        ["esi-characters.read_titles.v1"]                   = OverviewText.ScopeReadTitles,
        ["esi-characters.write_contacts.v1"]                = OverviewText.ScopeWriteContacts,
        ["esi-clones.read_clones.v1"]                       = OverviewText.ScopeReadClones,
        ["esi-clones.read_implants.v1"]                     = OverviewText.ScopeReadImplants,
        ["esi-contracts.read_character_contracts.v1"]       = OverviewText.ScopeReadCharacterContracts,
        ["esi-fittings.read_fittings.v1"]                   = OverviewText.ScopeReadFittings,
        ["esi-fittings.write_fittings.v1"]                  = OverviewText.ScopeWriteFittings,
        ["esi-fleets.read_fleet.v1"]                        = OverviewText.ScopeReadFleet,
        ["esi-fleets.write_fleet.v1"]                       = OverviewText.ScopeWriteFleet,
        ["esi-industry.read_character_jobs.v1"]             = OverviewText.ScopeReadCharacterJobs,
        ["esi-industry.read_character_mining.v1"]           = OverviewText.ScopeReadCharacterMining,
        ["esi-killmails.read_killmails.v1"]                 = OverviewText.ScopeReadKillmails,
        ["esi-location.read_location.v1"]                   = OverviewText.ScopeReadLocation,
        ["esi-location.read_online.v1"]                     = OverviewText.ScopeReadOnline,
        ["esi-location.read_ship_type.v1"]                  = OverviewText.ScopeReadShipType,
        ["esi-mail.organize_mail.v1"]                       = OverviewText.ScopeOrganizeMail,
        ["esi-mail.read_mail.v1"]                           = OverviewText.ScopeReadMail,
        ["esi-mail.send_mail.v1"]                           = OverviewText.ScopeSendMail,
        ["esi-markets.read_character_orders.v1"]            = OverviewText.ScopeReadCharacterOrders,
        ["esi-markets.structure_markets.v1"]                = OverviewText.ScopeStructureMarkets,
        ["esi-planets.manage_planets.v1"]                   = OverviewText.ScopeManagePlanets,
        ["esi-planets.read_customs_offices.v1"]             = OverviewText.ScopeReadCustomsOffices,
        ["esi-search.search_structures.v1"]                 = OverviewText.ScopeSearchStructures,
        ["esi-skills.read_skills.v1"]                       = OverviewText.ScopeReadSkills,
        ["esi-skills.read_skillqueue.v1"]                   = OverviewText.ScopeReadSkillQueue,
        ["esi-structures.read_character.v1"]                = OverviewText.ScopeReadCharacter,
        ["esi-ui.open_window.v1"]                           = OverviewText.ScopeOpenWindow,
        ["esi-ui.write_waypoint.v1"]                        = OverviewText.ScopeWriteWaypoint,
        ["esi-universe.read_structures.v1"]                 = OverviewText.ScopeReadStructures,
        ["esi-wallet.read_character_wallet.v1"]             = OverviewText.ScopeReadCharacterWallet,

        // A corporation's
        ["esi-assets.read_corporation_assets.v1"]           = OverviewText.ScopeReadAssets,
        ["esi-contracts.read_corporation_contracts.v1"]     = OverviewText.ScopeReadContracts,
        ["esi-corporations.read_blueprints.v1"]             = OverviewText.ScopeReadBlueprints,
        ["esi-corporations.read_contacts.v1"]               = OverviewText.ScopeReadContacts,
        ["esi-corporations.read_container_logs.v1"]         = OverviewText.ScopeReadContainerLogs,
        ["esi-corporations.read_corporation_membership.v1"] = OverviewText.ScopeReadMembership,
        ["esi-corporations.read_divisions.v1"]              = OverviewText.ScopeReadDivisions,
        ["esi-corporations.read_facilities.v1"]             = OverviewText.ScopeReadFacilities,
        ["esi-corporations.read_freelance_jobs.v1"]         = OverviewText.ScopeReadFreelanceJobs,
        ["esi-corporations.read_fw_stats.v1"]               = OverviewText.ScopeReadFwStats,
        ["esi-corporations.read_medals.v1"]                 = OverviewText.ScopeReadMedals,
        ["esi-corporations.read_projects.v1"]               = OverviewText.ScopeReadProjects,
        ["esi-corporations.read_standings.v1"]              = OverviewText.ScopeReadStandings,
        ["esi-corporations.read_starbases.v1"]              = OverviewText.ScopeReadStarbases,
        ["esi-corporations.read_structures.v1"]             = OverviewText.ScopeReadStructures,
        ["esi-corporations.read_titles.v1"]                 = OverviewText.ScopeReadTitles,
        ["esi-corporations.track_members.v1"]               = OverviewText.ScopeTrackMembers,
        ["esi-industry.read_corporation_jobs.v1"]           = OverviewText.ScopeReadJobs,
        ["esi-industry.read_corporation_mining.v1"]         = OverviewText.ScopeReadMining,
        ["esi-killmails.read_corporation_killmails.v1"]     = OverviewText.ScopeReadKillmails,
        ["esi-markets.read_corporation_orders.v1"]          = OverviewText.ScopeReadOrders,
        ["esi-structures.read_corporation.v1"]              = OverviewText.ScopeReadCorpStructures,
        ["esi-wallet.read_corporation_wallet.v1"]           = OverviewText.ScopeReadWallet,
        ["esi-wallet.read_corporation_wallets.v1"]          = OverviewText.ScopeReadWallets,
    };

    // By the word a scope id starts with, capitalised: esi-assets.… is listed under "Assets".
    private static readonly Dictionary<string, string> ByGroup = new(StringComparer.Ordinal)
    {
        ["Access"]     = OverviewText.ScopeGroupAccess,
        ["Activities"] = OverviewText.ScopeGroupActivities,
        ["Alliances"]  = OverviewText.ScopeGroupAlliances,
        ["Assets"]     = OverviewText.ScopeGroupAssets,
        ["Calendar"]   = OverviewText.ScopeGroupCalendar,
        ["Characters"] = OverviewText.ScopeGroupCharacters,
        ["Clones"]     = OverviewText.ScopeGroupClones,
        ["Contracts"]  = OverviewText.ScopeGroupContracts,
        ["Fittings"]   = OverviewText.ScopeGroupFittings,
        ["Fleets"]     = OverviewText.ScopeGroupFleets,
        ["Industry"]   = OverviewText.ScopeGroupIndustry,
        ["Killmails"]  = OverviewText.ScopeGroupKillmails,
        ["Location"]   = OverviewText.ScopeGroupLocation,
        ["Mail"]       = OverviewText.ScopeGroupMail,
        ["Markets"]    = OverviewText.ScopeGroupMarkets,
        ["Planets"]    = OverviewText.ScopeGroupPlanets,
        ["Search"]     = OverviewText.ScopeGroupSearch,
        ["Skills"]     = OverviewText.ScopeGroupSkills,
        ["Structures"] = OverviewText.ScopeGroupStructures,
        ["Ui"]         = OverviewText.ScopeGroupUi,
        ["Universe"]   = OverviewText.ScopeGroupUniverse,
        ["Wallet"]     = OverviewText.ScopeGroupWallet,
    };

    /// <summary>The label for a scope id, or null when it has none.</summary>
    public static string? Of(string scope) => ByScope.GetValueOrDefault(scope);

    /// <summary>The heading for a group of scopes. <paramref name="group"/> is the word their ids
    /// start with, capitalised ("Assets"); "Corporations" is "Corp" in a corporation's list. A group
    /// with no label keeps that word.</summary>
    public static string Group(string group, bool corporation) =>
        corporation && group == "Corporations" ? OverviewText.ScopeGroupCorp
        : ByGroup.GetValueOrDefault(group) ?? group;
}
