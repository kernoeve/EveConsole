using System.Text.RegularExpressions;
using EveConsole.Localization;

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
        ["CorpAppNewMsg"]      = CommsText.NotifTitleCorpAppNewMsg,
        ["CorpAppInvitedMsg"]  = CommsText.NotifTitleCorpAppInvitedMsg,
        ["CorpAppAcceptMsg"]   = CommsText.NotifTitleCorpAppAcceptMsg,
        ["CharAppAcceptMsg"]   = CommsText.NotifTitleCharAppAcceptMsg,
        ["CharAppRejectMsg"]   = CommsText.NotifTitleCharAppRejectMsg,
        ["CharAppWithdrawMsg"] = CommsText.NotifTitleCharAppWithdrawMsg,
        ["CharTerminationMsg"] = CommsText.NotifTitleCharTerminationMsg,
        ["CorpTaxChangeMsg"]   = CommsText.NotifTitleCorpTaxChangeMsg,

        // Money
        ["CorpAllBillMsg"]     = CommsText.NotifTitleCorpAllBillMsg,
        ["InsurancePayoutMsg"] = CommsText.NotifTitleInsurancePayoutMsg,

        // Standings. ⚠️ Both named as a change, not a loss or a gain: ESI reports a mission's
        // standing GAIN as NPCStandingsLost too, so the type cannot say which way it went — the
        // sign of each change in the body does.
        ["NPCStandingsLost"]   = CommsText.NotifTitleNPCStandingsLost,
        ["NPCStandingsGained"] = CommsText.NotifTitleNPCStandingsGained,

        // Projects
        ["CorporationGoalCreated"]     = CommsText.NotifTitleCorporationGoalCreated,
        ["CorporationGoalCompleted"]   = CommsText.NotifTitleCorporationGoalCompleted,
        ["CorporationGoalClosed"]      = CommsText.NotifTitleCorporationGoalClosed,
        ["FreelanceProjectCreated"]    = CommsText.NotifTitleFreelanceProjectCreated,
        ["FreelanceProjectCompleted"]  = CommsText.NotifTitleFreelanceProjectCompleted,
        ["FreelanceProjectExpired"]    = CommsText.NotifTitleFreelanceProjectExpired,
        ["FreelanceProjectACLDeleted"] = CommsText.NotifTitleFreelanceProjectACLDeleted,

        // Moon mining
        ["MoonminingExtractionStarted"]   = CommsText.NotifTitleMoonminingExtractionStarted,
        ["MoonminingExtractionFinished"]  = CommsText.NotifTitleMoonminingExtractionFinished,
        ["MoonminingExtractionCancelled"] = CommsText.NotifTitleMoonminingExtractionCancelled,
        ["MoonminingAutomaticFracture"]   = CommsText.NotifTitleMoonminingAutomaticFracture,
        ["MoonminingLaserFired"]          = CommsText.NotifTitleMoonminingLaserFired,

        // Structures
        ["StructureAnchoring"]            = CommsText.NotifTitleStructureAnchoring,
        ["StructureUnanchoring"]          = CommsText.NotifTitleStructureUnanchoring,
        ["StructureOnline"]               = CommsText.NotifTitleStructureOnline,
        ["StructureWentHighPower"]        = CommsText.NotifTitleStructureWentHighPower,
        ["StructureWentLowPower"]         = CommsText.NotifTitleStructureWentLowPower,
        ["StructureUnderAttack"]          = CommsText.NotifTitleStructureUnderAttack,
        ["StructureLostShields"]          = CommsText.NotifTitleStructureLostShields,
        ["StructureLostArmor"]            = CommsText.NotifTitleStructureLostArmor,
        ["StructureDestroyed"]            = CommsText.NotifTitleStructureDestroyed,
        ["StructureFuelAlert"]            = CommsText.NotifTitleStructureFuelAlert,
        ["StructureLowReagentsAlert"]     = CommsText.NotifTitleStructureLowReagentsAlert,
        ["StructureNoReagentsAlert"]      = CommsText.NotifTitleStructureNoReagentsAlert,
        ["StructureServicesOffline"]      = CommsText.NotifTitleStructureServicesOffline,
        ["StructureItemsMovedToSafety"]   = CommsText.NotifTitleStructureItemsMovedToSafety,
        ["StructureItemsDelivered"]       = CommsText.NotifTitleStructureItemsDelivered,
        ["StructureImpendingAbandonmentAssetsAtRisk"] = CommsText.NotifTitleStructureImpendingAbandonmentAssetsAtRisk,
        ["StructurePaintPurchased"]       = CommsText.NotifTitleStructurePaintPurchased,
        ["OwnershipTransferred"]          = CommsText.NotifTitleOwnershipTransferred,
        ["EntosisCaptureStarted"]         = CommsText.NotifTitleEntosisCaptureStarted,

        // Starbases
        ["TowerAlertMsg"]         = CommsText.NotifTitleTowerAlertMsg,
        ["TowerResourceAlertMsg"] = CommsText.NotifTitleTowerResourceAlertMsg,

        // Clones
        ["CloneActivationMsg2"]  = CommsText.NotifTitleCloneActivationMsg2,
        ["CloneActivationMsg"]   = CommsText.NotifTitleCloneActivationMsg,
        ["CloneRevokedMsg2"]     = CommsText.NotifTitleCloneRevokedMsg2,
        ["JumpCloneDeletedMsg1"] = CommsText.NotifTitleJumpCloneDeletedMsg1,
        ["JumpCloneDeletedMsg2"] = CommsText.NotifTitleJumpCloneDeletedMsg2,

        // Combat
        ["KillReportVictim"]    = CommsText.NotifTitleKillReportVictim,
        ["KillReportFinalBlow"] = CommsText.NotifTitleKillReportFinalBlow,
        ["KillRightEarned"]     = CommsText.NotifTitleKillRightEarned,

        // War
        ["WarDeclared"]               = CommsText.NotifTitleWarDeclared,
        ["WarInherited"]              = CommsText.NotifTitleWarInherited,
        ["WarAllyInherited"]          = CommsText.NotifTitleWarAllyInherited,
        ["WarInvalid"]                = CommsText.NotifTitleWarInvalid,
        ["WarRetractedByConcord"]     = CommsText.NotifTitleWarRetractedByConcord,
        ["WarHQRemovedFromSpace"]     = CommsText.NotifTitleWarHQRemovedFromSpace,
        ["MutualWarInviteSent"]       = CommsText.NotifTitleMutualWarInviteSent,
        ["OfferedToAlly"]             = CommsText.NotifTitleOfferedToAlly,
        ["AllyJoinedWarAggressorMsg"] = CommsText.NotifTitleAllyJoinedWarAggressorMsg,
        ["AllyJoinedWarAllyMsg"]      = CommsText.NotifTitleAllyJoinedWarAllyMsg,
        ["CorpBecameWarEligible"]     = CommsText.NotifTitleCorpBecameWarEligible,
        ["CorpNoLongerWarEligible"]   = CommsText.NotifTitleCorpNoLongerWarEligible,
        ["AllianceCapitalChanged"]    = CommsText.NotifTitleAllianceCapitalChanged,

        // Account
        ["ExpertSystemExpired"] = CommsText.NotifTitleExpertSystemExpired,
        ["GameTimeAdded"]       = CommsText.NotifTitleGameTimeAdded,
    };
}
