using System.Text.Json;

namespace EveConsole.Services.Fitting;

/// <summary>
/// What EFT text leaves out, kept beside a fit saved in EVE Console: each module's state (EFT has
/// only "/OFFLINE"), how many of each drone stack are launched, and which squadrons sit in a tube
/// with which abilities on. Listed in the order <see cref="EftFormat.Write"/> lists the items, which
/// is the order reading the text back gives, each entry with its type so a mismatch is passed over.
/// </summary>
public static class FitState
{
    private sealed record SavedModule(int Type, ModuleState State);
    private sealed record SavedDrone(int Type, int Active, List<int>? Abilities);
    private sealed record Saved(List<SavedModule> Modules, List<SavedDrone> Drones);

    /// <summary>The state of <paramref name="fit"/> to keep with its EFT text.</summary>
    public static string Write(FitDefinition fit, DogmaData data) =>
        JsonSerializer.Serialize(new Saved(
            EftFormat.Racks(fit, data).SelectMany(r => r).Select(m => new SavedModule(m.TypeId, m.State)).ToList(),
            EftFormat.Bays(fit, data).SelectMany(b => b).Select(d => new SavedDrone(d.TypeId, d.Active, d.Abilities?.ToList())).ToList()));

    /// <summary>Puts back on <paramref name="fit"/>, read from EFT text, the state kept with it. A
    /// fit saved before states were kept, or text edited since, keeps what reading it gave. False when
    /// there was no state to put back: the drones are then in the bay, for the caller to launch.</summary>
    public static bool Apply(FitDefinition fit, string? state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        Saved? saved;
        try { saved = JsonSerializer.Deserialize<Saved>(state); }
        catch (JsonException) { return false; }
        if (saved is null) return false;

        for (var i = 0; i < Math.Min(fit.Modules.Count, saved.Modules?.Count ?? 0); i++)
            if (saved.Modules![i].Type == fit.Modules[i].TypeId)
                fit.Modules[i] = fit.Modules[i] with { State = saved.Modules[i].State };
        for (var i = 0; i < Math.Min(fit.Drones.Count, saved.Drones?.Count ?? 0); i++)
            if (saved.Drones![i] is var d && d.Type == fit.Drones[i].TypeId)
                fit.Drones[i] = fit.Drones[i] with { Active = Math.Clamp(d.Active, 0, fit.Drones[i].Count), Abilities = d.Abilities };
        return true;
    }
}
