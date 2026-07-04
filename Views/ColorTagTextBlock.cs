using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace EveCortex.Views;

/// <summary>
/// TextBlock that understands Eve Online colour markup: &lt;color='0xAARRGGBB'&gt;…&lt;/color&gt;
/// </summary>
public class ColorTagTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> TaggedTextProperty =
        AvaloniaProperty.Register<ColorTagTextBlock, string?>(nameof(TaggedText));

    public string? TaggedText
    {
        get => GetValue(TaggedTextProperty);
        set => SetValue(TaggedTextProperty, value);
    }

    // Matches <color='0xAARRGGBB'> or <color=0xAARRGGBB> (with or without quotes)
    private static readonly Regex _colorOpen =
        new(@"<color='?0x([0-9A-Fa-f]{6,8})'?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _colorClose =
        new(@"</color>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _anyTag =
        new(@"<[^>]+>", RegexOptions.Compiled);

    static ColorTagTextBlock()
    {
        TaggedTextProperty.Changed.AddClassHandler<ColorTagTextBlock>((tb, _) => tb.Rebuild());
    }

    private void Rebuild()
    {
        var raw = TaggedText;
        Inlines?.Clear();

        if (string.IsNullOrEmpty(raw))
            return;

        if (!raw.Contains("<color", StringComparison.OrdinalIgnoreCase))
        {
            // No colour tags — strip any stray markup and display as plain text
            Inlines?.Add(new Run(_anyTag.Replace(raw, "")));
            return;
        }

        // Merge open/close matches sorted by position
        var events = new List<(int Index, int Length, bool IsOpen, string Hex)>();
        foreach (Match m in _colorOpen.Matches(raw))
            events.Add((m.Index, m.Length, true, m.Groups[1].Value));
        foreach (Match m in _colorClose.Matches(raw))
            events.Add((m.Index, m.Length, false, ""));
        events.Sort((a, b) => a.Index.CompareTo(b.Index));

        IBrush? current = null;
        var pos = 0;

        foreach (var (idx, len, isOpen, hex) in events)
        {
            if (idx > pos)
                AddRun(raw[pos..idx], current);
            pos = idx + len;

            current = isOpen ? ParseBrush(hex) : null;
        }

        if (pos < raw.Length)
            AddRun(raw[pos..], current);
    }

    private void AddRun(string text, IBrush? brush)
    {
        if (text.Length == 0) return;
        var run = new Run(text);
        if (brush != null) run.Foreground = brush;
        Inlines?.Add(run);
    }

    private static IBrush? ParseBrush(string hex)
    {
        try
        {
            if (hex.Length == 8)
            {
                // AARRGGBB
                var a = Convert.ToByte(hex[0..2], 16);
                var r = Convert.ToByte(hex[2..4], 16);
                var g = Convert.ToByte(hex[4..6], 16);
                var b = Convert.ToByte(hex[6..8], 16);
                return new SolidColorBrush(Color.FromArgb(a, r, g, b));
            }
            if (hex.Length == 6)
            {
                var r = Convert.ToByte(hex[0..2], 16);
                var g = Convert.ToByte(hex[2..4], 16);
                var b = Convert.ToByte(hex[4..6], 16);
                return new SolidColorBrush(Color.FromRgb(r, g, b));
            }
        }
        catch { /* malformed hex → inherit parent foreground */ }
        return null;
    }
}
