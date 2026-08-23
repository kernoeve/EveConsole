using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Media;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

// Result of the Add/Edit Posting dialog.
public record PostBlockDraft(string PostType, string Name, string? StaticContent, string Header, string Footer);

// Inline styles a preview run can carry (combinable). Emoji render as a placeholder glyph.
[Flags]
public enum SegStyle { None = 0, Bold = 1, Italic = 2, Underline = 4, Strike = 8, Link = 16 }

// A run of preview text with its styles.
public readonly record struct DisplaySeg(string Text, SegStyle Style);

// A rendered post block for the Posting tab: a header label, the raw markup (what the Copy button
// puts on the clipboard — with real *bold*/tags and literal :emoji:), and the styled preview segments.
public class RenderedBlock
{
    public string Header { get; }
    public string RawText { get; }
    public IReadOnlyList<DisplaySeg> Segments { get; }
    public RenderedBlock(string header, string rawText, IReadOnlyList<DisplaySeg> segments)
    { Header = header; RawText = rawText; Segments = segments; }
}

public record SectionDialogResult(
    string Name, string Prefix,
    bool OverrideScope, string Scope, long? LocationId, string LocationName,
    bool OverridePricing, string PricingBasis, double PricePercent,
    long? MarketStationId, string MarketStationName, string MarketPriceType,
    bool OverrideOnlyPackaged, bool OnlyPackaged);

public record PostingDialogResult(
    string Name, string Scope, long? LocationId, string LocationName,
    string PricingBasis, double PricePercent,
    long? MarketStationId, string MarketStationName, string MarketPriceType,
    bool ShowInStock, bool ShowInBuild, bool ShowReserved, bool IncludeCompletionDate,
    bool OnlyPackaged, IReadOnlyList<PostBlockDraft> Posts);

// ── Shared display helpers ──────────────────────────────────────────────────────
internal static class SalePostFmt
{
    public static readonly IBrush Green   = new SolidColorBrush(Color.Parse("#4a9a5a"));
    public static readonly IBrush Red     = new SolidColorBrush(Color.Parse("#c85a5a"));
    public static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#888899"));

    public static string Isk(double? v)
    {
        if (v is not double d) return "—";
        double a = Math.Abs(d);
        return a >= 1_000_000_000 ? (d / 1_000_000_000).ToString("0.00", CultureInfo.InvariantCulture) + "B"
             : a >= 1_000_000     ? (d / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + "M"
             : a >= 1_000         ? (d / 1_000).ToString("0.0", CultureInfo.InvariantCulture) + "K"
             :                      d.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string Qty(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    public static string Pct(double? v) =>
        v is double d ? d.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%" : "—";
}

// Output format for the rendered posting text. Bold() wraps a section line in the format's
// bold syntax; Post() runs whole-text substitutions (emoji placeholders, HTML line breaks,
// plain-text markup stripping) over the finished block.
internal sealed class OutputFormat
{
    // Slack/Discord shortcode: colon-wrapped token, no spaces — matches only genuine :emoji:
    // macros (times like 14:30, http://, spaced ratios never match), so no escaping is needed.
    private static readonly Regex Emoji = new(@":([a-z0-9_+\-]+):", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string Placeholder = "🔳";   // stand-in for a Slack/Discord icon in the preview

    public string Name { get; }
    private readonly Func<string, string> _bold;                 // wrap a section line in markup
    private readonly Func<string, string> _underline;            // wrap in underline (identity where unsupported)
    private readonly Func<string, string> _finalize;             // whole block -> clipboard text
    private readonly Func<string, List<DisplaySeg>> _toDisplay;  // clipboard text -> preview segments

    /// <summary>
    /// Turn an item name into a link to that type, where the target has such a thing.
    ///
    /// <para>Defaults to plain text, and that is the right default for nearly everywhere: Slack
    /// and Discord have no way to open an EVE type, and emitting an anchor into them would print
    /// raw markup at the reader. EVE mail is the exception — the client resolves
    /// <c>showinfo:</c> links, so a hull name becomes something the buyer can click to open the
    /// item, which is most of the value of answering in-game rather than in chat.</para>
    /// </summary>
    private readonly Func<int, string, string> _link;

    /// <summary>Wraps text in a colour, given an eight-digit ARGB hex. Null where the format has
    /// no colours, which is every format except EVE mail.</summary>
    private readonly Func<string, string, string>? _color;

    private OutputFormat(string name, Func<string, string> bold, Func<string, string> underline,
        Func<string, string> finalize, Func<string, List<DisplaySeg>> toDisplay,
        Func<int, string, string>? link = null,
        Func<string, string, string>? color = null)
    {
        Name = name; _bold = bold; _underline = underline;
        _finalize = finalize; _toDisplay = toDisplay;
        _link = link ?? ((_, text) => text);
        _color = color;
    }

    public string Bold(string s) => _bold(s);
    public string Underline(string s) => _underline(s);
    public string Finalize(string s) => _finalize(s);
    public List<DisplaySeg> ToDisplay(string s) => _toDisplay(s);

    /// <summary>The name, linked to its type where the format supports it.</summary>
    public string ItemLink(int typeId, string text) => typeId > 0 ? _link(typeId, text) : text;

    /// <summary>
    /// Text in a colour, where the target has colours.
    ///
    /// <para><b>⚠️ Applied at render time, not carried as markup.</b> Colour could have been a
    /// tag in the text that each format strips on the way out, the way <c>&lt;u&gt;</c> is — and
    /// that is exactly why it is not. That marker has to be stripped explicitly at the Slack copy
    /// site, and anything that forgets prints raw tags at the reader. A capability cannot be
    /// forgotten: every format that has no colours returns the text untouched, so there is
    /// nothing to strip anywhere.</para>
    ///
    /// <para>EVE's colours are ARGB and the alpha is not optional — <c>#ffRRGGBB</c>. A
    /// six-digit value is read as ARGB with a zero alpha, which is invisible text.</para>
    /// </summary>
    public string Color(string? hex, string text)
    {
        if (_color is null || string.IsNullOrWhiteSpace(hex)) return text;
        var rgb = hex.TrimStart('#');
        return rgb.Length is 6 or 8 ? _color(rgb.Length == 6 ? "ff" + rgb : rgb, text) : text;
    }

    // ── clipboard-side transforms ──
    private static string Identity(string s) => s;
    private static string HtmlBreaks(string s) => s.Replace("\n", "<br>\n");
    private static string StripMarkup(string s)   // best-effort for Plain (unknown source format)
    {
        s = Regex.Replace(s, @"<a\b[^>]*>(.*?)</a>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = Regex.Replace(s, @"\[url[^\]]*\](.*?)\[/url\]", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[/?[a-zA-Z0-9=#*]+\]", "");
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)", "$1");   // [text](url) -> text
        s = Regex.Replace(s, @"<[^>|]*\|([^>]*)>", "$1");         // <url|text>  -> text
        s = Emoji.Replace(s, "");
        s = s.Replace("**", "").Replace("__", "").Replace("~~", "")
             .Replace("*", "").Replace("_", "").Replace("~", "").Replace("`", "");
        return Regex.Replace(s, @"[ \t]{2,}", " ");
    }

    // ── display-side parsing (markup -> styled segments) ──
    private static string EmojiPh(string s) => Emoji.Replace(s, Placeholder);

    private sealed record InlineRule(Regex Re, SegStyle Style, int TextGroup, bool Recurse);

    private static Regex R(string p, bool singleline = false) =>
        new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | (singleline ? RegexOptions.Singleline : RegexOptions.None));

    private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex BbTag   = new(@"\[/?[^\]]+\]", RegexOptions.Compiled);

    // Per-format inline rules — multi-char delimiters listed before single-char so they win ties.
    // Underline is intentionally absent where the platform lacks it (Markdown has no underline
    // syntax anywhere). Slack's legacy mrkdwn text has no underline token either, but Slack's real
    // posting mechanism now uses Block Kit rich_text (see BuildSlackRichTextBlock below), which DOES
    // support underline (confirmed against Slack's live API) — so <u>x</u> here is just this app's
    // own internal marker, parsed by both this preview tokenizer and the rich_text converter; it's
    // never sent to Slack as literal text. Deliberately NOT __x__: Slack's own composer partially
    // auto-formats a pasted double-underscore as italic (mistaking one underscore on each side for
    // its real _italic_ token), leaving a stray leftover underscore — an HTML-style tag isn't one of
    // Slack's own markdown triggers, so pasting it manually into Slack leaves it inert instead.
    private static readonly InlineRule[] SlackRules =
    [
        new(R(@"\*([^*\n]+)\*"),               SegStyle.Bold,      1, true),
        new(R(@"<u>(.*?)</u>", true),          SegStyle.Underline, 1, true),
        new(R(@"_([^_\n]+)_"),                 SegStyle.Italic,    1, true),
        new(R(@"~([^~\n]+)~"),                 SegStyle.Strike,    1, true),
        new(R(@"<([^>|\n]+)\|([^>\n]+)>"),     SegStyle.Link,      2, false),
        new(R(@"<((?:https?|mailto)[^>\n]+)>"),SegStyle.Link,      1, false),
    ];
    private static readonly InlineRule[] DiscordRules =
    [
        new(R(@"\*\*([^\n]+?)\*\*"),           SegStyle.Bold,      1, true),
        new(R(@"__([^\n]+?)__"),               SegStyle.Underline, 1, true),
        new(R(@"~~([^\n]+?)~~"),               SegStyle.Strike,    1, true),
        new(R(@"\*([^*\n]+?)\*"),              SegStyle.Italic,    1, true),
        new(R(@"_([^_\n]+?)_"),                SegStyle.Italic,    1, true),
        new(R(@"\[([^\]\n]+)\]\(([^)\n]+)\)"), SegStyle.Link,      1, false),
    ];
    private static readonly InlineRule[] MdRules =
    [
        new(R(@"\*\*([^\n]+?)\*\*"),           SegStyle.Bold,   1, true),
        new(R(@"__([^\n]+?)__"),               SegStyle.Bold,   1, true),
        new(R(@"~~([^\n]+?)~~"),               SegStyle.Strike, 1, true),
        new(R(@"\*([^*\n]+?)\*"),              SegStyle.Italic, 1, true),
        new(R(@"_([^_\n]+?)_"),                SegStyle.Italic, 1, true),
        new(R(@"\[([^\]\n]+)\]\(([^)\n]+)\)"), SegStyle.Link,   1, false),
    ];
    private static readonly InlineRule[] HtmlRules =
    [
        new(R(@"<(?:strong|b)>(.*?)</(?:strong|b)>", true),      SegStyle.Bold,      1, true),
        new(R(@"<(?:em|i)>(.*?)</(?:em|i)>", true),              SegStyle.Italic,    1, true),
        new(R(@"<u>(.*?)</u>", true),                            SegStyle.Underline, 1, true),
        new(R(@"<(?:s|del|strike)>(.*?)</(?:s|del|strike)>", true),SegStyle.Strike,  1, true),
        new(R(@"<a\b[^>]*>(.*?)</a>", true),                     SegStyle.Link,      1, false),
    ];
    private static readonly InlineRule[] BbRules =
    [
        new(R(@"\[b\](.*?)\[/b\]", true),          SegStyle.Bold,      1, true),
        new(R(@"\[i\](.*?)\[/i\]", true),          SegStyle.Italic,    1, true),
        new(R(@"\[u\](.*?)\[/u\]", true),          SegStyle.Underline, 1, true),
        new(R(@"\[s\](.*?)\[/s\]", true),          SegStyle.Strike,    1, true),
        new(R(@"\[url[^\]]*\](.*?)\[/url\]", true), SegStyle.Link,     1, false),
    ];

    // Recursive tokenizer: at each position take the earliest-matching rule, recursing into styled
    // spans so styles can nest (e.g. bold+italic). Leaf text runs through xform (emoji placeholder /
    // leftover-tag stripping).
    private static List<DisplaySeg> Tokenize(string s, InlineRule[] rules, Func<string, string> xform)
    {
        var o = new List<DisplaySeg>();
        Walk(s, SegStyle.None, rules, xform, o);
        return o;
    }
    private static void Walk(string text, SegStyle inherited, InlineRule[] rules, Func<string, string> xform, List<DisplaySeg> o)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            Match? best = null; InlineRule? br = null;
            foreach (var r in rules)
            {
                var m = r.Re.Match(text, pos);
                if (m.Success && (best is null || m.Index < best.Index)) { best = m; br = r; }
            }
            if (best is null || br is null) { Emit(o, xform(text[pos..]), inherited); return; }
            if (best.Index > pos) Emit(o, xform(text[pos..best.Index]), inherited);
            var inner = best.Groups[br.TextGroup].Value;
            if (br.Recurse) Walk(inner, inherited | br.Style, rules, xform, o);
            else Emit(o, xform(inner), inherited | br.Style);
            pos = best.Index + best.Length;
        }
    }
    private static void Emit(List<DisplaySeg> o, string t, SegStyle s) { if (t.Length > 0) o.Add(new(t, s)); }

    // Block-level list markup -> bullet/plain lines (best-effort; numbered HTML/BBCode become bullets).
    private static string HtmlLists(string s)
    {
        s = Regex.Replace(s, @"<li[^>]*>(.*?)</li>", "• $1\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"</?[uo]l[^>]*>", "\n", RegexOptions.IgnoreCase);
        return s;
    }
    private static string BbLists(string s)
    {
        s = Regex.Replace(s, @"\[\*\][ \t]*", "\n• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\[/?list[^\]]*\]", "", RegexOptions.IgnoreCase);
        return s;
    }
    private static string MdLists(string s) => Regex.Replace(s, @"(?m)^[ \t]*[-*+][ \t]+", "• ");

    private static List<DisplaySeg> HtmlDisplay(string s)
    {
        s = HtmlLists(s);
        s = Regex.Replace(s, @"<br\s*/?>\r?\n?", "\n", RegexOptions.IgnoreCase);   // fixes double-spacing
        return Tokenize(s, HtmlRules, t => HtmlTag.Replace(t, ""));
    }

    public static readonly OutputFormat[] All =
    [
        // bold, underline (identity where the platform has no underline), finalize, toDisplay.
        // Slack/Discord: swap :emoji: for the placeholder FIRST so underscores inside a shortcode
        // (e.g. :white_small_square:) aren't mistaken for italics by the style parser.
        new("Plain Text", Identity,                     Identity,             StripMarkup, s => [new(s, SegStyle.None)]),
        new("Slack",      x => $"*{x}*",                x => $"<u>{x}</u>",   Identity,    s => Tokenize(EmojiPh(s), SlackRules, Identity)),
        new("Discord",    x => $"**{x}**",              x => $"__{x}__",      Identity,    s => Tokenize(EmojiPh(s), DiscordRules, Identity)),
        new("Markdown",   x => $"**{x}**",              Identity,             Identity,    s => Tokenize(MdLists(s), MdRules, Identity)),
        new("HTML",       x => $"<strong>{x}</strong>", x => $"<u>{x}</u>",   HtmlBreaks,  HtmlDisplay),
        // ⚠️ EVE mail is HTML, but only a little of it. The client renders <b>, <i>, <u>, <br>,
        // <font> and showinfo links, and ignores or mangles the rest — <strong> among them, which
        // is why this cannot just reuse the HTML row above. Line breaks must be <br>: a raw
        // newline in a mail body collapses to a space.
        //
        // The link is the reason to answer in-game at all: showinfo:<typeId> opens the item in
        // the client, so the buyer reads a list they can click rather than one they have to
        // search for. The preview strips the anchor and shows the name, which is what it looks
        // like in the mail.
        new("EVE Mail",   x => $"<b>{x}</b>",           x => $"<u>{x}</u>",   HtmlBreaks,  HtmlDisplay,
            (typeId, text) => $"<a href=\"showinfo:{typeId}\">{text}</a>",
            (argb,   text) => $"<font color=\"#{argb}\">{text}</font>"),
        new("BBCode",     x => $"[b]{x}[/b]",           x => $"[u]{x}[/u]",   Identity,    s => Tokenize(BbLists(s), BbRules, t => BbTag.Replace(t, ""))),
    ];

    public static OutputFormat ByName(string? name) => All.FirstOrDefault(f => f.Name == name) ?? All[0];

    // ── Slack rich_text (real posting, not the preview/clipboard markup above) ─────────────
    // Slack's legacy mrkdwn `text` field has no underline token at all, but Slack's Block Kit
    // rich_text format DOES support it (confirmed live against Slack's API: an unlisted-in-docs
    // but genuinely accepted `style.underline` — Slack echoed it back on the posted message).
    // This walks the SAME "Slack" markup this class produces (*bold*, <u>underline</u>, _italic_,
    // ~strike~, <url|label>/<url>) into a rich_text block, preserving link URLs (which the shared
    // DisplaySeg/preview pipeline discards, since the preview never needs to open a link).
    private static readonly (Regex Re, int TextGroup, int? UrlGroup, SegStyle Style)[] SlackBlockRules =
    [
        (R(@"\*([^*\n]+)\*"),                1, null, SegStyle.Bold),
        (R(@"<u>(.*?)</u>", true),           1, null, SegStyle.Underline),
        (R(@"_([^_\n]+)_"),                  1, null, SegStyle.Italic),
        (R(@"~([^~\n]+)~"),                  1, null, SegStyle.Strike),
        (R(@"<([^>|\n]+)\|([^>\n]+)>"),      2, 1,    SegStyle.Link),
        (R(@"<((?:https?|mailto)[^>\n]+)>"), 1, 1,    SegStyle.Link),
    ];

    /// <summary>Converts Slack-format markup text into a Block Kit "rich_text" block object, one
    /// rich_text_section per line, ready to pass as SlackService.PostMessageAsync's `blocks`.</summary>
    public static object BuildSlackRichTextBlock(string markup)
    {
        var lines = markup.Replace("\r\n", "\n").Split('\n');
        var sections = new List<object>(lines.Length);
        foreach (var line in lines)
        {
            // Protect :emoji_name: shortcodes from the bold/italic/strike/underline matching below
            // FIRST — same reason the preview tokenizer swaps them out via EmojiPh (see ToDisplay
            // above): an emoji name containing an underscore (e.g. :white_small_square:) has no
            // partner underscore of its own, so the _italic_ rule instead pairs it with the
            // underscore in the NEXT unrelated shortcode later on the same line, and silently eats
            // both underscores plus everything between them. Swap each shortcode for a placeholder
            // with no markup-significant characters, tokenize, then restore the literal text —
            // unlike the preview (which shows an emoji placeholder glyph since it can't render real
            // custom emoji anyway), the actual posted text must keep the real shortcode so Slack
            // renders the emoji.
            var emojis = new List<string>();
            var protectedLine = Emoji.Replace(line, m => { emojis.Add(m.Value); return $"{emojis.Count - 1}"; });

            var elements = new List<object>();
            WalkSlackBlock(protectedLine, SegStyle.None, emojis, elements);
            // A blank line (paragraph spacing) has nothing to emit, but Slack's rich_text schema
            // rejects a zero-length text element outright ("must be more than 0 characters") --
            // a single space satisfies that while still rendering as an empty-looking line.
            if (elements.Count == 0) elements.Add(new { type = "text", text = " " });
            sections.Add(new { type = "rich_text_section", elements });
        }
        return new { type = "rich_text", elements = sections };
    }

    private static string RestoreEmojis(string s, List<string> emojis) =>
        emojis.Count == 0 ? s : Regex.Replace(s, "(\\d+)", m => emojis[int.Parse(m.Groups[1].Value)]);

    private static void WalkSlackBlock(string text, SegStyle inherited, List<string> emojis, List<object> o)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            Match? best = null;
            (Regex Re, int TextGroup, int? UrlGroup, SegStyle Style)? br = null;
            foreach (var r in SlackBlockRules)
            {
                var m = r.Re.Match(text, pos);
                if (m.Success && (best is null || m.Index < best.Index)) { best = m; br = r; }
            }
            if (best is null || br is null) { EmitSlackText(o, text[pos..], inherited, emojis); return; }
            if (best.Index > pos) EmitSlackText(o, text[pos..best.Index], inherited, emojis);

            var inner = best.Groups[br.Value.TextGroup].Value;
            if (br.Value.Style == SegStyle.Link)
                o.Add(new { type = "link", url = RestoreEmojis(best.Groups[br.Value.UrlGroup!.Value].Value, emojis), text = RestoreEmojis(inner, emojis) });
            else
                WalkSlackBlock(inner, inherited | br.Value.Style, emojis, o);   // recurse — styles can nest
            pos = best.Index + best.Length;
        }
    }

    private static void EmitSlackText(List<object> o, string t, SegStyle style, List<string> emojis)
    {
        if (t.Length == 0) return;
        t = RestoreEmojis(t, emojis);
        if (style == SegStyle.None) { o.Add(new { type = "text", text = t }); return; }
        var styleObj = new Dictionary<string, object>();
        if (style.HasFlag(SegStyle.Bold))      styleObj["bold"]      = true;
        if (style.HasFlag(SegStyle.Italic))    styleObj["italic"]    = true;
        if (style.HasFlag(SegStyle.Strike))    styleObj["strike"]    = true;
        if (style.HasFlag(SegStyle.Underline)) styleObj["underline"] = true;
        o.Add(new { type = "text", text = t, style = styleObj });
    }
}

// ── Posting row (top level) ─────────────────────────────────────────────────────
public class SalePostingRow : ReactiveObject
{
    public bool IsPosting => true;
    public bool IsSection => false;
    public bool IsItem    => false;

    // Warm/amber tint so posting rows stand out from sections (blue) and items (near-black).
    public IBrush RowBackground { get; } = new SolidColorBrush(Color.Parse("#241c10"));

    public SalePosting Model { get; private set; }
    public int PostingId => Model.Id;
    public List<SalePostingSectionRow> Sections { get; set; } = [];

    private string _postingName = "";
    public string PostingName { get => _postingName; private set => this.RaiseAndSetIfChanged(ref _postingName, value); }

    private string _scopeDisplay = "";
    public string ScopeDisplay { get => _scopeDisplay; private set => this.RaiseAndSetIfChanged(ref _scopeDisplay, value); }

    // ── The scope's location, as a link ───────────────────────────────────────
    //
    // ScopeDisplay reads "Jita IV - Moon 4 · Station". The name half points somewhere, the scope
    // word does not, so the view renders the two separately and only the name links. Same split
    // as the Inventory Levels group line.
    private string _locationName = "";
    private string _scope        = "Everywhere";
    private long?  _locationId;

    public string LocationName    => _locationName;
    public string ScopeSuffix     => _scope == "Everywhere" ? "" : $" · {_scope}";
    public bool   HasLocationLink => _locationId is > 0 && _locationName.Length > 0
                                  && _scope != "Everywhere";

    /// <summary>⚠️ A "Station" scope holds an NPC station or a player structure in the one column,
    /// so it splits on int range: SdeStations keys on an int, which a structure id cannot fit.
    /// </summary>
    public void OpenLocation()
    {
        var id = _locationId ?? 0;
        if (id <= 0) return;

        switch (_scope)
        {
            case "Region":  EntityNavigator.Instance.Region((int)id); break;
            case "System":  EntityNavigator.Instance.System((int)id); break;
            case "Station" when id <= int.MaxValue:
                EntityNavigator.Instance.Entity(EntityKind.Station, id); break;
            case "Station":
                EntityNavigator.Instance.Structure(id); break;
        }
    }

    private string _pricingDisplay = "";
    public string PricingDisplay { get => _pricingDisplay; private set => this.RaiseAndSetIfChanged(ref _pricingDisplay, value); }

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set { this.RaiseAndSetIfChanged(ref _isExpanded, value); this.RaisePropertyChanged(nameof(ExpanderIcon)); } }
    public string ExpanderIcon => IsExpanded ? "▼" : "▶";

    public ReactiveCommand<Unit, Unit> ToggleCommand     { get; }
    public ReactiveCommand<Unit, Unit> EditCommand       { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand     { get; }
    public ReactiveCommand<Unit, Unit> AddSectionCommand { get; }

    public SalePostingRow(SalePosting model, Action toggle, Func<Task> edit, Func<Task> delete, Func<Task> addSection)
    {
        Model             = model;
        ToggleCommand     = ReactiveCommand.Create(toggle);
        EditCommand       = ReactiveCommand.CreateFromTask(edit);
        DeleteCommand     = ReactiveCommand.CreateFromTask(delete);
        AddSectionCommand = ReactiveCommand.CreateFromTask(addSection);
        ApplyData(model);
    }

    public void ApplyData(SalePosting m)
    {
        Model          = m;
        PostingName    = m.Name;
        ScopeDisplay   = m.Scope == "Everywhere" ? "Everywhere" : $"{m.LocationName} · {m.Scope}";
        _scope         = m.Scope;
        _locationId    = m.LocationId;
        _locationName  = m.LocationName;
        this.RaisePropertyChanged(nameof(LocationName));
        this.RaisePropertyChanged(nameof(ScopeSuffix));
        this.RaisePropertyChanged(nameof(HasLocationLink));
        string basis   = m.PricingBasis switch
        {
            "Contract" => "Contract",
            "Market"   => $"Market: {m.MarketStationName} ({m.MarketPriceType})",
            _          => "Build",
        };
        PricingDisplay = $"{basis} × {m.PricePercent:0.#}%";
    }

    // ── Posting-level totals (weighted by each item's effective In Stock) ──
    private double? _totalSaleValue, _totalProfit, _totalProfitPct;
    public string TotalSaleValueText => SalePostFmt.Isk(_totalSaleValue);
    public string TotalProfitText    => SalePostFmt.Isk(_totalProfit);
    public string TotalProfitPctText => SalePostFmt.Pct(_totalProfitPct);
    public IBrush TotalProfitColor =>
        _totalProfit is not double p ? SalePostFmt.Neutral : p >= 0 ? SalePostFmt.Green : SalePostFmt.Red;

    public void RecomputeTotals()
    {
        // Each section totals its own items (weighted by effective In Stock + In Build); the
        // posting is the sum of its sections.
        double saleValue = 0, cost = 0;
        foreach (var s in Sections)
        {
            s.RecomputeTotals();
            saleValue += s.SaleValueRaw;
            cost      += s.CostRaw;
        }
        _totalSaleValue = saleValue;
        _totalProfit    = saleValue - cost;
        _totalProfitPct = cost != 0 ? (saleValue - cost) / cost * 100 : null;
        this.RaisePropertyChanged(nameof(TotalSaleValueText));
        this.RaisePropertyChanged(nameof(TotalProfitText));
        this.RaisePropertyChanged(nameof(TotalProfitPctText));
        this.RaisePropertyChanged(nameof(TotalProfitColor));
    }
}

// ── Section row (middle level) ──────────────────────────────────────────────────
public class SalePostingSectionRow : ReactiveObject
{
    public bool IsPosting => false;
    public bool IsSection => true;
    public bool IsItem    => false;

    // Blue tint so section rows stand out from postings (amber) and items (near-black).
    public IBrush RowBackground { get; } = new SolidColorBrush(Color.Parse("#16162e"));

    public SalePostingSection Model { get; private set; }
    public int SectionId => Model.Id;
    public int PostingId => Model.PostingId;
    public List<SalePostingItemRow> AllItems { get; set; } = [];

    private string _sectionName;
    public string SectionName { get => _sectionName; private set => this.RaiseAndSetIfChanged(ref _sectionName, value); }

    private string _overrideSummary = "";
    public string OverrideSummary { get => _overrideSummary; private set => this.RaiseAndSetIfChanged(ref _overrideSummary, value); }

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set { this.RaiseAndSetIfChanged(ref _isExpanded, value); this.RaisePropertyChanged(nameof(ExpanderIcon)); } }
    public string ExpanderIcon => IsExpanded ? "▼" : "▶";

    public ReactiveCommand<Unit, Unit> ToggleCommand  { get; }
    public ReactiveCommand<Unit, Unit> EditCommand    { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand  { get; }
    public ReactiveCommand<Unit, Unit> AddItemCommand { get; }

    public SalePostingSectionRow(SalePostingSection model, Action toggle, Func<Task> edit, Func<Task> delete, Func<Task> addItem)
    {
        Model          = model;
        _sectionName   = model.Name;
        ToggleCommand  = ReactiveCommand.Create(toggle);
        EditCommand    = ReactiveCommand.CreateFromTask(edit);
        DeleteCommand  = ReactiveCommand.CreateFromTask(delete);
        AddItemCommand = ReactiveCommand.CreateFromTask(addItem);
        ApplyData(model);
    }

    public void ApplyData(SalePostingSection m)
    {
        Model       = m;
        SectionName = m.Name;

        var parts = new List<string>();
        if (m.OverrideScope)
            parts.Add("scope: " + (m.Scope == "Everywhere" ? "Everywhere" : m.LocationName));
        if (m.OverridePricing)
        {
            string b = m.PricingBasis switch
            {
                "Contract" => "Contract",
                "Market"   => $"Market:{m.MarketStationName}",
                _          => "Build",
            };
            parts.Add($"{b} ×{m.PricePercent:0.#}%");
        }
        if (m.OverrideOnlyPackaged)
            parts.Add(m.OnlyPackaged ? "packaged only" : "all items");
        OverrideSummary = parts.Count > 0 ? "⚙ " + string.Join(" · ", parts) : "";
    }

    // ── Section-level totals (weighted by each item's effective In Stock + In Build) ──
    private double? _totalSaleValue, _totalProfit, _totalProfitPct;
    public double SaleValueRaw { get; private set; }
    public double CostRaw      { get; private set; }
    public string TotalSaleValueText => SalePostFmt.Isk(_totalSaleValue);
    public string TotalProfitText    => SalePostFmt.Isk(_totalProfit);
    public string TotalProfitPctText => SalePostFmt.Pct(_totalProfitPct);
    public IBrush TotalProfitColor =>
        _totalProfit is not double p ? SalePostFmt.Neutral : p >= 0 ? SalePostFmt.Green : SalePostFmt.Red;

    public void RecomputeTotals()
    {
        double saleValue = 0, cost = 0;
        foreach (var it in AllItems)
        {
            long qty = it.EffectiveInStock + it.EffectiveInBuild;
            if (qty <= 0) continue;
            if (it.SalePriceValue  is double sp) saleValue += sp * qty;
            if (it.CurrentUnitCost is double c)  cost      += c  * qty;
        }
        SaleValueRaw    = saleValue;
        CostRaw         = cost;
        _totalSaleValue = saleValue;
        _totalProfit    = saleValue - cost;
        _totalProfitPct = cost != 0 ? (saleValue - cost) / cost * 100 : null;
        this.RaisePropertyChanged(nameof(TotalSaleValueText));
        this.RaisePropertyChanged(nameof(TotalProfitText));
        this.RaisePropertyChanged(nameof(TotalProfitPctText));
        this.RaisePropertyChanged(nameof(TotalProfitColor));
    }
}

// ── Item row (leaf) ─────────────────────────────────────────────────────────────
public class SalePostingItemRow : ReactiveObject
{
    public bool IsPosting => false;
    public bool IsSection => false;
    public bool IsItem    => true;

    public IBrush RowBackground { get; } = new SolidColorBrush(Color.Parse("#0d0d12"));

    private readonly SalePostingService _svc;

    public int    ItemId    { get; }
    public int    SectionId { get; }
    public int    TypeId    { get; }
    public string TypeName  { get; private set; }

    public bool HasItemLink => TypeId > 0 && TypeName.Length > 0;
    public void OpenItem() => EntityNavigator.Instance.Item(TypeId);

    public SalePostingItemRow(SalePostingItem model, string typeName, SalePostingService svc)
    {
        _svc            = svc;
        ItemId          = model.Id;
        SectionId       = model.SectionId;
        TypeId          = model.TypeId;
        TypeName        = typeName;
        _nameOverride   = model.NameOverride;
        _namePrefix     = model.NamePrefix;
        _inStockOverride  = model.InStockOverride;
        _inBuildOverride  = model.InBuildOverride;
        _reservedOverride = model.ReservedOverride;
    }

    // ── Editable: name override + prefix (persist on commit) ──
    private string? _nameOverride;
    public string? NameOverride
    {
        get => _nameOverride;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v == _nameOverride) return;
            this.RaiseAndSetIfChanged(ref _nameOverride, v);
            _ = _svc.UpdateItemNameOverrideAsync(ItemId, v);
        }
    }

    private string? _namePrefix;
    public string? NamePrefix
    {
        get => _namePrefix;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v == _namePrefix) return;
            this.RaiseAndSetIfChanged(ref _namePrefix, v);
            _ = _svc.UpdateItemNamePrefixAsync(ItemId, v);
        }
    }

    // ── Editable: quantity overrides (string-backed for clean null/empty handling) ──
    private int? _inStockOverride;
    public string InStockOverrideText
    {
        get => _inStockOverride?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            var p = ParseNullableInt(value);
            if (p == _inStockOverride) return;
            _inStockOverride = p; this.RaisePropertyChanged();
            _ = _svc.UpdateItemInStockOverrideAsync(ItemId, p);
        }
    }

    private int? _inBuildOverride;
    public string InBuildOverrideText
    {
        get => _inBuildOverride?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            var p = ParseNullableInt(value);
            if (p == _inBuildOverride) return;
            _inBuildOverride = p; this.RaisePropertyChanged();
            _ = _svc.UpdateItemInBuildOverrideAsync(ItemId, p);
        }
    }

    private int? _reservedOverride;
    public string ReservedOverrideText
    {
        get => _reservedOverride?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            var p = ParseNullableInt(value);
            if (p == _reservedOverride) return;
            _reservedOverride = p; this.RaisePropertyChanged();
            _ = _svc.UpdateItemReservedOverrideAsync(ItemId, p);
        }
    }

    // ── Computed quantities (base, pre-override) ──
    private long _inStock, _inBuild, _reserved;
    public string InStockText  => SalePostFmt.Qty(_inStock);
    public string InBuildText  => SalePostFmt.Qty(_inBuild);
    public string ReservedText => SalePostFmt.Qty(_reserved);

    // ── Prices ──
    private double? _buildCost, _marketValue, _contractValue, _salePrice;
    public string BuildCostText     => SalePostFmt.Isk(_buildCost);
    public string MarketValueText   => SalePostFmt.Isk(_marketValue);
    public string ContractValueText => SalePostFmt.Isk(_contractValue);
    public string SalePriceText     => SalePostFmt.Isk(_salePrice);

    // ── Profit (vs the selected cost basis) ──
    private double? _profit, _profitPct;
    public string ProfitText    => SalePostFmt.Isk(_profit);
    public string ProfitPctText => SalePostFmt.Pct(_profitPct);
    public IBrush ProfitColor => _profit is not double p ? SalePostFmt.Neutral : p >= 0 ? SalePostFmt.Green : SalePostFmt.Red;

    // ── Earliest job completion (shown only when out of stock but building, and the posting opts in) ──
    private DateTimeOffset? _earliestJobEnd;
    private bool _showCompletion;
    // Through the renderer, so the grid column and the posting text cannot disagree about when
    // a date is worth showing — the rule has three parts and two copies of it would drift.
    public string CompletionDateText => SalePostingRenderer.CompletionText(ToView(), _showCompletion);

    /// <summary>This row as plain data for <see cref="SalePostingRenderer"/>.</summary>
    internal PostingItemView ToView() => new(
        TypeId, TypeName, _nameOverride, _namePrefix,
        _inStock, _inBuild, _reserved,
        _inStockOverride, _inBuildOverride, _reservedOverride,
        _salePrice, _earliestJobEnd);

    public void ApplyCalc(SalePostingCalc c, string basis, bool showCompletion)
    {
        TypeName        = c.Name; this.RaisePropertyChanged(nameof(TypeName));
        _inStock        = c.InStock;
        _inBuild        = c.InBuild;
        _reserved       = c.Reserved;
        _buildCost      = c.BuildCost;
        _marketValue    = c.MarketValue;
        _contractValue  = c.ContractValue;
        _salePrice      = c.SalePrice;
        _earliestJobEnd = c.EarliestJobEnd;
        _showCompletion = showCompletion;
        this.RaisePropertyChanged(nameof(InStockText));
        this.RaisePropertyChanged(nameof(InBuildText));
        this.RaisePropertyChanged(nameof(ReservedText));
        this.RaisePropertyChanged(nameof(BuildCostText));
        this.RaisePropertyChanged(nameof(MarketValueText));
        this.RaisePropertyChanged(nameof(ContractValueText));
        this.RaisePropertyChanged(nameof(SalePriceText));
        this.RaisePropertyChanged(nameof(CompletionDateText));
        ApplyBasis(basis);
    }

    public void ApplyBasis(string basis)
    {
        _currentCost = basis switch { "Market" => _marketValue, "Contract" => _contractValue, _ => _buildCost };
        _profit    = _salePrice is double s && _currentCost is double c ? s - c : null;
        _profitPct = _profit is double pr && _currentCost is double c2 && c2 != 0 ? pr / c2 * 100 : null;
        this.RaisePropertyChanged(nameof(ProfitText));
        this.RaisePropertyChanged(nameof(ProfitPctText));
        this.RaisePropertyChanged(nameof(ProfitColor));
    }

    // ── Exposed for posting-level totals ──
    private double? _currentCost;
    public long    EffectiveInStock  => _inStockOverride ?? _inStock;
    public long    EffectiveInBuild  => _inBuildOverride ?? _inBuild;
    public long    EffectiveReserved => _reservedOverride ?? _reserved;
    public double? SalePriceValue    => _salePrice;
    public double? CurrentUnitCost   => _currentCost;

    private static int? ParseNullableInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
    }
}

// ── Window view-model ───────────────────────────────────────────────────────────
public class SalePostingViewModel : ReactiveObject, IPeriodicRefresh
{
    /// <summary>Set the first time this tool is opened; until then its refresh timer is a
    /// no-op. See IPeriodicRefresh.</summary>
    public bool AutoRefreshEnabled { get; set; }

    private readonly SalePostingService _svc;
    private readonly BatchAddService?   _batchSvc;
    private readonly SlackService?      _slack;
    private readonly ExportFormatSettings? _exportFormat;

    private List<SalePostingRow> _allPostings = [];

    public ObservableCollection<object> GridRows { get; } = [];

    private object? _selectedRow;
    public object? SelectedRow
    {
        get => _selectedRow;
        set { this.RaiseAndSetIfChanged(ref _selectedRow, value); this.RaisePropertyChanged(nameof(IsItemRowSelected)); }
    }
    public bool IsItemRowSelected => _selectedRow is SalePostingItemRow;

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private bool _hasAnyPosting;
    public bool HasAnyPosting { get => _hasAnyPosting; private set => this.RaiseAndSetIfChanged(ref _hasAnyPosting, value); }

    // ── Posting tab (rendered output) ──
    public ObservableCollection<SalePostingRow> Postings       { get; } = [];
    public ObservableCollection<RenderedBlock>  RenderedBlocks { get; } = [];

    private SalePostingRow? _selectedPostingForTab;
    public SalePostingRow? SelectedPostingForTab
    {
        get => _selectedPostingForTab;
        set { this.RaiseAndSetIfChanged(ref _selectedPostingForTab, value); _ = RenderSelectedAsync(); }
    }

    public IReadOnlyList<string> FormatOptions { get; } = OutputFormat.All.Select(f => f.Name).ToList();
    // Shared with Corp Activity's Top 10 and Monthly Summary, and remembered across
    // sessions — see ExportFormatSettings for why it is one setting rather than three.
    private string _selectedFormat = ExportFormatSettings.Default;
    public string SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedFormat, value ?? ExportFormatSettings.Default);
            if (_exportFormat is not null) _exportFormat.Format = _selectedFormat;
            _ = RenderSelectedAsync();
        }
    }

    // Profit basis toggle (mirrors the Sales Tracker), default Build.
    public IReadOnlyList<string> ProfitBasisOptions { get; } = ["Build", "Market", "Contract"];
    private string _selectedProfitBasis = "Build";
    public string SelectedProfitBasis
    {
        get => _selectedProfitBasis;
        set { this.RaiseAndSetIfChanged(ref _selectedProfitBasis, value ?? "Build"); ApplyProfitBasis(); }
    }

    public ReactiveCommand<Unit, Unit> AddPostingCommand         { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand            { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedItemCommand { get; }
    public ReactiveCommand<Unit, Unit> AddFromMarketGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenInItemBrowserCommand  { get; }
    public ReactiveCommand<Unit, Unit> RenderRefreshCommand      { get; }
    public ReactiveCommand<Unit, Unit> PostToSlackCommand        { get; }

    // Dialog delegates — wired by the view (ShowDialog decoupling).
    public Func<Task<PostingDialogResult?>>?                        ShowAddPostingDialog;
    public Func<SalePostingRow, Task<PostingDialogResult?>>?        ShowEditPostingDialog;
    public Func<Task<SectionDialogResult?>>?                        ShowAddSectionDialog;
    public Func<SalePostingSectionRow, Task<SectionDialogResult?>>? ShowEditSectionDialog;
    public Func<Task<AddItemDialogResult?>>?                        ShowAddItemDialog;
    public Func<Task<MarketGroupPickerResult?>>?                    ShowMarketGroupPickerDialog;
    public Action<int, string>?                                     OpenInItemBrowser;

    public SalePostingViewModel(SalePostingService svc, IDbContextFactory<AppDbContext> dbFactory,
        BatchAddService? batchSvc = null, SlackService? slack = null,
        ExportFormatSettings? exportFormat = null)
    {
        _svc          = svc;
        _batchSvc     = batchSvc;
        _slack        = slack;
        _exportFormat = exportFormat;

        // Restored before the dropdown binds, so the saved choice is what the user sees.
        if (exportFormat is not null) _selectedFormat = exportFormat.Format;

        AddPostingCommand         = ReactiveCommand.CreateFromTask(AddPostingAsync);
        RefreshCommand            = ReactiveCommand.CreateFromTask(RefreshAllAsync);
        DeleteSelectedItemCommand = ReactiveCommand.CreateFromTask(DeleteSelectedItemAsync);
        AddFromMarketGroupCommand = ReactiveCommand.CreateFromTask(AddFromMarketGroupAsync);
        OpenInItemBrowserCommand  = ReactiveCommand.Create(OpenSelectedInItemBrowser);
        RenderRefreshCommand      = ReactiveCommand.CreateFromTask(RenderSelectedAsync);
        PostToSlackCommand        = ReactiveCommand.CreateFromTask(PostSelectedToSlackAsync);

        _ = InitAsync();

        // ⚠️ Gated and labelled — see InvLevelViewModel for why.
        Observable.Interval(TimeSpan.FromMinutes(1))
            .Where(_ => AutoRefreshEnabled)
            .ObserveOnUi("SalePosting.AutoRefresh")
            .Subscribe(tick => { _ = RefreshAllAsync(); });
    }

    public Task<IReadOnlyList<InvTypeResult>>  SearchTypesAsync(string text) => _svc.SearchTypesAsync(text);
    public Task<IReadOnlyList<LocationOption>> SearchLocationsAsync(string scope, string text) => _svc.SearchLocationsAsync(scope, text);
    public Task<List<StationOption>>           GetMarketStationsAsync() => _svc.GetMarketStationsAsync();
    public BatchAddService? GetBatchAddService() => _batchSvc;

    public async Task<List<PostBlockDraft>> GetPostsAsync(int postingId)
        => (await _svc.LoadPostsAsync(postingId))
            .Select(p => new PostBlockDraft(p.PostType, p.Name, p.StaticContent, p.Header, p.Footer)).ToList();

    // ── Export / import ───────────────────────────────────────────────────────
    //
    // Thin: the service owns the file format, because it owns every other way a posting is
    // written. These exist so the view can hand over a stream from the file picker and get a
    // status line back.

    public async Task ExportPostingAsync(int postingId, Stream stream)
    {
        try
        {
            await _svc.ExportPostingAsync(postingId, stream);
            StatusText = "Posting exported.";
        }
        catch (Exception ex) { StatusText = $"Export failed: {ex.Message}"; }
    }

    public async Task ImportPostingAsync(Stream stream)
    {
        try
        {
            var posting = await _svc.ImportPostingAsync(stream);
            if (posting is null) { StatusText = "That file is not a sale posting."; return; }

            // Reloaded rather than appended: the import wrote sections, items and post blocks,
            // and the grid is built from all of them. Rebuilding the one posting by hand here
            // would be a second implementation of what InitAsync already does correctly.
            await InitAsync();
            StatusText = $"Imported \"{posting.Name}\".";
        }
        catch (Exception ex) { StatusText = $"Import failed: {ex.Message}"; }
    }

    // ── Load ──────────────────────────────────────────────────────────────────
    private async Task InitAsync()
    {
        try
        {
            StatusText = "Loading…";
            var postings = await _svc.LoadPostingsAsync();
            var sections = await _svc.LoadSectionsAsync();

            _allPostings = postings.Select(MakePostingRow).ToList();
            foreach (var pr in _allPostings)
            {
                pr.Sections = sections.Where(s => s.PostingId == pr.PostingId)
                    .Select(s => MakeSectionRow(pr, s)).ToList();
                foreach (var sr in pr.Sections)
                {
                    var items = await _svc.LoadItemsAsync(sr.SectionId);
                    // Names are placeholders here; ComputePostingAsync fills them from ComputeAsync.
                    sr.AllItems = items
                        .Select(i => new SalePostingItemRow(i, $"Type {i.TypeId}", _svc))
                        .ToList();
                }
                await ComputePostingAsync(pr);
                SortPostingItems(pr);
            }

            RebuildGridRows();
            SyncPostings();
            StatusText = _allPostings.Count == 0 ? "No postings yet — add one to get started." : "";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    private async Task RefreshAllAsync()
    {
        foreach (var pr in _allPostings) await ComputePostingAsync(pr);
        StatusText = "";
    }

    private async Task ComputePostingAsync(SalePostingRow pr)
    {
        // Each section resolves its own effective scope + pricing (its overrides, else the posting's).
        foreach (var s in pr.Sections)
        {
            var typeIds = s.AllItems.Select(i => i.TypeId).Distinct().ToList();
            if (typeIds.Count == 0) continue;
            var eff  = BuildEffectivePosting(pr.Model, s.Model);
            var calc = await _svc.ComputeAsync(eff, typeIds);
            foreach (var it in s.AllItems)
                if (calc.TryGetValue(it.TypeId, out var c))
                    it.ApplyCalc(c, _selectedProfitBasis, pr.Model.IncludeCompletionDate);
        }
        pr.RecomputeTotals();
    }

    // Moved to the service: the headless render resolves overrides the same way, and two copies
    // of this would let a mailed listing price a section differently from the one on screen.
    private static SalePosting BuildEffectivePosting(SalePosting p, SalePostingSection s)
        => SalePostingService.EffectiveFor(p, s);

    private void ApplyProfitBasis()
    {
        foreach (var pr in _allPostings)
        {
            foreach (var s in pr.Sections)
                foreach (var it in s.AllItems)
                    it.ApplyBasis(_selectedProfitBasis);
            pr.RecomputeTotals();
        }
    }

    private void SortPostingItems(SalePostingRow pr)
    {
        foreach (var s in pr.Sections)
            s.AllItems = s.AllItems.OrderBy(i => i.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void RebuildGridRows()
    {
        GridRows.Clear();
        foreach (var p in _allPostings)
        {
            GridRows.Add(p);
            if (!p.IsExpanded) continue;
            foreach (var s in p.Sections)
            {
                GridRows.Add(s);
                if (!s.IsExpanded) continue;
                foreach (var it in s.AllItems) GridRows.Add(it);
            }
        }
        HasAnyPosting = _allPostings.Count > 0;
    }

    // ── Posting-tab rendering ───────────────────────────────────────────────────
    private void SyncPostings()
    {
        var keepId = _selectedPostingForTab?.PostingId;
        Postings.Clear();
        foreach (var p in _allPostings) Postings.Add(p);
        if (keepId is int id) _selectedPostingForTab = _allPostings.FirstOrDefault(p => p.PostingId == id);
    }

    private async Task RenderSelectedAsync()
    {
        RenderedBlocks.Clear();
        if (_selectedPostingForTab is not SalePostingRow pr) return;
        var fmt   = OutputFormat.ByName(_selectedFormat);
        var posts = await _svc.LoadPostsAsync(pr.PostingId);
        foreach (var post in posts.OrderBy(p => p.Ordinal))
        {
            // Rendered through the shared renderer, off a snapshot of the rows. The tool and the
            // mail responder must produce the same listing from the same posting, and the surest
            // way to guarantee that is for there to be one implementation.
            var body = SalePostingRenderer.Render(Snapshot(pr), fmt, post);
            var clip = fmt.Finalize(body);        // clipboard: raw markup + literal :emoji:
            var segs = fmt.ToDisplay(clip);       // preview: real bold + emoji placeholder

            // Slack's own composer has no typed/pasted syntax for underline (toolbar/shortcut
            // only — confirmed there's no character sequence for it, unlike *bold*/_italic_), so
            // this app's internal <u>...</u> marker would show up as literal, confusing text if
            // manually copy-pasted into Slack. Strip it from what gets copied (keep the inner
            // text) for that one target — the preview above already parsed the tags for display,
            // and the real "Post to Slack" button re-renders independently of this copy text, so
            // dropping it here doesn't touch either.
            var clipboardText = _selectedFormat == "Slack"
                ? Regex.Replace(clip, "</?u>", "", RegexOptions.IgnoreCase)
                : clip;
            RenderedBlocks.Add(new RenderedBlock($"{post.Name}  ·  {post.PostType}", clipboardText, segs));
        }
    }

    // ── Slack ────────────────────────────────────────────────────────────────
    // The post button only shows once a token and a Sale Posting channel are configured.
    // Re-checked when the Settings window closes (see MainWindow.OpenSettingsAsync).

    public bool IsSlackConfigured => _slack?.IsConfigured(SlackService.AreaSalePosting) == true;

    public string SlackChannelText =>
        _slack?.ChannelName(SlackService.AreaSalePosting) is { Length: > 0 } n ? $"#{n}" : "";

    private string _slackStatus = "";
    public string SlackStatus { get => _slackStatus; private set => this.RaiseAndSetIfChanged(ref _slackStatus, value); }

    private bool _isPostingToSlack;
    public bool IsPostingToSlack { get => _isPostingToSlack; private set => this.RaiseAndSetIfChanged(ref _isPostingToSlack, value); }

    public void RefreshSlackState()
    {
        this.RaisePropertyChanged(nameof(IsSlackConfigured));
        this.RaisePropertyChanged(nameof(SlackChannelText));
    }

    // A posting is a deliberate, low-volume post. Re-posting inside this window asks for
    // confirmation first, so a stray double-click doesn't spam the channel. Keyed per posting
    // (not just the area) so posting one listing doesn't gate a re-post of a different one.
    private static readonly TimeSpan SlackRepostWindow = TimeSpan.FromHours(24);

    /// <summary>Asked before re-posting within the cooldown; return true to post anyway.</summary>
    public Func<string, Task<bool>>? ConfirmSlackRepost { get; set; }

    /// <summary>
    /// Posts the selected posting's blocks to Slack in order: the first (Ordinal 0) becomes a
    /// standalone message in the configured channel, and every block after that is posted as a
    /// threaded reply under it — mirroring how SalePostingPost already models a posting's blocks.
    /// </summary>
    private async Task PostSelectedToSlackAsync()
    {
        if (_slack is null) return;
        if (_selectedPostingForTab is not SalePostingRow pr) { SlackStatus = "Select a posting first."; return; }
        var channel = _slack.ChannelId(SlackService.AreaSalePosting);
        if (string.IsNullOrEmpty(channel)) { SlackStatus = "No Slack channel configured."; return; }

        var guardKey = $"{SlackService.AreaSalePosting}.{pr.PostingId}";
        if (_slack.LastPostAt(guardKey) is { } last
            && DateTimeOffset.UtcNow - last < SlackRepostWindow
            && ConfirmSlackRepost is not null)
        {
            var confirmed = await ConfirmSlackRepost(
                $"\"{pr.PostingName}\" was already posted to Slack {NotificationSummary.Age(last)}.\n\n" +
                "Post it again?");
            if (!confirmed) { SlackStatus = "Post cancelled."; return; }
        }

        IsPostingToSlack = true;
        SlackStatus = "Posting to Slack…";
        try
        {
            var fmt   = OutputFormat.ByName("Slack");
            var posts = (await _svc.LoadPostsAsync(pr.PostingId)).OrderBy(p => p.Ordinal).ToList();
            if (posts.Count == 0) { SlackStatus = "Nothing to post — this posting has no post blocks."; return; }

            string? threadTs = null;
            int posted = 0;
            foreach (var post in posts)
            {
                var markup = fmt.Finalize(SalePostingRenderer.Render(Snapshot(pr), fmt, post));
                if (string.IsNullOrWhiteSpace(markup)) continue;

                // Slack's legacy mrkdwn `text` field can't underline, so the real message is a
                // Block Kit rich_text block (built from the same *bold*/__underline__ markup);
                // `text` is still sent as the notification/accessibility fallback Slack expects.
                var fallbackText = OutputFormat.ByName("Plain Text").Finalize(markup);
                var block        = OutputFormat.BuildSlackRichTextBlock(markup);
                var res = await _slack.PostMessageAsync(channel, fallbackText, threadTs, blocks: new[] { block });
                if (!res.Ok) { SlackStatus = $"Slack post failed on \"{post.Name}\": {res.Error}"; return; }
                threadTs ??= res.Ts;
                posted++;
            }

            if (posted == 0) { SlackStatus = "Nothing to post — all post blocks were empty."; return; }
            await _slack.SetLastPostAsync(guardKey, DateTimeOffset.UtcNow);
            SlackStatus = $"Posted to {SlackChannelText} — {DateTimeOffset.Now:t}";
        }
        finally { IsPostingToSlack = false; }
    }

    /// <summary>
    /// The grid's rows as plain data, for <see cref="SalePostingRenderer"/>.
    ///
    /// <para>Taken from the rows rather than re-read from the database on purpose: the tool
    /// renders what is on screen, including edits typed a moment ago that have been persisted
    /// but not reloaded. A re-read would show the posting as it was, which is not what the person
    /// looking at it is about to copy.</para>
    /// </summary>
    private static PostingView Snapshot(SalePostingRow pr)
    {
        var m = pr.Model;
        return new PostingView(
            m.ShowInStock, m.ShowInBuild, m.ShowReserved, m.IncludeCompletionDate,
            pr.Sections.Select(s => new PostingSectionView(
                s.SectionName, s.Model.Prefix,
                s.AllItems.Select(i => i.ToView()).ToList())).ToList());
    }

    // ── Posting factory + CRUD ──────────────────────────────────────────────────
    private SalePostingRow MakePostingRow(SalePosting p)
    {
        SalePostingRow row = null!;
        row = new SalePostingRow(p,
            toggle:     () => { row.IsExpanded = !row.IsExpanded; RebuildGridRows(); },
            edit:       () => EditPostingAsync(row),
            delete:     () => DeletePostingAsync(row),
            addSection: () => AddSectionAsync(row));
        return row;
    }

    private async Task AddPostingAsync()
    {
        if (ShowAddPostingDialog is null) return;
        var r = await ShowAddPostingDialog();
        if (r is null) return;
        var model = await _svc.AddPostingAsync(r);
        await _svc.ReplacePostsAsync(model.Id, r.Posts);
        var row = MakePostingRow(model);
        _allPostings.Add(row);
        _allPostings = _allPostings.OrderBy(p => p.PostingName, StringComparer.OrdinalIgnoreCase).ToList();
        RebuildGridRows();
        SyncPostings();
    }

    private async Task EditPostingAsync(SalePostingRow row)
    {
        if (ShowEditPostingDialog is null) return;
        var r = await ShowEditPostingDialog(row);
        if (r is null) return;
        await _svc.UpdatePostingAsync(row.PostingId, r);
        await _svc.ReplacePostsAsync(row.PostingId, r.Posts);

        // Apply the edit to the in-memory model so display + compute reflect it.
        var m = row.Model;
        m.Name = r.Name; m.Scope = r.Scope; m.LocationId = r.LocationId; m.LocationName = r.LocationName;
        m.PricingBasis = r.PricingBasis; m.PricePercent = r.PricePercent;
        m.MarketStationId = r.MarketStationId; m.MarketStationName = r.MarketStationName; m.MarketPriceType = r.MarketPriceType;
        m.ShowInStock = r.ShowInStock; m.ShowInBuild = r.ShowInBuild; m.ShowReserved = r.ShowReserved;
        m.IncludeCompletionDate = r.IncludeCompletionDate; m.OnlyPackaged = r.OnlyPackaged;
        row.ApplyData(m);

        await ComputePostingAsync(row);
        RebuildGridRows();
    }

    private async Task DeletePostingAsync(SalePostingRow row)
    {
        await _svc.DeletePostingAsync(row.PostingId);
        _allPostings.Remove(row);
        RebuildGridRows();
        SyncPostings();
        if (_selectedPostingForTab == row) SelectedPostingForTab = null;
    }

    // ── Section factory + CRUD ──────────────────────────────────────────────────
    private SalePostingSectionRow MakeSectionRow(SalePostingRow parent, SalePostingSection s)
    {
        SalePostingSectionRow row = null!;
        row = new SalePostingSectionRow(s,
            toggle:  () => { row.IsExpanded = !row.IsExpanded; RebuildGridRows(); },
            edit:    () => EditSectionAsync(parent, row),
            delete:  () => DeleteSectionAsync(parent, row),
            addItem: () => AddItemToSectionAsync(parent, row));
        return row;
    }

    private async Task AddSectionAsync(SalePostingRow parent)
    {
        if (ShowAddSectionDialog is null) return;
        var r = await ShowAddSectionDialog();
        if (r is null) return;
        var model = await _svc.AddSectionAsync(parent.PostingId, r);
        parent.Sections = parent.Sections.Append(MakeSectionRow(parent, model))
            .OrderBy(s => s.SectionName, StringComparer.OrdinalIgnoreCase).ToList();
        parent.IsExpanded = true;
        RebuildGridRows();
    }

    private async Task EditSectionAsync(SalePostingRow parent, SalePostingSectionRow row)
    {
        if (ShowEditSectionDialog is null) return;
        var r = await ShowEditSectionDialog(row);
        if (r is null) return;
        await _svc.UpdateSectionAsync(row.SectionId, r);

        // Apply to the in-memory model so display + compute reflect the edit.
        var m = row.Model;
        m.Name = r.Name; m.Prefix = r.Prefix;
        m.OverrideScope = r.OverrideScope; m.Scope = r.Scope; m.LocationId = r.LocationId; m.LocationName = r.LocationName;
        m.OverridePricing = r.OverridePricing; m.PricingBasis = r.PricingBasis; m.PricePercent = r.PricePercent;
        m.MarketStationId = r.MarketStationId; m.MarketStationName = r.MarketStationName; m.MarketPriceType = r.MarketPriceType;
        m.OverrideOnlyPackaged = r.OverrideOnlyPackaged; m.OnlyPackaged = r.OnlyPackaged;
        row.ApplyData(m);

        parent.Sections = parent.Sections.OrderBy(s => s.SectionName, StringComparer.OrdinalIgnoreCase).ToList();
        await ComputePostingAsync(parent);   // scope/pricing may have changed
        RebuildGridRows();
    }

    private async Task DeleteSectionAsync(SalePostingRow parent, SalePostingSectionRow row)
    {
        await _svc.DeleteSectionAsync(row.SectionId);
        parent.Sections = parent.Sections.Where(s => s.SectionId != row.SectionId).ToList();
        RebuildGridRows();
    }

    // ── Item add / delete / context actions ─────────────────────────────────────
    private async Task AddItemToSectionAsync(SalePostingRow parent, SalePostingSectionRow section)
    {
        if (ShowAddItemDialog is null) return;
        var r = await ShowAddItemDialog();
        if (r is null) return;
        var model = await _svc.AddItemAsync(section.SectionId, r.TypeId);
        if (model is null) return; // duplicate in section
        section.AllItems = section.AllItems.Append(new SalePostingItemRow(model, r.TypeName, _svc))
            .OrderBy(i => i.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
        section.IsExpanded = true;
        await ComputePostingAsync(parent);
        RebuildGridRows();
    }

    private async Task AddFromMarketGroupAsync()
    {
        var section = GetContextSection();
        if (section is null) { StatusText = "Select a section (or an item in one) first."; return; }
        if (ShowMarketGroupPickerDialog is null || _batchSvc is null) return;

        var pick = await ShowMarketGroupPickerDialog();
        if (pick is null) return;

        var groupItems = await _batchSvc.GetItemsInGroupTreeAsync(pick.MarketGroupId);
        if (groupItems.Count == 0) { StatusText = "No items in that market group."; return; }

        var parent = _allPostings.First(p => p.Sections.Any(s => s.SectionId == section.SectionId));
        int added = 0;
        var newRows = new List<SalePostingItemRow>();
        foreach (var (typeId, name) in groupItems)
        {
            var model = await _svc.AddItemAsync(section.SectionId, typeId);
            if (model is null) continue;
            newRows.Add(new SalePostingItemRow(model, name, _svc));
            added++;
        }
        if (added == 0) { StatusText = "All those items are already in the section."; return; }

        section.AllItems = section.AllItems.Concat(newRows)
            .OrderBy(i => i.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
        section.IsExpanded = true;
        await ComputePostingAsync(parent);
        SortPostingItems(parent);
        RebuildGridRows();
        StatusText = $"Added {added} item(s).";
    }

    private async Task DeleteSelectedItemAsync()
    {
        if (_selectedRow is not SalePostingItemRow item) return;
        await _svc.DeleteItemAsync(item.ItemId);
        foreach (var pr in _allPostings)
            foreach (var s in pr.Sections)
                if (s.AllItems.Any(i => i.ItemId == item.ItemId))
                    s.AllItems = s.AllItems.Where(i => i.ItemId != item.ItemId).ToList();
        RebuildGridRows();
    }

    private void OpenSelectedInItemBrowser()
    {
        if (_selectedRow is SalePostingItemRow item)
            OpenInItemBrowser?.Invoke(item.TypeId, item.TypeName);
    }

    private SalePostingSectionRow? GetContextSection()
    {
        return _selectedRow switch
        {
            SalePostingSectionRow s => s,
            SalePostingItemRow item => _allPostings.SelectMany(p => p.Sections)
                .FirstOrDefault(s => s.AllItems.Any(i => i.ItemId == item.ItemId)),
            _ => null,
        };
    }
}
