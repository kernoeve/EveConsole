using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EveConsole.Services.ClientLayouts;

namespace EveConsole.Views;

/// <summary>One monitor as the map draws it. Result is null while it has no layout.</summary>
public sealed record MonitorMapItem(string Key, string Label, LayoutRect Bounds, ClientLayoutResult? Result, bool Selected);

/// <summary>All of a desktop's monitors drawn to scale, as Windows arranges them. Click one to pick it.</summary>
public sealed class ClientMonitorMap : Control
{
    public static readonly StyledProperty<IReadOnlyList<MonitorMapItem>?> ItemsProperty =
        AvaloniaProperty.Register<ClientMonitorMap, IReadOnlyList<MonitorMapItem>?>(nameof(Items));

    public static readonly StyledProperty<ICommandLike?> PickProperty =
        AvaloniaProperty.Register<ClientMonitorMap, ICommandLike?>(nameof(Pick));

    private readonly ClientLayoutPainter _painter;

    static ClientMonitorMap() => AffectsRender<ClientMonitorMap>(ItemsProperty);

    public ClientMonitorMap()
    {
        _painter = new ClientLayoutPainter(this);
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<MonitorMapItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>Told the key of a monitor when it's clicked.</summary>
    public ICommandLike? Pick
    {
        get => GetValue(PickProperty);
        set => SetValue(PickProperty, value);
    }

    private ClientLayoutPainter.View CurrentView()
    {
        var items = Items;
        if (items == null || items.Count == 0) return new ClientLayoutPainter.View(1, 0, 0);
        var left = items.Min(i => i.Bounds.X);
        var top = items.Min(i => i.Bounds.Y);
        var right = items.Max(i => i.Bounds.Right);
        var bottom = items.Max(i => i.Bounds.Bottom);
        return ClientLayoutPainter.View.Fit(new LayoutRect(left, top, right - left, bottom - top), Bounds.Size, 6);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var items = Items;
        if (items == null) return;

        var view = CurrentView();
        foreach (var item in items)
        {
            var rect = view.ToControl(item.Bounds);
            context.DrawRectangle(_painter.Brush("SurfacePanelBrush"), null, rect);
            if (item.Result != null) _painter.Draw(context, item.Result, view, null);
        }
        foreach (var item in items)
        {
            var rect = view.ToControl(item.Bounds);
            var pen = item.Selected
                ? new Pen(_painter.Brush("AccentBrush"), 3)
                : new Pen(_painter.Brush("BorderStrongBrush"), 1);
            context.DrawRectangle(null, pen, rect);

            var label = ClientLayoutPainter.Text(item.Label, 11, _painter.Brush("TextPrimaryBrush"));
            var plate = new Rect(rect.X + 4, rect.Y + 4, label.Width + 8, label.Height + 2);
            if (plate.Right <= rect.Right && plate.Bottom <= rect.Bottom)
            {
                context.DrawRectangle(_painter.Brush("SurfaceHeaderBrush"), null, plate);
                context.DrawText(label, new Point(plate.X + 4, plate.Y + 1));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var items = Items;
        if (items == null) return;
        var (x, y) = CurrentView().ToScreen(e.GetPosition(this));
        foreach (var item in items)
        {
            if (x < item.Bounds.X || x >= item.Bounds.Right || y < item.Bounds.Y || y >= item.Bounds.Bottom) continue;
            Pick?.Invoke(item.Key);
            e.Handled = true;
            return;
        }
    }
}

/// <summary>A callback a view model can hand to a drawn control through a binding.</summary>
public interface ICommandLike
{
    void Invoke(string value);
}

public sealed class ActionCommand(Action<string> action) : ICommandLike
{
    public void Invoke(string value) => action(value);
}
