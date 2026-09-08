using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EveConsole.Agent;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public class SkillDotBrushConverter : IValueConverter
{
    public static readonly SkillDotBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Palette.Accent : Palette.SurfaceRaised;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class IsSummaryBorderConverter : IValueConverter
{
    public static readonly IsSummaryBorderConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? Palette.GoodSurface : Brushes.Transparent;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class IsSummaryForegroundConverter : IValueConverter
{
    public static readonly IsSummaryForegroundConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? Palette.Good : Palette.TextPrimary;
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

    // The panel itself sits on SurfaceBase, so both bubbles read as raised against it and the
    // one the reader wrote reads higher still. The ramp holds either way up: base is the darkest
    // of the three on a dark theme and the darkest of the three on a light one.
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is MessageRole r && r == MessageRole.User ? Palette.SurfaceRaised : Palette.SurfacePanel;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class SecurityStatusBrushConverter : IValueConverter
{
    public static readonly SecurityStatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is float f) return f > 0 ? Palette.Good : f < 0 ? Palette.Bad : Palette.TextMuted;
        return Palette.TextMuted;
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

// Converts decimal? (NumericUpDown.Value) ↔ double for the percentage fields on the worklist's
// inventory rules. Same contract as the int converter above: null while a cell is being cleared
// returns UnsetValue, so the rule keeps its last valid percentage instead of taking a zero and
// saving it — these fields write straight to the database on change.
public class NullableDecimalToPositiveDoubleConverter : IValueConverter
{
    public static readonly NullableDecimalToPositiveDoubleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? (decimal)d : (object?)null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not decimal v || v <= 0) return AvaloniaProperty.UnsetValue;
        return (double)v;
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

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal d) return d > 0 ? Palette.Good : d < 0 ? Palette.Bad : Palette.TextMuted;
        return Palette.TextMuted;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Path data to a <see cref="Geometry"/>, so a view model can name a shape without referencing
/// drawing types.
///
/// <para>Used by the worklist's kind glyphs. Parsing is cached: the same handful of strings come
/// back on every row of every refresh, and re-parsing each one per row is work with a known
/// answer.</para>
/// </summary>
public class PathGeometryConverter : IValueConverter
{
    public static readonly PathGeometryConverter Instance = new();

    private static readonly Dictionary<string, Geometry> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string data || data.Length == 0) return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(data, out var cached)) return cached;

            // A malformed path must not take the grid down with it — the row is still readable
            // without its glyph.
            try
            {
                var geometry = Geometry.Parse(data);
                Cache[data] = geometry;
                return geometry;
            }
            catch { return null; }
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The engine name to its logo, so the Database Type dropdown shows the same marks the title bar
/// does — and so both can be seen without switching engines to find out what the other looks like.
/// </summary>
public class DbEngineLogoConverter : IValueConverter
{
    public static readonly DbEngineLogoConverter Instance = new();

    // ⚠️ Loaded once each. A converter runs on every item render, and decoding a PNG per pass
    // for a two-item list would be silly.
    private static readonly Lazy<Bitmap> Postgres = new(() => Load("postgresql.png"));
    private static readonly Lazy<Bitmap> Sqlite   = new(() => Load("sqlite.png"));

    private static Bitmap Load(string file) =>
        new(AssetLoader.Open(new Uri($"avares://EveConsole/Assets/{file}")));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value as string == DatabaseSettingsViewModel.PostgresName
            ? Postgres.Value
            : Sqlite.Value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Which client is doing the background work, as a colour.
///
/// <para>⚠️ Only one of the four states is a problem, and it is the one that otherwise reads like
/// the others: "none" means nothing is polling ESI, recalculating build costs or taking backups,
/// and it looks exactly as calm as a host name until it is coloured differently. Unknown stays
/// grey rather than amber — the first read has not come back yet, and alarming about a worker
/// that is very probably fine is how an indicator teaches people to ignore it.</para>
/// </summary>
public class WorkerStateBrushConverter : IValueConverter
{
    public static readonly WorkerStateBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            WorkerOwnership.Mine  => Palette.Good,
            WorkerOwnership.Other => Palette.TextMuted,
            WorkerOwnership.None  => Palette.Warn,
            _                     => Palette.TextFaint,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Whether this client will make a noise about alarms, as a colour.
///
/// <para>Muted takes the same amber as a missing worker, because it is the same kind of fact: a
/// thing that is supposed to happen is not going to. Green for active rather than the bar's
/// ordinary grey — this one is worth being able to confirm at a glance mid-fleet, and grey would
/// read as "off" to anyone scanning quickly.</para>
/// </summary>
public class AlarmMuteBrushConverter : IValueConverter
{
    public static readonly AlarmMuteBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Palette.Warn : Palette.Good;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
