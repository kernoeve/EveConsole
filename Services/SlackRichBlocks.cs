using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EveConsole.Services;

/// <summary>
/// Columns in Slack text, where every row needs an image and so nothing can be a table.
///
/// <para>⚠️ The only way to have an image at the start of a tight row is a context block, and it
/// has no columns: the numbers line up only because each name is padded to a width worked out here.
/// Slack sends one text to every device, so the padding has to come out the same width on all.</para>
///
/// <para>⚠️ Measured on the PC and Android apps, in a container and out of one: an en space and a
/// three-per-em space keep their width everywhere. A no-break space draws about a quarter narrower
/// in a container on the phone, a figure space slightly narrower, and a run of normal spaces far
/// narrower, so a list padded with any of those lines up on a PC and falls apart on a phone. The
/// two kept here only make steps of about 2 px, which is the price of holding everywhere.</para>
/// </summary>
public static class SlackColumns
{
    private const char EnSpace = '\u2002';
    private const char ThreePerEmSpace = '\u2004';
    private const int EnWidth = 497;
    private const int ThreePerEmWidth = 331;

    /// <summary>
    /// A word space, between what the two apps draw: 256 on a PC, 193 in a container on a phone.
    /// A name with a word more than its neighbours is about a pixel out on one of them.
    /// </summary>
    private const int WordSpaceWidth = 225;

    /// <summary>The arrow in a delivery project's name. On every row, so only the header row
    /// depends on the figure.</summary>
    private const int ArrowWidth = 1000;

    /// <summary>What a character this table does not hold counts as: Lato's lower case average.</summary>
    private const int UnknownWidth = 556;

    /// <summary>Chinese, Japanese and Korean, which Slack draws a full em wide from another font.</summary>
    private const int WideWidth = 1000;

    /// <summary>
    /// Lato Regular's widths, in thousandths of the text's size, from the space (32) to the tilde
    /// (126). Measured from the font itself, so they hold at any text size.
    /// </summary>
    private static readonly int[] Advance =
    [
        WordSpaceWidth, 343, 397, 580, 580, 786, 703, 230, 300, 300, 400, 580, 212, 347, 212, 373,
        580, 580, 580, 580, 580, 580, 580, 580, 580, 580, 252, 252, 580, 580, 580, 398,
        822, 680, 647, 685, 753, 581, 566, 734, 756, 307, 444, 681, 514, 920, 756, 798,
        611, 798, 644, 530, 590, 730, 680, 1019, 643, 629, 624, 300, 375, 300, 580, 394,
        307, 507, 559, 467, 559, 524, 337, 511, 556, 256, 254, 524, 256, 821, 556, 556,
        552, 559, 403, 434, 373, 556, 512, 766, 504, 512, 462, 300, 300, 300, 580,
    ];

    /// <summary>
    /// Lato's kerning: pairs of letters drawn closer than their widths add up to, as much as 3 px
    /// on "Ta" or "Te". Each entry is the pair and the adjustment. A name without these sits
    /// visibly left of its column.
    /// </summary>
    private static readonly Dictionary<(char, char), int> Kerning = ParseKerning(
        "AC-21|AG-21|AJ25|AO-21|AQ-21|AT-66|AU-28|AV-68|AW-42|AY-82|Av-41|Ay-41|A--26|A'-91|C--75|DA-21|DT-49|DV-26|DX-15|DY-40|DZ-35|D'-23|D.-28|D&-21|D,-28|" +
        "FA-66|FJ-99|Fc-35|Fd-35|Fe-35|Fm-30|Fn-30|Fo-35|Fp-30|Fq-35|Fr-30|Fu-30|F.-90|F&-66|F,-90|JA-28|J.-25|J&-28|J,-25|KC-15|KG-15|KO-15|KQ-15|Kc-18|Kd-18|Ke-18|Kf-26|Ko-18|Kq-18|Kt-41|Kv-33|Kw-28|Ky-33|K--31|" +
        "LC-40|LG-40|LO-40|LQ-40|LT-86|LV-91|LW-76|LY-106|Lc-18|Ld-18|Le-18|Lo-18|Lq-18|Lv-54|Lw-40|Ly-54|L--99|L'-145|L.27|L,27|OA-21|OT-49|OV-26|OX-15|OY-40|OZ-35|O'-23|O.-28|O&-21|O,-28|" +
        "PA-69|PJ-91|Pa-25|Pc-15|Pd-15|Pe-15|Po-15|Pq-15|P.-124|P&-69|P,-124|QA-21|QT-49|QV-26|QX-15|QY-40|QZ-35|Q'-23|Q.-28|Q&-21|Q,-28|RC-23|RG-23|RO-23|RQ-23|RT-26|RU-21|" +
        "TA-66|TC-49|TG-49|TJ-100|TO-49|TQ-49|Ta-125|Tc-105|Td-105|Te-105|Tg-94|Tm-80|Tn-80|To-105|Tp-80|Tq-105|Tr-80|Ts-81|Tu-80|Tv-90|Tw-70|Tx-72|Ty-90|Tz-60|T--90|T.-90|T&-66|T,-90|UA-28|U.-25|U&-28|U,-25|" +
        "VA-68|VC-26|VG-26|VJ-76|VO-26|VQ-26|Va-58|Vc-58|Vd-58|Ve-58|Vf-15|Vg-68|Vm-44|Vn-44|Vo-58|Vp-44|Vq-58|Vr-44|Vs-53|Vt-21|Vu-44|Vv-24|Vx-26|Vy-24|Vz-41|V--56|V'24|V.-96|V&-68|V,-96|" +
        "WA-47|WJ-51|Wa-44|Wc-16|Wd-16|We-16|Wg-49|Wo-16|Wq-16|Ws-23|W--16|W'24|W.-61|W&-47|W,-61|XC-15|XG-15|XO-15|XQ-15|Xc-18|Xd-18|Xe-18|Xf-26|Xo-18|Xq-18|Xt-41|Xv-33|Xw-28|Xy-33|X--31|" +
        "YA-82|YC-40|YG-40|YJ-100|YO-40|YQ-40|Ya-64|Yc-80|Yd-80|Ye-80|Yg-86|Ym-61|Yn-61|Yo-80|Yp-61|Yq-80|Yr-61|Ys-64|Yu-61|Yv-50|Yw-46|Yx-66|Yy-50|Y--80|Y'15|Y.-76|Y&-82|Y,-76|" +
        "ZC-29|ZG-29|ZO-29|ZQ-29|Zc-19|Zd-19|Ze-19|Zo-19|Zq-19|Zs-14|Zv-20|Zy-20|Z--35|av-16|aw-8|ay-16|a'-36|bV-58|bW-16|bv-13|bx-30|by-13|b'-46|eV-58|eW-16|ev-13|ex-30|ey-13|e'-46|f'34|f.-65|f,-65|" +
        "hv-16|hw-8|hy-16|h'-36|kc-30|kd-30|ke-30|ko-30|kq-30|mv-16|mw-8|my-16|m'-36|nv-16|nw-8|ny-16|n'-36|oV-58|oW-16|ov-13|ox-30|oy-13|o'-46|pV-58|pW-16|pv-13|px-30|py-13|p'-46|ra-19|r.-66|r,-66|" +
        "vA-41|vc-13|vd-13|ve-13|vo-13|vq-13|v.-66|v&-41|v,-66|w.-31|w,-31|xc-30|xd-30|xe-30|xo-30|xq-30|yA-41|yc-13|yd-13|ye-13|yo-13|yq-13|y.-66|y&-41|y,-66|" +
        "-A-26|-T-90|-V-56|-W-16|-X-31|-Y-80|-Z-23|-'-89|-.-68|-&-26|-,-68|'A-91|'C-23|'G-23|'O-23|'Q-23|'V24|'W24|'Y15|'a-32|'c-46|'d-46|'e-46|'o-46|'q-46|'--89|'.-114|'&-91|',-114|" +
        ".C-28|.G-28|.O-28|.Q-28|.T-90|.V-90|.W-61|.Y-76|.v-66|.w-31|.y-66|.--68|.'-114|,C-28|,G-28|,O-28|,Q-28|,T-90|,V-90|,W-61|,Y-76|,v-66|,w-31|,y-66|,--68|,'-114");

    private static Dictionary<(char, char), int> ParseKerning(string table)
    {
        var pairs = new Dictionary<(char, char), int>();
        foreach (var entry in table.Split('|'))
            pairs[(entry[0], entry[1])] = int.Parse(entry[2..], System.Globalization.CultureInfo.InvariantCulture);
        return pairs;
    }

    /// <summary>How wide text draws, in thousandths of the text's size.</summary>
    public static int Width(string text)
    {
        var total = 0;
        for (var i = 0; i < text.Length; i++)
        {
            total += CharWidth(text[i]);
            if (i > 0 && Kerning.TryGetValue((text[i - 1], text[i]), out var kern)) total += kern;
        }
        return total;
    }

    private static int CharWidth(char c)
    {
        if (c is >= ' ' and <= '~') return Advance[c - ' '];
        if (c == '\u2192') return ArrowWidth;
        return c >= '\u2E80' ? WideWidth : UnknownWidth;
    }

    /// <summary>The blanks nearest to a width, and the width they really come to. Nothing, for a
    /// width too small for even the smallest of them.</summary>
    public static (string Text, int Width) Blanks(int width)
    {
        var bestError = Math.Abs(width);
        var (bestEn, bestThird) = (0, 0);

        for (var en = 0; en * EnWidth <= Math.Max(width, 0) + EnWidth; en++)
        {
            var rest  = width - en * EnWidth;
            var third = Math.Max((int)Math.Round(rest / (double)ThreePerEmWidth), 0);
            var error = Math.Abs(rest - third * ThreePerEmWidth);

            if (error >= bestError) continue;

            bestError = error;
            (bestEn, bestThird) = (en, third);
        }

        return (new string(EnSpace, bestEn) + new string(ThreePerEmSpace, bestThird),
                bestEn * EnWidth + bestThird * ThreePerEmWidth);
    }

    /// <summary>
    /// One row: each cell at its column, right-aligned or not, ready to send.
    ///
    /// <para>⚠️ Each gap is worked out from where the text before it really ended, not from the
    /// column it was aiming for, so rounding does not add up along the row.</para>
    /// </summary>
    /// <param name="offset">Blank space before the first column: a header row has no icon, so it
    /// starts where the text of the rows under it starts.</param>
    public static string Place(
        IReadOnlyList<string> cells, IReadOnlyList<int> starts, IReadOnlyList<int> widths,
        IReadOnlyList<bool> right, int offset = 0)
    {
        var line = new StringBuilder();
        var at   = 0;

        if (offset > 0)
        {
            var (lead, leadWidth) = Blanks(offset);
            line.Append(lead);
            at = leadWidth;
        }

        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i].Length == 0) continue;

            var size   = Width(cells[i]);
            var target = offset + starts[i] + (right[i] ? widths[i] - size : 0);

            var (pad, padWidth) = Blanks(target - at);
            line.Append(pad).Append(Escape(cells[i]));
            at += padWidth + size;
        }

        return line.ToString();
    }

    /// <summary>A name shortened to fit, with its end marked. A name that fits comes back whole.</summary>
    public static string Cut(string text, int limit)
    {
        const string Mark = "...";

        if (Width(text) <= limit) return text;

        var room = limit - Width(Mark);
        var end  = text.Length;
        while (end > 0 && Width(text[..end]) > room) end--;

        return text[..end].TrimEnd() + Mark;
    }

    /// <summary>The three characters Slack's markup requires written as entities. A corp's name
    /// is anyone's to choose, and an unescaped one could break the block it sits in.</summary>
    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>A list for the Top 10 boxes. The first row is number 1.</summary>
public sealed record SlackRichList(string Title, IReadOnlyList<SlackRichRow> Rows);

/// <summary>One ranked entity.</summary>
/// <param name="Value">The row's amount, or null when the section hides it.</param>
/// <param name="FeaturedValue">The amount as number 1 prints it, with its unit; null when hidden.</param>
/// <param name="Share">The share of the whole, not of the list.</param>
public sealed record SlackRichRow(string Name, string Icon, string LargeIcon, string? Value, string? FeaturedValue, string Share);

/// <summary>One column of a wide table.</summary>
public sealed record SlackRichColumn(string Header, bool Right);

/// <summary>One row of a wide table. The icon is null where the row has nothing to show.</summary>
public sealed record SlackRichTableRow(IReadOnlyList<string> Cells, string? Icon, string IconAlt);

/// <summary>A block on its way into a message.</summary>
/// <param name="Block">What Slack gets.</param>
/// <param name="Repeat">Goes first in the next message if this block has to start one: the header
/// row of the table the block belongs to.</param>
/// <param name="Room">How many blocks must fit for this one to stay in the current message, so a
/// heading never ends a message with its table in the next.</param>
public sealed record SlackRichItem(object Block, object? Repeat = null, int Room = 1);

/// <summary>
/// Slack's Block Kit, as the scheduled posts' rich formats use it. Nothing here touches Slack: what
/// comes out is the JSON for a message's blocks.
/// </summary>
public static class SlackRichBlocks
{
    /// <summary>
    /// The most top-level blocks one message takes is 50. A few short of it, so a repeated header
    /// and a heading can never be what tips one over.
    /// </summary>
    public const int MaxBlocksPerMessage = 48;

    /// <summary>A container holds 10 blocks. Its first holds number 1, leaving nine for rows.</summary>
    private const int RowsPerList = 9;

    /// <summary>What Slack allows in a plain text title or a section of text.</summary>
    public const int MaxTitle = 150;
    public const int MaxSectionText = 2900;

    /// <summary>A layout row takes two boxes before it wraps, as it would have by itself: the
    /// layout stops at about 715 px and a box needs about 320.</summary>
    private const int BoxesPerRow = 2;

    /// <summary>What fits on one row of a box two to a row, about 240 px. Past it the row wraps
    /// under its icon.</summary>
    private const int ListRowLimit = 18460;

    private const int NameGap    = 750;
    private const int NumberGap  = 600;
    private const int TableGap   = 1000;

    /// <summary>An icon and the gap after it.</summary>
    private const int IconOffset = 1923;

    /// <summary>Beside a lone box, so a list on its own keeps the width of the others. Slack
    /// refuses an empty section, and this draws as nothing.</summary>
    private const string NothingToShow = "\u00A0";

    /// <summary>A context block holds 10 elements: the rank, four names and their icons, and the
    /// numbers. Ties past that are counted rather than named.</summary>
    private const int TiedNamesShown = 4;

    private static Dictionary<string, object> Mrkdwn(string text, bool verbatim = true)
    {
        var block = new Dictionary<string, object> { ["type"] = "mrkdwn", ["text"] = text };

        // ⚠️ Data, not somebody's message. Left off, a corp named after a website would become a
        // link, and one named after @channel could become a ping.
        if (verbatim) block["verbatim"] = true;
        return block;
    }

    private static Dictionary<string, object> Image(string url, string alt) => new()
    {
        ["type"]      = "image",
        ["image_url"] = url,
        ["alt_text"]  = alt.Length > 0 ? alt : "-",
    };

    private static Dictionary<string, object> Context(params object[] elements) => new()
    {
        ["type"]     = "context",
        ["elements"] = elements,
    };

    private static Dictionary<string, object> PlainTitle(string text) => new()
    {
        ["type"]  = "plain_text",
        ["text"]  = text.Length > MaxTitle ? text[..MaxTitle] : text,
        ["emoji"] = true,
    };

    /// <summary>
    /// A heading as rich text, so its emoji are drawn.
    ///
    /// <para>⚠️ A container's plain title prints :name: as those letters, in a heading somebody
    /// wrote with an emoji in it. Only a rich text title can hold one, as an element of its own. A
    /// name needs a letter, so the 30 in 12:30:45 stays a time; +1 and -1 are the two without.</para>
    /// </summary>
    public static Dictionary<string, object> RichTitle(string text)
    {
        if (text.Length > MaxTitle) text = text[..MaxTitle];

        var elements = new List<object>();
        var at = 0;

        void Words(string words)
        {
            if (words.Length == 0) return;
            elements.Add(new Dictionary<string, object>
            {
                ["type"]  = "text",
                ["text"]  = words,
                ["style"] = new Dictionary<string, object> { ["bold"] = true },
            });
        }

        foreach (Match m in EmojiName.Matches(text))
        {
            Words(text[at..m.Index]);
            elements.Add(new Dictionary<string, object> { ["type"] = "emoji", ["name"] = m.Groups[1].Value });
            at = m.Index + m.Length;
        }
        Words(text[at..]);

        return new Dictionary<string, object>
        {
            ["type"]     = "rich_text",
            ["elements"] = new object[]
            {
                new Dictionary<string, object> { ["type"] = "rich_text_section", ["elements"] = elements },
            },
        };
    }

    private static readonly Regex EmojiName = new(@":(\+1|-1|(?=[a-z0-9_+'-]*[a-z])[a-z0-9_+'-]+):", RegexOptions.Compiled);

    /// <summary>Typed text, or a section still written as text. Its mentions and links work as
    /// they do in a plain message.</summary>
    public static Dictionary<string, object> TextSection(string mrkdwn) => new()
    {
        ["type"] = "section",
        ["text"] = Mrkdwn(mrkdwn, verbatim: false),
    };

    /// <summary>An entity's image by what it is: a pilot's portrait, a corp's or alliance's logo.</summary>
    public static string EntityImage(long id, string kind, int size) => kind switch
    {
        "corporation" => $"https://images.evetech.net/corporations/{id}/logo?size={size}",
        "alliance"    => $"https://images.evetech.net/alliances/{id}/logo?size={size}",
        _             => $"https://images.evetech.net/characters/{id}/portrait?size={size}",
    };

    public static string ItemIcon(int typeId, int size = 64) =>
        $"https://images.evetech.net/types/{typeId}/icon?size={size}";

    // ── Top 10 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The lists as boxes, two to a row. On a phone Slack stacks them.
    ///
    /// <para>⚠️ A layout cannot hold a layout inside a container inside it, so number 1 cannot have
    /// its image on the left here: a section only puts one on the right.</para>
    /// </summary>
    public static Dictionary<string, object> Top10Boxes(
        IReadOnlyList<SlackRichList> lists, string label, string topLabel, string shareOfTotal)
    {
        var rows = new List<object>();

        for (var i = 0; i < lists.Count; i += BoxesPerRow)
        {
            var items = lists.Skip(i).Take(BoxesPerRow)
                .Select(l => (object)new Dictionary<string, object> { ["block"] = ListBox(l, topLabel, shareOfTotal) })
                .ToList();

            if (items.Count < BoxesPerRow)
                items.Add(new Dictionary<string, object> { ["block"] = TextSection(NothingToShow) });

            rows.Add(new Dictionary<string, object> { ["items"] = items });
        }

        return new Dictionary<string, object>
        {
            ["type"]       = "layout",
            ["label"]      = label,
            ["grid_lines"] = "none",
            ["rows"]       = rows,
        };
    }

    private static Dictionary<string, object> ListBox(SlackRichList list, string topLabel, string shareOfTotal)
    {
        var children = new List<object>();
        if (list.Rows.Count > 0) children.Add(Featured(list.Rows[0], topLabel, shareOfTotal));
        children.AddRange(ListRows(list.Rows));

        return new Dictionary<string, object>
        {
            ["type"]         = "container",
            // Both: the rich one is drawn where it can be, and the plain one says what it is where not.
            ["title"]           = PlainTitle(list.Title),
            ["rich_text_title"] = RichTitle(list.Title),
            ["width"]        = "full",
            ["child_blocks"] = children,
        };
    }

    private static Dictionary<string, object> Featured(SlackRichRow row, string topLabel, string shareOfTotal)
    {
        var lines = new List<string> { topLabel, $"*{SlackColumns.Escape(row.Name)}*" };
        if (row.FeaturedValue is { } value) lines.Add($"*{SlackColumns.Escape(value)}*");
        if (row.Share.Length > 0) lines.Add(string.Format(shareOfTotal, row.Share));

        return new Dictionary<string, object>
        {
            ["type"]      = "section",
            ["text"]      = Mrkdwn(string.Join("\n", lines)),
            ["accessory"] = Image(row.LargeIcon, row.Name),
        };
    }

    /// <summary>
    /// Every row after number 1.
    ///
    /// <para>⚠️ Ties at the foot of a Top 10 have no ceiling, least of all on a list of small
    /// counts like solo kills, and a container holds nine rows. Past that, the rest are all tied
    /// with the tenth, so they share its row rather than being lost.</para>
    /// </summary>
    private static List<object> ListRows(IReadOnlyList<SlackRichRow> rows)
    {
        var body = rows.Skip(1).ToList();
        if (body.Count == 0) return [];

        var tied  = body.Count > RowsPerList;
        var plain = tied ? body.Take(RowsPerList - 1).ToList() : body;

        var withValue = body.Any(r => r.Value is not null);
        var valueWidth = withValue ? body.Max(r => SlackColumns.Width(r.Value ?? "")) : 0;
        var shareWidth = body.Max(r => SlackColumns.Width(r.Share));

        var numbers   = (withValue ? valueWidth + NumberGap : 0) + shareWidth;
        var nameLimit = ListRowLimit - NameGap - numbers;
        var names     = plain.Select(r => SlackColumns.Cut(r.Name, nameLimit)).ToList();
        var nameWidth = names.Count > 0 ? names.Max(SlackColumns.Width) : 0;

        int[] starts = withValue
            ? [0, nameWidth + NameGap, nameWidth + NameGap + valueWidth + NumberGap]
            : [0, nameWidth + NameGap];
        int[] widths = withValue ? [nameWidth, valueWidth, shareWidth] : [nameWidth, shareWidth];
        bool[] right = withValue ? [false, true, true] : [false, true];

        var blocks = new List<object>();

        for (var i = 0; i < plain.Count; i++)
        {
            string[] cells = withValue
                ? [names[i], plain[i].Value ?? "", plain[i].Share]
                : [names[i], plain[i].Share];

            blocks.Add(Context(
                Mrkdwn(RankText(i + 2)),
                Image(plain[i].Icon, plain[i].Name),
                Mrkdwn(SlackColumns.Place(cells, starts, widths, right))));
        }

        if (tied) blocks.Add(TiedRow(body.Skip(plain.Count).ToList(), plain.Count + 2));

        return blocks;
    }

    private static Dictionary<string, object> TiedRow(List<SlackRichRow> rows, int rank)
    {
        var elements = new List<object> { Mrkdwn(RankText(rank)) };

        foreach (var row in rows.Take(TiedNamesShown))
        {
            elements.Add(Image(row.Icon, row.Name));
            elements.Add(Mrkdwn(SlackColumns.Escape(row.Name)));
        }

        var first = rows[0];
        var said  = first.Value is null ? first.Share : $"{first.Value}  {first.Share}";
        var more  = rows.Count - TiedNamesShown;

        // The count goes with the numbers, in the one element left.
        var last = (Dictionary<string, object>)elements[^1];
        last["text"] = $"{last["text"]}   {SlackColumns.Escape(said)}{(more > 0 ? $"  (+{more})" : "")}";

        return Context([.. elements]);
    }

    /// <summary>Bold, and with a digit's width in front of a single digit, so "9." and "10." end
    /// together.</summary>
    private static string RankText(int rank) => rank < 10 ? $"*\u2007{rank}.*" : $"*{rank}.*";

    // ── Wide tables ──────────────────────────────────────────────────────────

    /// <summary>
    /// A wide table in the message itself, with no box.
    ///
    /// <para>⚠️ Not in containers, though rows are a little tighter there. A container holds 10
    /// blocks and each row is one, so a real list becomes several boxes, and on a phone their
    /// columns stop agreeing with each other.</para>
    /// </summary>
    public static List<SlackRichItem> Table(
        string heading, bool showHeaders, IReadOnlyList<SlackRichColumn> columns, IReadOnlyList<SlackRichTableRow> rows)
    {
        var items = new List<SlackRichItem>();
        if (rows.Count == 0) return items;

        var right  = columns.Select(c => c.Right).ToArray();
        var widths = new int[columns.Count];

        for (var i = 0; i < columns.Count; i++)
        {
            widths[i] = rows.Max(r => SlackColumns.Width(r.Cells[i]));
            if (showHeaders) widths[i] = Math.Max(widths[i], SlackColumns.Width(columns[i].Header));
        }

        var starts = new int[columns.Count];
        for (var i = 1; i < columns.Count; i++) starts[i] = starts[i - 1] + widths[i - 1] + TableGap;

        if (heading.Length > 0) items.Add(new SlackRichItem(TextSection(heading), Room: showHeaders ? 3 : 2));

        object? header = null;
        if (showHeaders)
        {
            var offset = rows.Any(r => r.Icon is not null) ? IconOffset : 0;
            header = Context(Mrkdwn(SlackColumns.Place(columns.Select(c => c.Header).ToArray(), starts, widths, right, offset)));
            items.Add(new SlackRichItem(header, Room: 2));
        }

        foreach (var row in rows)
        {
            var text = Mrkdwn(SlackColumns.Place(row.Cells, starts, widths, right));
            var block = row.Icon is { } icon ? Context(Image(icon, row.IconAlt), text) : Context(text);
            items.Add(new SlackRichItem(block, header));
        }

        return items;
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Blocks into messages Slack will take, in order. A table that has to carry on in another
    /// message takes its header row with it.
    /// </summary>
    public static List<List<object>> Pack(IEnumerable<SlackRichItem> items, int max = MaxBlocksPerMessage)
    {
        var messages = new List<List<object>>();
        var current  = new List<object>();

        foreach (var item in items)
        {
            if (current.Count > 0 && current.Count + item.Room > max)
            {
                messages.Add(current);
                current = [];
                if (item.Repeat is { } repeat) current.Add(repeat);
            }

            current.Add(item.Block);
        }

        if (current.Count > 0) messages.Add(current);
        return messages;
    }

    /// <summary>
    /// Whether Slack turned a message down for what is in it, rather than for where it was going.
    ///
    /// <para>Only this is worth sending again as plain text. A bad address or a removed webhook
    /// would refuse the plain text too, and an outage is what the next pass is for.</para>
    /// </summary>
    public static bool RefusedForFormat(string? error) =>
        error is not null
        && (error.Contains("invalid_blocks", StringComparison.OrdinalIgnoreCase)
            || error.Contains("invalid_payload", StringComparison.OrdinalIgnoreCase)
            || error.Contains("msg_too_long", StringComparison.OrdinalIgnoreCase)
            || error.Contains("msg_blocks_too_long", StringComparison.OrdinalIgnoreCase));
}
