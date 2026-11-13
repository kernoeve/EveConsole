using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace EveConsole.Views;

/// <summary>
/// One line of intel tokens that fits its width: laid out left to right, and as the space narrows, the pieces marked
/// to go (a name beside its icon, a second group, the further hulls) are left out in the order marked, so the icons
/// stay and the line stays one line. <see cref="DropProperty"/> 0 (the default) is never left out; 1 goes first,
/// then 2, and so on. What still does not fit is clipped at the end.
///
/// <para>A piece left out is not hidden by the panel (its visibility may be bound): it is laid out at no size, not
/// drawn and not clickable, until there is room again.</para>
/// </summary>
public sealed class IntelFitPanel : Panel
{
    public static readonly AttachedProperty<int> DropProperty =
        AvaloniaProperty.RegisterAttached<IntelFitPanel, Control, int>("Drop");

    public static int GetDrop(Control c) => c.GetValue(DropProperty);
    public static void SetDrop(Control c, int value) => c.SetValue(DropProperty, value);

    private readonly HashSet<Control> _dropped = [];

    static IntelFitPanel()
    {
        ClipToBoundsProperty.OverrideDefaultValue<IntelFitPanel>(true);
        AffectsParentMeasure<IntelFitPanel>(DropProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : availableSize.Height;
        double total = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, height));
            total += child.DesiredSize.Width;
        }

        _dropped.Clear();
        if (total > availableSize.Width)
            foreach (var level in Children.Select(GetDrop).Where(d => d > 0).Distinct().Order())
            {
                foreach (var child in Children.Where(c => GetDrop(c) == level))
                {
                    _dropped.Add(child);
                    total -= child.DesiredSize.Width;
                }
                if (total <= availableSize.Width) break;
            }

        var tallest = Children.Where(c => !_dropped.Contains(c)).Select(c => c.DesiredSize.Height).DefaultIfEmpty(0).Max();
        return new Size(Math.Min(total, availableSize.Width), tallest);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children)
        {
            var dropped = _dropped.Contains(child);
            child.Opacity = dropped ? 0 : 1;
            child.IsHitTestVisible = !dropped;
            if (dropped || !child.IsVisible)
            {
                child.Arrange(new Rect(x, 0, 0, 0));
                continue;
            }
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;
            var y = child.VerticalAlignment switch
            {
                VerticalAlignment.Top => 0,
                VerticalAlignment.Bottom => finalSize.Height - h,
                VerticalAlignment.Stretch => 0,
                _ => (finalSize.Height - h) / 2,
            };
            if (child.VerticalAlignment == VerticalAlignment.Stretch) h = finalSize.Height;
            child.Arrange(new Rect(x, Math.Max(0, y), w, h));
            x += w;
        }
        return finalSize;
    }
}
