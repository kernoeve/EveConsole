using EveConsole.Localization;

namespace EveConsole.Agent;

/// <summary>
/// A model a service offers, as the service's own list names it. The settings tab offers these
/// to choose from.
///
/// <para>⚠️ Asked for, never written into the app. A list of model names in the source is out of
/// date by the time it ships: the model help named Claude models two generations old, and the
/// default for a new Claude model was one of them.</para>
/// </summary>
/// <param name="Id">What a request names it by.</param>
/// <param name="Name">What people call it — "Claude Opus 5". The id again where the list gives no other.</param>
/// <param name="Listed">False for a model chosen before that the service no longer lists.</param>
public sealed record ModelListing(string Id, string Name, bool Listed = true)
{
    /// <summary>As the list shows it.</summary>
    public string Label =>
        !Listed        ? string.Format(SettingsText.ModelNotInListNow, Id)
      : Name == Id     ? Id
      :                  $"{Name}  ({Id})";
}
