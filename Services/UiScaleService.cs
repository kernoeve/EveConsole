using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveConsole.Services;

/// <summary>Applies the user's UI scale to every Avalonia window, including dialogs.</summary>
public sealed class UiScaleService
{
    private UiScaleService() { }
    public const double DefaultScale = 1.0;
    public const double MinimumScale = 0.75;
    public const double MaximumScale = 2.0;
    public const string PreferenceKey = "ui.scale";

    private static readonly List<WeakReference<LayoutTransformControl>> Transforms = [];
    private static double _scale = DefaultScale;

    public static double Scale => _scale;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<UiScaleService, Window, bool>("IsEnabled");

    public static void SetIsEnabled(Window window, bool value) => window.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(Window window) => window.GetValue(IsEnabledProperty);

    static UiScaleService()
    {
        IsEnabledProperty.Changed.AddClassHandler<Window>((window, args) =>
        {
            if (args.NewValue is true)
                Attach(window);
        });
    }

    public static void SetScale(double scale)
    {
        _scale = Math.Clamp(scale, MinimumScale, MaximumScale);

        for (var i = Transforms.Count - 1; i >= 0; i--)
        {
            if (!Transforms[i].TryGetTarget(out var transform))
            {
                Transforms.RemoveAt(i);
                continue;
            }

            transform.LayoutTransform = new ScaleTransform(_scale, _scale);
        }
    }

    private static void Attach(Window window)
    {
        // The style can be evaluated before the Window's content is assigned.
        window.Opened += (_, _) => WrapContent(window);
        window.GetObservable(ContentControl.ContentProperty).Subscribe(_ => WrapContent(window));
        WrapContent(window);
    }

    private static void WrapContent(Window window)
    {
        if (window.Content is null || window.Content is LayoutTransformControl)
            return;

        var content = window.Content as Control;

        // A control cannot have two logical parents. Detach the original content from the Window
        // before making the LayoutTransformControl its new parent.
        if (content is null)
            return;

        window.Content = null;

        var transform = new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(_scale, _scale),
            Child = content,
        };

        Transforms.Add(new WeakReference<LayoutTransformControl>(transform));
        window.Content = transform;
    }
}
