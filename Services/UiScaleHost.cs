using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveConsole.Services;

/// <summary>
/// A window's content at the UI scale: laid out in the window's room divided by the scale, and
/// drawn scaled to fill the window again.
///
/// <para>⚠️ Not Avalonia's LayoutTransformControl, which this replaced. Built for any transform,
/// it guesses how much room its content will take; when an arrange finds the content bigger than
/// the guess, it keeps that bigger size and centres the content on it, measure after measure.
/// Changing the scale on Settings' Other tab lays the content out once at the OLD frame — at 75%,
/// a third wider than the window is about to be — and on Windows the frame's resize can reach the
/// content as an arrange before any new measure. The oversized layout then stuck: Settings drawn
/// cut off on both sides, with an empty band above. A scale needs no guess: the room is the
/// window's divided by the scale, on every pass.</para>
/// </summary>
public sealed class UiScaleHost : Decorator
{
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<UiScaleHost, double>(nameof(Scale), 1.0);

    static UiScaleHost() => AffectsMeasure<UiScaleHost>(ScaleProperty);

    /// <summary>Carries the drawing transform, so the content's own RenderTransform stays its own.</summary>
    private readonly Decorator _root = new() { RenderTransformOrigin = RelativePoint.TopLeft };

    public UiScaleHost(Control content)
    {
        _root.Child = content;
        Child = _root;
    }

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>The window's content, as it was before it was wrapped.</summary>
    public Control? Content => _root.Child;

    /// <summary>The room the content was last measured in, in its own units.</summary>
    private Size _measuredIn;

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = Scale;
        _measuredIn = new Size(availableSize.Width / s, availableSize.Height / s);
        _root.Measure(_measuredIn);
        return new Size(_root.DesiredSize.Width * s, _root.DesiredSize.Height * s);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = Scale;
        var room = new Size(finalSize.Width / s, finalSize.Height / s);

        // ⚠️ Arranged in room it was not measured in — the frame's resize reaching the content
        // ahead of its measure: measured again in the room it has. Otherwise what was laid out
        // for the old frame is arranged into the new one, and a scroll area inside keeps the old
        // width for its viewport until something else changes.
        if (Differs(room.Width, _measuredIn.Width) || Differs(room.Height, _measuredIn.Height))
            _root.Measure(room);

        _root.RenderTransform = new ScaleTransform(s, s);
        _root.Arrange(new Rect(room));
        return finalSize;
    }

    /// <summary>A bounded room that changed. A window sized to its content measures it unbounded,
    /// and is arranged in what it asked for: left as measured.</summary>
    private static bool Differs(double arranged, double measured) =>
        !double.IsInfinity(measured) && Math.Abs(arranged - measured) > 0.5;
}
