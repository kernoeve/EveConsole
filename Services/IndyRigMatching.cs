namespace EveConsole.Services;

/// <summary>
/// Which structure rigs bonus which items.
///
/// ⚠ These rules also exist inside BuildCostService and ProductionCalculatorService as
/// local functions. Those two were deliberately not switched over in the same change that
/// introduced this file — they drive build costing, which cannot be functionally verified
/// without running the app, and a silent error there is worse than the duplication.
///
/// The three copies have already drifted once: the R.A.M. tools / Data Interfaces rule
/// existed in both originals but was missed here at extraction time, so the rig check
/// skipped those jobs until it was restored. Treat "they are identical" as something to
/// re-check, not to assume. If you change a rule here, change it in all three.
/// Consolidating them remains the real fix.
/// </summary>
public static class IndyRigMatching
{
    /// <summary>Group identity needed to classify an item. Mirrors what the SDE
    /// exposes via SdeTypes.GroupId → SdeGroups.</summary>
    public readonly record struct GroupInfo(int GroupId, int CategoryId, string Name);

    /// <summary>Rig type name → the item category key it bonuses. Empty when the rig
    /// isn't an industry rig at all.</summary>
    public static string RigCategoryFromName(string n)
    {
        if (n.Contains("Advanced Small Ship"))     return "adv_small_ships";
        if (n.Contains("Basic Small Ship"))        return "small_ships";
        if (n.Contains("Advanced Medium Ship"))    return "adv_medium_ships";
        if (n.Contains("Basic Medium Ship"))       return "medium_ships";
        if (n.Contains("Advanced Large Ship"))     return "adv_large_ships";
        if (n.Contains("Basic Large Ship"))        return "large_ships";
        if (n.Contains("Capital Ship"))            return "capital_ships";
        if (n.Contains("Drone and Fighter"))       return "drones_fighters";
        if (n.Contains("Equipment"))               return "modules_equipment";
        if (n.Contains("Ammunition"))              return "ammo_charges";
        if (n.Contains("Basic Capital Component")) return "capital_components";
        if (n.Contains("Advanced Component"))      return "adv_components";
        if (n.Contains("Structure"))               return "structure_ammo";
        // Tatara L-Set: one generic rig applies to ALL reaction types — wildcard key.
        // Athanor M-Set: separate rigs per reaction subcategory — specific keys.
        if (n.Contains("L-Set Reactor"))           return "biochemical_reactions";
        if (n.Contains("Biochemical Reactor"))     return "react_bio_gas";
        if (n.Contains("Composite Reactor"))       return "react_composite";
        if (n.Contains("Hybrid Reactor"))          return "react_composite";
        if (n.Contains("Reactor"))                 return "biochemical_reactions";
        return "";
    }

    /// <summary>Item type → the category key whose rig would bonus it. Empty when the
    /// item doesn't fall into any rig-bonusable category.</summary>
    public static string ItemCategoryKey(
        int typeId,
        bool isReaction,
        IReadOnlyDictionary<int, int> typeToGroup,
        IReadOnlyDictionary<int, GroupInfo> groupInfo)
    {
        if (!typeToGroup.TryGetValue(typeId, out var groupId)) return "";

        if (isReaction)
            return groupId switch
            {
                712                => "react_bio_gas",     // Biochemical Material (gas)
                428                => "react_biochemical", // Intermediate Materials (moon)
                429 or 974 or 4096 => "react_composite",   // Composite / Hybrid Polymers
                _                  => "",
            };

        if (!groupInfo.TryGetValue(groupId, out var gc)) return "";

        return (gc.CategoryId, gc.Name) switch
        {
            // ── Category 6: Ships ────────────────────────────────────────────
            (6, "Frigate" or "Destroyer" or "Shuttle" or "Corvette" or "Rookie Ship"
               or "Hauler" or "Mining Barge")                                        => "small_ships",
            (6, "Cruiser" or "Battlecruiser" or "Combat Battlecruiser"
               or "Attack Battlecruiser")                                            => "medium_ships",
            (6, "Battleship" or "Freighter")                                         => "large_ships",
            // SDE group is "Interdictor", not "Interdiction Destroyer"
            (6, "Interceptor" or "Assault Frigate" or "Covert Ops"
               or "Electronic Attack Ship" or "Interdictor" or "Tactical Destroyer"
               or "Logistics Frigate" or "Expedition Frigate"
               or "Stealth Bomber" or "Command Destroyer" or "Exhumer")              => "adv_small_ships",
            // SDE groups are "Force Recon Ship" / "Combat Recon Ship", not "Recon Ship"
            (6, "Heavy Assault Cruiser" or "Force Recon Ship" or "Combat Recon Ship"
               or "Heavy Interdiction Cruiser" or "Logistics" or "Command Ship"
               or "Strategic Cruiser" or "Blockade Runner" or "Deep Space Transport"
               or "Flag Cruiser" or "Expedition Command Ship")                       => "adv_medium_ships",
            (6, "Marauder" or "Black Ops")                                           => "adv_large_ships",
            (6, "Dreadnought" or "Carrier" or "Force Auxiliary" or "Capital Industrial Ship"
               or "Supercarrier" or "Titan" or "Command Carrier" or "Lancer Dreadnought"
               or "Jump Freighter" or "Industrial Command Ship")                     => "capital_ships",

            // ── Other categories ─────────────────────────────────────────────
            (7, _)          => "modules_equipment",
            // Structure Modules — service modules and structure rigs — are built at
            // engineering complexes like equipment.
            (66, _)         => "modules_equipment",
            // T3 subsystems — Loki/Tengu/Legion/Proteus. Previously unmapped, so every
            // subsystem threw "cannot be assigned to a structure" and lost its chain cost.
            (32, _)         => "modules_equipment",
            // Implants and boosters (20), starbase structures and POS modules (23), and
            // sovereignty / infrastructure hub upgrades (39). Each of these categories
            // holds nothing but its own kind, so matching the whole category is safe.
            (20, _)         => "modules_equipment",
            (23, _)         => "modules_equipment",
            (39, _)         => "modules_equipment",
            // Category 2 (Celestial) is a junk drawer — planets, suns, wrecks, wormholes,
            // 1,697 non-interactable objects. Only its container groups are manufacturable,
            // so match those by name rather than blanketing the category and quietly
            // classifying anything CCP makes buildable there in future.
            (2, var celestial) when celestial.Contains("Container") => "modules_equipment",
            // Mutaplasmids. Matched by group rather than category — category 17 also holds
            // fuel blocks and capital components, which have their own rigs below. Only
            // mutaplasmids with a blueprint ever reach here, so the "manufacturable ones"
            // qualifier takes care of itself.
            (17, "Mutaplasmids")                                    => "modules_equipment",
            // Abyssal, jump and warp matrix filaments. Four groups all named "… Filaments";
            // the other filament groups in category 17 have no manufacturable members, so
            // the name match cannot reach them.
            (17, var fil) when fil.Contains("Filament")             => "modules_equipment",
            // Individually classified. These sit in category 17's junk-drawer groups —
            // "Miscellaneous" runs to thousands of unrelated items and "Commodities" mixes
            // industry inputs with trinkets — so the type id is the only thing precise
            // enough to match on.
            _ when typeId is 76203 or 76204 or 29226                => "structure_ammo",
            //     76203 Stellar Transmuter Datacore, 76204 Transport Relay Datacore,
            //     29226 Basic Robotics
            _ when typeId == 3585                                   => "modules_equipment",
            //     3585 Mangled Sansha Data Analyzer
            _ when typeId == 29202                                  => "ammo_charges",
            //     29202 Modified Augumene Antidote
            _ when typeId is >= 88172 and <= 88177                  => "adv_components",
            //     88172-88177 Narrow/Mid/Wideband Emission Amplifiers and Limiters.
            //     A contiguous block holding exactly those six and nothing else.
            (8, _)          => "ammo_charges",
            (18, _) or (87, _)                                                       => "drones_fighters",
            _ when groupId == 1136                                                   => "structure_ammo", // Fuel Blocks
            // Group 873 "Capital Construction Components" — Capital Propulsion Engine,
            // Armor Plates and the rest of the T1 capital parts, covered by the
            // "Basic Capital Component" rig. They sit in category 17 (Commodity), so
            // the old `CategoryId == 4` guard matched no group at all and every one
            // fell through to the generic Component rule below.
            //
            // "Advanced" is excluded on purpose: group 913 "Advanced Capital
            // Construction Components" is bonused by the "Advanced Component" rig,
            // not the capital one, so it belongs in adv_components with group 334.
            _ when gc.Name.Contains("Capital") && gc.Name.Contains("Component")
                                               && !gc.Name.Contains("Advanced")       => "capital_components",
            _ when gc.Name.Contains("Component")                                     => "adv_components",
            _ when gc.CategoryId is 22 or 65                                         => "structure_ammo",
            // R.A.M. tools and Data Interfaces are built at standard facilities. This rule
            // was present in both other copies but missing here, so the rig check treated
            // those jobs as uncheckable rather than comparing them against the equipment
            // rig. See the header note on divergence.
            (17, "Tool" or "Data Interfaces")                                        => "modules_equipment",
            _                                                                        => "",
        };
    }

    /// <summary>Does a rig fitted for <paramref name="rigCategory"/> bonus an item in
    /// <paramref name="itemCategory"/>? Straight equality, except that the Tatara's
    /// generic reactor rig covers every reaction subcategory.</summary>
    public static bool RigApplies(string rigCategory, string itemCategory)
    {
        if (string.IsNullOrEmpty(rigCategory) || string.IsNullOrEmpty(itemCategory)) return false;
        if (rigCategory == itemCategory) return true;
        return itemCategory.StartsWith("react_") && rigCategory == "biochemical_reactions";
    }

    /// <summary>ESI industry activity IDs.</summary>
    public static class Activity
    {
        public const int Manufacturing = 1;
        public const int Researching   = 3;   // time efficiency
        public const int ResearchingMe = 4;   // material efficiency
        public const int Copying       = 5;
        public const int Invention     = 8;
        public const int Reactions     = 9;
    }

    /// <summary>
    /// Only manufacturing and reactions are bonused by the ME rigs this check knows
    /// about. Research, copying and invention use different rigs entirely, so a job of
    /// those kinds is not something to flag as "unrigged" here — that would be a false
    /// alarm rather than a finding.
    /// </summary>
    public static bool IsRigCheckable(int activityId) =>
        activityId is Activity.Manufacturing or Activity.Reactions;
}
