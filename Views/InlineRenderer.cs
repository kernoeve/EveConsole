using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using EveConsole.ViewModels;

namespace EveConsole.Views;

// Attached property that renders a list of DisplaySeg (text + bold flag) into a
// SelectableTextBlock's Inlines — so the posting preview shows real bold instead of markup.
public sealed class InlineRenderer
{
    private InlineRenderer() { }

    public static readonly AttachedProperty<IReadOnlyList<DisplaySeg>?> SegmentsProperty =
        AvaloniaProperty.RegisterAttached<InlineRenderer, SelectableTextBlock, IReadOnlyList<DisplaySeg>?>("Segments");

    public static void SetSegments(SelectableTextBlock e, IReadOnlyList<DisplaySeg>? v) => e.SetValue(SegmentsProperty, v);
    public static IReadOnlyList<DisplaySeg>? GetSegments(SelectableTextBlock e) => e.GetValue(SegmentsProperty);

    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#5a9be0"));

    private static Run StyledRun(string text, SegStyle style)
    {
        var run = new Run(text);
        if (style.HasFlag(SegStyle.Bold))   run.FontWeight = FontWeight.Bold;
        if (style.HasFlag(SegStyle.Italic)) run.FontStyle  = FontStyle.Italic;
        if (style.HasFlag(SegStyle.Link))   run.Foreground = LinkBrush;

        TextDecorationCollection? deco = null;
        if (style.HasFlag(SegStyle.Underline) || style.HasFlag(SegStyle.Link))
        { deco ??= []; deco.AddRange(TextDecorations.Underline); }
        if (style.HasFlag(SegStyle.Strike))
        { deco ??= []; deco.AddRange(TextDecorations.Strikethrough); }
        if (deco is not null) run.TextDecorations = deco;

        return run;
    }

    static InlineRenderer()
    {
        SegmentsProperty.Changed.AddClassHandler<SelectableTextBlock>((tb, e) =>
        {
            tb.Inlines ??= new InlineCollection();
            tb.Inlines.Clear();
            if (e.NewValue is not IReadOnlyList<DisplaySeg> segs) return;

            foreach (var seg in segs)
            {
                var lines = seg.Text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) tb.Inlines.Add(new LineBreak());
                    tb.Inlines.Add(StyledRun(lines[i], seg.Style));
                }
            }
        });
    }
}
