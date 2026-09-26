using System.Text.RegularExpressions;

namespace EveConsole.ViewModels;

/// <summary>
/// What a notification type is called on screen — the Notifications tool's type list, grid and
/// detail title, and the Overview's notification cards all ask here.
///
/// <para>ESI's type names are identifiers ("CharAppAcceptMsg", "NPCStandingsLost"), and splitting
/// them into words only goes so far: "Char App Accept Msg" is still not something a capsuleer
/// would say. The types this app actually receives are named here in the game's own terms; any
/// other falls back to <see cref="Humanize"/>.</para>
/// </summary>
public static class NotificationTitles
{
    public static string For(string type) =>
        Titles.TryGetValue(type, out var title) ? title : Humanize(type);

    /// <summary>An identifier as words: "structureTypeID" → "Structure Type ID". For a type or a
    /// field nobody has named yet.</summary>
    public static string Humanize(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        // The second split breaks an acronym off the word after it: "NPCStandingsLost" was
        // "NPCStandings Lost", since only a lower-to-upper step was ever a boundary.
        var spaced = Regex.Replace(key, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        spaced = spaced.Replace("_", " ");
        spaced = Regex.Replace(spaced, @"\bID\b", "ID", RegexOptions.IgnoreCase);
        var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Equals("id", StringComparison.OrdinalIgnoreCase) ? "ID"
                       : char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(" ", words);
    }

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        // Corporation membership
        ["CorpAppNewMsg"]      = "New corporation application",
        ["CorpAppInvitedMsg"]  = "Invited to a corporation",
        ["CorpAppAcceptMsg"]   = "Corporation application accepted",
        ["CharAppAcceptMsg"]   = "Application accepted",
        ["CharAppRejectMsg"]   = "Application rejected",
        ["CharAppWithdrawMsg"] = "Application withdrawn",
        ["CharTerminationMsg"] = "Member left the corporation",
        ["CorpTaxChangeMsg"]   = "Corporation tax changed",

        // Money
        ["CorpAllBillMsg"]     = "Corporation bill",
        ["InsurancePayoutMsg"] = "Insurance payout",

        // Standings. ⚠️ Both named as a change, not a loss or a gain: ESI reports a mission's
        // standing GAIN as NPCStandingsLost too, so the type cannot say which way it went — the
        // sign of each change in the body does.
        ["NPCStandingsLost"]   = "NPC standings changed",
        ["NPCStandingsGained"] = "NPC standings changed",

        // Projects
        ["CorporationGoalCreated"]     = "Corporation project created",
        ["CorporationGoalCompleted"]   = "Corporation project completed",
        ["CorporationGoalClosed"]      = "Corporation project closed",
        ["FreelanceProjectCreated"]    = "Freelance project created",
        ["FreelanceProjectCompleted"]  = "Freelance project completed",
        ["FreelanceProjectExpired"]    = "Freelance project expired",
        ["FreelanceProjectACLDeleted"] = "Freelance project access list deleted",

        // Moon mining
        ["MoonminingExtractionStarted"]   = "Moon extraction started",
        ["MoonminingExtractionFinished"]  = "Moon extraction finished",
        ["MoonminingExtractionCancelled"] = "Moon extraction cancelled",
        ["MoonminingAutomaticFracture"]   = "Moon fractured automatically",
        ["MoonminingLaserFired"]          = "Moon drill fired",

        // Structures
        ["StructureAnchoring"]            = "Structure anchoring",
        ["StructureUnanchoring"]          = "Structure unanchoring",
        ["StructureOnline"]               = "Structure online",
        ["StructureWentHighPower"]        = "Structure went to high power",
        ["StructureWentLowPower"]         = "Structure went to low power",
        ["StructureUnderAttack"]          = "Structure under attack",
        ["StructureLostShields"]          = "Structure lost shields",
        ["StructureLostArmor"]            = "Structure lost armor",
        ["StructureDestroyed"]            = "Structure destroyed",
        ["StructureFuelAlert"]            = "Structure low on fuel",
        ["StructureLowReagentsAlert"]     = "Structure low on reagents",
        ["StructureNoReagentsAlert"]      = "Structure out of reagents",
        ["StructureServicesOffline"]      = "Structure services offline",
        ["StructureItemsMovedToSafety"]   = "Items moved to asset safety",
        ["StructureItemsDelivered"]       = "Items delivered to a structure",
        ["StructureImpendingAbandonmentAssetsAtRisk"] = "Structure being abandoned — assets at risk",
        ["StructurePaintPurchased"]       = "Structure paint purchased",
        ["OwnershipTransferred"]          = "Structure ownership transferred",
        ["EntosisCaptureStarted"]         = "Entosis capture started",

        // Starbases
        ["TowerAlertMsg"]         = "Starbase under attack",
        ["TowerResourceAlertMsg"] = "Starbase low on fuel",

        // Clones
        ["CloneActivationMsg2"]  = "Clone activated",
        ["CloneActivationMsg"]   = "Clone activated",
        ["CloneRevokedMsg2"]     = "Clone revoked",
        ["JumpCloneDeletedMsg1"] = "Jump clone destroyed",
        ["JumpCloneDeletedMsg2"] = "Jump clone destroyed",

        // Combat
        ["KillReportVictim"]    = "Ship lost",
        ["KillReportFinalBlow"] = "Final blow",
        ["KillRightEarned"]     = "Kill right earned",

        // War
        ["WarDeclared"]               = "War declared",
        ["WarInherited"]              = "War inherited",
        ["WarAllyInherited"]          = "War ally inherited",
        ["WarInvalid"]                = "War invalidated",
        ["WarRetractedByConcord"]     = "War retracted by CONCORD",
        ["WarHQRemovedFromSpace"]     = "War HQ removed from space",
        ["MutualWarInviteSent"]       = "Mutual war invitation sent",
        ["OfferedToAlly"]             = "Offered to join a war as an ally",
        ["AllyJoinedWarAggressorMsg"] = "Ally joined the aggressor",
        ["AllyJoinedWarAllyMsg"]      = "Ally joined the war",
        ["CorpBecameWarEligible"]     = "Corporation became war eligible",
        ["CorpNoLongerWarEligible"]   = "Corporation no longer war eligible",
        ["AllianceCapitalChanged"]    = "Alliance capital changed",

        // Account
        ["ExpertSystemExpired"] = "Expert system expired",
        ["GameTimeAdded"]       = "Game time added",
    };
}
