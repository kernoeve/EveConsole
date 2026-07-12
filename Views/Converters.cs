using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using EveCortex.Agent;

namespace EveCortex.Views;

public class SkillDotBrushConverter : IValueConverter
{
    public static readonly SkillDotBrushConverter Instance = new();
    private static readonly IBrush Filled = new SolidColorBrush(Color.Parse("#c8a84b"));
    private static readonly IBrush Empty  = new SolidColorBrush(Color.Parse("#2a2a38"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Filled : Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class IsSummaryBorderConverter : IValueConverter
{
    public static readonly IsSummaryBorderConverter Instance = new();
    private static readonly IBrush SummaryBrush = new SolidColorBrush(Color.Parse("#1a3a1a"));
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? SummaryBrush : Brushes.Transparent;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class IsSummaryForegroundConverter : IValueConverter
{
    public static readonly IsSummaryForegroundConverter Instance = new();
    private static readonly IBrush SummaryFg = new SolidColorBrush(Color.Parse("#7a9a7a"));
    private static readonly IBrush NormalFg  = new SolidColorBrush(Color.Parse("#c0c0cc"));
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? SummaryFg : NormalFg;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class MessageRoleAlignmentConverter : IValueConverter
{
    public static readonly MessageRoleAlignmentConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is MessageRole r && r == MessageRole.User
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class MessageRoleBackgroundConverter : IValueConverter
{
    public static readonly MessageRoleBackgroundConverter Instance = new();
    private static readonly IBrush UserBrush      = new SolidColorBrush(Color.Parse("#1a2535"));
    private static readonly IBrush AssistantBrush = new SolidColorBrush(Color.Parse("#111117"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is MessageRole r && r == MessageRole.User ? UserBrush : AssistantBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class SecurityStatusBrushConverter : IValueConverter
{
    public static readonly SecurityStatusBrushConverter Instance = new();
    private static readonly IBrush Positive = new SolidColorBrush(Color.Parse("#4caf6a"));
    private static readonly IBrush Negative = new SolidColorBrush(Color.Parse("#cc4444"));
    private static readonly IBrush Neutral  = new SolidColorBrush(Color.Parse("#888899"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is float f) return f > 0 ? Positive : f < 0 ? Negative : Neutral;
        return Neutral;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Converts decimal? (NumericUpDown.Value) ↔ int for target/multiplier fields.
// Returns UnsetValue when null so the binding skips the update and the source
// keeps its last valid value — no type-conversion error, no validation popup.
public class NullableDecimalToPositiveIntConverter : IValueConverter
{
    public static readonly NullableDecimalToPositiveIntConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? (decimal)i : (object?)null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || value is decimal d && d == 0)
            return AvaloniaProperty.UnsetValue;
        if (value is decimal v)
            return (int)Math.Max(1m, v);
        return AvaloniaProperty.UnsetValue;
    }
}

// Display name for the LLM provider dropdown — flags Local as untested.
public class AgentProviderDisplayConverter : IValueConverter
{
    public static readonly AgentProviderDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AgentProviderType.Local ? "Local (Untested)" : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class ProfitColorConverter : IValueConverter
{
    public static readonly ProfitColorConverter Instance = new();
    private static readonly IBrush Profit = new SolidColorBrush(Color.Parse("#4caf50"));
    private static readonly IBrush Loss   = new SolidColorBrush(Color.Parse("#cc4444"));
    private static readonly IBrush Zero   = new SolidColorBrush(Color.Parse("#888899"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal d) return d > 0 ? Profit : d < 0 ? Loss : Zero;
        return Zero;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
