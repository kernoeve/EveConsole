using Avalonia.Media;

namespace EveConsole.Controls;

/// <summary>One label, ready to draw: its text and the three brushes its chip is made of.</summary>
public record LabelChip(string Text, IBrush Fill, IBrush Stroke, IBrush Ink);

/// <summary>
/// The colour a label is drawn in.
///
/// <para>Derived from the text, so one label is the same colour everywhere it appears — in the
/// editor, in the grid, on any order carrying it — with nothing stored and nothing to choose.
/// Colour is what makes a column of tags scannable; a grid of identically-tinted boxes is a
/// column of text with extra decoration.</para>
///
/// <para><b>⚠️ Hashed here rather than with <c>string.GetHashCode</c>.</b> .NET randomises string
/// hashing per process, so that would repaint every label a different colour on each launch —
/// which is worse than one colour, because it looks like it means something.</para>
///
/// <para>⚠️ Case-insensitive, like every other comparison on labels. Two spellings of one tag
/// should never be two colours while the app is still treating them as the same tag.</para>
/// </summary>
public static class LabelPalette
{
    /// <summary>
    /// Hues picked to stay apart from each other on this background, and clear of the amber the
    /// app uses for selection and emphasis.
    /// </summary>
    private static readonly Color[] Bases =
    [
        Color.Parse("#5b8dd9"),   // blue
        Color.Parse("#4fae7a"),   // green
        Color.Parse("#a86ed0"),   // purple
        Color.Parse("#3fb0b0"),   // teal
        Color.Parse("#d06a86"),   // rose
        Color.Parse("#d08a44"),   // orange
        Color.Parse("#7a7ad9"),   // indigo
        Color.Parse("#8ec24a"),   // lime
        Color.Parse("#4fb4d9"),   // cyan
        Color.Parse("#c96fb0"),   // magenta
    ];

    /// <summary>The panel colour chips sit on, which the tint is mixed into.</summary>
    private static readonly Color Ground = Color.Parse("#131320");

    private static readonly Dictionary<string, LabelChip> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything needed to draw one label.</summary>
    public static LabelChip Chip(string label)
    {
        // Chips are rebuilt on every grid refresh and every keystroke in the editor; the brushes
        // are immutable, so they are made once and shared.
        lock (Cache)
        {
            if (Cache.TryGetValue(label, out var hit)) return hit;

            var b    = Bases[Index(label)];
            var chip = new LabelChip(
                label,
                new SolidColorBrush(Mix(b, Ground, 0.18)),
                new SolidColorBrush(Mix(b, Ground, 0.62)),
                new SolidColorBrush(Lighten(b, 0.35)));

            Cache[label] = chip;
            return chip;
        }
    }

    /// <summary>Chips for a list of labels, in the order given.</summary>
    public static List<LabelChip> Chips(IEnumerable<string> labels) =>
        labels.Select(Chip).ToList();

    /// <summary>FNV-1a over the lower-cased text: same colour in every process, forever.</summary>
    private static int Index(string label)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in label)
            {
                hash ^= char.ToLowerInvariant(ch);
                hash *= 16777619u;
            }
            return (int)(hash % (uint)Bases.Length);
        }
    }

    private static Color Mix(Color c, Color onto, double amount) => Color.FromRgb(
        (byte)(onto.R + (c.R - onto.R) * amount),
        (byte)(onto.G + (c.G - onto.G) * amount),
        (byte)(onto.B + (c.B - onto.B) * amount));

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));
}
