using System.Globalization;
using Avalonia.Data.Converters;

namespace EveConsole.ViewModels;

public static class EntityConverters
{
    /// <summary>
    /// Renders a count-as-flag column: any non-zero value becomes a tick, zero becomes
    /// blank. The underlying queries return counts rather than booleans because
    /// EXISTS-style subqueries are cheaper to express as COUNT(*), and a column of "0"s
    /// would read as data rather than as absence.
    /// </summary>
    public static readonly IValueConverter YesBlank =
        new FuncValueConverter<int, string>(v => v > 0 ? "✓" : "");

    /// <summary>A formatted count, or blank at zero, for columns where zero is the norm.</summary>
    public static readonly IValueConverter CountOrBlank =
        new FuncValueConverter<int, string>(v => v > 0 ? v.ToString("N0", CultureInfo.CurrentCulture) : "");
}
