using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using EveConsole.Services;

namespace EveConsole.Views;

/// <summary>
/// A corner of the application wearing a theme that is not the one in use.
///
/// <para>⚠️ Not done by pointing the scope at the theme's variant. The palette's brushes are
/// declared once, for the whole application, and their colour follows the APPLICATION's variant —
/// so a scope asking for another variant gets the right Fluent controls and every palette brush in
/// the old colours. Instead the scope carries brushes of its own under the same keys, found before
/// the application's, and the variant only says which ground Fluent draws for.</para>
/// </summary>
public partial class ThemePreview : UserControl
{
    /// <summary>One brush per key for the preview's life, re-coloured in place: a colour dragged
    /// across the picker repaints without a single resource being replaced.</summary>
    private readonly Dictionary<string, SolidColorBrush> _brushes = [];

    public ThemePreview() => InitializeComponent();

    public void Show(IReadOnlyDictionary<string, Color> colours, bool light)
    {
        Scope.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;

        foreach (var (key, colour) in colours.Concat(ThemeTokens.Derived(colours, light)))
        {
            if (!Scope.Resources.TryGetValue(key, out var old) || old is not Color c || c != colour)
                Scope.Resources[key] = colour;

            var brushKey = key + "Brush";
            if (_brushes.TryGetValue(brushKey, out var brush)) brush.Color = colour;
            else Scope.Resources[brushKey] = _brushes[brushKey] = new SolidColorBrush(colour);
        }
    }
}
