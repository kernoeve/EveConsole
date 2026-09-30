using System.Globalization;
using System.Text.RegularExpressions;
using EveConsole.Localization;

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
    //
    // The words come from OverviewText with their holes numbered, so a translation can put them in
    // its own order; T() names the fact that fills each hole. A counted line ("3 days left") is
    // Counted(): which words it takes depends on the number, known only once a card is built.
    private static readonly Dictionary<string, string[]> Lines = new(StringComparer.Ordinal)
    {
        // Corporation membership
        ["CorpAppNewMsg"]      = [T(OverviewText.BriefCharToCorp, "charID", "corpID"), "{applicationText?}"],
        ["CorpAppAcceptMsg"]   = [T(OverviewText.BriefCharToCorp, "charID", "corpID")],
        ["CorpAppInvitedMsg"]  = [T(OverviewText.BriefCharToCorp, "charID", "corpID"), T(OverviewText.BriefBy, "invokingCharID")],
        ["CharAppAcceptMsg"]   = [T(OverviewText.BriefCharToCorp, "charID", "corpID")],
        ["CharAppRejectMsg"]   = [T(OverviewText.BriefCharToCorp, "charID", "corpID")],
        ["CharAppWithdrawMsg"] = [T(OverviewText.BriefCharToCorp, "charID", "corpID")],
        ["CharTerminationMsg"] = [T(OverviewText.BriefCharLeftCorp, "charID", "corpID")],
        ["CorpTaxChangeMsg"]   = ["{corpID}", "{oldTaxRate} → {newTaxRate}"],

        // Money
        ["CorpAllBillMsg"]     = [Or(T(OverviewText.BriefBillAt, "bill", "location"), T(OverviewText.BriefBillFor, "bill", "alliance"), "{bill}"),
                                  Or(T(OverviewText.BriefAmountDue, "amount", "due"), "{amount}")],
        ["InsurancePayoutMsg"] = ["{amount}"],

        // Standings: who, by how much, for whom
        ["NPCStandingsLost"]   = [Or(T(OverviewText.BriefStandingFor, "with", "change", "recipient"), "{with} {change}"), "{others?}"],
        ["NPCStandingsGained"] = [Or(T(OverviewText.BriefStandingFor, "with", "change", "recipient"), "{with} {change}"), "{others?}"],

        // Projects
        ["CorporationGoalCreated"]     = ["{goal_name}", T(OverviewText.BriefBy, "creator_id")],
        ["CorporationGoalCompleted"]   = ["{goal_name}"],
        ["CorporationGoalClosed"]      = ["{goal_name}", T(OverviewText.BriefBy, "closer_id")],
        ["FreelanceProjectCreated"]    = ["{project_name}", T(OverviewText.BriefBy, "creator_id")],
        ["FreelanceProjectCompleted"]  = ["{project_name}"],
        ["FreelanceProjectExpired"]    = ["{project_name}"],
        ["FreelanceProjectACLDeleted"] = ["{project_name}"],

        // Moon mining
        ["MoonminingExtractionStarted"]   = ["{structureID}", T(OverviewText.BriefReady, "readyTime")],
        ["MoonminingExtractionFinished"]  = ["{structureID}", T(OverviewText.BriefFractures, "autoTime")],
        ["MoonminingExtractionCancelled"] = ["{structureID}", T(OverviewText.BriefBy, "cancelledBy")],
        ["MoonminingAutomaticFracture"]   = ["{structureID}", T(OverviewText.BriefOfOre, "ore.total")],
        ["MoonminingLaserFired"]          = ["{structureID}", T(OverviewText.BriefBy, "firedBy")],

        // Structures
        ["StructureUnderAttack"]        = ["{structureID}", T(OverviewText.BriefByAttacker, "charID", "corpLinkData?"),
                                           T(OverviewText.BriefShieldArmorHull, "shieldPercentage", "armorPercentage", "hullPercentage")],
        ["StructureLostShields"]        = ["{structureID}", T(OverviewText.BriefReinforcedUntil, "timestamp")],
        ["StructureLostArmor"]          = ["{structureID}", T(OverviewText.BriefReinforcedUntil, "timestamp")],
        ["StructureDestroyed"]          = ["{structureID}"],
        ["StructureFuelAlert"]          = ["{structureID}", T(OverviewText.BriefQuantityLeft, "listOfTypesAndQty")],
        ["StructureLowReagentsAlert"]   = ["{structureID}"],
        ["StructureNoReagentsAlert"]    = ["{structureID}"],
        ["StructureServicesOffline"]    = ["{structureID}", "{listOfServiceModuleIDs?}"],
        ["StructureAnchoring"]          = ["{structureID}", T(OverviewText.BriefTimeLeft, "timeLeft")],
        ["StructureUnanchoring"]        = ["{structureID}", T(OverviewText.BriefTimeLeft, "timeLeft")],
        ["StructureOnline"]             = ["{structureID}"],
        ["StructureWentHighPower"]      = ["{structureID}"],
        ["StructureWentLowPower"]       = ["{structureID}"],
        ["StructureItemsMovedToSafety"] = ["{structureID}", T(OverviewText.BriefMovedTo, "newStationID")],
        ["StructureItemsDelivered"]     = ["{structureID}", Or(T(OverviewText.BriefDeliveredFor, "listOfTypesAndQty", "charID"), "{listOfTypesAndQty}")],
        ["StructureImpendingAbandonmentAssetsAtRisk"] = ["{structureID}", Counted(nameof(OverviewText.BriefDaysLeftOther), "daysUntilAbandon")],
        ["StructurePaintPurchased"]     = [Counted(nameof(OverviewText.BriefStructuresByOther), "structureIDs.count", "purchasedByCharacterID")],
        ["OwnershipTransferred"]        = ["{structureID}", T(OverviewText.BriefTransferredTo, "newOwnerCorpID")],
        ["EntosisCaptureStarted"]       = [T(OverviewText.BriefStructureIn, "structureTypeID", "solarSystemID")],

        // Starbases
        ["TowerAlertMsg"]         = [T(OverviewText.BriefTowerAt, "typeID", "moonID"), T(OverviewText.BriefByAttacker, "aggressorID?", "aggressorCorpID?"),
                                     T(OverviewText.BriefShieldArmorHull, "shieldValue", "armorValue", "hullValue")],
        ["TowerResourceAlertMsg"] = [T(OverviewText.BriefTowerAt, "typeID", "moonID"), T(OverviewText.BriefQuantityLeft, "wants")],

        // Clones
        ["CloneActivationMsg2"]  = ["{cloneStationID}"],
        ["CloneActivationMsg"]   = ["{cloneStationID}"],
        ["CloneRevokedMsg2"]     = ["{stationID}", T(OverviewText.BriefBy, "corpID")],
        ["JumpCloneDeletedMsg1"] = ["{locationID}"],
        ["JumpCloneDeletedMsg2"] = ["{locationID}", T(OverviewText.BriefBy, "destroyerID")],

        // Combat
        ["KillReportVictim"]    = ["{victimShipTypeID}"],
        ["KillReportFinalBlow"] = ["{victimShipTypeID}"],
        ["KillRightEarned"]     = [T(OverviewText.BriefKillRightOn, "charID")],

        // War
        ["WarDeclared"]               = [T(OverviewText.BriefVersus, "declaredByID", "againstID"), T(OverviewText.BriefFightingStarts, "timeStarted")],
        ["WarInvalid"]                = [T(OverviewText.BriefVersus, "declaredByID", "againstID"), T(OverviewText.BriefEnds, "endDate")],
        ["WarRetractedByConcord"]     = [T(OverviewText.BriefVersus, "declaredByID", "againstID"), T(OverviewText.BriefEnds, "endDate")],
        ["WarHQRemovedFromSpace"]     = [T(OverviewText.BriefVersus, "declaredByID", "againstID")],
        ["WarInherited"]              = [T(OverviewText.BriefVersus, "declaredByID", "againstID")],
        ["WarAllyInherited"]          = [T(OverviewText.BriefVersus, "declaredByID", "againstID")],
        ["MutualWarInviteSent"]       = [T(OverviewText.BriefVersus, "declaredByID", "againstID")],
        ["AllyJoinedWarAggressorMsg"] = [T(OverviewText.BriefJoins, "allyID", "aggressorID"), T(OverviewText.BriefAgainst, "defenderID")],
        ["AllyJoinedWarAllyMsg"]      = [T(OverviewText.BriefJoins, "allyID", "defenderID"), T(OverviewText.BriefAgainst, "aggressorID")],
        ["OfferedToAlly"]             = [T(OverviewText.BriefVersus, "defenderID", "aggressorID"), T(OverviewText.BriefBy, "charID")],
        ["AllianceCapitalChanged"]    = ["{allianceID}", T(OverviewText.BriefNewCapital, "solarSystemID")],

        // Nothing but their name
        ["CorpBecameWarEligible"]   = ["{summary}"],
        ["CorpNoLongerWarEligible"] = ["{summary}"],
        ["GameTimeAdded"]           = ["{summary}"],
        ["ExpertSystemExpired"]     = ["{typeID}"],
    };

    private static readonly Regex Fact = new(@"\{([^}?]+)(\?)?\}", RegexOptions.Compiled);

    /// <summary>A line's words with a fact in each numbered hole: <c>T(OverviewText.BriefBy, "firedBy")</c>
    /// is "by {firedBy}" in English. A fact ending in "?" may be missing.</summary>
    private static string T(string words, params string[] facts) =>
        string.Format(CultureInfo.InvariantCulture, words, [.. facts.Select(f => (object)$"{{{f}}}")]);

    /// <summary>Alternatives for one line, the first that can be shown winning.</summary>
    private static string Or(params string[] alternatives) => string.Join(" | ", alternatives);

    /// <summary>
    /// A counted line: the words are the form of the plural <paramref name="family"/> (named by its
    /// Other entry, as <see cref="Plurals"/> names them) for the number in the first of
    /// <paramref name="facts"/>, which fills {0}; the rest fill {1} on. Only marked here — see
    /// <see cref="Template"/>.
    /// </summary>
    private static string Counted(string family, params string[] facts) => $"#{family} {string.Join(' ', facts)}";

    /// <summary>
    /// What one alternative says for this notification: itself, or for a counted line the form its
    /// number needs — null when the number is missing, so the line is not shown, as for any other
    /// missing fact.
    ///
    /// <para>The form is looked up the way <see cref="Plurals.Format"/> looks it up, but not
    /// formatted with the number: the number stays a fact, drawn as a value rather than as one of
    /// the dim joining words.</para>
    /// </summary>
    private static string? Template(string alternative, NotificationBodyVm body)
    {
        if (!alternative.StartsWith('#')) return alternative;

        var words  = alternative[1..].Split(' ');
        var family = words[0];
        var facts  = words[1..];
        if (body.Facts.GetValueOrDefault(facts[0]) is not [{ } count, ..]) return null;

        // The body writes the number with this culture's digit grouping ("1,234").
        var form = long.TryParse(count.Text, NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var n)
            ? OverviewText.ResourceManager.GetString(family[..^"Other".Length] + Plurals.Category(n, CultureInfo.CurrentUICulture))
            : null;
        return T(form ?? OverviewText.ResourceManager.GetString(family) ?? family, facts);
    }

    /// <summary>The lines for one notification. <paramref name="recipient"/> is the character it
    /// came to, or null when it came to several.</summary>
    public static IReadOnlyList<NotifBriefLineVm> For(string type, NotificationBodyVm body, NotifValueVm? recipient)
    {
        if (!Lines.TryGetValue(type, out var spec)) return Fallback(body);

        var lines = new List<NotifBriefLineVm>(spec.Length);
        foreach (var line in spec)
            foreach (var alternative in line.Split(" | "))
                if (Template(alternative, body) is { } template
                    && Compose(template, body, recipient) is { } built) { lines.Add(built); break; }
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
