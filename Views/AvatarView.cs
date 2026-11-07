using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using EveConsole.Services;

namespace EveConsole.Views;

/// <summary>
/// Someone's picture in a circle, or, with none, their first initial on a colour that is always the same for them.
/// </summary>
public sealed class AvatarView : Control
{
    public static readonly StyledProperty<AvatarRef?> OfProperty = AvaloniaProperty.Register<AvatarView, AvatarRef?>(nameof(Of));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<AvatarView, double>(nameof(Size), 24);

    // Mid tones that white reads on, on a dark ground or a light one.
    private static readonly IBrush[] Colors =
    [
        new SolidColorBrush(Color.Parse("#5B7DB1")), new SolidColorBrush(Color.Parse("#4E9A8C")), new SolidColorBrush(Color.Parse("#8E6BB8")),
        new SolidColorBrush(Color.Parse("#B0705A")), new SolidColorBrush(Color.Parse("#6F8F4E")), new SolidColorBrush(Color.Parse("#B35C82")),
        new SolidColorBrush(Color.Parse("#A0863F")), new SolidColorBrush(Color.Parse("#5E8FA8")),
    ];

    private AvatarService? _listening;

    static AvatarView()
    {
        AffectsMeasure<AvatarView>(SizeProperty);
        AffectsRender<AvatarView>(OfProperty);
    }

    public AvatarRef? Of { get => GetValue(OfProperty); set => SetValue(OfProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _listening = AvatarService.Shared;
        if (_listening is not null) _listening.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_listening is not null) _listening.Changed -= InvalidateVisual;
        _listening = null;
    }

    protected override Size MeasureOverride(Size availableSize) => Of is null ? default : new Size(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Of is not { } who) return;

        var box = new Rect(0, 0, Size, Size);
        var circle = new EllipseGeometry(box);

        if (AvatarService.Shared?.ImageFor(who) is { } image)
        {
            // The middle of it, square, so a wide or tall picture is not squashed.
            var (w, h) = (image.Size.Width, image.Size.Height);
            var side = Math.Min(w, h);
            using (context.PushGeometryClip(circle))
                context.DrawImage(image, new Rect((w - side) / 2, (h - side) / 2, side, side), box);
            return;
        }

        context.DrawGeometry(Colors[AvatarService.ColorIndex(who.Name, Colors.Length)], null, circle);
        var text = new FormattedText(AvatarService.Initial(who.Name), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), Size * 0.48, Brushes.White);
        context.DrawText(text, new Point((Size - text.Width) / 2, (Size - text.Height) / 2));
    }
}
