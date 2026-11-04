using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using EveConsole.Services;

namespace EveConsole.Views;

/// <summary>
/// Plain text with its web addresses made clickable, for a SelectableTextBlock: the agent chat,
/// where an intel report's d-scan link arrives as part of what was said.
///
/// <para>The text stays one string of runs, newlines kept inside them, so a character's place in
/// the layout is its place in the text and a click is found by hit-testing. A drag still selects:
/// only a click that selected nothing opens a link, through <see cref="ExternalLinks"/>, which
/// asks first — the address came from somebody else.</para>
/// </summary>
public sealed partial class LinkText
{
    private LinkText() { }

    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<LinkText, SelectableTextBlock, string?>("Text");

    public static void SetText(SelectableTextBlock e, string? v) => e.SetValue(TextProperty, v);
    public static string? GetText(SelectableTextBlock e) => e.GetValue(TextProperty);

    /// <summary>The links in the text shown, by character range.</summary>
    private static readonly AttachedProperty<List<(int Start, int End, string Url)>?> LinksProperty =
        AvaloniaProperty.RegisterAttached<LinkText, SelectableTextBlock, List<(int, int, string)>?>("Links");

    /// <summary>
    /// http(s) or www, up to whitespace — less what ends a sentence: "see https://x.y/z." links
    /// to https://x.y/z.
    /// </summary>
    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+?(?=[.,;:!?)\]'""]*(?:\s|$))", RegexOptions.IgnoreCase)]
    internal static partial Regex UrlPattern { get; }

    static LinkText()
    {
        TextProperty.Changed.AddClassHandler<SelectableTextBlock>((tb, e) => Render(tb, e.NewValue as string ?? ""));
    }

    private static void Render(SelectableTextBlock tb, string text)
    {
        var links = new List<(int, int, string)>();
        tb.Inlines ??= new InlineCollection();
        tb.Inlines.Clear();

        var at = 0;
        foreach (Match m in UrlPattern.Matches(text))
        {
            if (m.Index > at) tb.Inlines.Add(new Run(text[at..m.Index]));
            var run = new Run(m.Value) { TextDecorations = TextDecorations.Underline };
            run.Bind(TextElement.ForegroundProperty, tb.GetResourceObservable("AccentBrush"));
            tb.Inlines.Add(run);
            links.Add((m.Index, m.Index + m.Length, m.Value));
            at = m.Index + m.Length;
        }
        if (at < text.Length) tb.Inlines.Add(new Run(text[at..]));

        // Wired once per control, and only when it has had a link — most messages never do.
        if (links.Count > 0 && tb.GetValue(LinksProperty) is null)
        {
            // ⚠️ handledEventsToo: the text block handles its own presses for selection.
            tb.AddHandler(InputElement.PointerMovedEvent,    OnMoved,    handledEventsToo: true);
            tb.AddHandler(InputElement.PointerReleasedEvent, OnReleased, handledEventsToo: true);
        }
        tb.SetValue(LinksProperty, links);
    }

    private static string? LinkAt(SelectableTextBlock tb, PointerEventArgs e)
    {
        if (tb.GetValue(LinksProperty) is not { Count: > 0 } links) return null;
        var p   = e.GetPosition(tb) - new Point(tb.Padding.Left, tb.Padding.Top);
        var hit = tb.TextLayout.HitTestPoint(p);
        if (!hit.IsInside) return null;
        var index = hit.TextPosition;
        foreach (var (start, end, url) in links)
            if (index >= start && index < end) return url;
        return null;
    }

    private static readonly Cursor Hand = new(StandardCursorType.Hand);

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not SelectableTextBlock tb) return;
        // The text block's own cursor is the I-beam; over a link, the hand.
        if (LinkAt(tb, e) is null) tb.ClearValue(InputElement.CursorProperty);
        else                       tb.Cursor = Hand;
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not SelectableTextBlock tb || e.InitialPressMouseButton != MouseButton.Left) return;
        if (tb.SelectionEnd != tb.SelectionStart) return;   // a drag that selected text
        if (LinkAt(tb, e) is { } url) ExternalLinks.Open(url);
    }
}
