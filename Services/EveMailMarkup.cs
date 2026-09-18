using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EveConsole.Services;

/// <summary>
/// One run of an EVE mail body: text, and how it is dressed.
/// </summary>
/// <param name="Color">ARGB, or null for the default foreground.</param>
/// <param name="Size">EVE font size, or null for the default. EVE's default is 12.</param>
/// <param name="Href">The link target exactly as written, or null when not a link.</param>
public sealed record MailSegment(
    string Text, bool Bold, bool Italic, bool Underline, uint? Color, int? Size, string? Href);

/// <summary>
/// Turns the markup EVE writes into a mail body into runs the UI can render.
/// </summary>
/// <remarks>
/// <para>A mail body arrives from ESI as a subset of HTML: <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>,
/// <c>&lt;u&gt;</c>, <c>&lt;br&gt;</c>, <c>&lt;font size color&gt;</c>, and <c>&lt;a href&gt;</c>
/// whose targets are EVE's own schemes — <c>showinfo:</c>, <c>killReport:</c>,
/// <c>contract:</c> — beside ordinary <c>https:</c>. Older mail carries the client's
/// pre-HTML forms too, <c>&lt;color=0xAARRGGBB&gt;</c> and <c>&lt;url=…&gt;</c>. All of it was
/// being stripped to text for display, which threw away the one thing in a mail worth
/// clicking: an item link carries its type id, a name carries its character id.</para>
///
/// <para>⚠️ A tokenizer, not an HTML parser. The input is machine-written by one client and
/// small; the point is to be right about the handful of tags EVE uses and to drop anything
/// else silently rather than show it. Unknown tags vanish, unbalanced tags are tolerated,
/// and a body with no markup at all comes out as one run.</para>
/// </remarks>
public static class EveMailMarkup
{
    private static readonly Regex Tag = new(@"<(/?)([a-zA-Z]+)([^>]*)>", RegexOptions.Compiled);
    private static readonly Regex Attr = new(
        """([a-zA-Z]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))""", RegexOptions.Compiled);

    public static IReadOnlyList<MailSegment> Parse(string? body)
    {
        var segs = new List<MailSegment>();
        if (string.IsNullOrEmpty(body)) return segs;

        int bold = 0, italic = 0, underline = 0;
        var colors = new Stack<uint?>();
        var sizes  = new Stack<int?>();
        var hrefs  = new Stack<string?>();
        var text   = new StringBuilder();

        void Flush()
        {
            if (text.Length == 0) return;
            segs.Add(new MailSegment(
                Decode(text.ToString()), bold > 0, italic > 0, underline > 0,
                colors.Count > 0 ? colors.Peek() : null,
                sizes.Count  > 0 ? sizes.Peek()  : null,
                hrefs.Count  > 0 ? hrefs.Peek()  : null));
            text.Clear();
        }

        var pos = 0;
        foreach (Match m in Tag.Matches(body))
        {
            text.Append(body, pos, m.Index - pos);
            pos = m.Index + m.Length;

            var closing = m.Groups[1].Value == "/";
            var name    = m.Groups[2].Value.ToLowerInvariant();
            var attrs   = m.Groups[3].Value;

            switch (name)
            {
                case "b" or "strong": Flush(); bold      += closing ? -1 : 1; break;
                case "i" or "em":     Flush(); italic    += closing ? -1 : 1; break;
                case "u":             Flush(); underline += closing ? -1 : 1; break;

                case "br":            text.Append('\n'); break;
                case "p" or "div":    if (closing) text.Append('\n'); break;

                case "font":
                    Flush();
                    if (closing) { if (colors.Count > 0) colors.Pop(); if (sizes.Count > 0) sizes.Pop(); }
                    else
                    {
                        var a = Attrs(attrs);
                        colors.Push(a.TryGetValue("color", out var c) ? ParseColor(c) : (colors.Count > 0 ? colors.Peek() : null));
                        sizes.Push(a.TryGetValue("size", out var s) && int.TryParse(s, out var sz) ? sz : (sizes.Count > 0 ? sizes.Peek() : null));
                    }
                    break;

                // The client's older colour form: <color=0xAARRGGBB>…</color>
                case "color":
                    Flush();
                    if (closing) { if (colors.Count > 0) colors.Pop(); }
                    else colors.Push(ParseColor(attrs.TrimStart('=', ' ', '\'', '"').TrimEnd('\'', '"', ' ')));
                    break;

                case "a":
                    Flush();
                    if (closing) { if (hrefs.Count > 0) hrefs.Pop(); }
                    else hrefs.Push(Attrs(attrs).TryGetValue("href", out var h) ? h : null);
                    break;

                // The client's older link form: <url=showinfo:123>text</url>
                case "url":
                    Flush();
                    if (closing) { if (hrefs.Count > 0) hrefs.Pop(); }
                    else hrefs.Push(attrs.TrimStart('=', ' ', '\'', '"').TrimEnd('\'', '"', ' '));
                    break;

                // Everything else — <span>, <loc>, <html>, <body> — is dropped and its text kept.
                default: break;
            }

            bold = Math.Max(0, bold); italic = Math.Max(0, italic); underline = Math.Max(0, underline);
        }
        text.Append(body, pos, body.Length - pos);
        Flush();
        return segs;
    }

    private static Dictionary<string, string> Attrs(string raw)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attr.Matches(raw))
        {
            var v = m.Groups[2].Success ? m.Groups[2].Value
                  : m.Groups[3].Success ? m.Groups[3].Value
                  : m.Groups[4].Value;
            d[m.Groups[1].Value] = v;
        }
        return d;
    }

    /// <summary>
    /// EVE writes colour as <c>#AARRGGBB</c>, <c>#RRGGBB</c>, or <c>0xAARRGGBB</c>. Returns ARGB.
    /// </summary>
    public static uint? ParseColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (s.StartsWith('#')) s = s[1..];

        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        if (s.Length == 6) v |= 0xFF000000;          // opaque when no alpha given
        if (s.Length != 6 && s.Length != 8) return null;

        // A fully transparent colour is the client's way of writing "no colour", not
        // "invisible text". Treat it as opaque so the words still show.
        if ((v & 0xFF000000) == 0) v |= 0xFF000000;
        return v;
    }

    private static string Decode(string s) =>
        s.Contains('&') ? WebUtility.HtmlDecode(s) : s;
}

/// <summary>
/// What a link in a mail body points at, decoded from EVE's own URL schemes.
/// </summary>
public abstract record MailLink
{
    public sealed record Item(int TypeId)                    : MailLink;
    public sealed record Entity(EntityKind Kind, long Id)     : MailLink;
    public sealed record SolarSystem(int SystemId)            : MailLink;
    public sealed record Region(int RegionId)                 : MailLink;
    public sealed record Structure(long StructureId)          : MailLink;
    public sealed record Killmail(int KillmailId)             : MailLink;
    public sealed record Contract(int ContractId)             : MailLink;
    public sealed record Url(string Address)                  : MailLink;
    /// <summary>A scheme this app has no page for — <c>joinChannel:</c>, <c>fitting:</c>.</summary>
    public sealed record Unsupported(string Href)             : MailLink;

    private static readonly Regex ShowInfo = new(@"^showinfo:(\d+)(?://(\d+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Kill     = new(@"^killReport:(\d+)",           RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Contr    = new(@"^contract:\d+//(\d+)",        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Decodes an href. The <c>showinfo:</c> scheme carries a type id and, for a specific
    /// thing rather than a kind of thing, an item id after <c>//</c>. The type id says what
    /// the item id IS, which is more than the id alone can: 1377 is a character, 2 a
    /// corporation, 16159 an alliance, 5 a solar system. Where the type id leaves it open —
    /// NPC corporation or player corporation share type 2 — the id's range decides, as it does
    /// everywhere else in the app.
    /// </summary>
    public static MailLink Parse(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return new Unsupported("");
        href = href.Trim();

        if (ShowInfo.Match(href) is { Success: true } si)
        {
            var typeId = int.Parse(si.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!si.Groups[2].Success) return new Item(typeId);

            var id = long.Parse(si.Groups[2].Value, CultureInfo.InvariantCulture);
            return typeId switch
            {
                >= 1373 and <= 1386 => new Entity(id is >= 3_000_000 and < 4_000_000 ? EntityKind.Agent : EntityKind.Pilot, id),
                2                   => new Entity(EntityLinks.KindOf(id) == EntityKind.NpcCorp ? EntityKind.NpcCorp : EntityKind.PlayerCorp, id),
                16159               => new Entity(EntityKind.Alliance, id),
                30                  => new Entity(EntityKind.Faction, id),
                5                   => new SolarSystem((int)id),
                3                   => new Region((int)id),
                _ when id >= 1_000_000_000_000 => new Structure(id),            // Upwell structures
                _ when id is >= 60_000_000 and < 64_100_000 => new Entity(EntityKind.Station, id),
                _                   => new Item(typeId),                        // a specific unit of a type: show the type
            };
        }

        if (Kill.Match(href)  is { Success: true } k) return new Killmail(int.Parse(k.Groups[1].Value, CultureInfo.InvariantCulture));
        if (Contr.Match(href) is { Success: true } c) return new Contract(int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture));
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
         || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return new Url(href);

        return new Unsupported(href);
    }

    /// <summary>Sends the link to the page that shows it. Unsupported links do nothing.</summary>
    public void Open()
    {
        var nav = EntityNavigator.Instance;
        switch (this)
        {
            case Item i:      nav.OpenItem?.Invoke(i.TypeId); break;
            case Entity e:    nav.Entity(e.Kind, e.Id); break;
            case SolarSystem s: nav.OpenSystem?.Invoke(s.SystemId); break;
            case Region r:    nav.OpenRegion?.Invoke(r.RegionId); break;
            case Structure st: nav.OpenStructure?.Invoke(st.StructureId); break;
            case Killmail km: nav.OpenKillmail?.Invoke(km.KillmailId); break;
            case Contract ct: nav.OpenContract?.Invoke(ct.ContractId); break;
            case Url u:       ExternalLinks.Open(u.Address); break;
            case Unsupported: break;
        }
    }

    public bool IsSupported => this is not Unsupported;
}
