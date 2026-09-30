using EveConsole.Localization;

namespace EveConsole.Services.Fitting;

/// <summary>What a fighter ability does, as far as the fitting numbers are concerned.</summary>
public enum FighterAbilityKind { Attack, Missiles, Bomb, Kamikaze, Utility, Propulsion }

/// <summary>A squadron's size class, which decides the launch slot it needs.</summary>
public enum FighterClass { Light, Support, Heavy, StandupLight, StandupSupport, StandupHeavy }

/// <summary>One ability a fighter type has, by the dogma effect behind it. <paramref name="Label"/>
/// is its name in the interface language, for showing only.</summary>
public sealed record FighterAbility(int EffectId, string Label, FighterAbilityKind Kind)
{
    public bool DealsDamage => Kind is FighterAbilityKind.Attack or FighterAbilityKind.Missiles
                                    or FighterAbilityKind.Bomb or FighterAbilityKind.Kamikaze;

    /// <summary>
    /// Switched on when a squadron is added: its standing attack and its secondary missiles, the
    /// damage a squadron is flown for. Bombs and kamikaze are occasional strikes — a heavy
    /// bomber's bomb recharges for a minute and a kamikaze strike spends the squadron — so they
    /// count only when switched on by hand.
    /// </summary>
    public bool OnByDefault => Kind is FighterAbilityKind.Attack or FighterAbilityKind.Missiles;
}

/// <summary>
/// Fighter abilities, read from the dogma effects a fighter type carries. The effects' own names
/// are internal ("fighterAbilityAttackM"), so the labels here are ours, in FittingText.
/// </summary>
/// <remarks>
/// The abilities' damage comes from attributes on the fighter named after the ability —
/// <c>fighterAbilityAttackMissileDamageEM</c> and so on — and the effect names the one holding
/// its duration. Charges and cooldowns live outside the dogma data and are not modelled: a
/// squadron's secondary missiles count as though they keep firing, which is how long it takes to
/// spend them in a fight anyway.
/// </remarks>
public static class FighterAbilities
{
    // Keyed on the effect's name; the label is only shown. The interface language is settled for
    // a run, so reading the labels once is enough.
    private static readonly Dictionary<string, (string Label, FighterAbilityKind Kind)> ByEffect = new()
    {
        ["fighterAbilityAttackM"]          = (FittingText.AbilityAttack,            FighterAbilityKind.Attack),
        ["fighterAbilityMissiles"]         = (FittingText.AbilityMissiles,          FighterAbilityKind.Missiles),
        ["fighterAbilityLaunchBomb"]       = (FittingText.AbilityBomb,              FighterAbilityKind.Bomb),
        ["fighterAbilityKamikaze"]         = (FittingText.AbilityKamikaze,          FighterAbilityKind.Kamikaze),
        ["fighterAbilityEnergyNeutralizer"] = (FittingText.AbilityEnergyNeutralizer, FighterAbilityKind.Utility),
        ["fighterAbilityStasisWebifier"]   = (FittingText.AbilityStasisWebifier,    FighterAbilityKind.Utility),
        ["fighterAbilityWarpDisruption"]   = (FittingText.AbilityWarpDisruptor,     FighterAbilityKind.Utility),
        ["fighterAbilityECM"]              = (FittingText.AbilityEcm,               FighterAbilityKind.Utility),
        ["fighterAbilityTackle"]           = (FittingText.AbilityTackle,            FighterAbilityKind.Utility),
        ["fighterAbilityEvasiveManeuvers"] = (FittingText.AbilityEvasiveManeuvers,  FighterAbilityKind.Propulsion),
        ["fighterAbilityAfterburner"]      = (FittingText.AbilityAfterburner,       FighterAbilityKind.Propulsion),
        ["fighterAbilityMicroWarpDrive"]   = (FittingText.AbilityMicrowarpdrive,    FighterAbilityKind.Propulsion),
        ["fighterAbilityMicroJumpDrive"]   = (FittingText.AbilityMicroJumpDrive,    FighterAbilityKind.Propulsion),
    };

    /// <summary>The abilities of <paramref name="type"/>: damage first, then the rest.</summary>
    public static IReadOnlyList<FighterAbility> Of(DogmaData data, DogmaTypeInfo type) =>
        type.EffectIds
            .Select(id => data.Effects.TryGetValue(id, out var fx) && ByEffect.TryGetValue(fx.Name, out var a)
                ? new FighterAbility(id, a.Label, a.Kind) : null)
            .OfType<FighterAbility>()
            .OrderBy(a => a.Kind)
            .ToList();

    /// <summary>The effect ids switched on by default for <paramref name="type"/>.</summary>
    public static IReadOnlyList<int> Defaults(DogmaData data, DogmaTypeInfo type) =>
        Of(data, type).Where(a => a.OnByDefault).Select(a => a.EffectId).ToList();

    /// <summary>The launch slot a squadron of <paramref name="type"/> needs, or null if it names none.</summary>
    public static FighterClass? ClassOf(DogmaData data, DogmaTypeInfo type)
    {
        bool Is(string attr) => data.Attribute(attr)?.Id is { } id && type.Attr(id) is > 0;
        if (Is("fighterSquadronIsLight"))          return FighterClass.Light;
        if (Is("fighterSquadronIsSupport"))        return FighterClass.Support;
        if (Is("fighterSquadronIsHeavy"))          return FighterClass.Heavy;
        if (Is("fighterSquadronIsStandupLight"))   return FighterClass.StandupLight;
        if (Is("fighterSquadronIsStandupSupport")) return FighterClass.StandupSupport;
        if (Is("fighterSquadronIsStandupHeavy"))   return FighterClass.StandupHeavy;
        return null;
    }

    /// <summary>The hull attribute counting the launch slots for <paramref name="c"/>.</summary>
    public static string SlotAttribute(FighterClass c) => c switch
    {
        FighterClass.Light          => "fighterLightSlots",
        FighterClass.Support        => "fighterSupportSlots",
        FighterClass.Heavy          => "fighterHeavySlots",
        FighterClass.StandupLight   => "fighterStandupLightSlots",
        FighterClass.StandupSupport => "fighterStandupSupportSlots",
        _                           => "fighterStandupHeavySlots",
    };

    /// <summary>The class as shown beside a squadron: light, support or heavy.</summary>
    public static string ClassName(FighterClass c) => c switch
    {
        FighterClass.Light or FighterClass.StandupLight     => FittingText.ClassLight,
        FighterClass.Support or FighterClass.StandupSupport => FittingText.ClassSupport,
        _                                                    => FittingText.ClassHeavy,
    };

    /// <summary>Most fighters a squadron of <paramref name="type"/> can hold.</summary>
    public static int MaxSquadron(DogmaData data, DogmaTypeInfo type) =>
        data.Attribute("fighterSquadronMaxSize")?.Id is { } id && type.Attr(id) is { } n and > 0 ? (int)n : 1;

    /// <summary>
    /// <paramref name="total"/> fighters of <paramref name="typeId"/> as full squadrons and one
    /// part squadron — how a fighter bay listed as a count (an in-game fitting, an EFT line) is
    /// put into tubes.
    /// </summary>
    public static IEnumerable<int> Squadrons(DogmaData data, int typeId, int total)
    {
        var max = data.TryType(typeId, out var t) ? MaxSquadron(data, t) : total;
        for (var left = total; left > 0; left -= max) yield return Math.Min(max, left);
    }
}
