using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// What an Item Browser asset group adds up to, for its header: "3 stacks · 1,250 units ·
/// 41.2M". The header is a DataGridCollectionViewGroup and knows only its key and its items, so
/// the totals are worked out here from the items rather than carried by a row.
/// </summary>
public sealed class ItemAssetGroupConverter : IValueConverter
{
    public static readonly ItemAssetGroupConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable items) return "";

        var rows = items.OfType<ItemAssetRowVm>().ToList();
        if (rows.Count == 0) return "";

        var units = rows.Sum(r => r.Quantity);
        var worth = rows.Sum(r => r.Value);
        var text  = $"{rows.Count:N0} stack{(rows.Count == 1 ? "" : "s")} · {units:N0} unit{(units == 1 ? "" : "s")}";
        return worth > 0 ? $"{text} · {MarketFmt.Isk(worth)}" : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
