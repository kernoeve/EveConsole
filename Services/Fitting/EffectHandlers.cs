namespace EveConsole.Services.Fitting;

/// <summary>
/// The place for effects the engine computes in code instead of from their modifierInfo — those
/// the SDE lists with no modifiers although they change attributes in game (propulsion modules,
/// subsystem slots, a Reactive Armor Hardener's adaptation, some skill bonuses), and any whose
/// modifiers do not produce the in-game result.
/// </summary>
/// <remarks>
/// Deliberately empty for now: which of these the tool handles, and how, is still to be decided.
/// Until then those effects contribute nothing, and fits that depend on them are incomplete in
/// the ways that implies — no speed from an afterburner or MWD, no T3 subsystem slots, no RAH
/// adaptation, no missile or drone damage skill bonus.
/// </remarks>
internal static class EffectHandlers
{
    /// <summary>Registers <paramref name="effect"/> on <paramref name="item"/> in code and returns
    /// true, or returns false to let the engine apply its modifierInfo.</summary>
    public static bool TryRegister(DogmaEngine engine, DogmaItem item, DogmaEffectInfo effect) => false;
}
