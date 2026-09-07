using Avalonia;
using Avalonia.Media;

namespace EveConsole.Services;

/// <summary>
/// The palette, reachable from code.
///
/// <para>⚠️ Named Palette, not Theme, and it cannot be Theme: Avalonia puts a Theme property on
/// StyledElement, so inside any Control subclass — every custom-drawn canvas here — the bare name
/// resolves to that instance property instead, and the compiler reports it as needing an object
/// reference rather than as a collision.</para>
///
/// <para>Everything in <c>Themes/Palette.axaml</c> that a view model or a custom-drawn control
/// needs. Markup gets these through <c>{DynamicResource}</c>; this is the same brushes, by the
/// same keys, for the code that cannot write markup.</para>
///
/// <para>⚠️ The brush INSTANCES are cached and that is not a shortcut — it is the mechanism. Each
/// brush in the palette is declared once, outside the theme dictionaries, with its Color bound by
/// DynamicResource to a token that is defined per variant. So the instance never changes; its
/// Colour does, the moment the variant does. Hold the instance and everything painted with it
/// re-themes for free. Resolve a fresh brush per call and it would work equally well and cost a
/// dictionary walk on every property read, several of which run per row of a grid.</para>
///
/// <para>⚠️ A view model returning one of these returns <see cref="IBrush"/>, not a hex string.
/// A string is parsed into a brush once at bind time and is then a fixed colour for ever, which
/// is exactly the bug this replaces: status text that stayed dark-theme green on a light page.</para>
/// </summary>
public static class Palette
{
    // ── Surfaces ──────────────────────────────────────────────────────────────
    public static IBrush SurfaceBase     => Brush("SurfaceBaseBrush");
    public static IBrush SurfacePanel    => Brush("SurfacePanelBrush");
    public static IBrush SurfacePanelAlt => Brush("SurfacePanelAltBrush");
    public static IBrush SurfaceHeader   => Brush("SurfaceHeaderBrush");
    public static IBrush SurfaceRaised   => Brush("SurfaceRaisedBrush");
    public static IBrush SurfaceInput    => Brush("SurfaceInputBrush");
    public static IBrush SurfaceHover    => Brush("SurfaceHoverBrush");
    public static IBrush SurfaceSelected => Brush("SurfaceSelectedBrush");

    // ── Lines ─────────────────────────────────────────────────────────────────
    public static IBrush BorderSubtle    => Brush("BorderSubtleBrush");
    public static IBrush BorderDefault   => Brush("BorderDefaultBrush");
    public static IBrush BorderStrong    => Brush("BorderStrongBrush");

    // ── Text ──────────────────────────────────────────────────────────────────
    public static IBrush TextFaint       => Brush("TextFaintBrush");
    public static IBrush TextDim         => Brush("TextDimBrush");
    public static IBrush TextMuted       => Brush("TextMutedBrush");
    public static IBrush TextSecondary   => Brush("TextSecondaryBrush");
    public static IBrush TextPrimary     => Brush("TextPrimaryBrush");
    public static IBrush TextBright      => Brush("TextBrightBrush");

    // ── Accent ────────────────────────────────────────────────────────────────
    public static IBrush Accent          => Brush("AccentBrush");
    public static IBrush AccentHover     => Brush("AccentHoverBrush");
    public static IBrush AccentPressed   => Brush("AccentPressedBrush");
    public static IBrush AccentSurface   => Brush("AccentSurfaceBrush");

    // ── Meaning ───────────────────────────────────────────────────────────────
    public static IBrush Good            => Brush("GoodBrush");
    public static IBrush Bad             => Brush("BadBrush");
    public static IBrush Warn            => Brush("WarnBrush");
    public static IBrush Info            => Brush("InfoBrush");
    public static IBrush GoodSurface     => Brush("GoodSurfaceBrush");
    public static IBrush BadSurface      => Brush("BadSurfaceBrush");
    public static IBrush WarnSurface     => Brush("WarnSurfaceBrush");
    public static IBrush InfoSurface     => Brush("InfoSurfaceBrush");

    /// <summary>
    /// The colour behind a token, for the drawing code that needs a Color rather than a Brush.
    ///
    /// <para>⚠️ Read fresh every time, unlike the brushes. This returns a VALUE, so a cached one
    /// would be whatever the theme was when it was first asked — the exact staleness the brushes
    /// avoid by being instances. Custom-drawn controls should call this inside Render, not hold
    /// the result in a field.</para>
    /// </summary>
    public static Color Colour(string token)
    {
        if (Application.Current is { } app && app.TryGetResource(token, app.ActualThemeVariant, out var found) && found is Color c)
            return c;

        return Colors.Magenta;   // loud on purpose: a missing token should be seen, not guessed at
    }

    /// <summary>
    /// A token as a SkiaSharp colour, for charts.
    ///
    /// <para>⚠️ LiveCharts draws through Skia, which knows nothing about Avalonia brushes — so a
    /// chart cannot share the self-updating instances everything else uses and has to be handed a
    /// value. That value is read when the paint is built, which means a chart already on screen
    /// keeps its colours until something rebuilds it.</para>
    /// </summary>
    public static SkiaSharp.SKColor Sk(string token)
    {
        var c = Colour(token);
        return new SkiaSharp.SKColor(c.R, c.G, c.B, c.A);
    }

    /// <summary>Whether the current theme is a light one, for the code that can only pick a side.</summary>
    public static bool IsLight =>
        Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;

    // ── Resolution ────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, IBrush> Cache = [];

    private static IBrush Brush(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;

        if (Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var found) && found is IBrush b)
        {
            Cache[key] = b;
            return b;
        }

        // ⚠️ Not cached. Application.Current is null in a headless worker and during very early
        // startup, and caching the fallback there would hand every later caller magenta for the
        // life of the process.
        return Brushes.Magenta;
    }
}
