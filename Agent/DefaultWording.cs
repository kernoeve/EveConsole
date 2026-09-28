namespace EveConsole.Agent;

/// <summary>
/// A text the capsuleer may reword — the router's rule, what is said when a model or a voice
/// changes — and the app's own words for it.
///
/// <para>⚠️ Stored EMPTY while it is the default. Saved as the default's words, a text kept them
/// for good: a better default — a clearer rule for the router, a better announcement — reached
/// nobody who had simply never changed it, and nothing on the tab told them theirs was an old
/// default rather than their own words. So only words the capsuleer wrote are stored, and a text
/// that matches a default, today's or an earlier one, counts as the default.</para>
///
/// <para>⚠️ When changing a default, keep the old words in <see cref="Earlier"/>. A settings file
/// saved before the change may still hold them, and without them there they would count as the
/// capsuleer's own and never change again.</para>
/// </summary>
public sealed class DefaultWording(string current, params string[] earlier)
{
    /// <summary>The default's words now.</summary>
    public string Current { get; } = current;

    /// <summary>Every default it has had before, word for word.</summary>
    public IReadOnlyList<string> Earlier { get; } = earlier;

    /// <summary>As stored: the capsuleer's own words, or empty for a default.</summary>
    public string Store(string? text) => IsDefault(text) ? "" : text!.Trim();

    /// <summary>As used and shown: the capsuleer's own words, or the default's current ones.</summary>
    public string Use(string? text) => IsDefault(text) ? Current : text!.Trim();

    private bool IsDefault(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Trim() == Current || Earlier.Contains(text.Trim());
}
