using System.Text.RegularExpressions;

namespace EveConsole.ViewModels;

/// <summary>One line of an Overview card: values, and the words between them.</summary>
public sealed class NotifBriefLineVm
{
    public IReadOnlyList<NotifValueVm> Parts { get; init; } = [];
}

/// <summary>
/// What an Overview card says about a notification. Not everything the Notifications tool
/// shows — a line or three per type of what tells you whether to look: a bill's kind, where,
/// how much and when it is due; a standing's who, by how much and for whom. Picked from the
/// facts <see cref="NotificationBody"/> already worked out, so a card and the tool never
/// disagree about what a field means.
/// </summary>
public static class NotificationBrief
{
    // Per type, the lines a card shows. {key} is one of the body's facts (NotificationBodyVm.Facts:
    // a YAML key, or bill/amount/location/alliance/due, with/change/now/others, summary,
    // key.count, ore.total); {recipient} is the character it came to, when there is just one;
    // {key?} may be missing. Words between are drawn dim. A line shows only when every fact in it
    // is there, and needs at least one; " | " separates alternatives, the first that can be shown
    // winning. A type not listed shows its first two fields.
    private static readonly Dictionary<string, string[]> Lines = new(StringComparer.Ordinal)
    {
        // Corporation membership
        ["CorpAppNewMsg"]      = ["{charID} to {corpID}", "{applicationText?}"],
        ["CorpAppAcceptMsg"]   = ["{charID} to {corpID}"],
        ["CorpAppInvitedMsg"]  = ["{charID} to {corpID}", "by {invokingCharID}"],
        ["CharAppAcceptMsg"]   = ["{charID} to {corpID}"],
        ["CharAppRejectMsg"]   = ["{charID} to {corpID}"],
        ["CharAppWithdrawMsg"] = ["{charID} to {corpID}"],
        ["CharTerminationMsg"] = ["{charID} left {corpID}"],
        ["CorpTaxChangeMsg"]   = ["{corpID}", "{oldTaxRate} → {newTaxRate}"],

        // Money
        ["CorpAllBillMsg"]     = ["{bill} at {location} | {bill} for {alliance} | {bill}",
                                  "{amount} · due {due} | {amount}"],
        ["InsurancePayoutMsg"] = ["{amount}"],

        // Standings: who, by how much, for whom
        ["NPCStandingsLost"]   = ["{with} {change} for {recipient} | {with} {change}", "{others?}"],
        ["NPCStandingsGained"] = ["{with} {change} for {recipient} | {with} {change}", "{others?}"],

        // Projects
        ["CorporationGoalCreated"]     = ["{goal_name}", "by {creator_id}"],
        ["CorporationGoalCompleted"]   = ["{goal_name}"],
        ["CorporationGoalClosed"]      = ["{goal_name}", "by {closer_id}"],
        ["FreelanceProjectCreated"]    = ["{project_name}", "by {creator_id}"],
        ["FreelanceProjectCompleted"]  = ["{project_name}"],
        ["FreelanceProjectExpired"]    = ["{project_name}"],
        ["FreelanceProjectACLDeleted"] = ["{project_name}"],

        // Moon mining
        ["MoonminingExtractionStarted"]   = ["{structureID}", "Ready {readyTime}"],
        ["MoonminingExtractionFinished"]  = ["{structureID}", "Fractures {autoTime}"],
        ["MoonminingExtractionCancelled"] = ["{structureID}", "by {cancelledBy}"],
        ["MoonminingAutomaticFracture"]   = ["{structureID}", "{ore.total} of ore"],
        ["MoonminingLaserFired"]          = ["{structureID}", "by {firedBy}"],

        // Structures
        ["StructureUnderAttack"]        = ["{structureID}", "by {charID} {corpLinkData?}",
                                           "Shield {shieldPercentage} · Armor {armorPercentage} · Hull {hullPercentage}"],
        ["StructureLostShields"]        = ["{structureID}", "Reinforced until {timestamp}"],
        ["StructureLostArmor"]          = ["{structureID}", "Reinforced until {timestamp}"],
        ["StructureDestroyed"]          = ["{structureID}"],
        ["StructureFuelAlert"]          = ["{structureID}", "{listOfTypesAndQty} left"],
        ["StructureLowReagentsAlert"]   = ["{structureID}"],
        ["StructureNoReagentsAlert"]    = ["{structureID}"],
        ["StructureServicesOffline"]    = ["{structureID}", "{listOfServiceModuleIDs?}"],
        ["StructureAnchoring"]          = ["{structureID}", "{timeLeft} left"],
        ["StructureUnanchoring"]        = ["{structureID}", "{timeLeft} left"],
        ["StructureOnline"]             = ["{structureID}"],
        ["StructureWentHighPower"]      = ["{structureID}"],
        ["StructureWentLowPower"]       = ["{structureID}"],
        ["StructureItemsMovedToSafety"] = ["{structureID}", "to {newStationID}"],
        ["StructureItemsDelivered"]     = ["{structureID}", "{listOfTypesAndQty} for {charID} | {listOfTypesAndQty}"],
        ["StructureImpendingAbandonmentAssetsAtRisk"] = ["{structureID}", "{daysUntilAbandon} days left"],
        ["StructurePaintPurchased"]     = ["{structureIDs.count} structures by {purchasedByCharacterID}"],
        ["OwnershipTransferred"]        = ["{structureID}", "to {newOwnerCorpID}"],
        ["EntosisCaptureStarted"]       = ["{structureTypeID} in {solarSystemID}"],

        // Starbases
        ["TowerAlertMsg"]         = ["{typeID} at {moonID}", "by {aggressorID?} {aggressorCorpID?}",
                                     "Shield {shieldValue} · Armor {armorValue} · Hull {hullValue}"],
        ["TowerResourceAlertMsg"] = ["{typeID} at {moonID}", "{wants} left"],

        // Clones
        ["CloneActivationMsg2"]  = ["{cloneStationID}"],
        ["CloneActivationMsg"]   = ["{cloneStationID}"],
        ["CloneRevokedMsg2"]     = ["{stationID}", "by {corpID}"],
        ["JumpCloneDeletedMsg1"] = ["{locationID}"],
        ["JumpCloneDeletedMsg2"] = ["{locationID}", "by {destroyerID}"],

        // Combat
        ["KillReportVictim"]    = ["{victimShipTypeID}"],
        ["KillReportFinalBlow"] = ["{victimShipTypeID}"],
        ["KillRightEarned"]     = ["on {charID}"],

        // War
        ["WarDeclared"]               = ["{declaredByID} vs {againstID}", "Fighting starts {timeStarted}"],
        ["WarInvalid"]                = ["{declaredByID} vs {againstID}", "Ends {endDate}"],
        ["WarRetractedByConcord"]     = ["{declaredByID} vs {againstID}", "Ends {endDate}"],
        ["WarHQRemovedFromSpace"]     = ["{declaredByID} vs {againstID}"],
        ["WarInherited"]              = ["{declaredByID} vs {againstID}"],
        ["WarAllyInherited"]          = ["{declaredByID} vs {againstID}"],
        ["MutualWarInviteSent"]       = ["{declaredByID} vs {againstID}"],
        ["AllyJoinedWarAggressorMsg"] = ["{allyID} joins {aggressorID}", "against {defenderID}"],
        ["AllyJoinedWarAllyMsg"]      = ["{allyID} joins {defenderID}", "against {aggressorID}"],
        ["OfferedToAlly"]             = ["{defenderID} vs {aggressorID}", "by {charID}"],
        ["AllianceCapitalChanged"]    = ["{allianceID}", "New capital {solarSystemID}"],

        // Nothing but their name
        ["CorpBecameWarEligible"]   = ["{summary}"],
        ["CorpNoLongerWarEligible"] = ["{summary}"],
        ["GameTimeAdded"]           = ["{summary}"],
        ["ExpertSystemExpired"]     = ["{typeID}"],
    };

    private static readonly Regex Fact = new(@"\{([^}?]+)(\?)?\}", RegexOptions.Compiled);

    /// <summary>The lines for one notification. <paramref name="recipient"/> is the character it
    /// came to, or null when it came to several.</summary>
    public static IReadOnlyList<NotifBriefLineVm> For(string type, NotificationBodyVm body, NotifValueVm? recipient)
    {
        if (!Lines.TryGetValue(type, out var spec)) return Fallback(body);

        var lines = new List<NotifBriefLineVm>(spec.Length);
        foreach (var line in spec)
            foreach (var alternative in line.Split(" | "))
                if (Compose(alternative, body, recipient) is { } built) { lines.Add(built); break; }
        return lines.Count > 0 ? lines : Fallback(body);
    }

    private static NotifBriefLineVm? Compose(string template, NotificationBodyVm body, NotifValueVm? recipient)
    {
        var parts = new List<NotifValueVm>();
        var facts = 0;
        var at    = 0;
        foreach (Match m in Fact.Matches(template))
        {
            Words(template[at..m.Index], parts);
            at = m.Index + m.Length;

            var key = m.Groups[1].Value;
            IReadOnlyList<NotifValueVm>? values = key == "recipient"
                ? recipient is null ? null : [recipient]
                : body.Facts.GetValueOrDefault(key);
            if (values is not { Count: > 0 })
            {
                if (m.Groups[2].Success) continue;   // optional
                return null;
            }
            parts.AddRange(values);
            facts++;
        }
        Words(template[at..], parts);
        return facts > 0 ? new NotifBriefLineVm { Parts = parts } : null;
    }

    private static void Words(string text, List<NotifValueVm> parts)
    {
        var t = text.Trim();
        if (t.Length > 0) parts.Add(new NotifValueVm { Text = t, IsDim = true });
    }

    /// <summary>A type with no card of its own: its sentence if it has one, else its first two
    /// fields, each after its label.</summary>
    private static IReadOnlyList<NotifBriefLineVm> Fallback(NotificationBodyVm body)
    {
        if (body.Summary.Length > 0)
            return [new NotifBriefLineVm { Parts = [new NotifValueVm { Text = body.Summary }] }];
        return [.. body.Fields.Take(2).Select(f => new NotifBriefLineVm
            { Parts = [new NotifValueVm { Text = f.Label, IsDim = true }, .. f.Values] })];
    }
}
