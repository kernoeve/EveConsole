using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// Totals a DataGrid group's rows for its header — "Haul · 54 · 46.0M m³".
///
/// <para>Bound to the group's <c>Items</c>, with the parameter naming what to add up. The header's
/// DataContext is the collection view group itself, so the rows are right there; the alternative
/// was a parallel per-group total in the view model, kept in step with the grouping by hand.</para>
///
/// <para>⚠️ Items can hold nested groups rather than rows when more than one grouping level is in
/// play. Only one level is used here, but the type check means a second would degrade to a blank
/// total rather than throwing.</para>
/// </summary>
public sealed class GroupTotalConverter : IValueConverter
{
    public static readonly GroupTotalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable items) return "";

        var rows = items.OfType<WorklistRowVm>().ToList();
        if (rows.Count == 0) return "";

        return (parameter as string) switch
        {
            // Counted here rather than bound to the group's own ItemCount so the label can be
            // written out, and so a single-item group reads "1 Task" rather than "1 Tasks".
            "Count"  => $"{rows.Count:N0} Task{(rows.Count == 1 ? "" : "s")}",
            "Volume" => Volume(rows.Sum(r => r.VolumeRaw)),
            "Value"  => Isk(rows.Sum(r => r.Value)),
            _        => "",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string Volume(double v) =>
        v <= 0            ? ""
        : v >= 1_000_000  ? $"{v / 1_000_000:N1}M m³"
        : v >= 1_000      ? $"{v / 1_000:N0}k m³"
                          : $"{v:N0} m³";

    private static string Isk(double v) =>
        v <= 0                 ? ""
        : v >= 1_000_000_000   ? $"{v / 1_000_000_000:N2}B"
        : v >= 1_000_000       ? $"{v / 1_000_000:N1}M"
                               : $"{v:N0}";
}
