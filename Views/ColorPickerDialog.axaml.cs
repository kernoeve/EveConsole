using Avalonia.Controls;
using Avalonia.Media;

namespace EveConsole.Views;

/// <summary>
/// The full colour wheel, for anything the palette does not carry.
///
/// <para>A dialog rather than a flyout inside the palette: nesting one popup in another is
/// fragile in Avalonia — the outer one closes when focus moves into the inner — and a wheel needs
/// more room than a flyout should take.</para>
/// </summary>
public partial class ColorPickerDialog : Window
{
    public ColorPickerDialog() : this("") { }

    public ColorPickerDialog(string? current)
    {
        InitializeComponent();

        if (Parse(current) is { } start) Picker.Color = start;

        // ⚠️ Sampled against EVE's background, not this dialog's. A colour chosen against a
        // lighter panel and then read on near-black is the one mistake this dialog can make that
        // the author will not notice until a mail has gone out.
        Picker.ColorChanged += (_, _) => Show(Picker.Color);
        Show(Picker.Color);

        OkButton.Click     += (_, _) => Close(Hex(Picker.Color));
        CancelButton.Click += (_, _) => Close(null);
    }

    private void Show(Color c)
    {
        Sample.Foreground = new SolidColorBrush(c);
        HexLabel.Text     = Hex(c);
    }

    /// <summary>Six digits, lower case — the form the rest of the app stores and the palette
    /// offers, so a custom colour is indistinguishable from a picked one afterwards.</summary>
    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static Color? Parse(string? hex)
    {
        var s = (hex ?? "").Trim();
        if (s.Length == 0) return null;
        if (!s.StartsWith('#')) s = "#" + s;
        try { return Color.Parse(s); }
        catch { return null; }
    }
}
