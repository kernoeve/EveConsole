using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using YamlDotNet.Serialization;

namespace EveConsole.ViewModels;

// ═══ What a notification body is drawn from ═════════════════════════════════════

/// <summary>One value in a notification: text, or a named thing with an icon and somewhere to
/// open it — a character, a corporation, an item, a structure.</summary>
public sealed class NotifValueVm : ReactiveObject
{
    public string  Text       { get; init; } = "";
    public string? Tip        { get; init; }
    public bool    AlignRight { get; init; }
    public bool    IsGood     { get; init; }
    public bool    IsBad      { get; init; }

    /// <summary>A joining word on an Overview card ("at", "due", "for") rather than a value.</summary>
    public bool    IsDim      { get; init; }

    /// <summary>An images.evetech.net path ("types/34/icon?size=32"); empty to keep the icon's
    /// room without a picture, so a system lines up under the structure above it; null for a
    /// value with no picture at all — a date, a sum.</summary>
    public string? IconUrl    { get; init; }
    public Action? Open       { get; init; }

    public bool IsLink      => Open is not null;
    public bool IsPlain     => Open is null;
    public bool HasIconSlot => IconUrl is not null;

    /// <summary>A picture to show, not just room kept for one — what a card asks, where a
    /// system needs no gap in front of it.</summary>
    public bool HasPicture  => !string.IsNullOrEmpty(IconUrl);

    /// <summary>
    /// Fetched the first time something asks for it: the binding of a card scrolled into view,
    /// or of the detail pane. The Overview holds up to a thousand notifications in a virtualised
    /// list, and only the pictures somebody looks at are worth the request.
    /// </summary>
    public Bitmap? Icon
    {
        get
        {
            if (!_iconRequested && !string.IsNullOrEmpty(IconUrl)) _ = LoadIconAsync();
            return _icon;
        }
        private set => this.RaiseAndSetIfChanged(ref _icon, value);
    }
    private Bitmap? _icon;
    private bool    _iconRequested;

    public void OpenIt() => Open?.Invoke();

    public async Task LoadIconAsync()
    {
        _iconRequested = true;
        if (string.IsNullOrEmpty(IconUrl)) return;
        var bmp = await EveImageCache.GetAsync($"https://images.evetech.net/{IconUrl}");
        await Dispatcher.UIThread.InvokeAsync(() => Icon = bmp);
    }
}

/// <summary>One labelled field.</summary>
public sealed class NotifFieldVm
{
    public string Label { get; init; } = "";
    public IReadOnlyList<NotifValueVm> Values { get; init; } = [];
    public string Text => Values.Count > 0 ? Values[0].Text : "";
}

/// <summary>A list the notification carries — ore by volume, fuel, implants, standings. With no
/// second column it is a plain list, laid out to flow across the pane.</summary>
public sealed class NotifTableVm
{
    public string Title   { get; init; } = "";
    public string Header1 { get; init; } = "";
    public string Header2 { get; init; } = "";
    public string Header3 { get; init; } = "";
    public bool HasCol2 => Header2.Length > 0;
    public bool HasCol3 => Header3.Length > 0;
    public bool IsList  => !HasCol2 && !HasCol3;
    public IReadOnlyList<NotifRowVm> Rows { get; init; } = [];
}

public sealed class NotifRowVm
{
    public NotifValueVm  Cell1 { get; init; } = new();
    public NotifValueVm? Cell2 { get; init; }
    public NotifValueVm? Cell3 { get; init; }
}

/// <summary>A notification's contents, laid out: a sentence where one helps, labelled fields,
/// lists, and any long text (an application) on its own.</summary>
public sealed class NotificationBodyVm
{
    public string Summary { get; init; } = "";
    public IReadOnlyList<NotifFieldVm> Fields { get; init; } = [];
    public IReadOnlyList<NotifTableVm> Tables { get; init; } = [];
    public IReadOnlyList<NotifFieldVm> Notes  { get; init; } = [];

    /// <summary>
    /// The same values by name, for the Overview's cards, which show a few per type. A YAML key
    /// ("structureID", "moonID"), or a name this layout gives: bill, amount, location, alliance,
    /// due; with, change, now, others for standings; summary; "key.count" for a list's length,
    /// "ore.total". A list's own key holds its first row. Case does not matter.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<NotifValueVm>> Facts { get; init; } =
        new Dictionary<string, IReadOnlyList<NotifValueVm>>();

    public bool HasSummary => Summary.Length > 0;
    public bool HasFields  => Fields.Count > 0;
    public bool IsEmpty    => Summary.Length == 0 && Fields.Count == 0 && Tables.Count == 0 && Notes.Count == 0;

    /// <summary>Two columns once there is enough to fill them, split down the middle so each reads
    /// top to bottom. A short notification stays in one, where it reads as a list.</summary>
    private bool TwoColumns => Fields.Count > 5;
    public IReadOnlyList<NotifFieldVm> FieldsLeft  => TwoColumns ? [.. Fields.Take((Fields.Count + 1) / 2)] : Fields;
    public IReadOnlyList<NotifFieldVm> FieldsRight => TwoColumns ? [.. Fields.Skip((Fields.Count + 1) / 2)] : [];
}

// ═══ Building one ═══════════════════════════════════════════════════════════════

/// <summary>
/// Turns a notification's YAML into a laid-out body: each field named for what it means, and every
/// id — character, corporation, item, system, station, structure — shown as its name, with its
/// icon, as a link.
///
/// <para>⚠️ Labels come from what a field MEANS in that notification, not from its key. The same
/// key means different things by type: a corporation bill's externalID is the thing rented (27,
/// "Office") on an office bill and the alliance billed on a maintenance bill; OfferedToAlly's
/// mercID is the ally on offer. So there are defaults per key, overridden per type, and bills are
/// read by bill type. Worked out from every type this app has received — see the notes by each.</para>
/// </summary>
public static class NotificationBody
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private enum K
    {
        Skip, Text, LongText,
        Character, Corporation, Alliance, Faction, Entity,
        Type, TypeList, System, Moon, Location, Structure, StructureList,
        Isk, Number, Decimal2, Percent, Fraction, Bool,
        Date, Duration, Seconds, Hours,
        Killmail, TypeQuantities, OreVolumes, Wants, LinkDataEntity, StructureIdType,
    }

    /// <summary>What a key means: its label, the kind of value, and where it sits among the
    /// fields (lower first; -1 places it by kind — see <see cref="KindRank"/>).</summary>
    private sealed record F(string Label, K Kind, int Rank = -1);

    // ── Every key in use, by what it usually means ────────────────────────────────
    private static readonly Dictionary<string, F> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        // where. ⚠️ Structure notifications spell it "solarsystemID"; the lookup ignores case.
        ["structureID"]     = new("Structure", K.Structure),
        ["structureTypeID"] = new("Structure type", K.Type, 1),
        ["structureIDs"]    = new("Structures", K.StructureList),
        ["stationID"]       = new("Station", K.Location),
        ["newStationID"]    = new("Moved to", K.Location),
        ["cloneStationID"]  = new("Clone location", K.Location),
        ["locationID"]      = new("Location", K.Location),
        ["solarSystemID"]   = new("System", K.System),
        ["moonID"]          = new("Moon", K.Moon),

        // who
        ["charID"]                 = new("Character", K.Character),
        ["corpID"]                 = new("Corporation", K.Corporation),
        ["corporation_id"]         = new("Corporation", K.Corporation),
        ["allianceID"]             = new("Alliance", K.Alliance),
        ["ownerID"]                = new("Owner", K.Entity),
        ["creator_id"]             = new("Created by", K.Character, 31),
        ["closer_id"]              = new("Closed by", K.Character, 32),
        ["invokingCharID"]         = new("Invited by", K.Character),
        ["startedBy"]              = new("Started by", K.Character),
        ["firedBy"]                = new("Fired by", K.Character),
        ["cancelledBy"]            = new("Cancelled by", K.Character),
        ["purchasedByCharacterID"] = new("Purchased by", K.Character),
        ["destroyerID"]            = new("Destroyed by", K.Character),
        ["podKillerID"]            = new("Podded by", K.Character),
        ["newOwnerCorpID"]         = new("New owner", K.Corporation),
        ["oldOwnerCorpID"]         = new("Previous owner", K.Corporation),
        ["locationOwnerID"]        = new("Location owner", K.Corporation),
        ["ownerCorpLinkData"]      = new("Owner", K.LinkDataEntity),
        ["corpLinkData"]           = new("Corporation", K.LinkDataEntity),
        ["bountyPlacerID"]         = new("Placed by", K.Entity),

        // war: who declared, on whom, then whoever joined or left
        ["declaredByID"]        = new("Declared by", K.Entity, 30),
        ["againstID"]           = new("Against", K.Entity, 31),
        ["aggressorID"]         = new("Aggressor", K.Entity, 30),
        ["aggressorCorpID"]     = new("Aggressor corporation", K.Corporation, 31),
        ["aggressorAllianceID"] = new("Aggressor alliance", K.Alliance, 32),
        ["defenderID"]          = new("Defender", K.Entity, 33),
        ["allyID"]              = new("Ally", K.Entity, 34),
        ["mercID"]              = new("Ally", K.Entity, 34),
        ["enemyID"]             = new("Enemy", K.Entity, 35),
        ["opponentID"]          = new("Opponent", K.Entity, 35),
        ["quitterID"]           = new("Left the war", K.Entity, 36),
        ["warHQ"]               = new("War HQ", K.Text, 37),
        ["warHQ_IdType"]        = new("War HQ", K.StructureIdType, 37),
        ["cost"]                = new("Cost", K.Isk),
        ["iskValue"]            = new("Fee", K.Isk),
        ["delayHours"]          = new("Starts after", K.Hours),
        ["hostileState"]        = new("Hostile now", K.Bool),

        // what
        ["typeID"]                 = new("Type", K.Type),
        ["typeIDs"]                = new("Types", K.TypeList),
        ["victimShipTypeID"]       = new("Ship", K.Type),
        ["requiresDeedTypeID"]     = new("Requires", K.Type),
        ["listOfTypesAndQty"]      = new("Items", K.TypeQuantities),
        ["listOfServiceModuleIDs"] = new("Services", K.TypeList),
        ["oreVolumeByType"]        = new("Ore", K.OreVolumes),
        ["wants"]                  = new("Needs", K.Wants),
        ["killMailID"]             = new("Killmail", K.Killmail),
        ["amount"]                 = new("Amount", K.Isk),
        ["bounty"]                 = new("Bounty", K.Isk),
        ["lpAmount"]               = new("Loyalty points", K.Number),
        ["goal_name"]              = new("Project", K.Text, 0),
        ["project_name"]           = new("Project", K.Text, 0),

        // state — shield, armor, hull, in the game's order
        ["shieldPercentage"] = new("Shield", K.Percent, 50),
        ["armorPercentage"]  = new("Armor", K.Percent, 51),
        ["hullPercentage"]   = new("Hull", K.Percent, 52),
        ["shieldValue"]      = new("Shield", K.Fraction, 50),
        ["armorValue"]       = new("Armor", K.Fraction, 51),
        ["hullValue"]        = new("Hull", K.Fraction, 52),
        ["isCorpOwned"]      = new("Corporation owned", K.Bool),
        ["isAbandoned"]      = new("Abandoned", K.Bool),
        ["daysUntilAbandon"] = new("Days until abandoned", K.Number),
        ["security"]         = new("Security status", K.Decimal2),
        ["newTaxRate"]       = new("New tax rate", K.Percent),
        ["oldTaxRate"]       = new("Previous tax rate", K.Percent),

        // when. EVE writes instants as Windows file times and spans as 100 ns ticks.
        ["timestamp"]                   = new("Time", K.Date),
        ["timeStarted"]                 = new("Started", K.Date),
        ["startTime"]                   = new("Starts", K.Date),
        ["timeDeclared"]                = new("Declared", K.Date),
        ["endDate"]                     = new("Ends", K.Date),
        ["expireTimeStamp"]             = new("Expires", K.Date),
        ["timeLeft"]                    = new("Time left", K.Duration),
        ["vulnerableTime"]              = new("Vulnerability window", K.Duration),
        ["readyTime"]                   = new("Ready", K.Date, 60),
        ["autoTime"]                    = new("Fractures automatically", K.Date, 61),
        ["lastCloned"]                  = new("Last cloned", K.Date),
        ["issuedAt"]                    = new("Purchased", K.Date),
        ["durationSeconds"]             = new("Lasts", K.Seconds),
        ["assetSafetyMinimumTimestamp"] = new("Deliverable from", K.Date, 60),
        ["assetSafetyFullTimestamp"]    = new("Delivered automatically", K.Date, 61),
        ["currentDate"]                 = new("Issued", K.Date),
        ["dueDate"]                     = new("Due", K.Date),

        ["applicationText"] = new("Application", K.LongText),

        // Not shown. Links and show-info arrays repeat an id already shown; the names are the
        // game's copy of what the ids resolve to (kept as a fallback, see Hints); the rest are
        // internal. The two asset-safety durations are the fixed spans behind the two dates.
        ["structureName"]              = new("", K.Skip),
        ["structureShowInfoData"]      = new("", K.Skip),
        ["ownerCorpName"]              = new("", K.Skip),
        ["corpName"]                   = new("", K.Skip),
        ["allianceName"]               = new("", K.Skip),
        ["allianceLinkData"]           = new("", K.Skip),
        ["killMailHash"]               = new("", K.Skip),
        ["goal_id"]                    = new("", K.Skip),
        ["project_id"]                 = new("", K.Skip),
        ["roleNameIDs"]                = new("", K.Skip),
        ["itemID"]                     = new("", K.Skip),
        ["payout"]                     = new("", K.Skip),
        ["assetSafetyDurationFull"]    = new("", K.Skip),
        ["assetSafetyDurationMinimum"] = new("", K.Skip),
    };

    // ── What changes by type ──────────────────────────────────────────────────────
    private static readonly Dictionary<string, Dictionary<string, F>> ByType = new(StringComparer.Ordinal)
    {
        // The applicant, not the recipient.
        ["CorpAppNewMsg"]      = Over(("charID", new("Applicant", K.Character))),
        ["CorpAppAcceptMsg"]   = Over(("charID", new("Applicant", K.Character))),
        ["CharAppAcceptMsg"]   = Over(("charID", new("Applicant", K.Character))),
        ["CharAppRejectMsg"]   = Over(("charID", new("Applicant", K.Character))),
        ["CharAppWithdrawMsg"] = Over(("charID", new("Applicant", K.Character))),
        ["CorpAppInvitedMsg"]  = Over(("charID", new("Invited", K.Character))),
        ["CharTerminationMsg"] = Over(("charID", new("Member", K.Character))),

        // corpID is the corporation that revoked the clone; stationID where it was (often a
        // structure), newStationID where it went.
        ["CloneRevokedMsg2"] = Over(("corpID", new("Revoked by", K.Corporation)),
                                    ("stationID", new("Was at", K.Location))),
        // corpStationID is the same place again: the YAML aliases it to cloneStationID.
        ["CloneActivationMsg2"] = Over(("corpStationID", new("", K.Skip))),

        // typeIDs are the implants that went with the clone; ownerID the clone's owner.
        ["JumpCloneDeletedMsg1"] = Over(("typeIDs", new("Implants lost", K.TypeList)),
                                        ("ownerID", new("Clone of", K.Character))),
        ["JumpCloneDeletedMsg2"] = Over(("typeIDs", new("Implants lost", K.TypeList)),
                                        ("ownerID", new("Clone of", K.Character))),

        ["InsurancePayoutMsg"]          = Over(("amount", new("Payout", K.Isk))),
        ["StructureItemsMovedToSafety"] = Over(("newStationID", new("Asset safety station", K.Location, 2))),
        ["StructureFuelAlert"]          = Over(("listOfTypesAndQty", new("Fuel remaining", K.TypeQuantities))),
        ["StructureItemsDelivered"]     = Over(("listOfTypesAndQty", new("Delivered", K.TypeQuantities)),
                                               ("charID", new("Delivered to", K.Character))),
        ["StructureServicesOffline"]    = Over(("listOfServiceModuleIDs", new("Services offline", K.TypeList))),

        // A starbase is named by its tower's type; "wants" is the fuel it has left.
        ["TowerResourceAlertMsg"] = Over(("typeID", new("Tower", K.Type, 1)),
                                         ("wants", new("Fuel remaining", K.Wants))),
        ["TowerAlertMsg"]         = Over(("typeID", new("Tower", K.Type, 1)),
                                         ("aggressorID", new("Aggressor", K.Character, 30))),

        // The attacker's character, corporation and alliance.
        ["StructureUnderAttack"] = Over(("charID", new("Attacker", K.Character, 30)),
                                        ("corpLinkData", new("Attacker corporation", K.LinkDataEntity, 31)),
                                        ("allianceID", new("Attacker alliance", K.Alliance, 32))),

        ["OwnershipTransferred"]    = Over(("charID", new("Transferred by", K.Character))),
        ["EntosisCaptureStarted"]   = Over(("structureTypeID", new("Structure", K.Type, 0))),
        ["ExpertSystemExpired"]     = Over(("typeID", new("Expert system", K.Type))),
        ["AllianceCapitalChanged"]  = Over(("solarSystemID", new("New capital", K.System))),
        ["StructurePaintPurchased"] = Over(("structureIDs", new("Structures painted", K.StructureList))),
        // mercID is the would-be ally; charID the character who made the offer.
        ["OfferedToAlly"]           = Over(("charID", new("Offered by", K.Character, 36))),
        ["KillRightEarned"]         = Over(("charID", new("Kill right on", K.Character))),

        // timestamp is when the reinforcement ends: the notice's own time plus timeLeft.
        ["StructureLostShields"] = Over(("timestamp", new("Reinforced until", K.Date))),
        ["StructureLostArmor"]   = Over(("timestamp", new("Reinforced until", K.Date))),
        // timeStarted is when the fighting starts: the declaration plus delayHours.
        ["WarDeclared"]          = Over(("timeStarted", new("Fighting starts", K.Date))),
    };

    private static Dictionary<string, F> Over(params (string Key, F Def)[] defs) =>
        defs.ToDictionary(d => d.Key, d => d.Def, StringComparer.OrdinalIgnoreCase);

    // ── Corporation bills. What externalID and externalID2 mean depends on the bill type. ──
    //
    // Office rental (2): externalID is the thing rented — 27, "Office" — and externalID2 the
    // station or structure the office is in. Alliance maintenance (5): externalID is the alliance
    // billed; externalID2 is unused (-1). Those two are every bill on record here; the other
    // types are the game's own names, and their references are shown as they come.
    private static readonly Dictionary<int, string> BillTypes = new()
    {
        [1] = "Market fine",
        [2] = "Office rental",
        [3] = "Broker fee",
        [4] = "War",
        [5] = "Alliance maintenance",
        [6] = "Sovereignty marker",
    };

    /// <summary>Types that carry nothing but their name.</summary>
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["CorpBecameWarEligible"]   = "Your corporation is now eligible for war declarations.",
        ["CorpNoLongerWarEligible"] = "Your corporation is no longer eligible for war declarations.",
        ["GameTimeAdded"]           = "Game time was added to the account.",
    };

    /// <summary>Lays out one notification. Text it cannot read comes back whole, as a note,
    /// rather than as an empty pane. Icons are fetched when first shown.</summary>
    public static async Task<NotificationBodyVm> BuildAsync(
        string type, string? text, ContractNameResolver names, IDbContextFactory<AppDbContext> dbFactory) =>
        (await BuildManyAsync([(type, text)], names, dbFactory))[0];

    /// <summary>
    /// Lays out many at once — the Overview's whole list — with every name, item, system,
    /// station and structure among them looked up in one pass rather than a pass each. In the
    /// order given.
    /// </summary>
    public static async Task<IReadOnlyList<NotificationBodyVm>> BuildManyAsync(
        IReadOnlyList<(string Type, string? Text)> notifications,
        ContractNameResolver names, IDbContextFactory<AppDbContext> dbFactory)
    {
        var r        = new Resolution();
        var prepared = notifications.Select(n => Prepare(n.Type, n.Text, r)).ToList();
        await r.ResolveAsync(names, dbFactory);
        return [.. prepared.Select(p => p.Finish(r))];
    }

    /// <summary>One notification read and waiting for its names: the parts of its layout, the
    /// sentence to write once names are known, or the raw text it could not be read as.</summary>
    private sealed class Prepared(string type, Parts parts, Func<Resolution, string>? summary, string? raw)
    {
        public NotificationBodyVm Finish(Resolution r)
        {
            if (raw is not null) return Raw(raw);

            var said = summary?.Invoke(r) ?? "";
            if (said.Length == 0 && parts.IsEmpty && Descriptions.TryGetValue(type, out var description))
                said = description;

            var facts = parts.Facts.ToDictionary(
                f => f.Key, f => (IReadOnlyList<NotifValueVm>)[.. f.Value.Select(p => p.Build(r))],
                StringComparer.OrdinalIgnoreCase);
            if (said.Length > 0) facts["summary"] = [new NotifValueVm { Text = said }];

            return new NotificationBodyVm
            {
                Summary = said,
                Fields  = [.. parts.Fields.OrderBy(f => f.Rank).Select(f => new NotifFieldVm
                              { Label = f.Label, Values = [.. f.Values.Select(p => p.Build(r))] })],
                Tables  = [.. parts.Tables.Select(t => t.Build(r))],
                Notes   = [.. parts.Notes.Select(n => new NotifFieldVm
                              { Label = n.Label, Values = [new NotifValueVm { Text = n.Text }] })],
                Facts   = facts,
            };
        }
    }

    private static Prepared Prepare(string type, string? text, Resolution r)
    {
        var parts = new Parts();
        if (string.IsNullOrWhiteSpace(text)) return new Prepared(type, parts, null, null);

        object? tree;
        try { tree = Yaml.Deserialize<object>(new StringReader(text)); }
        catch { return new Prepared(type, parts, null, text); }

        switch (tree)
        {
            case null:
                return new Prepared(type, parts, null, null);
            case IList<object> list when type.StartsWith("NPCStandings", StringComparison.Ordinal):
                return new Prepared(type, parts, Standings(list, r, parts), null);
            case IDictionary<object, object> bill when type == "CorpAllBillMsg":
                return new Prepared(type, parts, Bill(bill, r, parts), null);
            case IDictionary<object, object> map:
            {
                Hints(map, r);
                var overrides = ByType.GetValueOrDefault(type);
                var hqLinked  = Get(map, "warHQ_IdType") is IList<object>;
                foreach (var (k, v) in map)
                {
                    var key = k?.ToString() ?? "";
                    // The linked war HQ carries the name itself; the text copy would say it twice.
                    if (hqLinked && key.Equals("warHQ", StringComparison.OrdinalIgnoreCase)) continue;
                    AddField(key, v, overrides, r, parts);
                }
                return new Prepared(type, parts, null, null);
            }
            default:
                return new Prepared(type, parts, null, text);
        }
    }

    private static NotificationBodyVm Raw(string text) =>
        new() { Notes = [new NotifFieldVm { Label = "Details", Values = [new NotifValueVm { Text = text.Trim() }] }] };

    /// <summary>
    /// Names the notification itself carries, for things our own tables may not know: a structure
    /// the Structure Browser has never seen is still named in its notification, and its type is
    /// beside it. Used only where the tables have nothing — they are current, and a notification
    /// is as old as it is.
    /// </summary>
    private static void Hints(IDictionary<object, object> map, Resolution r)
    {
        var structureId = Long(Get(map, "structureID"));
        if (structureId > 0)
        {
            var name = Get(map, "structureName")?.ToString();
            if (string.IsNullOrWhiteSpace(name) && Get(map, "structureLink") is string link) name = Strip(link);
            r.StructureHint(structureId, name, Int(Get(map, "structureTypeID")));
        }
        if (Get(map, "warHQ_IdType") is IList<object> { Count: >= 2 } hq)
            r.StructureHint(Long(hq[0]), Get(map, "warHQ") is string hqName ? Strip(hqName) : null, Int(hq[1]));
    }

    // ── One field ─────────────────────────────────────────────────────────────────

    private sealed class Parts
    {
        public readonly List<(string Label, List<Pending> Values, int Rank)> Fields = [];
        public readonly List<PendingTable> Tables = [];
        public readonly List<(string Label, string Text)> Notes = [];

        /// <summary>The same values by name — see <see cref="NotificationBodyVm.Facts"/>.</summary>
        public readonly Dictionary<string, List<Pending>> Facts = new(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty => Fields.Count == 0 && Tables.Count == 0 && Notes.Count == 0;

        public void Field(string key, string label, Pending value, int rank)
        {
            Fields.Add((label, [value], rank));
            Facts[key] = [value];
        }

        public void Note(string key, string label, string text)
        {
            Notes.Add((label, text));
            Facts[key] = [Pending.Plain(text)];
        }

        /// <summary>A list, and under its key the first row as a card shows it — quantity
        /// first where there is one ("115 × Hydrogen Fuel Block") — with the rest counted.</summary>
        public void Table(string key, PendingTable table, bool quantityFirst = false)
        {
            Tables.Add(table);
            if (table.Rows.Count == 0) return;

            var (c1, c2, c3) = table.Rows[0];
            List<Pending> first = quantityFirst && c2 is not null
                ? [c2 with { Text = $"{c2.Text} ×", Flush = false }, c1]
                : [c1, .. new[] { c2, c3 }.OfType<Pending>()];
            if (table.Rows.Count > 1) first.Add(Pending.Muted($"+{table.Rows.Count - 1} more"));

            Facts[key] = first;
            Facts[$"{key}.count"] = [Pending.Plain(table.Rows.Count.ToString("N0", CultureInfo.CurrentCulture))];
        }
    }

    private static void AddField(string key, object? value, Dictionary<string, F>? overrides, Resolution r, Parts parts)
    {
        var def = overrides?.GetValueOrDefault(key) ?? Defaults.GetValueOrDefault(key) ?? Guess(key, value);
        if (def.Kind == K.Skip) return;
        var rank = def.Rank >= 0 ? def.Rank : KindRank(def.Kind);

        var scalar = value is IDictionary<object, object> or IList<object> ? null : value?.ToString() ?? "";

        switch (def.Kind)
        {
            case K.LongText:
                var t = Strip(scalar ?? "").Trim();
                if (t.Length > 0) parts.Note(key, def.Label, t);
                return;

            case K.TypeQuantities when value is IList<object> pairs:          // [[quantity, typeId], ...]
                parts.Table(key, new PendingTable(def.Label, "Type", "Quantity", "",
                    [.. pairs.OfType<IList<object>>().Where(p => p.Count >= 2)
                        .Select(p => (r.Type(Int(p[1])), (Pending?)Pending.Number(Long(p[0])), (Pending?)null))]),
                    quantityFirst: true);
                return;

            case K.Wants when value is IList<object> wants:                   // [{quantity, typeID}, ...]
                parts.Table(key, new PendingTable(def.Label, "Type", "Quantity", "",
                    [.. wants.OfType<IDictionary<object, object>>()
                        .Select(w => (r.Type(Int(Get(w, "typeID"))), (Pending?)Pending.Number(Long(Get(w, "quantity"))), (Pending?)null))]),
                    quantityFirst: true);
                return;

            case K.OreVolumes when value is IDictionary<object, object> ore:  // { typeId: m³ }
            {
                var total = M3(ore.Values.Sum(Dbl));
                var rows  = ore.Select(o => (r.Type(Int(o.Key)), (Pending?)Pending.Right(M3(Dbl(o.Value))), (Pending?)null)).ToList();
                if (rows.Count > 1) rows.Add((Pending.Plain("Total"), Pending.Right(total), null));
                parts.Tables.Add(new PendingTable(def.Label, "", "Volume", "", rows));
                parts.Facts["ore.total"] = [Pending.Plain(total)];
                parts.Facts["ore.count"] = [Pending.Plain(ore.Count.ToString("N0", CultureInfo.CurrentCulture))];
                return;
            }

            case K.TypeList when value is IList<object> types:
                if (types.Count > 0)
                    parts.Table(key, new PendingTable(def.Label, "", "", "",
                        [.. types.Select(x => (r.Type(Int(x)), (Pending?)null, (Pending?)null))]));
                return;

            case K.StructureList when value is IList<object> ids:
                if (ids.Count > 0)
                    parts.Table(key, new PendingTable(def.Label, "", "", "",
                        [.. ids.Select(x => (r.Structure(Long(x)), (Pending?)null, (Pending?)null))]));
                return;

            case K.LinkDataEntity when value is IList<object> { Count: >= 3 } link:   // ["showinfo", typeId, id]
                parts.Field(key, def.Label, r.Entity(Long(link[2]), ShowInfoKind(Int(link[1]))), rank);
                return;

            case K.StructureIdType when value is IList<object> { Count: >= 2 } idType:  // [id, typeId]
                parts.Field(key, def.Label, r.Structure(Long(idType[0]), Int(idType[1])), rank);
                return;
        }

        if (scalar is null)
        {
            // A shape nothing above expects: shown flattened, so it is at least visible.
            parts.Field(key, def.Label, Pending.Plain(Flatten(value)), rank);
            return;
        }

        if (Value(def.Kind, scalar, r) is { } p) parts.Field(key, def.Label, p, rank);
    }

    /// <summary>A key nobody listed. Named from the key, with the old id heuristics so an
    /// unfamiliar time or entity still reads as one.</summary>
    private static F Guess(string key, object? value)
    {
        var k = key.ToLowerInvariant();
        if (k.EndsWith("link") || k.EndsWith("linkdata") || k.Contains("showinfo")) return new("", K.Skip);
        var label = NotificationTitles.Humanize(key);
        var s = value?.ToString() ?? "";
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)
            || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return new(label, K.Bool);
        if (k.EndsWith("typeid"))                                    return new(label, K.Type);
        if (k.EndsWith("structureid"))                               return new(label, K.Structure);
        if (k.EndsWith("stationid"))                                 return new(label, K.Location);
        if (k.EndsWith("systemid"))                                  return new(label, K.System);
        if (k.EndsWith("moonid"))                                    return new(label, K.Moon);
        if (k.Contains("char") || k.Contains("pilot") || k.Contains("ceo")) return new(label, K.Character);
        if (k.Contains("corp"))                                      return new(label, K.Corporation);
        if (k.Contains("alliance"))                                  return new(label, K.Alliance);
        if (k.EndsWith("time") || k.EndsWith("date") || k.Contains("timestamp"))
            return new(label, long.TryParse(s, out var n) && n >= 10_000_000_000_000_000L ? K.Date : K.Duration);
        return new(label, K.Text);
    }

    private static int KindRank(K kind) => kind switch
    {
        K.Structure or K.Location or K.StructureIdType            => 0,
        K.System                                                  => 10,
        K.Moon                                                    => 20,
        K.Character or K.Corporation or K.Alliance or K.Faction
            or K.Entity or K.LinkDataEntity                       => 30,
        K.Type or K.Killmail                                      => 40,
        K.Isk or K.Number or K.Decimal2 or K.Percent
            or K.Fraction or K.Bool                               => 50,
        K.Date or K.Duration or K.Seconds or K.Hours              => 60,
        _                                                         => 70,
    };

    /// <summary>A show-info link names what it points at by type: 2 is a corporation, 16159 an
    /// alliance, 30 a faction, and 1373–1386 the character types (one per bloodline).</summary>
    private static K ShowInfoKind(int typeId) => typeId switch
    {
        2                   => K.Corporation,
        16159               => K.Alliance,
        30                  => K.Faction,
        >= 1373 and <= 1386 => K.Character,
        _                   => K.Entity,
    };

    private static Pending? Value(K kind, string s, Resolution r) => kind switch
    {
        K.Character or K.Corporation or K.Alliance or K.Faction or K.Entity
            => Long(s) > 0 ? r.Entity(Long(s), kind) : null,                  // 0 = nobody (podKillerID)
        K.Type      => Int(s) > 0 ? r.Type(Int(s)) : null,
        K.System    => Int(s) > 0 ? r.System(Int(s)) : null,
        K.Moon      => Int(s) > 0 ? r.Moon(Int(s)) : null,
        K.Structure => Long(s) > 0 ? r.Structure(Long(s)) : null,
        K.Location  => Long(s) > 0 ? r.Location(Long(s)) : null,
        K.Killmail  => Long(s) > 0
            ? Pending.Link(Long(s).ToString(CultureInfo.InvariantCulture), "Open the killmail",
                           () => EntityNavigator.Instance.Killmail((int)Long(s)))
            : null,
        K.Isk       => Pending.Right(Isk(Dbl(s))),
        K.Number    => Pending.Number(Long(s)),
        K.Decimal2  => Pending.Right(Dbl(s).ToString("0.00", CultureInfo.CurrentCulture)),
        K.Percent   => Pending.Right($"{Dbl(s):0.#}%"),
        K.Fraction  => Pending.Right($"{Dbl(s) * 100:0.#}%"),
        K.Bool      => Pending.Plain(s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" ? "Yes" : "No"),
        K.Date      => Pending.Plain(Date(Long(s)) ?? s),
        K.Duration  => Pending.Plain(Span(TimeSpan.FromTicks(Long(s)))),
        K.Seconds   => Pending.Plain(Span(TimeSpan.FromSeconds(Dbl(s)))),
        K.Hours     => Pending.Plain(Dbl(s) == 1 ? "1 hour" : $"{Dbl(s):0.#} hours"),
        _           => Strip(s).Trim() is { Length: > 0 } t ? Pending.Plain(t) : null,
    };

    // ── Standings ─────────────────────────────────────────────────────────────────
    //
    // A list of changes, each [from, to, change, -1.0, 1.0, standing now]: from is the NPC (faction,
    // corporation or agent), to is the character, change is on the ±1 scale the game shows ×10 (an
    // agent's 0.0462 is the "+0.462" in the notice), and the sixth item, on the first entry only, is
    // the standing that results, on the game's own scale. The -1.0 and 1.0 are the change's limits,
    // not data. The first entry is the one the notice is about; the rest are knock-on changes.
    //
    // ⚠️ The sign says which way it went, never the type: ESI files a mission's GAIN as
    // NPCStandingsLost too (checked against the in-game notice and standings window).
    private static Func<Resolution, string>? Standings(IList<object> list, Resolution r, Parts parts)
    {
        var rows = new List<(Pending, Pending?, Pending?)>();
        (long From, double Change, double? Now)? first = null;

        foreach (var e in list.OfType<IList<object>>().Where(e => e.Count >= 3))
        {
            var from    = Long(e[0]);
            var change  = Tidy(Dbl(e[2]) * 10);
            double? now = e.Count >= 6 ? Tidy(Dbl(e[5])) : null;
            first ??= (from, change, now);

            rows.Add((r.Entity(from, K.Entity),
                      new Pending(Signed(change), null, Flush: true, Good: change > 0, Bad: change < 0),
                      Pending.Right(now is double n ? Standing(n) : "—")));
        }

        if (first is not { } f) return null;
        parts.Tables.Add(new PendingTable("Standing changes", "With", "Change", "Standing now", rows));

        // For a card: who, by how much, to what — and how many more changed with it.
        var others = rows.Count - 1;
        parts.Facts["with"]   = [rows[0].Item1];
        parts.Facts["change"] = [rows[0].Item2!];
        if (f.Now is double resulting) parts.Facts["now"] = [Pending.Plain(Standing(resulting))];
        if (others > 0) parts.Facts["others"] = [Pending.Muted($"+{others} other{(others == 1 ? "" : "s")}")];
        return res =>
        {
            // The verb carries the direction, so the amount goes unsigned: "decreased by 0.0056".
            var which = f.Change >= 0 ? "increased" : "decreased";
            var to    = f.Now is double v ? $", to {Standing(v)}" : "";
            var more  = others > 0 ? $" {others} other standing{(others == 1 ? "" : "s")} changed with it." : "";
            return $"{res.EntityName(f.From)} {which} their standing towards you by {Standing(Math.Abs(f.Change))}{to}.{more}";
        };
    }

    private static double Tidy(double v)     => Math.Abs(v) < 0.00005 ? 0 : v;   // no "-0.0000"
    private static string Signed(double v)   => v.ToString("+0.00##;-0.00##;0.00", CultureInfo.CurrentCulture);
    private static string Standing(double v) => v.ToString("0.00##", CultureInfo.CurrentCulture);

    // ── Bills ─────────────────────────────────────────────────────────────────────
    private static Func<Resolution, string> Bill(IDictionary<object, object> map, Resolution r, Parts parts)
    {
        var billType = Int(Get(map, "billTypeID"));
        var amount   = Dbl(Get(map, "amount"));
        var ext1     = Long(Get(map, "externalID"));
        var ext2     = Long(Get(map, "externalID2"));
        var issued   = Date(Long(Get(map, "currentDate")));
        var due      = Date(Long(Get(map, "dueDate")));
        var what     = BillTypes.GetValueOrDefault(billType, $"Bill type {billType}");

        // All one rank: a bill reads in the order it is written here. The keys are the names a
        // card asks for (NotificationBodyVm.Facts).
        parts.Field("bill", "Bill", Pending.Plain(what), 0);
        parts.Field("amount", "Amount", Pending.Plain(Isk(amount)), 0);
        if (Long(Get(map, "debtorID")) is var debtor and > 0)     parts.Field("debtor", "Billed to",  r.Entity(debtor, K.Entity), 0);
        if (Long(Get(map, "creditorID")) is var creditor and > 0) parts.Field("creditor", "Payable to", r.Entity(creditor, K.Entity), 0);

        Pending? place = null;
        switch (billType)
        {
            case 2:
                if (ext1 > 0) parts.Field("rented", "Rented", r.Type((int)ext1), 0);
                if (ext2 > 0) parts.Field("location", "Location", place = r.Location(ext2), 0);
                break;
            case 5:
                if (ext1 > 0) parts.Field("alliance", "For alliance", r.Entity(ext1, K.Alliance), 0);
                break;
            default:
                if (ext1 > 0) parts.Field("reference", "Reference", Pending.Plain(ext1.ToString(CultureInfo.InvariantCulture)), 0);
                if (ext2 > 0) parts.Field("reference2", "Second reference", Pending.Plain(ext2.ToString(CultureInfo.InvariantCulture)), 0);
                break;
        }
        if (issued is not null) parts.Field("issued", "Issued", Pending.Plain(issued), 0);
        if (due is not null)    parts.Field("due", "Due", Pending.Plain(due), 0);

        return res =>
        {
            var at   = place is not null ? $" at {place.Build(res).Text}" : "";
            var when = due is not null ? $", due {due}" : "";
            return $"{what}{at}: {Isk(amount)}{when}.";
        };
    }

    // ── Values waiting for names ──────────────────────────────────────────────────

    private sealed record Ref(K Kind, long Id, int IconTypeId = 0);

    private sealed record Pending(string? Text, Ref? Ref, bool Flush = false, bool Good = false, bool Bad = false,
                                  Action? Open = null, string? Tip = null, bool Dim = false)
    {
        public static Pending Plain(string text)                         => new(text, null);
        public static Pending Muted(string text)                         => new(text, null, Dim: true);
        public static Pending Right(string text)                         => new(text, null, Flush: true);
        public static Pending Number(long n)                             => new(n.ToString("N0", CultureInfo.CurrentCulture), null, Flush: true);
        public static Pending Link(string text, string tip, Action open) => new(text, null, Open: open, Tip: tip);

        public NotifValueVm Build(Resolution r) =>
            Ref is null
                ? new NotifValueVm { Text = Text ?? "", AlignRight = Flush, IsGood = Good, IsBad = Bad, Open = Open, Tip = Tip, IsDim = Dim }
                : r.Build(Ref);
    }

    private sealed record PendingTable(string Title, string H1, string H2, string H3,
                                       List<(Pending C1, Pending? C2, Pending? C3)> Rows)
    {
        public NotifTableVm Build(Resolution r) => new()
        {
            Title = Title, Header1 = H1, Header2 = H2, Header3 = H3,
            Rows  = [.. Rows.Select(x => new NotifRowVm { Cell1 = x.C1.Build(r), Cell2 = x.C2?.Build(r), Cell3 = x.C3?.Build(r) })],
        };
    }

    /// <summary>Every id the body names, gathered first and looked up in one pass per kind.</summary>
    private sealed class Resolution
    {
        private const string EntityTip = "Open in the entity browser";

        private readonly HashSet<long> _entities   = [];
        private readonly HashSet<int>  _types      = [];
        private readonly HashSet<int>  _systems    = [];
        private readonly HashSet<int>  _moons      = [];
        private readonly HashSet<long> _stations   = [];
        private readonly HashSet<long> _structures = [];
        private readonly Dictionary<long, (string? Name, int TypeId)> _structureHints = [];

        private IReadOnlyDictionary<long, string>           _entityNames = new Dictionary<long, string>();
        private readonly Dictionary<long, string>           _categories  = [];
        private Dictionary<int, string>                     _typeNames   = [];
        private Dictionary<int, (string Name, double Sec)>  _systemInfo  = [];
        private IReadOnlyDictionary<int, string>            _moonNames   = new Dictionary<int, string>();
        private Dictionary<long, (string Name, int TypeId)> _stationInfo = [];
        private readonly Dictionary<long, (string Name, int TypeId)> _structureInfo = [];

        public Pending Entity(long id, K kind)  { _entities.Add(id); return new(null, new Ref(kind, id)); }
        public Pending Type(int id)             { _types.Add(id);    return new(null, new Ref(K.Type, id)); }
        public Pending System(int id)           { _systems.Add(id);  return new(null, new Ref(K.System, id)); }
        public Pending Moon(int id)             { _moons.Add(id);    return new(null, new Ref(K.Moon, id)); }

        public Pending Structure(long id, int typeId = 0)
        {
            _structures.Add(id);
            if (typeId > 0) _types.Add(typeId);   // named by its type if its own name is unknown
            return new(null, new Ref(K.Structure, id, typeId));
        }

        /// <summary>An NPC station or a player structure, told apart by the id: stations sit
        /// below 100,000,000; structures are item ids, in the trillions.</summary>
        public Pending Location(long id)
        {
            if (id >= 100_000_000L) return Structure(id);
            _stations.Add(id);
            return new(null, new Ref(K.Location, id));
        }

        public void StructureHint(long id, string? name, int typeId)
        {
            _structureHints[id] = (string.IsNullOrWhiteSpace(name) ? null : name.Trim(), typeId);
            if (typeId > 0) _types.Add(typeId);
        }

        public string EntityName(long id) =>
            _entityNames.TryGetValue(id, out var n) && n.Length > 0 ? n : $"#{id}";

        public async Task ResolveAsync(ContractNameResolver names, IDbContextFactory<AppDbContext> dbFactory)
        {
            if (_entities.Count > 0) _entityNames = await names.ResolveAsync(_entities);
            if (_moons.Count > 0)    _moonNames   = await names.ResolveMoonsAsync(_moons);
            if (_entities.Count + _types.Count + _systems.Count + _stations.Count + _structures.Count == 0) return;

            await using var db = await dbFactory.CreateDbContextAsync();

            if (_entities.Count > 0)
            {
                // What each entity is, where anything knows: the name cache keeps ESI's own
                // category, the resolver remembers what ESI just said, and our own characters
                // and corporations are certain.
                var ids = _entities.ToList();
                foreach (var u in await db.UniverseNames.AsNoTracking().Where(u => ids.Contains(u.EntityId))
                             .Select(u => new { u.EntityId, u.Category }).ToListAsync())
                    _categories[u.EntityId] = u.Category;
                foreach (var id in ids)
                    if (names.CategoryOf(id) is { } said) _categories[id] = said;
                foreach (var c in await db.Characters.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync())
                    _categories[c] = "character";
                var intIds = ids.Where(i => i <= int.MaxValue).Select(i => (int)i).ToList();
                foreach (var c in await db.Corporations.AsNoTracking().Where(c => intIds.Contains(c.Id)).Select(c => c.Id).ToListAsync())
                    _categories[c] = "corporation";
            }

            if (_types.Count > 0)
            {
                var ids = _types.ToList();
                _typeNames = await db.SdeTypes.AsNoTracking().Where(t => ids.Contains(t.TypeId))
                    .ToDictionaryAsync(t => t.TypeId, t => t.Name);
            }

            if (_systems.Count > 0)
            {
                var ids = _systems.ToList();
                _systemInfo = (await db.SdeSolarSystems.AsNoTracking().Where(s => ids.Contains(s.SolarSystemId))
                        .Select(s => new { s.SolarSystemId, s.Name, s.Security }).ToListAsync())
                    .ToDictionary(s => s.SolarSystemId, s => (s.Name, s.Security));
            }

            if (_stations.Count > 0)
            {
                var ids = _stations.Select(i => (int)i).ToList();
                _stationInfo = (await db.SdeStations.AsNoTracking().Where(s => ids.Contains(s.StationId))
                        .Select(s => new { s.StationId, s.Name, s.StationTypeId }).ToListAsync())
                    .ToDictionary(s => (long)s.StationId, s => (s.Name, s.StationTypeId ?? 0));
            }

            if (_structures.Count > 0)
            {
                var ids = _structures.ToList();
                foreach (var s in await db.Structures.AsNoTracking().Where(s => ids.Contains(s.StructureId))
                             .Select(s => new { s.StructureId, s.Name, s.TypeId }).ToListAsync())
                    _structureInfo[s.StructureId] = (s.Name, s.TypeId);
                foreach (var s in await db.EsiStructureNames.AsNoTracking().Where(s => ids.Contains(s.StructureId))
                             .Select(s => new { s.StructureId, s.Name, s.TypeId }).ToListAsync())
                    if (!_structureInfo.TryGetValue(s.StructureId, out var have) || have.Name.Length == 0)
                        _structureInfo[s.StructureId] = (s.Name, have.TypeId > 0 ? have.TypeId : s.TypeId);
            }
        }

        public NotifValueVm Build(Ref x)
        {
            switch (x.Kind)
            {
                case K.Type:
                {
                    var id = (int)x.Id;
                    return new NotifValueVm
                    {
                        Text    = _typeNames.TryGetValue(id, out var n) ? n : $"Type {id}",
                        IconUrl = $"types/{id}/icon?size=32",
                        Tip     = "Open in the Item Browser",
                        Open    = () => EntityNavigator.Instance.Item(id),
                    };
                }
                case K.System:
                {
                    var id = (int)x.Id;
                    var known = _systemInfo.TryGetValue(id, out var s);
                    return new NotifValueVm
                    {
                        Text    = known ? $"{s.Name} ({SecurityColors.Text(s.Sec)})" : $"System {id}",
                        IconUrl = "",
                        Tip     = known ? $"{SecurityColors.Tip(s.Sec)}\nOpen on the map" : "Open on the map",
                        Open    = () => EntityNavigator.Instance.System(id),
                    };
                }
                case K.Moon:
                    return new NotifValueVm
                    {
                        Text    = _moonNames.TryGetValue((int)x.Id, out var mn) && mn.Length > 0 ? mn : $"Moon {x.Id}",
                        IconUrl = "",
                    };
                case K.Location:   // an NPC station
                {
                    var id = x.Id;
                    var known = _stationInfo.TryGetValue(id, out var st);
                    return new NotifValueVm
                    {
                        Text    = known ? st.Name : $"Station {id}",
                        IconUrl = known && st.TypeId > 0 ? $"types/{st.TypeId}/icon?size=32" : null,
                        Tip     = "Open in NPC Entities",
                        Open    = () => EntityNavigator.Instance.Entity(EntityKind.Station, id),
                    };
                }
                case K.Structure:
                {
                    var id = x.Id;
                    _structureInfo.TryGetValue(id, out var st);
                    _structureHints.TryGetValue(id, out var hint);
                    var typeId = x.IconTypeId > 0 ? x.IconTypeId : st.TypeId > 0 ? st.TypeId : hint.TypeId;
                    var name   = st.Name is { Length: > 0 } ? st.Name : hint.Name;
                    // A structure nobody has named to us: its type says more than its id would.
                    var unknown = typeId > 0 && _typeNames.TryGetValue(typeId, out var tn) ? $"{tn} (name unknown)"
                                                                                           : "Structure (name unknown)";
                    return new NotifValueVm
                    {
                        Text    = name ?? unknown,
                        IconUrl = typeId > 0 ? $"types/{typeId}/icon?size=32" : null,
                        Tip     = name is null ? $"Structure {id}\nOpen in the Structure Browser" : "Open in the Structure Browser",
                        Open    = () => EntityNavigator.Instance.Structure(id),
                    };
                }
                default:           // an entity
                {
                    var id   = x.Id;
                    var kind = EntityKindOf(id, x.Kind);
                    return new NotifValueVm
                    {
                        Text    = EntityName(id),
                        IconUrl = kind switch
                        {
                            EntityKind.Pilot or EntityKind.Agent => $"characters/{id}/portrait?size=32",
                            EntityKind.Alliance                  => $"alliances/{id}/logo?size=32",
                            _                                    => $"corporations/{id}/logo?size=32",   // corporations and factions
                        },
                        Tip     = EntityTip,
                        Open    = () => EntityNavigator.Instance.Entity(kind, id),
                    };
                }
            }
        }

        /// <summary>
        /// What an entity is. The fixed NPC ranges first, then the field's own meaning — a
        /// corpID is a corporation — then what the name cache recorded, and the player id ranges
        /// only as a last resort.
        ///
        /// <para>⚠️ Player ranges are unreliable for older entities: early alliances and
        /// corporations were numbered from the same range as characters, and both turn up in real
        /// notifications — so guessing from the range turns their logos into portraits.</para>
        /// </summary>
        private EntityKind EntityKindOf(long id, K declared)
        {
            var npcCorp = id is >= 1_000_000 and < 2_000_000;
            if (id is >= 500_000 and < 1_000_000)   return EntityKind.Faction;
            if (id is >= 3_000_000 and < 4_000_000) return EntityKind.Agent;

            switch (declared)
            {
                case K.Character:   return EntityKind.Pilot;
                case K.Corporation: return npcCorp ? EntityKind.NpcCorp : EntityKind.PlayerCorp;
                case K.Alliance:    return EntityKind.Alliance;
                case K.Faction:     return EntityKind.Faction;
            }

            return _categories.GetValueOrDefault(id) switch
            {
                "character"   => EntityKind.Pilot,
                "corporation" => npcCorp ? EntityKind.NpcCorp : EntityKind.PlayerCorp,
                "alliance"    => EntityKind.Alliance,
                "faction"     => EntityKind.Faction,
                _             => npcCorp ? EntityKind.NpcCorp : EntityLinks.KindOf(id),
            };
        }
    }

    // ── Small readers ─────────────────────────────────────────────────────────────

    private static object? Get(IDictionary<object, object> map, string key) =>
        map.FirstOrDefault(kv => string.Equals(kv.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)).Value;

    private static double Dbl(object? v) =>
        double.TryParse(v?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static long Long(object? v) =>
        long.TryParse(v?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (long)Dbl(v);

    private static int Int(object? v) => (int)Math.Clamp(Long(v), int.MinValue, int.MaxValue);

    private static string Isk(double amount) =>
        (amount == Math.Floor(amount) ? amount.ToString("N0", CultureInfo.CurrentCulture)
                                      : amount.ToString("N2", CultureInfo.CurrentCulture)) + " ISK";

    private static string M3(double volume) => volume.ToString("N0", CultureInfo.CurrentCulture) + " m³";

    /// <summary>A Windows file time as local date and time, or null for something that is not one.</summary>
    private static string? Date(long fileTime)
    {
        if (fileTime < 10_000_000_000_000_000L) return null;
        try { return DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture); }
        catch { return null; }
    }

    /// <summary>"1d 4h 12m", leaving out the units that are zero.</summary>
    private static string Span(TimeSpan t)
    {
        var parts = new List<string>(3);
        if (t.TotalDays >= 1) parts.Add($"{(int)t.TotalDays}d");
        if (t.Hours > 0)      parts.Add($"{t.Hours}h");
        if (t.Minutes > 0)    parts.Add($"{t.Minutes}m");
        return parts.Count > 0 ? string.Join(" ", parts) : $"{Math.Max(0, (int)t.TotalSeconds)}s";
    }

    private static string Strip(string s) =>
        Regex.Replace(Regex.Replace(s, @"<a[^>]*>(.*?)</a>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline),
                      @"</?[a-z][^>]*>", "", RegexOptions.IgnoreCase);

    private static string Flatten(object? v) => v switch
    {
        IDictionary<object, object> m => string.Join(", ", m.Select(kv => $"{kv.Key}: {Flatten(kv.Value)}")),
        IList<object> l               => string.Join(", ", l.Select(Flatten)),
        _                             => v?.ToString() ?? "",
    };
}
