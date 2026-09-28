using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EveConsole.Services;

/// <summary>What became of a message sent in parts.</summary>
/// <param name="Posted">How many parts Slack took, in order. None after the first refusal was sent.</param>
/// <param name="Total">How many parts the message was cut into.</param>
/// <param name="Characters">What the parts Slack took add up to.</param>
/// <param name="Error">Why the part that stopped it was refused; null when every part went.</param>
public sealed record SlackPartsResult(int Posted, int Total, int Characters, string? Error)
{
    public bool AllPosted => Total > 0 && Posted == Total;
}

/// <summary>
/// Cuts a long Slack message into posts Slack will not cut for us.
///
/// <para>⚠️ Slack splits a message whose text runs much past 4,000 characters into two, by length
/// alone — through the middle of a code block. The first half never closes its block, the second
/// opens inside a table with no fence, and every column in both falls apart. A weekly scheduled
/// post arrived exactly like that once its lists grew: ties at the foot of a Top 10 have no
/// ceiling, and a standing project scoped to a region is a row per system.</para>
///
/// <para>So a long message goes out as several, each whole in itself:</para>
/// <list type="bullet">
/// <item>A section — a heading and its table, or a paragraph of text — that fits in one message is
/// never split. If it does not fit in what is left of the current message, it starts the next.</item>
/// <item>Only a section longer than a whole message is split, between two of its lines. A code
/// block cut there is closed at the end of one message and opened again at the start of the next,
/// with its header row and rule repeated, so the continuation still says what its columns are.</item>
/// <item>A heading never ends a message with its table in the next, and a block is never cut so
/// that one side of the cut holds no rows.</item>
/// </list>
/// </summary>
public static class SlackMessageSplitter
{
    /// <summary>
    /// The most one part holds.
    ///
    /// <para>Slack's own guidance is 4,000 characters. This stays well inside it, because a part
    /// that crossed the line would be split again by Slack, fence and all — and Slack may count
    /// the text after escaping it, where every ampersand is five characters.</para>
    /// </summary>
    public const int MaxPartLength = 3500;

    private const string Fence = "```";

    /// <summary>Slack asks for no more than about one message a second to a channel or a
    /// webhook. The parts are spaced to match rather than risk a refusal halfway through.</summary>
    private static readonly TimeSpan PartGap  = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Posts <paramref name="text"/> in as many parts as it needs, in order, through
    /// <paramref name="post"/>. Stops at the first part Slack refuses, since anything sent after a
    /// gap would read as though nothing were missing.
    /// </summary>
    public static async Task<SlackPartsResult> PostAsync(
        string text,
        Func<string, CancellationToken, Task<SlackPostResult>> post,
        CancellationToken ct = default)
    {
        var parts = Split(text);
        if (parts.Count == 0) return new SlackPartsResult(0, 0, 0, "Nothing to post.");

        var characters = 0;

        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) await Task.Delay(PartGap, ct);

            var res = await post(parts[i], ct);

            // ⚠️ A later part gets a second try. The parts before it are already in the channel,
            // so giving up leaves a post with its end missing — and nobody can simply run the
            // whole thing again without repeating its start.
            if (!res.Ok && i > 0)
            {
                await Task.Delay(RetryGap, ct);
                res = await post(parts[i], ct);
            }

            if (!res.Ok) return new SlackPartsResult(i, parts.Count, characters, res.Error);

            characters += parts[i].Length;
        }

        return new SlackPartsResult(parts.Count, parts.Count, characters, null);
    }

    /// <summary>
    /// Cuts <paramref name="text"/> into parts of at most <paramref name="max"/> characters. Text
    /// that already fits comes back whole, as the one part.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int max = MaxPartLength)
    {
        text = text.Replace("\r\n", "\n").Trim();

        if (text.Length == 0)   return [];
        if (text.Length <= max) return [text];

        var parts   = new List<string>();
        var current = new StringBuilder();
        var inFence = false;

        foreach (var unit in Units(text, max))
        {
            var fenceAfter = inFence ^ unit.Toggles;
            var separator  = current.Length == 0 ? "" : unit.Separator;

            // Room for the unit, and for the fence that would close its block if the message had
            // to end straight after it.
            var needed = separator.Length + unit.Text.Length + (fenceAfter ? 1 + Fence.Length : 0);

            if (current.Length > 0 && current.Length + needed > max)
            {
                if (inFence) current.Append('\n').Append(Fence);
                parts.Add(current.ToString());
                current.Clear();

                // The block this unit belongs to carries on in the new message, so it opens again,
                // header first.
                if (inFence) current.Append(unit.Reopen);
                separator = current.Length == 0 ? "" : "\n";
            }

            current.Append(separator).Append(unit.Text);
            inFence = fenceAfter;
        }

        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    /// <summary>A run of lines that goes into one message or the next, never both.</summary>
    /// <param name="Separator">What joins it to the text before it when both land in one message:
    /// the blank lines that stood between two sections, or a line break within one.</param>
    /// <param name="Toggles">Whether it leaves a code block open that was shut before it, or the
    /// other way round.</param>
    /// <param name="Reopen">What starts the next message when this unit continues a block the
    /// last message had to close.</param>
    private readonly record struct Unit(string Separator, string Text, bool Toggles, string Reopen);

    private static IEnumerable<Unit> Units(string text, int max)
    {
        // Sections first: the runs of lines between blank lines. ⚠️ Only a blank line OUTSIDE a
        // code block ends one — inside a block it is part of the table.
        var sections = new List<(string Separator, List<string> Lines)>();
        List<string>? section = null;
        var blanks  = 0;
        var inFence = false;

        foreach (var line in text.Split('\n'))
        {
            if (!inFence && line.Trim().Length == 0)
            {
                section = null;
                blanks++;
                continue;
            }

            if (section is null)
            {
                section = [];
                sections.Add((new string('\n', blanks + 1), section));
                blanks = 0;
            }

            section.Add(line);
            if (TogglesFence(line)) inFence = !inFence;
        }

        // Lines longer than this are wrapped, and lines are only held together up to it. That is
        // what keeps a reopened block, its repeated header and the unit after them inside one
        // message however long the lines get.
        var cap = max / 4;

        foreach (var (separator, lines) in sections)
        {
            var whole   = string.Join("\n", lines);
            var toggles = lines.Count(TogglesFence) % 2 == 1;

            if (whole.Length + (toggles ? 1 + Fence.Length : 0) <= max)
            {
                yield return new Unit(separator, whole, toggles, Fence);
                continue;
            }

            foreach (var unit in LineUnits(separator, lines, cap)) yield return unit;
        }
    }

    /// <summary>
    /// A section too long for one message, as the runs of lines it may be cut between.
    ///
    /// <para>Most lines stand alone. The ones that cannot begin or end a message are held to the
    /// line before them: a block's opening fence to the heading over it, its first row to the
    /// fence, a column rule to the header it underlines, the row under a rule to the rule, and a
    /// closing fence to the last row.</para>
    /// </summary>
    private static IEnumerable<Unit> LineUnits(string separator, List<string> lines, int cap)
    {
        var inFence = false;
        var rows    = 0;         // lines of the current block before this one
        var ruled   = false;     // the line before this one was a column rule
        string? header = null;   // the block's first row, until the second says whether it heads a table
        string? head   = null;   // the header and its rule, once both have been seen

        var unit        = new List<string>();
        var unitLength  = 0;
        var unitToggles = false;
        var unitReopen  = Fence;
        var first       = true;

        Unit Take()
        {
            var taken = new Unit(first ? separator : "\n", string.Join("\n", unit), unitToggles, unitReopen);
            first = false;
            unit.Clear();
            unitLength  = 0;
            unitToggles = false;
            return taken;
        }

        foreach (var raw in lines)
        foreach (var line in Wrap(raw, cap))
        {
            var toggles = TogglesFence(line);
            var opening = toggles && !inFence;
            var closing = toggles && inFence;
            var rule    = inFence && !toggles && IsColumnRule(line);

            var held = opening
                    || closing
                    || (inFence && rows == 0)
                    || rule
                    || (inFence && ruled);

            if (unit.Count > 0 && !(held && unitLength + 1 + line.Length <= cap))
                yield return Take();

            // Decided by where the unit starts: inside a table with a header, the header comes
            // back with the fence.
            if (unit.Count == 0) unitReopen = head is null ? Fence : $"{Fence}\n{head}";

            unitLength  += (unit.Count > 0 ? 1 : 0) + line.Length;
            unitToggles ^= toggles;
            unit.Add(line);

            if (opening || closing)
            {
                inFence = opening;
                rows    = 0;
                ruled   = false;
                header  = null;
                head    = null;
                continue;
            }

            if (!inFence) continue;

            if (rows == 0)                          header = line;
            else if (rows == 1 && rule && header is not null) head = $"{header}\n{line}";

            ruled = rule;
            rows++;
        }

        if (unit.Count > 0) yield return Take();
    }

    /// <summary>Whether a line opens or closes a code block: an odd number of fences, so one
    /// written inline and closed on the same line changes nothing.</summary>
    private static bool TogglesFence(string line)
    {
        var count = 0;
        for (var i = line.IndexOf(Fence, StringComparison.Ordinal);
             i >= 0;
             i = line.IndexOf(Fence, i + Fence.Length, StringComparison.Ordinal))
            count++;

        return count % 2 == 1;
    }

    /// <summary>
    /// A rule under a table's columns: dashes and spaces, in two runs or more.
    ///
    /// <para>⚠️ Two runs, not one. A single run is a line under a title — the plain-text exports
    /// print one under every heading — and repeating a title as though it were a column header
    /// would put the first list's name on top of every continuation.</para>
    /// </summary>
    private static bool IsColumnRule(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.Any(c => c != '-' && c != ' ')) return false;

        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }

    /// <summary>A line cut to fit, at a space where there is one. Only a line hundreds of
    /// characters long ever reaches here — a paragraph typed as one line.</summary>
    private static IEnumerable<string> Wrap(string line, int cap)
    {
        if (line.Length <= cap)
        {
            yield return line;
            yield break;
        }

        while (line.Length > cap)
        {
            var cut = line.LastIndexOf(' ', cap);
            if (cut < cap / 2) cut = cap;

            yield return line[..cut].TrimEnd();
            line = line[cut..].TrimStart();
        }

        if (line.Length > 0) yield return line;
    }
}
