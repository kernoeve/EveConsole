using EveConsole.Models;

namespace EveConsole.ViewModels;

/// <summary>
/// One entry in a list the user picks from: the value the code keeps, saves and compares, and the
/// words shown for it. A ComboBox shows it by its label.
///
/// <para>⚠️ Pick lists whose items were plain strings compared the displayed English word —
/// "Market", "List", "All senders" — and some saved it; translated, they would have matched
/// nothing and saved the translation. The value is the same in every language; only the label is
/// looked up.</para>
/// </summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Pick lists used in more than one place.</summary>
public static class Choice
{
    /// <summary>The market price types: the key a posting saves, and the name the market tools
    /// show (Midpoint as Split).</summary>
    public static IReadOnlyList<Choice<string>> PriceTypes { get; } =
        MarketPriceType.Keys.Select(k => new Choice<string>(k, MarketPriceType.Label(k))).ToList();
}
