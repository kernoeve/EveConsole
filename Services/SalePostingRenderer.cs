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

internal sealed record PostingSectionView(
    string Name, string Prefix, IReadOnlyList<PostingItemView> Items);

internal sealed record PostingView(
    bool ShowInStock, bool ShowInBuild, bool ShowReserved, bool IncludeCompletionDate,
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
            _         => post.StaticContent ?? "",
        };

    /// <summary>Section names and their price ranges — the short form.</summary>
    public static string Summary(PostingView v, OutputFormat fmt, SalePostingPost post)
    {
        var sb = new StringBuilder();
        AppendBlock(sb, post.Header);
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
        AppendBlock(sb, post.Footer);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Every item under every section — the full list.</summary>
    public static string Detail(PostingView v, OutputFormat fmt, SalePostingPost post)
    {
        var sb = new StringBuilder();
        AppendBlock(sb, post.Header);
        foreach (var s in v.Sections)
        {
            // Bold and underlined, and no prefix: the prefix is a Summary device (a Slack icon
            // standing in for the section) and repeating it here would print the shortcode twice.
            sb.AppendLine(fmt.Bold(fmt.Underline(s.Name)));
            foreach (var it in s.Items) sb.AppendLine(ItemLine(it, v, fmt));
        }
        AppendBlock(sb, post.Footer);
        return sb.ToString().TrimEnd();
    }

    private static string ItemLine(PostingItemView it, PostingView v, OutputFormat fmt)
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
        return sb.ToString();
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
