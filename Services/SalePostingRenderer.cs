using System.Globalization;
using System.Text;
using EveConsole.Models;
using EveConsole.ViewModels;

namespace EveConsole.Services;

/// <summary>
/// One item as the posting text needs it: the numbers, the price, and the name tweaks.
///
/// <para>⚠️ Raw counts AND their overrides, not just the effective figures. The item line prints
/// the override where there is one, but the completion date is shown only when the REAL stock is
/// zero and something is really building — an override that says "3 in stock" is a display
/// choice and must not suppress a date, nor conjure one. The two rules disagree on purpose, so
/// both inputs have to survive the trip.</para>
/// </summary>
internal sealed record PostingItemView(
    int TypeId,
    string TypeName,
    string? NameOverride,
    string? NamePrefix,
    string? Color,
    long InStock,
    long InBuild,
    long Reserved,
    int? InStockOverride,
    int? InBuildOverride,
    int? ReservedOverride,
    double? SalePrice,
    DateTimeOffset? EarliestJobEnd)
{
    public long EffectiveInStock  => InStockOverride  ?? InStock;
    public long EffectiveInBuild  => InBuildOverride  ?? InBuild;
    public long EffectiveReserved => ReservedOverride ?? Reserved;
}

/// <param name="HeaderColor">The heading line.</param>
/// <param name="RowColor">The item lines under it — ⚠️ only reached when the posting is not
/// colouring by state, which is per line and therefore wins.</param>
internal sealed record PostingSectionView(
    string Name, string Prefix, string? HeaderColor, string? RowColor,
    IReadOnlyList<PostingItemView> Items);

/// <param name="ColorByState">Colour each item line by whether it is on the shelf, being built,
/// or neither — the one colouring rule that cannot go stale, because stock moves and a colour set
/// by hand does not move with it.</param>
internal sealed record PostingView(
    bool ShowInStock, bool ShowInBuild, bool ShowReserved, bool IncludeCompletionDate,
    bool ColorByState, string ColorInStock, string ColorInBuild, string ColorNone,
    IReadOnlyList<PostingSectionView> Sections);

/// <summary>One post block, rendered.</summary>
internal sealed record RenderedPost(string Name, string PostType, string Text);

/// <summary>
/// Turns a posting into the text that gets sent, in whichever output format.
///
/// <para><b>⚠️ Why this is not in the view model any more.</b> It was, and only the Sale Posting
/// tab could produce a listing — a background service answering a mailed request for prices had
/// no way to reach it, because the render read directly off the grid's row objects. Rendering is
/// not a property of a screen; it is what the posting IS when written down.</para>
///
/// <para>The tool still owns the rows, because they carry the edit bindings and the persistence
/// callbacks. It hands this a plain snapshot of them instead, so both callers render through the
/// same code and a listing mailed to a buyer cannot drift from the one on screen.</para>
///
/// <para><see cref="OutputFormat"/> stays where it is: it is internal, this assembly can see it,
/// and moving two hundred lines of Slack rich-text handling to make a point about layering would
/// risk a live feature for no gain.</para>
/// </summary>
internal static class SalePostingRenderer
{
    /// <summary>Renders one post block, dispatching on its type.</summary>
    public static string Render(PostingView v, OutputFormat fmt, SalePostingPost post) =>
        post.PostType switch
        {
            "Summary" => Summary(v, fmt, post),
            "Detail"  => Detail(v, fmt, post),
            // A Static block is all content and no header, so its one colour setting colours the
            // content. Applied per line, not to the whole block: a colour wrapped around text
            // containing line breaks would put a <font> across them, which EVE renders unevenly.
            _         => Lines(post.StaticContent ?? "", fmt, post.HeaderColor),
        };

    /// <summary>Colours each line of a free-text block, leaving blank lines alone.</summary>
    private static string Lines(string text, OutputFormat fmt, string? color)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(color)) return text;

        return string.Join("\n", text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Length == 0 ? line : fmt.Color(color, line)));
    }

    /// <summary>Section names and their price ranges — the short form.</summary>
    public static string Summary(PostingView v, OutputFormat fmt, SalePostingPost post)
    {
        var sb = new StringBuilder();
        AppendBlock(sb, Lines(post.Header ?? "", fmt, post.HeaderColor));
        foreach (var s in v.Sections)
        {
            var line = new StringBuilder();
            line.Append(Pfx(s.Prefix)).Append(s.Name);

            var prices = s.Items.Select(i => i.SalePrice)
                                .Where(p => p.HasValue).Select(p => p!.Value).ToList();
            if (prices.Count > 0)
                line.Append(" - ").Append(SalePostFmt.Isk(prices.Min()))
                    .Append('-').Append(SalePostFmt.Isk(prices.Max()));

            sb.AppendLine(fmt.Bold(line.ToString()));   // section lines bold
        }
        AppendBlock(sb, Lines(post.Footer ?? "", fmt, post.FooterColor));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Every item under every section — the full list.</summary>
    public static string Detail(PostingView v, OutputFormat fmt, SalePostingPost post)
    {
        var sb = new StringBuilder();
        AppendBlock(sb, Lines(post.Header ?? "", fmt, post.HeaderColor));
        foreach (var s in v.Sections)
        {
            // Bold and underlined, and no prefix: the prefix is a Summary device (a Slack icon
            // standing in for the section) and repeating it here would print the shortcode twice.
            sb.AppendLine(fmt.Color(s.HeaderColor, fmt.Bold(fmt.Underline(s.Name))));
            foreach (var it in s.Items) sb.AppendLine(ItemLine(it, v, fmt, s));
        }
        AppendBlock(sb, Lines(post.Footer ?? "", fmt, post.FooterColor));
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The colour for one item line, by precedence: the item's own, then what its state says,
    /// then its section's.
    ///
    /// <para>⚠️ Most specific wins, and a colour set on the item is the most specific thing
    /// anyone can say. The by-state rule sits under it rather than over it so that marking one
    /// line by hand is not silently undone the next time stock moves — someone who coloured a
    /// row meant that row.</para>
    /// </summary>
    private static string? ColorFor(PostingItemView it, PostingView v, PostingSectionView section)
    {
        if (!string.IsNullOrWhiteSpace(it.Color)) return it.Color;

        if (v.ColorByState)
            return it.EffectiveInStock > 0 ? v.ColorInStock
                 : it.EffectiveInBuild > 0 ? v.ColorInBuild
                 :                           v.ColorNone;

        return section.RowColor;
    }

    private static string ItemLine(PostingItemView it, PostingView v, OutputFormat fmt,
                                   PostingSectionView section)
    {
        // ⚠️ The prefix stays outside the link. It is a decoration — a chat icon shortcode in
        // practice — and wrapping it would make the clickable region include a token that is not
        // part of the item's name. The override is linked, though: it is still this type, just
        // called something the seller prefers.
        var shown = string.IsNullOrWhiteSpace(it.NameOverride) ? it.TypeName : it.NameOverride;
        var name  = Pfx(it.NamePrefix) + fmt.ItemLink(it.TypeId, shown);

        // Just the numbers for the enabled columns, e.g. (9,2,0).
        var counts = new List<string>();
        if (v.ShowInStock)  counts.Add(it.EffectiveInStock .ToString(CultureInfo.InvariantCulture));
        if (v.ShowInBuild)  counts.Add(it.EffectiveInBuild .ToString(CultureInfo.InvariantCulture));
        if (v.ShowReserved) counts.Add(it.EffectiveReserved.ToString(CultureInfo.InvariantCulture));

        var sb = new StringBuilder(name);
        if (counts.Count > 0) sb.Append(" (").Append(string.Join(",", counts)).Append(')');
        sb.Append(" - ").Append(SalePostFmt.Isk(it.SalePrice));

        var done = CompletionText(it, v.IncludeCompletionDate);
        if (done.Length > 0) sb.Append(" - ").Append(done);

        // The whole line, so the price and counts carry the colour too — a coloured name beside
        // uncoloured numbers reads as a link rather than as a state.
        return fmt.Color(ColorFor(it, v, section), sb.ToString());
    }

    /// <summary>
    /// When the first one will be ready — shown only where it answers a question the reader
    /// actually has: nothing on the shelf, something on the way, and the posting asked for it.
    /// </summary>
    internal static string CompletionText(PostingItemView it, bool includeCompletionDate) =>
        includeCompletionDate && it.InStock == 0 && it.InBuild >= 1
            && it.EarliestJobEnd is { } d
            ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "";

    private static void AppendBlock(StringBuilder sb, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text.TrimEnd());
    }

    private static string Pfx(string? p) => string.IsNullOrWhiteSpace(p) ? "" : p.Trim() + " ";
}
