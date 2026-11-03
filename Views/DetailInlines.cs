using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// Fills a TextBlock from <see cref="DetailRun"/>s: text as it is, each item as its picture and
/// then its name, so the line still wraps as one paragraph.
/// </summary>
public static class DetailInlines
{
    public static readonly AttachedProperty<IReadOnlyList<DetailRun>?> RunsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<DetailRun>?>("Runs", typeof(DetailInlines));

    public static IReadOnlyList<DetailRun>? GetRuns(TextBlock t) => t.GetValue(RunsProperty);
    public static void SetRuns(TextBlock t, IReadOnlyList<DetailRun>? value) => t.SetValue(RunsProperty, value);

    static DetailInlines() =>
        RunsProperty.Changed.AddClassHandler<TextBlock>((t, e) => Fill(t, e.NewValue as IReadOnlyList<DetailRun>));

    private static void Fill(TextBlock t, IReadOnlyList<DetailRun>? runs)
    {
        var inlines = t.Inlines ??= new InlineCollection();
        inlines.Clear();
        if (runs is null) return;
        foreach (var r in runs)
        {
            if (!r.IsItem) { inlines.Add(new Run(r.Text)); continue; }
            inlines.Add(new InlineUIContainer(new TypeIcon
            {
                TypeId = r.TypeId, Width = 16, Height = 16,
                Margin = new Thickness(0, 0, 3, -3), VerticalAlignment = VerticalAlignment.Center,
            }) { BaselineAlignment = BaselineAlignment.Center });
            inlines.Add(new Run(r.Text) { FontWeight = FontWeight.SemiBold });
        }
    }
}
