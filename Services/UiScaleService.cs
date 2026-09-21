using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveConsole.Services;

/// <summary>
/// The UI scale: every window's content drawn larger or smaller than the platform's own scaling
/// would have it, 50% to 200%, chosen by the user and kept for this machine.
///
/// <para>Applied through a style on Window. The attached property switches on for every window
/// as it is created, and the window's content is wrapped in a LayoutTransformControl carrying the
/// scale. A window with a fixed size is resized along with its content as it opens, since the
/// dialog was authored for 100% and its frame has to hold what it now draws; the main window keeps
/// the size the user gave it and its content simply gets denser or roomier.</para>
///
/// <para>⚠️ Kept in this client's own config (UiState), never the shared preference table. Two
/// clients on one database are two screens, and the note that says "this client only" has to be
/// true.</para>
/// </summary>
public sealed class UiScaleService
{
    private UiScaleService() { }

    public const double DefaultScale = 1.0;
    public const double MinimumScale = 0.5;
    public const double MaximumScale = 2.0;

    /// <summary>The scales on offer, as percentages.</summary>
    public static IReadOnlyList<int> Presets { get; } = [50, 60, 70, 80, 90, 100, 110, 125, 150, 175, 200];

    private static readonly List<WeakReference<LayoutTransformControl>> Transforms = [];

    /// <summary>The scale each open window's frame was last sized for, so a change resizes it by
    /// the ratio rather than compounding.</summary>
    private static readonly ConditionalWeakTable<Window, StrongBox<double>> SizedFor = new();

    private static double _scale = DefaultScale;

    public static double Scale => _scale;

    /// <summary>The scale in force as the label shows it: "100%".</summary>
    public static string Label => Percent(_scale);

    public static string Percent(double scale) => $"{Math.Round(scale * 100)}%";

    /// <summary>Raised after the scale changes, from whichever control changed it, so every label
    /// and picker that names it can follow.</summary>
    public static event Action? Changed;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<UiScaleService, Window, bool>("IsEnabled");

    public static void SetIsEnabled(Window window, bool value) => window.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(Window window) => window.GetValue(IsEnabledProperty);

    /// <summary>Off for a window whose size is the user's own — the main window — so only its
    /// content rescales, never its frame.</summary>
    public static readonly AttachedProperty<bool> ScalesSizeProperty =
        AvaloniaProperty.RegisterAttached<UiScaleService, Window, bool>("ScalesSize", defaultValue: true);

    public static void SetScalesSize(Window window, bool value) => window.SetValue(ScalesSizeProperty, value);
    public static bool GetScalesSize(Window window) => window.GetValue(ScalesSizeProperty);

    static UiScaleService()
    {
        IsEnabledProperty.Changed.AddClassHandler<Window>((window, args) =>
        {
            if (args.NewValue is true)
                Attach(window);
        });
    }

    /// <summary>Reads this machine's saved scale. Before the first window, splash included, so
    /// nothing opens at one size and jumps to another.</summary>
    public static void Load()
    {
        var saved = UiState.Get(UiState.Scale);
        _scale = double.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
            ? Clamp(scale)
            : DefaultScale;
    }

    /// <summary>Applies a scale to every open window and remembers it for this machine.</summary>
    public static void Apply(double scale)
    {
        scale = Clamp(scale);
        if (Math.Abs(scale - _scale) < 0.001) return;
        _scale = scale;
        UiState.Set(UiState.Scale, scale.ToString("0.00", CultureInfo.InvariantCulture));

        for (var i = Transforms.Count - 1; i >= 0; i--)
        {
            if (!Transforms[i].TryGetTarget(out var transform))
            {
                Transforms.RemoveAt(i);
                continue;
            }

            transform.LayoutTransform = new ScaleTransform(_scale, _scale);
            if (TopLevel.GetTopLevel(transform) is Window window) ResizeFor(window);
        }

        Changed?.Invoke();
    }

    public static double Clamp(double scale) => Math.Clamp(scale, MinimumScale, MaximumScale);

    private static void Attach(Window window)
    {
        // The style can be evaluated before the Window's content is assigned, and before its
        // size is: the resize is tried now, so a dialog opens at its scaled size rather than
        // jumping to it, and again once open in case the size was not there yet.
        window.Opened += (_, _) => { WrapContent(window); ResizeFor(window); };
        window.GetObservable(ContentControl.ContentProperty).Subscribe(_ => WrapContent(window));
        WrapContent(window);
        ResizeFor(window);
    }

    private static void WrapContent(Window window)
    {
        if (window.Content is null || window.Content is LayoutTransformControl)
            return;

        // A control cannot have two logical parents. Detach the original content from the Window
        // before making the LayoutTransformControl its new parent.
        if (window.Content is not Control content)
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

    /// <summary>
    /// Sizes a window's frame for the scale in force. Its markup gives the size its content needs
    /// at 100%; the frame grows or shrinks by the same factor, kept within the screen it is on.
    ///
    /// <para>Only sizes that were set are touched. A dimension left to SizeToContent follows the
    /// content on its own, and NaN means the window was never given one.</para>
    /// </summary>
    private static void ResizeFor(Window window)
    {
        if (!GetScalesSize(window)) return;

        var sizedFor = SizedFor.GetValue(window, _ => new StrongBox<double>(DefaultScale));
        var ratio = _scale / sizedFor.Value;
        if (Math.Abs(ratio - 1) < 0.001) return;

        var widthIsManual  = window.SizeToContent is SizeToContent.Manual or SizeToContent.Height;
        var heightIsManual = window.SizeToContent is SizeToContent.Manual or SizeToContent.Width;
        var anySize = (widthIsManual && !double.IsNaN(window.Width)) || (heightIsManual && !double.IsNaN(window.Height));
        if (!anySize) return;   // nothing to size yet: the Opened pass will find it
        sizedFor.Value = _scale;

        if (widthIsManual  && !double.IsNaN(window.Width))  window.Width  *= ratio;
        if (heightIsManual && !double.IsNaN(window.Height)) window.Height *= ratio;
        if (window.MinWidth  > 0) window.MinWidth  *= ratio;
        if (window.MinHeight > 0) window.MinHeight *= ratio;
        if (!double.IsPositiveInfinity(window.MaxWidth))  window.MaxWidth  *= ratio;
        if (!double.IsPositiveInfinity(window.MaxHeight)) window.MaxHeight *= ratio;

        FitToScreen(window);
    }

    /// <summary>Keeps a scaled-up frame inside its screen's working area, a margin short of the
    /// edges. ⚠️ WorkingArea is physical pixels; the window's sizes are logical.</summary>
    private static void FitToScreen(Window window)
    {
        try
        {
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null) return;
            var maxW = screen.WorkingArea.Width  / screen.Scaling - 40;
            var maxH = screen.WorkingArea.Height / screen.Scaling - 40;
            if (!double.IsNaN(window.Width)  && window.Width  > maxW) window.Width  = Math.Max(maxW, window.MinWidth);
            if (!double.IsNaN(window.Height) && window.Height > maxH) window.Height = Math.Max(maxH, window.MinHeight);
        }
        catch
        {
            // No screen to ask yet: the size stands, and the platform clamps a window it cannot fit.
        }
    }
}

/// <summary>One entry of the scale pickers: the factor and its label.</summary>
public sealed record UiScaleChoice(double Scale, string Name)
{
    public static IReadOnlyList<UiScaleChoice> All { get; } =
        UiScaleService.Presets.Select(p => new UiScaleChoice(p / 100.0, $"{p}%")).ToList();

    /// <summary>The entry for the scale in force. A scale set outside the presets gets an entry of
    /// its own rather than being shown as the nearest one.</summary>
    public static UiScaleChoice Current() =>
        All.FirstOrDefault(c => Math.Abs(c.Scale - UiScaleService.Scale) < 0.001)
        ?? new UiScaleChoice(UiScaleService.Scale, UiScaleService.Label);

    public override string ToString() => Name;
}
