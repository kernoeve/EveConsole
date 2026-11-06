using System.Globalization;

namespace EveConsole.ViewModels;

/// <summary>
/// How the chat tools show a time: always EVE time, which is UTC, and on a message always with its date. A client left
/// running for days would otherwise show yesterday's messages as if they were today's, or need redrawing at midnight.
/// </summary>
public static class ChatTime
{
    public static string Of(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The same for a time as saved. Blank if it cannot be read.</summary>
    public static string Of(string saved) =>
        DateTimeOffset.TryParse(saved, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? Of(when) : "";

    /// <summary>A time a few moments away, where a date would say nothing.</summary>
    public static string Clock(DateTimeOffset when) => when.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
