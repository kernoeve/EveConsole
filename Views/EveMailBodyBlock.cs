using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using EveConsole.Services;

namespace EveConsole.Views;

/// <summary>
/// Shows an EVE mail body the way the game does: bold, italic, colour, and links that go
/// somewhere — an item to the Item Browser, a pilot to the Player Entities page, a
/// killmail to the Killmail Browser.
/// </summary>
/// <remarks>
/// <para>A <see cref="SelectableTextBlock"/>, so the text still selects and copies like any
/// other. Links are plain runs with a link colour, not embedded buttons: a button inside a
/// text flow breaks selection across it and sits a pixel off the baseline. Instead the
/// block remembers where each link's characters are, and on a click asks the text layout
/// which character is under the pointer. A click that was really the end of a drag —
/// selection made — is left to the selection.</para>
///
/// <para>Colours are EVE's, written for the client's dark background, and this app's is
/// dark too, so they are used as given. A body with no markup renders as one run in the
/// default foreground, exactly as before.</para>
/// </remarks>
public class EveMailBodyBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> MarkupProperty =
        AvaloniaProperty.Register<EveMailBodyBlock, string?>(nameof(Markup));

    public string? Markup
    {
        get => GetValue(MarkupProperty);
        set => SetValue(MarkupProperty, value);
    }

    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#5a9be0"));

    /// <summary>EVE's default mail font size; sizes in markup are relative to it.</summary>
    private const double EveBaseSize = 12.0;

    private readonly record struct LinkRange(int Start, int Length, MailLink Link);
    private readonly List<LinkRange> _links = [];
    private bool _overLink;

    static EveMailBodyBlock()
    {
        MarkupProperty.Changed.AddClassHandler<EveMailBodyBlock>((tb, _) => tb.Rebuild());
    }

    private void Rebuild()
    {
        _links.Clear();
        Inlines ??= new InlineCollection();
        Inlines.Clear();

        var segs = EveMailMarkup.Parse(Markup);
        if (segs.Count == 0) { Text = ""; return; }

        // The character offset each run starts at, in the flattened text the layout sees.
        //
        // ⚠️ A LineBreak is Environment.NewLine in that text — TWO characters on Windows, not
        // one. Counted as one, every link after the first line break was recorded a character
        // early per break above it, and in a store mail the item link sits on line three: the
        // hit-test landed two characters past the range and nothing was clickable.
        var offset = 0;
        var newline = Environment.NewLine.Length;
        foreach (var seg in segs)
        {
            var link = seg.Href is null ? null : MailLink.Parse(seg.Href);
            var lines = seg.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) { Inlines.Add(new LineBreak()); offset += newline; }
                if (lines[i].Length == 0) continue;

                var run = new Run(lines[i]);
                if (seg.Bold)   run.FontWeight = FontWeight.Bold;
                if (seg.Italic) run.FontStyle  = FontStyle.Italic;

                // Fully qualified: inside a TextBlock subclass the bare name is the inherited
                // TextDecorations PROPERTY, not the static class of presets.
                if (seg.Underline || (link is { IsSupported: true }))
                    run.TextDecorations = Avalonia.Media.TextDecorations.Underline;

                if (link is { IsSupported: true }) run.Foreground = LinkBrush;
                else if (seg.Color is { } argb)     run.Foreground = new SolidColorBrush(Color.FromUInt32(argb));

                if (seg.Size is { } size && size > 0)
                    run.FontSize = Math.Clamp(FontSize * (size / EveBaseSize), 9, 32);

                Inlines.Add(run);
                if (link is { IsSupported: true })
                    _links.Add(new LinkRange(offset, lines[i].Length, link));
                offset += lines[i].Length;
            }
        }
    }

    private MailLink? LinkAt(Point p)
    {
        if (_links.Count == 0 || TextLayout is null) return null;

        var hit = TextLayout.HitTestPoint(new Point(p.X - Padding.Left, p.Y - Padding.Top));
        if (!hit.IsInside) return null;

        var idx = hit.TextPosition;
        foreach (var l in _links)
            if (idx >= l.Start && idx < l.Start + l.Length) return l.Link;
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var over = LinkAt(e.GetPosition(this)) is not null;
        if (over == _overLink) return;
        _overLink = over;
        Cursor = over ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _overLink = false;
        Cursor = Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // A drag that ended here is a selection, not a click.
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (SelectionStart != SelectionEnd) return;

        if (LinkAt(e.GetPosition(this)) is { } link)
        {
            link.Open();
            e.Handled = true;
        }
    }
}
