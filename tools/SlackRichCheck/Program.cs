using System.Text.Json;
using EveConsole.Api;
using EveConsole.Auth;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

// ─────────────────────────────────────────────────────────────────────────────
//  Slack rich format check
//
//  Checks the scheduled posts' rich Slack format, on constructed lists and a throwaway database.
//
//  WHY. Nothing here fails loudly. Columns that drift, a corp shown with a pilot's portrait, a
//  share worked out from the wrong total or a kill with two pilots counted as solo all look like a
//  finished post. Slack itself only refuses the shapes it has limits on, and a refusal at 00:01
//  is a post nobody sees.
//
//  1. Padding (2026-11-04). Measured on PC and Android: only an en space and a three-per-em space
//     keep their width in a container on a phone. A list padded with anything else lined up on the
//     PC it was checked on, and fell apart on the phone.
//  2. Share (found 2026-11-04). A post worked out each share from the rows it printed, so the top
//     ratter read 10% where the Top 10 tab said 2%.
// ─────────────────────────────────────────────────────────────────────────────

// ⚠️ SQLite, pinned, before anything reads the engine: the database section must never fall through
// to a developer machine's PostgreSQL config and touch a real server.
DbEngine.Pin(DbBackend.Sqlite);

// The words and numbers below are compared in English, whatever the machine's language.
var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = english;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = english;
System.Globalization.CultureInfo.CurrentCulture = english;
System.Globalization.CultureInfo.CurrentUICulture = english;

var failures = new List<string>();
var checks   = 0;

void Check(string name, bool ok, string detail = "")
{
    checks++;
    if (!ok) failures.Add($"  FAIL  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
}

const char En = '\u2002';
const char Third = '\u2004';

JsonElement Json(object block) => JsonDocument.Parse(JsonSerializer.Serialize(block)).RootElement;

// How wide a placed row really draws, counting only the two blanks it may hold.
int Drawn(string text)
{
    var total = 0;
    var word  = new System.Text.StringBuilder();

    void Flush() { total += SlackColumns.Width(word.ToString()); word.Clear(); }

    foreach (var c in text)
    {
        if (c is En)         { Flush(); total += 497; }
        else if (c is Third) { Flush(); total += 331; }
        else word.Append(c);
    }
    Flush();
    return total;
}

// ── Widths ────────────────────────────────────────────────────────────────────

Check("width: letters add up", SlackColumns.Width("Hi") == 756 + 256, SlackColumns.Width("Hi").ToString());
Check("width: a kerned pair is narrower", SlackColumns.Width("Ta") == 590 + 507 - 125, SlackColumns.Width("Ta").ToString());
Check("width: digits are all one width", SlackColumns.Width("111") == SlackColumns.Width("808"));
Check("width: a word space is between what the two apps draw", SlackColumns.Width(" ") is > 193 and < 256);
Check("width: wide characters count as a full em", SlackColumns.Width("\u4E2D") == 1000);

// ── Blanks ────────────────────────────────────────────────────────────────────

foreach (var want in new[] { 400, 700, 1000, 2345, 5000, 20000 })
{
    var (text, width) = SlackColumns.Blanks(want);
    Check($"blanks: only the two that hold on a phone, for {want}", text.All(c => c is En or Third));
    Check($"blanks: close to {want}", Math.Abs(width - want) <= 166, $"{width}");
    Check($"blanks: say what they come to, for {want}", width == text.Count(c => c == En) * 497 + text.Count(c => c == Third) * 331);
}

Check("blanks: none for nothing", SlackColumns.Blanks(0).Text.Length == 0);
Check("blanks: none for a negative width", SlackColumns.Blanks(-500).Text.Length == 0);
Check("blanks: none for less than the smallest", SlackColumns.Blanks(100).Text.Length == 0);

// ── A row's columns ───────────────────────────────────────────────────────────

{
    string[][] rows =
    [
        ["Tamsin Rook", "14.07B", "13.2%"],
        ["Self-Harmonizing Power Core", "9.61B", "9.0%"],
        ["Li", "1.06B", "0.4%"],
        ["Wmm Mmmm", "812.50M", "100.0%"],
    ];
    int[] widths = [rows.Max(r => SlackColumns.Width(r[0])), rows.Max(r => SlackColumns.Width(r[1])), rows.Max(r => SlackColumns.Width(r[2]))];
    int[] starts = [0, widths[0] + 750, widths[0] + 750 + widths[1] + 600];
    bool[] right = [false, true, true];

    foreach (var r in rows)
    {
        var line  = SlackColumns.Place(r, starts, widths, right);
        var parts = line.Split(new[] { En, Third }, StringSplitOptions.RemoveEmptyEntries);
        Check($"place: cells in order, {r[0]}", parts.SequenceEqual(r));

        // Right edges are where the columns line up: within one step of the padding.
        var valueEnd = Drawn(line[..(line.IndexOf(r[1], StringComparison.Ordinal) + r[1].Length)]);
        var shareEnd = Drawn(line);
        // ⚠️ Half a step, on every column. Each gap comes from where the text before it really
        // ended, so the error does not add up along the row.
        Check($"place: value column ends together, {r[0]}", Math.Abs(valueEnd - (starts[1] + widths[1])) <= 85, $"{valueEnd} vs {starts[1] + widths[1]}");
        Check($"place: share column ends together, {r[0]}", Math.Abs(shareEnd - (starts[2] + widths[2])) <= 85, $"{shareEnd} vs {starts[2] + widths[2]}");
    }

    var blank = SlackColumns.Place(["Nocxium", "", "2026-11-04"], [0, 3000, 8000], [2000, 2000, 3000], [false, true, false]);
    Check("place: a blank cell keeps the next column in place", Math.Abs(Drawn(blank) - (8000 + SlackColumns.Width("2026-11-04"))) <= 166, Drawn(blank).ToString());

    var offset = SlackColumns.Place(["Project"], [0], [3000], [false], 1923);
    Check("place: an offset moves the first column", offset.StartsWith(SlackColumns.Blanks(1923).Text, StringComparison.Ordinal) && offset.EndsWith("Project", StringComparison.Ordinal));

    Check("escape: the three characters Slack's markup needs", SlackColumns.Escape("A&B <c>") == "A&amp;B &lt;c&gt;");
    Check("place: a name is escaped", SlackColumns.Place(["A&B"], [0], [2000], [false]) == "A&amp;B");
    Check("place: a name is measured as it draws, not escaped",
        Drawn(SlackColumns.Place(["A&B", "1"], [0, 5000], [SlackColumns.Width("A&B"), 580], [false, false]).Replace("&amp;", "&")) is > 5000 - 170 and < 5580 + 170);
}

{
    const string Long = "Interstellar Mining & Logistics Consortium";
    var cut = SlackColumns.Cut(Long, 11000);
    Check("cut: fits", SlackColumns.Width(cut) <= 11000, SlackColumns.Width(cut).ToString());
    Check("cut: marked", cut.EndsWith("...", StringComparison.Ordinal) && Long.StartsWith(cut[..^3], StringComparison.Ordinal));
    Check("cut: not by more than a letter or two", SlackColumns.Width(cut) > 11000 - 1500);
    Check("cut: a name that fits comes back whole", SlackColumns.Cut("Vael Orrin", 11000) == "Vael Orrin");
}

// ── Top 10 boxes ──────────────────────────────────────────────────────────────

SlackRichRow Row(int i, string kind = "character", string? name = null, string? value = "1.00B") => new(
    name ?? $"Pilot {i}",
    SlackRichBlocks.EntityImage(90_000_000 + i, kind, 64),
    SlackRichBlocks.EntityImage(90_000_000 + i, kind, 128),
    value, value is null ? null : value + " ISK", "2.0%");

List<SlackRichRow> Rows(int n) => [.. Enumerable.Range(1, n).Select(i => Row(i))];

{
    Check("image: a pilot's portrait", SlackRichBlocks.EntityImage(5, "character", 64).Contains("/characters/5/portrait?size=64"));
    Check("image: a corp's logo", SlackRichBlocks.EntityImage(5, "corporation", 64).Contains("/corporations/5/logo?size=64"));
    Check("image: an alliance's logo", SlackRichBlocks.EntityImage(5, "alliance", 128).Contains("/alliances/5/logo?size=128"));
    Check("image: unknown is a pilot", SlackRichBlocks.EntityImage(5, "", 64).Contains("/characters/5/portrait"));
    Check("image: an item's icon", SlackRichBlocks.ItemIcon(34).Contains("/types/34/icon?size=64"));

    JsonElement Boxes(params SlackRichList[] lists) => Json(SlackRichBlocks.Top10Boxes(lists, "Top 10", "First", "{0} of total"));

    var three = Boxes(new("A", Rows(10)), new("B", Rows(10)), new("C", Rows(10)));
    var rows3 = three.GetProperty("rows");
    Check("boxes: a layout", three.GetProperty("type").GetString() == "layout");
    Check("boxes: two to a row", rows3.GetArrayLength() == 2 && rows3[0].GetProperty("items").GetArrayLength() == 2);
    Check("boxes: a lone box has something beside it", rows3[1].GetProperty("items").GetArrayLength() == 2
        && rows3[1].GetProperty("items")[1].GetProperty("block").GetProperty("type").GetString() == "section");
    Check("boxes: ten lists stay inside a layout's five rows",
        Boxes([.. Enumerable.Range(0, 10).Select(i => new SlackRichList($"L{i}", Rows(3)))]).GetProperty("rows").GetArrayLength() == 5);

    var box = rows3[0].GetProperty("items")[0].GetProperty("block");
    var kids = box.GetProperty("child_blocks");
    Check("box: a container", box.GetProperty("type").GetString() == "container");
    Check("box: titled, which a container must be", box.GetProperty("title").GetProperty("text").GetString() == "A");
    Check("box: number 1 and nine rows", kids.GetArrayLength() == 10, kids.GetArrayLength().ToString());
    Check("box: number 1's image is the 128 px one", kids[0].GetProperty("accessory").GetProperty("image_url").GetString()!.EndsWith("size=128", StringComparison.Ordinal));
    var featured = kids[0].GetProperty("text").GetProperty("text").GetString()!;
    Check("box: number 1 has its label, name, value and share, in that order", featured == "First\n*Pilot 1*\n*1.00B ISK*\n2.0% of total", featured);
    Check("box: no medal", !featured.Contains(":first_place_medal:"));
    Check("box: rows go on from rank 2", kids[1].GetProperty("elements")[0].GetProperty("text").GetString()!.Contains("2."));
    Check("box: the last row is rank 10", kids[9].GetProperty("elements")[0].GetProperty("text").GetString() == "*10.*");
    Check("box: a row's image is its own", kids[3].GetProperty("elements")[1].GetProperty("image_url").GetString()!.Contains("/characters/90000004/"));
    Check("box: data is sent verbatim, so a name cannot ping or link", kids[1].GetProperty("elements")[2].GetProperty("verbatim").GetBoolean());

    var longTitle = Boxes(new SlackRichList(new string('x', 400), Rows(2)));
    Check("box: a title is cut to what Slack takes",
        longTitle.GetProperty("rows")[0].GetProperty("items")[0].GetProperty("block").GetProperty("title").GetProperty("text").GetString()!.Length <= SlackRichBlocks.MaxTitle);

    // A heading somebody wrote with an emoji in it has to draw one.
    var section = box.GetProperty("rich_text_title").GetProperty("elements")[0];
    Check("title: rich text, which is the only kind that can draw an emoji",
        box.GetProperty("rich_text_title").GetProperty("type").GetString() == "rich_text" && section.GetProperty("type").GetString() == "rich_text_section");
    Check("title: words stay bold", section.GetProperty("elements")[0].GetProperty("style").GetProperty("bold").GetBoolean());

    var parts = Json(SlackRichBlocks.RichTitle(":explodey: Kills :top: - October")).GetProperty("elements")[0].GetProperty("elements");
    string[] said = [.. parts.EnumerateArray().Select(e => e.GetProperty("type").GetString() == "emoji"
        ? "E:" + e.GetProperty("name").GetString() : e.GetProperty("text").GetString()!)];
    Check("title: emoji are elements of their own, in order", said.SequenceEqual(["E:explodey", " Kills ", "E:top", " - October"]), string.Join("|", said));

    string[] Said(string title) => [.. Json(SlackRichBlocks.RichTitle(title)).GetProperty("elements")[0].GetProperty("elements").EnumerateArray()
        .Select(e => e.GetProperty("type").GetString()!)];
    Check("title: no emoji is one run of words", Said("Kills - October 2026").SequenceEqual(["text"]));
    Check("title: a clock time is not an emoji", Said("Report at 12:30:45").SequenceEqual(["text"]));
    Check("title: a name with a digit, dash or plus is", Said(":indy3: a :+1: b :a-b:").Count(s => s == "emoji") == 3);
    Check("title: an emoji alone is fine", Said(":bolt:").SequenceEqual(["emoji"]));
    var longRich = Json(SlackRichBlocks.RichTitle(new string('x', 400))).GetProperty("elements")[0].GetProperty("elements")[0].GetProperty("text").GetString()!;
    Check("title: cut to what Slack takes", longRich.Length <= SlackRichBlocks.MaxTitle);

    var alone = Boxes(new SlackRichList("A", Rows(1))).GetProperty("rows")[0].GetProperty("items")[0].GetProperty("block").GetProperty("child_blocks");
    Check("box: a list of one is only number 1", alone.GetArrayLength() == 1 && alone[0].GetProperty("type").GetString() == "section");

    // Ties have no ceiling, and a container holds ten blocks.
    var tiedKids = Boxes(new SlackRichList("A", Rows(16))).GetProperty("rows")[0].GetProperty("items")[0].GetProperty("block").GetProperty("child_blocks");
    Check("ties: still ten blocks", tiedKids.GetArrayLength() == 10, tiedKids.GetArrayLength().ToString());
    var tied = tiedKids[9].GetProperty("elements");
    Check("ties: a context's ten elements at most", tied.GetArrayLength() <= 10, tied.GetArrayLength().ToString());
    Check("ties: at rank 10", tied[0].GetProperty("text").GetString() == "*10.*");
    Check("ties: the rest are counted, not lost", tied[tied.GetArrayLength() - 1].GetProperty("text").GetString()!.Contains("(+3)"),
        tied[tied.GetArrayLength() - 1].GetProperty("text").GetString() ?? "");
    var justOver = Boxes(new SlackRichList("A", Rows(11))).GetProperty("rows")[0].GetProperty("items")[0].GetProperty("block").GetProperty("child_blocks")[9].GetProperty("elements");
    Check("ties: two at rank 10 are both named", justOver.GetArrayLength() == 5 && !justOver[4].GetProperty("text").GetString()!.Contains("(+"));

    // The amount hidden: a share and nothing else.
    var hidden = Boxes(new SlackRichList("A", [.. Enumerable.Range(1, 5).Select(i => Row(i, value: null))]))
        .GetProperty("rows")[0].GetProperty("items")[0].GetProperty("block").GetProperty("child_blocks");
    Check("hidden: number 1 has no amount", !hidden[0].GetProperty("text").GetProperty("text").GetString()!.Contains("ISK"));
    Check("hidden: rows have no amount", !hidden[1].GetProperty("elements")[2].GetProperty("text").GetString()!.Contains("B"));
    Check("hidden: rows keep their share", hidden[1].GetProperty("elements")[2].GetProperty("text").GetString()!.Contains("2.0%"));

    // A name too long for a box two to a row would wrap under its icon.
    var longRow = Boxes(new SlackRichList("A", [Row(1), Row(2, name: "Interstellar Mining & Logistics Consortium of the Far Reach")]))
        .GetProperty("rows")[0].GetProperty("items")[0].GetProperty("block").GetProperty("child_blocks")[1].GetProperty("elements")[2].GetProperty("text").GetString()!;
    Check("long name: cut to fit the box", longRow.Contains("...") && Drawn(longRow.Replace("&amp;", "&")) <= 18460 + 166, Drawn(longRow.Replace("&amp;", "&")).ToString());
}

// ── System Index and Indy Park Compare ────────────────────────────────────────

{
    Check("emoji name: with colons", SlackRichBlocks.EmojiNameOr(":green_down:", "x") == "green_down");
    Check("emoji name: without", SlackRichBlocks.EmojiNameOr(" green_down ", "x") == "green_down");
    Check("emoji name: blank is the default", SlackRichBlocks.EmojiNameOr("", "x") == "x" && SlackRichBlocks.EmojiNameOr(null, "x") == "x");
    Check("emoji name: nonsense is the default, not a failed post", SlackRichBlocks.EmojiNameOr("a b!", "x") == "x" && SlackRichBlocks.EmojiNameOr("UPPER", "x") == "x");

    Check("outputs: the Slack table is not a picture", !SystemIndexOutputs.IsPicture(SystemIndexOutputs.NativeTable) && !SystemIndexOutputs.IsPicture(SystemIndexOutputs.Table));
    Check("outputs: combined and activities are", SystemIndexOutputs.IsPicture(SystemIndexOutputs.Combined) && SystemIndexOutputs.IsPicture("manufacturing"));
    Check("outputs: a new index section uses one upload", SystemIndexOutputs.Default.Count(SystemIndexOutputs.IsPicture) == 1);
    Check("outputs: a new park section uses none", !ParkCompareOutputs.Default.Any(ParkCompareOutputs.IsPicture));

    MessageBlock Index(params string[] o) => new() { Type = MessageBlock.TypeSystemIndex, IndexOutputs = [.. o] };
    MessageBlock Park(params string[] o) => new() { Type = MessageBlock.TypeParkCompare, CompareOutputs = [.. o] };
    Check("saved: a schedule's text table stays text", !Index(SystemIndexOutputs.Table).UsesSlackRich && !Park(ParkCompareOutputs.SummaryJobsTable).UsesSlackRich);
    Check("saved: a picture alone is not blocks", !Index("manufacturing").UsesSlackRich && !Park(ParkCompareOutputs.SummaryJobsImage).UsesSlackRich);
    Check("ticked: the Slack table is", Index(SystemIndexOutputs.NativeTable).UsesSlackRich);
    Check("ticked: the park table and boxes are", Park(ParkCompareOutputs.NativeTable).UsesSlackRich && Park(ParkCompareOutputs.JobBoxes).UsesSlackRich);
    Check("pictures: the Slack table draws none", !Index(SystemIndexOutputs.NativeTable).HasPictures && Index(SystemIndexOutputs.Combined).HasPictures);

    // Index table: real cells, the dot before the value, ties marked, gaps said.
    var indexItems = SlackRichBlocks.IndexTable("*Heading*", ["A", "B"], ["Mfg", "Inv"], "System",
        [[0.05, 0.02], [0.04, 0.02]], [[0.11, -0.5], [0.0, null]], "dot", "up", "down", "{0} low {1} {2} change");
    Check("index: heading, table, note", indexItems.Count == 3);
    var indexTable = Json(indexItems[1].Block);
    Check("index: a table with the system first", indexTable.GetProperty("type").GetString() == "table" && indexTable.GetProperty("rows")[0][0].GetProperty("text").GetString() == "System");
    Check("index: every value column on the right", indexTable.GetProperty("column_settings")[1].GetProperty("align").GetString() == "right" && indexTable.GetProperty("column_settings")[0].GetProperty("align").GetString() == "left");

    string[] Parts(JsonElement cell) => [.. cell.GetProperty("elements")[0].GetProperty("elements").EnumerateArray().Select(e =>
        e.GetProperty("type").GetString() == "emoji" ? ":" + e.GetProperty("name").GetString() : e.GetProperty("text").GetString()!)];

    var low = Parts(indexTable.GetProperty("rows")[2][1]);
    Check("index: the lowest has its dot first, so the number stays in line", low[0] == ":dot" && low.Contains("4.00%"), string.Join("|", low));
    var notLow = Parts(indexTable.GetProperty("rows")[1][1]);
    Check("index: the others have none", !notLow.Contains(":dot"), string.Join("|", notLow));
    Check("index: a rise", notLow.Contains(":up") && notLow.Contains(" 0.11"), string.Join("|", notLow));
    var fall = Parts(indexTable.GetProperty("rows")[1][2]);
    Check("index: a fall, and a tie for lowest marks both", fall.Contains(":down") && fall.Contains(" 0.50") && fall[0] == ":dot", string.Join("|", fall));
    Check("index: no change says so", low.Contains("="), string.Join("|", low));
    Check("index: no reading a week ago says nothing", Parts(indexTable.GetProperty("rows")[2][2]).Length == 3, string.Join("|", Parts(indexTable.GetProperty("rows")[2][2])));
    var noteText = Json(indexItems[2].Block).GetProperty("elements")[0].GetProperty("text").GetString()!;
    Check("index: the note shows all three of the section's emoji", noteText == ":dot: low :up: :down: change", noteText);

    var gaps = Json(SlackRichBlocks.IndexTable("", ["A"], ["Mfg"], "System", [[null]], [[null]], "d", "u", "w", "{0}{1}{2}")[0].Block);
    Check("index: a missing reading is a dash", gaps.GetProperty("rows")[1][1].GetProperty("text").GetString() == "–");

    // Week's change.
    var today = new DateOnly(2026, 11, 5);
    SortedDictionary<DateOnly, double> Series(params (int Back, double V)[] s) => new(s.ToDictionary(x => today.AddDays(-x.Back), x => x.V));
    Check("week: against 7 days back, in points", SystemIndexReport.WeekChange(Series((0, 0.05), (7, 0.04)), 0.05) is { } w && Math.Abs(w - 1) < 1e-9);
    Check("week: a day or two's gap still counts", SystemIndexReport.WeekChange(Series((0, 0.05), (9, 0.03)), 0.05) is { } g && Math.Abs(g - 2) < 1e-9);
    Check("week: a long gap is not a week's change", SystemIndexReport.WeekChange(Series((0, 0.05), (20, 0.03)), 0.05) is null);
    Check("week: nothing older, no change", SystemIndexReport.WeekChange(Series((0, 0.05), (2, 0.04)), 0.05) is null);
    Check("week: no history, no change", SystemIndexReport.WeekChange(null, 0.05) is null);

    // Park table.
    List<SlackParkRow> ParkRows(int parks) =>
    [
        new("Vexor", [.. Enumerable.Range(0, parks).Select(p => (SlackParkCell?)new(p == 0 ? "10.91M" : "11.01M +0.9%", p == 0, p == 1 ? "45m" : "1h 32m +109.4%", p == 1))]),
        new("Ishtar", [.. Enumerable.Range(0, parks).Select(p => p == 2 ? null : (SlackParkCell?)new("1.00M", false, "1h", false))]),
    ];
    var parkItems = SlackRichBlocks.ParkTable("", "Job", ParkRows(3), ["P1", "P2", "P3"], "Cost", "Time", "dot", "{0} best");
    var parkTable = Json(parkItems[0].Block);
    string[] parkHead = [.. parkTable.GetProperty("rows")[0].EnumerateArray().Select(c => c.GetProperty("text").GetString()!)];
    Check("park: job, then cost and time for each", parkHead.SequenceEqual(["Job", "P1 Cost", "P1 Time", "P2 Cost", "P2 Time", "P3 Cost", "P3 Time"]), string.Join("|", parkHead));
    Check("park: the best has its dot first", Parts(parkTable.GetProperty("rows")[1][1])[0] == ":dot" && Parts(parkTable.GetProperty("rows")[1][4])[0] == ":dot");
    Check("park: the others are plain, with how far behind", parkTable.GetProperty("rows")[1][3].GetProperty("text").GetString() == "11.01M +0.9%");
    Check("park: a park that cannot do it is a dash", parkTable.GetProperty("rows")[2][5].GetProperty("text").GetString() == "–");
    var manyParks = SlackRichBlocks.ParkTable("*H*", "Job", ParkRows(12), [.. Enumerable.Range(0, 12).Select(i => $"P{i}")], "Cost", "Time", "dot", "{0} best");
    var tables = manyParks.Select(i => Json(i.Block)).Where(j => j.GetProperty("type").GetString() == "table").ToList();
    Check("park: past a table's 20 columns, another table", tables.Count == 2 && tables.All(t => t.GetProperty("rows")[0].GetArrayLength() <= 20), tables.Count.ToString());
    Check("park: every park shows", tables.Sum(t => (t.GetProperty("rows")[0].GetArrayLength() - 1) / 2) == 12);

    // Job boxes.
    SlackJobBox Box(int i, int parks = 3) => new($"Job {i}", "About", "https://images.evetech.net/types/34/icon?size=64",
        [.. Enumerable.Range(0, parks).Select(p => p == 2 ? null : (SlackJobCell?)new(p == 0 ? "10.91M" : "119.64M", p == 0 ? "Best" : "+119.4%", p == 1 ? "45m" : "30d 16h", p == 1 ? "Best" : "+109.4%"))]);
    var boxItems = SlackRichBlocks.JobBoxes([.. Enumerable.Range(0, 6).Select(i => Box(i))], ["North", "South", "East"], "the key", "+{0} more");
    Check("boxes: a layout and the key", boxItems.Count == 2);
    var boxLayout = Json(boxItems[0].Block);
    Check("boxes: six jobs, three rows of two", boxLayout.GetProperty("rows").GetArrayLength() == 3 && boxLayout.GetProperty("rows")[0].GetProperty("items").GetArrayLength() == 2);
    var firstBox = boxLayout.GetProperty("rows")[0].GetProperty("items")[0].GetProperty("block");
    Check("boxes: the job's title", firstBox.GetProperty("title").GetProperty("text").GetString() == "Job 0");
    var boxKids = firstBox.GetProperty("child_blocks");
    Check("boxes: details beside the picture, and every park under them", boxKids.GetArrayLength() == 4 && boxKids[0].GetProperty("accessory").GetProperty("type").GetString() == "image");

    string BoxRow(JsonElement kid) => kid.GetProperty("elements")[0].GetProperty("text").GetString()!;
    string[] Cells(string row) => row.Split(new[] { En, Third }, StringSplitOptions.RemoveEmptyEntries);
    Check("boxes: park, cost and time, each with how far behind", Cells(BoxRow(boxKids[2])).SequenceEqual(["South", "119.64M", "+119.4%", "45m", "Best"]), BoxRow(boxKids[2]));
    Check("boxes: the word Best, not an icon", Cells(BoxRow(boxKids[1])).Contains("Best") && !BoxRow(boxKids[1]).Contains(':'), BoxRow(boxKids[1]));
    Check("boxes: not numbered", !BoxRow(boxKids[1]).StartsWith("1."));
    Check("boxes: a park that cannot do it is a dash", Cells(BoxRow(boxKids[3])).SequenceEqual(["East", "–", "–"]), BoxRow(boxKids[3]));
    Check("boxes: columns line up down a box", Math.Abs(Drawn(BoxRow(boxKids[1])) - Drawn(BoxRow(boxKids[2]))) <= 170, $"{Drawn(BoxRow(boxKids[1]))} {Drawn(BoxRow(boxKids[2]))}");

    var oddBoxes = Json(SlackRichBlocks.JobBoxes([Box(0)], ["A", "B", "C"], "k", "+{0}")[0].Block);
    Check("boxes: a lone job keeps its width", oddBoxes.GetProperty("rows")[0].GetProperty("items").GetArrayLength() == 2);
    var lots = SlackRichBlocks.JobBoxes([.. Enumerable.Range(0, 13).Select(i => Box(i))], ["A", "B", "C"], "k", "+{0}");
    Check("boxes: past ten jobs, another layout", lots.Count == 3 && Json(lots[0].Block).GetProperty("rows").GetArrayLength() == 5, lots.Count.ToString());
    var crowd = Json(SlackRichBlocks.JobBoxes([Box(0, 12)], [.. Enumerable.Range(0, 12).Select(i => $"P{i}")], "k", "+{0} more")[0].Block)
        .GetProperty("rows")[0].GetProperty("items")[0].GetProperty("block").GetProperty("child_blocks");
    Check("boxes: past a box's ten blocks, the rest are counted", crowd.GetArrayLength() == 10 && BoxRow(crowd[9]) == "+4 more", crowd.GetArrayLength().ToString());
    var longPark = Json(SlackRichBlocks.JobBoxes([Box(0)], ["A park with a very long name that goes on and on", "B", "C"], "k", "+{0}")[0].Block)
        .GetProperty("rows")[0].GetProperty("items")[0].GetProperty("block").GetProperty("child_blocks");
    Check("boxes: a long park name is cut to fit", BoxRow(longPark[1]).Contains("...") && Drawn(BoxRow(longPark[1])) <= 21500 + 170, Drawn(BoxRow(longPark[1])).ToString());

    // What a box says.
    ParkJobFacts Facts(CompareOp op, bool render = false, bool reaction = false, decimal? value = 11_000_000m) =>
        new(0, op, 626, "Vexor", render, reaction, 10, value, Copies: 3, Level: 10, Target: "Ishtar", TargetRuns: 1, TargetMe: 2, TargetTe: 4);
    var about = ParkCompareReport.BoxAbout(Facts(CompareOp.Build), 10);
    Check("about: a build says how many of what, and what they sell for", about.Contains("Manufacturing") && about.Contains("10 × Vexor") && about.Contains("110.00M"), about);
    Check("about: a reaction says so", ParkCompareReport.BoxAbout(Facts(CompareOp.Build, reaction: true), 1).StartsWith("Reaction"));
    Check("about: with no price, no value line", !ParkCompareReport.BoxAbout(Facts(CompareOp.Build, value: null), 1).Contains("ISK"));
    var copy = ParkCompareReport.BoxAbout(Facts(CompareOp.Copy), 30);
    Check("about: a copy says copies and runs", copy.Contains('3') && copy.Contains("10"), copy);
    var invent = ParkCompareReport.BoxAbout(Facts(CompareOp.Invention), 1);
    Check("about: an invention says its target, runs, ME and TE", invent.Contains("Ishtar BPC") && invent.Contains("ME 2") && invent.Contains("TE 4"), invent);
    Check("about: ME research says from and to", ParkCompareReport.BoxAbout(Facts(CompareOp.MeResearch), 1).Contains("ME 0 to ME 10"));
    Check("about: TE research is in percent", ParkCompareReport.BoxAbout(Facts(CompareOp.TeResearch), 1).Contains("TE 0% to TE 20%"));
    Check("title: a build names the operation", ParkCompareReport.BoxTitle(Facts(CompareOp.Build), "Vexor") == "Vexor — Build");
    Check("title: a science job keeps its label", ParkCompareReport.BoxTitle(Facts(CompareOp.Copy), "Vexor Blueprint — Copy") == "Vexor Blueprint — Copy");
    Check("image: a ship's render", ParkCompareReport.BoxImage(Facts(CompareOp.Build, render: true)).EndsWith("/626/render?size=128"));
    Check("image: anything else built, an icon the server has", ParkCompareReport.BoxImage(Facts(CompareOp.Build)).EndsWith("/626/icon?size=64"));
    Check("image: a copy, the copy's image", ParkCompareReport.BoxImage(Facts(CompareOp.Copy)).EndsWith("/626/bpc?size=64"));
    Check("image: other science, the blueprint's", ParkCompareReport.BoxImage(Facts(CompareOp.MeResearch)).EndsWith("/626/bp?size=64"));
}

// ── The pictures ──────────────────────────────────────────────────────────────

{
    ParkCost Cost(int line, int park, string name, decimal total, double seconds) =>
        new(park, name, park, new ParkCompareJob(34, "Item", 1, 10, Line: line), true, 1, total, 0, total, [], seconds);

    var ranked = ParkCompareReport.Rank(
    [
        Cost(0, 1, "North", 100m, 3600), Cost(0, 2, "South", 110m, 1800), Cost(0, 3, "East", 200m, 7200),
        Cost(1, 1, "North", 500m, 3600), Cost(1, 2, "South", 450m, 3600), Cost(1, 3, "East", 460m, 3600),
    ]);
    var summary = SKBitmap.Decode(ParkCompareReport.SummaryPng(ParkCompareReport.Pivot(ranked, ["First job", "Second job"], byPark: false)));
    Check("summary picture: draws", summary is { Width: > 0, Height: > 0 });
    Check("summary picture: wide enough to read when Slack makes it 600 across, not a sliver", summary.Width / (double)summary.Height is > 1.2 and < 4, $"{summary.Width}x{summary.Height}");
    Check("post label: a build says so", ParkCompareReport.PostLabel(626, "Vexor", CompareOp.Build) == "Vexor — Build");
    Check("post label: so does science", ParkCompareReport.PostLabel(971, "Vexor Blueprint", CompareOp.Copy) == "Vexor Blueprint — Copy");
    Check("summary picture: nothing past its edge, so no heading is forcing the width", summary.Width < 1800, summary.Width.ToString());
    bool Green(SKColor c) => c.Green > 150 && c.Red < 120;
    bool Red(SKColor c)   => c.Red > 200 && c.Green < 130;
    var colours = Enumerable.Range(0, summary.Height).SelectMany(y => Enumerable.Range(0, summary.Width).Select(x => summary.GetPixel(x, y))).ToList();
    Check("summary picture: the best is in green", colours.Any(Green));
    Check("summary picture: far behind is in red", colours.Any(Red));

    var tablePicture = SKBitmap.Decode(SystemIndexReport.TablePng(["A-1", "B-2", "C-3"], ["Manufacturing", "Reaction", "Invention", "Copying", "ME Research", "TE Research"], "System",
        [[0.05, 0.06, null, 0.03, 0.04, 0.05], [0.04, 0.07, 0.02, 0.05, 0.03, 0.06], [0.06, 0.05, 0.03, 0.04, 0.05, 0.04]],
        [[0.11, -0.2, null, 0.0, 0.3, null], [-0.1, 0.2, 0.1, 0.0, -0.3, 0.1], [0.0, 0.0, 0.0, 0.0, 0.0, 0.0]]));
    Check("index picture: wide, so Slack's size leaves it readable", tablePicture.Width / (double)tablePicture.Height is > 1.5 and < 5, $"{tablePicture.Width}x{tablePicture.Height}");
    var tableColours = Enumerable.Range(0, tablePicture.Height).SelectMany(y => Enumerable.Range(0, tablePicture.Width).Select(x => tablePicture.GetPixel(x, y))).ToList();
    Check("index picture: the lowest and a fall are in green", tableColours.Any(Green));
    Check("index picture: a rise is in red", tableColours.Any(Red));
    Check("index picture: is a picture output, not a text one", SystemIndexOutputs.IsPicture(SystemIndexOutputs.TableImage)
        && new MessageBlock { Type = MessageBlock.TypeSystemIndex, IndexOutputs = [SystemIndexOutputs.TableImage] }.HasPictures
        && !new MessageBlock { Type = MessageBlock.TypeSystemIndex, IndexOutputs = [SystemIndexOutputs.TableImage] }.UsesSlackRich);

    IndexSystem Sys(int i) => new(100 + i, $"System {i}", "Region", i);
    var day = new DateOnly(2026, 11, 5);
    var daily = new Dictionary<(int, string), SortedDictionary<DateOnly, double>>();
    foreach (var a in CostIndexHistoryService.Activities)
        foreach (var i in Enumerable.Range(0, 3))
            daily[(100 + i, a)] = new(Enumerable.Range(0, 60).ToDictionary(d => day.AddDays(-d), d => 0.02 + i * 0.01 + d * 0.0001));

    var combined = SystemIndexReport.CombinedPng([Sys(0), Sys(1), Sys(2)], daily);
    Check("combined picture: draws", combined is not null);
    var bitmap = SKBitmap.Decode(combined);
    Check("combined picture: wide, because Slack limits a picture's height", bitmap.Width / (double)bitmap.Height > 1.7, $"{bitmap.Width}x{bitmap.Height}");
    Check("combined picture: no history, no picture", SystemIndexReport.CombinedPng([Sys(0)], new Dictionary<(int, string), SortedDictionary<DateOnly, double>>()) is null);

    var crowded = SKBitmap.Decode(SystemIndexReport.CombinedPng([.. Enumerable.Range(0, 3).Select(Sys).Select(s => s with { Name = new string('X', 30) + s.Name })], daily));
    Check("combined picture: a legend too long for a row wraps rather than running off", crowded.Height > bitmap.Height, $"{crowded.Height} vs {bitmap.Height}");
}

// ── Wide tables and messages ──────────────────────────────────────────────────

{
    SlackRichColumn[] cols = [new("Project", false), new("Status", false), new("Remaining", true), new("Last Completed", false)];
    SlackRichTableRow Item(int i) => new(
        [$"Item {i} \u2192 Somewhere", i % 2 == 0 ? "Active" : "No Project", i % 2 == 0 ? "12.5K" : "", i % 2 == 0 ? "" : "2026-11-04"],
        SlackRichBlocks.ItemIcon(34 + i), $"Item {i}");

    var items = SlackRichBlocks.Table("*Heading*", true, cols, [.. Enumerable.Range(0, 5).Select(Item)]);
    Check("table: heading, header, rows", items.Count == 7);
    var first = Json(items[0].Block);
    Check("table: the heading is a section", first.GetProperty("type").GetString() == "section" && first.GetProperty("text").GetProperty("text").GetString() == "*Heading*");
    Check("table: the header row has no icon", Json(items[1].Block).GetProperty("elements").GetArrayLength() == 1);
    var row = Json(items[2].Block).GetProperty("elements");
    Check("table: a row has its item's icon", row[0].GetProperty("image_url").GetString()!.Contains("/types/34/icon"));
    var header = Json(items[1].Block).GetProperty("elements")[0].GetProperty("text").GetString()!;
    Check("table: the header starts past where the icon goes", header[0] is En or Third);

    Check("table: no header when not asked", SlackRichBlocks.Table("", false, cols, [Item(0)]).Count == 1);
    Check("table: no heading when blank", Json(SlackRichBlocks.Table("", false, cols, [Item(0)])[0].Block).GetProperty("type").GetString() == "context");
    Check("table: nothing for no rows", SlackRichBlocks.Table("*Heading*", true, cols, []).Count == 0);

    var noIcon = SlackRichBlocks.Table("", true, cols, [new(["A", "B", "C", "D"], null, "")]);
    Check("table: a row with no icon is only its text", Json(noIcon[1].Block).GetProperty("elements").GetArrayLength() == 1);
    Check("table: with no icons, the header does not step in", Json(noIcon[0].Block).GetProperty("elements")[0].GetProperty("text").GetString()![0] == 'P');

    // A long list carries on in another message with its header.
    var many = SlackRichBlocks.Table("*Heading*", true, cols, [.. Enumerable.Range(0, 120).Select(Item)]);
    var packed = SlackRichBlocks.Pack(many);
    Check("pack: cut to Slack's limit", packed.All(m => m.Count <= 50), string.Join(",", packed.Select(m => m.Count)));
    Check("pack: nothing lost", packed.Sum(m => m.Count) == many.Count + (packed.Count - 1), $"{packed.Sum(m => m.Count)} of {many.Count}");
    Check("pack: three messages", packed.Count == 3, packed.Count.ToString());
    string HeaderText(object block) => Json(block).GetProperty("elements")[0].GetProperty("text").GetString()!;
    Check("pack: each carries the header", packed.Skip(1).All(m => HeaderText(m[0]).Contains("Remaining")));

    var filler = Enumerable.Range(0, 47).Select(_ => new SlackRichItem(SlackRichBlocks.TextSection("x"))).ToList();
    var kept   = SlackRichBlocks.Pack([.. filler, .. SlackRichBlocks.Table("*Heading*", true, cols, [Item(0), Item(1)])]);
    Check("pack: a heading is not left at the end of a message", kept.Count == 2 && kept[0].Count == 47, string.Join(",", kept.Select(m => m.Count)));
}

// ── Sending ───────────────────────────────────────────────────────────────────

{
    SlackRichMessage[] three = [new([1], "a"), new([2], "b"), new([3], "c")];
    var quick = TimeSpan.FromMilliseconds(1);
    SlackPostResult Good() => new(true, null, null, null);
    SlackPostResult Bad(string e) => new(false, null, null, e);

    var seen = new List<string>();
    var all = await SchedulerService.PostRichAsync(three, (m, _) => { seen.Add(m.Fallback); return Task.FromResult(Good()); }, gap: quick);
    Check("post: every message, in order", all.AllPosted && all.Posted == 3 && string.Concat(seen) == "abc");

    var tries = 0;
    var firstRefused = await SchedulerService.PostRichAsync(three, (_, _) => { tries++; return Task.FromResult(Bad("invalid_blocks")); }, gap: quick);
    Check("post: stops at a refused first message", firstRefused.Posted == 0 && firstRefused.Error == "invalid_blocks" && tries == 1, tries.ToString());

    tries = 0;
    var laterRecovers = await SchedulerService.PostRichAsync(three, (m, _) =>
        { tries++; return Task.FromResult(m.Fallback == "b" && tries == 2 ? Bad("timeout") : Good()); }, gap: quick);
    Check("post: a later message gets a second try", laterRecovers.AllPosted && tries == 4, tries.ToString());

    seen.Clear();
    var part = await SchedulerService.PostRichAsync(three, (m, _) =>
        { seen.Add(m.Fallback); return Task.FromResult(m.Fallback == "b" ? Bad("rate_limited") : Good()); }, gap: quick);
    Check("post: says how far it got, and sends nothing after a gap", part.Posted == 1 && part.Total == 3 && part.Error == "rate_limited" && !seen.Contains("c"));

    Check("refusal: the format", SlackRichBlocks.RefusedForFormat("invalid_blocks"));
    Check("refusal: the payload", SlackRichBlocks.RefusedForFormat("invalid_payload"));
    Check("refusal: too long", SlackRichBlocks.RefusedForFormat("msg_too_long"));
    Check("refusal: a removed webhook is not the format", !SlackRichBlocks.RefusedForFormat("no_service"));
    Check("refusal: nor a missing channel", !SlackRichBlocks.RefusedForFormat("channel_not_found"));
    Check("refusal: nor an outage", !SlackRichBlocks.RefusedForFormat(null));
}

// ── Saved sections ────────────────────────────────────────────────────────────

{
    MessageBlock Saved(string json) => ScheduledTaskConfig.FromJson($$"""{"Blocks":[{{json}}]}""").Blocks[0];

    Check("saved: a section from before the option stays text", !Saved("""{"Type":"corp_top10"}""").UsesSlackRich);
    Check("saved: a project list from before the option stays text", !Saved("""{"Type":"standing_projects"}""").UsesSlackRich);
    Check("saved: ticked, a Top 10 is rich", Saved("""{"Type":"corp_top10","SlackRich":true}""").UsesSlackRich);
    Check("saved: ticked, a project list is rich", Saved("""{"Type":"standing_projects","SlackRich":true}""").UsesSlackRich);
    Check("saved: other kinds have no rich format", !Saved("""{"Type":"text","SlackRich":true}""").UsesSlackRich);
    Check("saved: nor does the monthly summary", !Saved("""{"Type":"corp_monthly","SlackRich":true}""").UsesSlackRich);

    var json = new ScheduledTaskConfig { Blocks = [new MessageBlock { Type = MessageBlock.TypeTop10, SlackRich = true }] }.ToJson();
    Check("saved: the choice survives saving", ScheduledTaskConfig.FromJson(json).Blocks[0].SlackRich);
    Check("saved: what is worked out is not stored", !json.Contains("UsesSlackRich"));
}

// ── The lists, from a database ────────────────────────────────────────────────

var dbPath = Path.Combine(Path.GetTempPath(), $"eveconsole-slackrich-{Guid.NewGuid():N}.db");
try
{
    void Sqlite(DbContextOptionsBuilder o) => o.UseSqlite($"Data Source={dbPath};Pooling=False");

    var builder = new DbContextOptionsBuilder<AppDbContext>();
    Sqlite(builder);
    var factory = new Factory(builder.Options);

    const long Corp = 98_000_001;
    const long OtherCorp = 98_000_099;
    var inMonth  = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    var outMonth = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    await using (var db = factory.CreateDbContext())
    {
        db.Database.EnsureCreated();

        var mail = 0;
        void Kill(DateTimeOffset when, long victimCorp, params (long? Char, long? Corp)[] attackers)
        {
            mail++;
            db.KillMailDetails.Add(new KillMailDetail { KillMailId = mail, KillMailHash = "h", KillMailTime = when, VictimCorpId = victimCorp });
            db.EsiKillMailRefs.Add(new KillMailRef { OwnerId = Corp, OwnerType = "corporation", KillMailId = mail, KillMailHash = "h" });
            foreach (var (c, co) in attackers)
                db.KillMailAttackers.Add(new KillMailAttacker { KillMailId = mail, CharacterId = c, CorporationId = co });
        }

        // Pilot 1: three alone, one alone but for a rat, one with a friend, one last month, one on
        // a corpmate.
        Kill(inMonth, OtherCorp, (90_000_001, Corp));
        Kill(inMonth, OtherCorp, (90_000_001, Corp));
        Kill(inMonth, OtherCorp, (90_000_001, Corp));
        Kill(inMonth, OtherCorp, (90_000_001, Corp), (null, null));
        Kill(inMonth, OtherCorp, (90_000_001, Corp), (90_000_002, Corp));
        Kill(outMonth, OtherCorp, (90_000_001, Corp));
        Kill(inMonth, Corp, (90_000_001, Corp));

        // Pilot 2 has a kill with someone from outside the corp, which is not solo either.
        Kill(inMonth, OtherCorp, (90_000_002, Corp), (90_000_500, OtherCorp));

        // Pilot 3: three alone, in a structure's company.
        for (var i = 0; i < 3; i++) Kill(inMonth, OtherCorp, (90_000_003, Corp), (null, OtherCorp));

        // Nine pilots on two kills each, which fills the top ten with ties at its foot.
        for (var i = 10; i < 19; i++)
        {
            Kill(inMonth, OtherCorp, (90_000_000 + i, Corp));
            Kill(inMonth, OtherCorp, (90_000_000 + i, Corp));
        }

        // ⚠️ And ten below them, off the list. Without people below the top ten, a share of the
        // rows listed and a share of everyone are the same number and this cannot tell them apart.
        for (var i = 30; i < 40; i++) Kill(inMonth, OtherCorp, (90_000_000 + i, Corp));

        db.UniverseNames.AddRange(Enumerable.Range(1, 45).Select(i => new UniverseName
            { EntityId = 90_000_000 + i, Name = $"Pilot {i}", Category = "character", PulledAt = inMonth }));
        db.UniverseNames.Add(new UniverseName { EntityId = 1_500_000_000, Name = "Old Corp", Category = "corporation", PulledAt = inMonth });
        await db.SaveChangesAsync();
    }

    var sp    = new ServiceCollection().AddDbContext<AppDbContext>(Sqlite).BuildServiceProvider();
    var prefs = new AppPreferencesService(sp.GetRequiredService<IServiceScopeFactory>());
    await prefs.LoadAsync();

    var esi      = new EsiClient(new NoNetwork(), new EsiAuthService(new HttpClient()));
    var corp     = new CorpActivityService(factory, esi);
    var excludes = new CorpTop10ExcludeService(factory, esi);
    var renderer = new ScheduledBlockRenderer(corp, null!, excludes, new CorpReportTitles(prefs), null!, null!, new CostIndexHistoryService(factory), factory);

    var (from, to) = (new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

    var solo = await corp.GetTopSoloKillersAsync(Corp, from, to);
    var one  = solo.FirstOrDefault(r => r.CharacterId == 90_000_001);
    Check("solo: alone, or with only a rat; not with a friend, last month, or on a corpmate", one is { Amount: 4 }, $"{one?.Amount}");
    Check("solo: first in the list", solo[0].CharacterId == 90_000_001);
    Check("solo: a pilot from outside the corp on the mail means not solo", solo.All(r => r.CharacterId != 90_000_002));
    Check("solo: an attacker from outside the corp is not listed", solo.All(r => r.CharacterId != 90_000_500));
    Check("solo: a structure on the mail is not a second pilot", solo.Any(r => r.CharacterId == 90_000_003));
    Check("solo: no more than that pilot's kills",
        (await corp.GetTopKillersAsync(Corp, from, to)).First(r => r.CharacterId == 90_000_001).Amount == 5);
    Check("solo: exclusions are honoured", (await corp.GetTopSoloKillersAsync(Corp, from, to, new HashSet<long> { 90_000_001 })).All(r => r.CharacterId != 90_000_001));
    Check("solo: ties at the foot are kept", solo.Count > 10, solo.Count.ToString());

    var kinds = await corp.GetEntityKindsAsync([90_000_001, 1_500_000_000, 98_000_555, 99_000_555, 1_000_125]);
    Check("kinds: from what ESI said", kinds[90_000_001] == "character" && kinds[1_500_000_000] == "corporation");
    Check("kinds: a player corp by its range, with ESI out of reach", kinds[98_000_555] == "corporation");
    Check("kinds: an alliance by its range", kinds[99_000_555] == "alliance");
    Check("kinds: an NPC corp by its range", kinds[1_000_125] == "corporation");

    MessageBlock Top10(string key, bool rich = true, bool hideIsk = false) => new()
    {
        Type = MessageBlock.TypeTop10, CorpId = Corp, MonthsBack = 1, Categories = [key], SlackRich = rich, HideIsk = hideIsk,
    };
    var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    // Pilot 1's share of the whole: 5 kills of the 38 counted, pilot by pilot, where the 11 rows
    // listed only hold 28 of them.
    var killers = await corp.GetTopKillersAsync(Corp, from, to);
    var whole   = killers.First(r => r.CharacterId == 90_000_001).Percent;
    Check("share: the whole is everyone, off the list too", Math.Abs(whole - 5.0 / 38 * 100) < 0.01 && killers.Count == 12, $"{whole:F2}% over {killers.Count} rows");
    var text  = (await renderer.RenderAsync([Top10("kills", rich: false)], now)).Text;
    Check("share: not of the rows listed", !text.Contains((5.0 / killers.Sum(k => (double)k.Amount) * 100).ToString("F1") + "%"), text);
    Check("share: a text post gives the same share as the Top 10 tab", text.Contains(whole.ToString("F1") + "%"), $"{whole:F1}% in {text.Split('\n').FirstOrDefault(l => l.Contains("Pilot 1 "))}");

    var plainSolo = (await renderer.RenderAsync([Top10("solokills", rich: false)], now)).Text;
    Check("text post: solo kills has its title", plainSolo.Contains("Solo"), plainSolo.Split('\n')[0]);
    Check("text post: solo kills prints a count", plainSolo.Split('\n').First(l => l.Contains("Pilot 1 ")).Contains("  4"), plainSolo);
    Check("text post: a count is not ISK, even with ISK hidden",
        (await renderer.RenderAsync([Top10("solokills", rich: false, hideIsk: true)], now)).Text.Split('\n').First(l => l.Contains("Pilot 1 ")).Contains("  4"));

    var rich = await renderer.RenderSlackRichAsync(
        [new MessageBlock { Type = MessageBlock.TypeText, Text = "Words first" }, Top10("kills")], now);
    Check("rich: text and list in one message", rich.Messages.Count == 1 && rich.Messages[0].Blocks.Count == 2);
    Check("rich: in the order composed", Json(rich.Messages[0].Blocks[0]).GetProperty("text").GetProperty("text").GetString() == "Words first");
    Check("rich: a dynamic section is noticed", rich.AnyDynamicContent);
    Check("rich: says something where blocks cannot show", rich.Messages[0].Fallback.Contains("Words first"));
    var richJson = JsonSerializer.Serialize(rich.Messages[0].Blocks);
    Check("rich: the same share as the text post", richJson.Contains(whole.ToString("F1") + "%"));
    Check("rich: number 1's amount is a count, not ISK", !richJson.Contains(" ISK"));
    Check("rich: real images", richJson.Contains("/characters/90000001/portrait?size=128") && richJson.Contains("/characters/90000002/portrait?size=64"));

    var empty = await renderer.RenderSlackRichAsync([Top10("ratting")], now);
    Check("rich: an empty month is no messages", empty.Messages.Count == 0 && !empty.AnyDynamicContent);
    var justText = await renderer.RenderSlackRichAsync([new MessageBlock { Type = MessageBlock.TypeText, Text = "Words" }, Top10("ratting")], now);
    Check("rich: text alone is not dynamic, so skip-if-empty still works", justText.Messages.Count == 1 && !justText.AnyDynamicContent);

    var longText = await renderer.RenderSlackRichAsync([new MessageBlock { Type = MessageBlock.TypeText, Text = string.Join("\n\n", Enumerable.Repeat(new string('y', 900), 8)) }], now);
    var sections = longText.Messages.SelectMany(m => m.Blocks).Select(b => Json(b).GetProperty("text").GetProperty("text").GetString()!).ToList();
    Check("rich: long text is cut to what a section takes", sections.Count > 1 && sections.All(s => s.Length <= SlackRichBlocks.MaxSectionText), string.Join(",", sections.Select(s => s.Length)));

    // ── A whole run, through the scheduler, to a Slack that is only this check ──

    await using (var db = factory.CreateDbContext())
    {
        db.SlackWebhooks.Add(new SlackWebhook { Id = 1, Name = "fake", Url = "https://hooks.slack.invalid/fake" });
        await db.SaveChangesAsync();
    }

    var fake   = new FakeSlack();
    var errors = new AppErrorLogger(sp.GetRequiredService<IServiceScopeFactory>());
    var slack  = new SlackService(fake, prefs, errors, null!, factory);
    var online = new OnlineServices(fake, factory, prefs, null!, errors);
    var discord = new DiscordService(fake, prefs, errors, factory);
    var runner = new SchedulerService(factory, renderer, slack, discord, online, errors);

    // The serializer writes a backtick as an escape, so that is what a fence looks like in a post.
    bool Fenced(string post) => post.Contains(@"\u0060\u0060\u0060");

    async Task<(bool Ok, string Message)> Run(List<MessageBlock> blocks, bool skipEmpty = false, string? answer = null, int failFrom = int.MaxValue)
    {
        fake.Reset(answer, failFrom);
        var cfg = new ScheduledTaskConfig { DestinationKind = SlackDestination.KindWebhook, DestinationId = "1", Blocks = blocks, SkipIfNoDynamicContent = skipEmpty };
        return await runner.RunOneAsync(new ScheduledTask { Id = 1, Name = "t", TaskType = ScheduledTaskType.SlackPost, Config = cfg.ToJson() }, now);
    }

    var (okRich, saidRich) = await Run([Top10("kills")]);
    Check("run: ticked, the post carries blocks", okRich && fake.Posts.Count == 1 && fake.Posts[0].Contains("\"blocks\""), saidRich);
    Check("run: ticked, it carries images", fake.Posts[0].Contains("images.evetech.net"));
    Check("run: ticked, not a fenced table", !Fenced(fake.Posts[0]));
    Check("run: says it posted with images", saidRich == string.Format(EveConsole.Localization.AlarmsText.RunPostedRich, 1), saidRich);

    var (okText, saidText) = await Run([Top10("kills", rich: false)]);
    Check("run: unticked, the text as it always was", okText && fake.Posts.Count == 1 && !fake.Posts[0].Contains("\"blocks\"") && Fenced(fake.Posts[0]), saidText);

    var (okBack, saidBack) = await Run([Top10("kills")], answer: "invalid_blocks");
    Check("run: format refused, the numbers still go as text", okBack && fake.Posts.Count == 2 && fake.Posts[0].Contains("\"blocks\"") && !fake.Posts[1].Contains("\"blocks\"") && Fenced(fake.Posts[1]), saidBack);
    Check("run: and it says why it looks different", saidBack.Contains("invalid_blocks"), saidBack);

    var (okGone, saidGone) = await Run([Top10("kills")], answer: "no_service");
    Check("run: a removed webhook is a failed run, to try again", !okGone && fake.Posts.Count == 1 && saidGone.Contains("no_service"), saidGone);

    var (okQuiet, saidQuiet) = await Run([new MessageBlock { Type = MessageBlock.TypeText, Text = "Words" }, Top10("ratting")], skipEmpty: true);
    Check("run: nothing dynamic to say, nothing posted", okQuiet && fake.Posts.Count == 0 && saidQuiet == EveConsole.Localization.AlarmsText.RunNothingDynamic, saidQuiet);

    var (okNone, saidNone) = await Run([Top10("ratting")]);
    Check("run: an empty month posts nothing", okNone && fake.Posts.Count == 0 && saidNone == EveConsole.Localization.AlarmsText.RunRenderedEmpty, saidNone);

    // ── Sections in order, their pictures with them, their heading once ──

    await using (var db = factory.CreateDbContext())
    {
        db.SdeRegions.Add(new SdeRegion { RegionId = 1, Name = "Region" });
        db.SdeSolarSystems.AddRange(new SdeSolarSystem { SolarSystemId = 501, RegionId = 1, Name = "Alpha" },
                                    new SdeSolarSystem { SolarSystemId = 502, RegionId = 1, Name = "Beta" },
                                    new SdeSolarSystem { SolarSystemId = 503, RegionId = 1, Name = "No History" });
        foreach (var a in CostIndexHistoryService.Activities)
            foreach (var s in new[] { 501, 502 })
            {
                db.IndustryCostIndices.Add(new IndustryCostIndex { SolarSystemId = s, Activity = a, CostIndex = s == 501 ? 0.04 : 0.05 });
                for (var d = 0; d < 10; d++)
                    db.MapIndustryIndices.Add(new MapIndustryIndex { SystemId = s, Activity = a, Bucket = $"2026-11-{d + 10:00} 00", CostIndex = 0.03 + d * 0.001 });
            }
        db.AppPreferences.Add(new AppPreference { Key = "online.key", Value = "test-key" });
        await db.SaveChangesAsync();
    }

    MessageBlock Index(string title, params string[] outputs) => new()
    {
        Type = MessageBlock.TypeSystemIndex, SectionTitle = title, IndexSystemIds = [501, 502], IndexOutputs = [.. outputs],
    };
    MessageBlock Words(string text) => new() { Type = MessageBlock.TypeText, Text = text };
    int Count(string text, string of) => text.Split(of).Length - 1;
    string All(PostSegment s) => s.Text + JsonSerializer.Serialize(s.Messages.SelectMany(m => m.Blocks));

    var ordered = await renderer.PlanPostAsync(
        [Words("first"), Index("Head", SystemIndexOutputs.Combined), Words("second"), Index("Other", SystemIndexOutputs.Table)],
        now, "Slack", canDraw: true, forWebhook: true);
    Check("order: cut after the section with the picture", ordered.Segments.Count == 2, ordered.Segments.Count.ToString());
    Check("order: words before it stay with it", ordered.Segments[0].Text.Contains("first") && !ordered.Segments[0].Text.Contains("second"), ordered.Segments[0].Text);
    Check("order: its picture goes with it", ordered.Segments[0].Pictures.Count == 1 && ordered.Segments[1].Pictures.Count == 0);
    Check("order: the next section comes after", ordered.Segments[1].Text.Contains("second") && ordered.Segments[1].Text.Contains("Other"));

    Check("heading: a section of only a picture still has one", Count(ordered.Segments[0].Text, "Head") == 1, ordered.Segments[0].Text);
    Check("heading: not over a table it has no table for", !ordered.Segments[0].Text.Contains("```"));
    Check("picture title: only what it is, the heading is above it", ordered.Segments[0].Pictures[0].Title == "Index history", ordered.Segments[0].Pictures[0].Title);

    var everything = await renderer.PlanPostAsync(
        [Index("Head", SystemIndexOutputs.Table, SystemIndexOutputs.TableImage, SystemIndexOutputs.Combined, "manufacturing")],
        now, "Slack", canDraw: true, forWebhook: true);
    Check("heading: once, however many outputs", Count(All(everything.Segments[0]), "Head") == 1 && everything.Segments[0].Pictures.Count == 3, All(everything.Segments[0]));
    Check("pictures: none repeats the heading", everything.Segments[0].Pictures.All(p => !p.Title.Contains("Head")));

    var richPlan = await renderer.PlanPostAsync([Index("Head", SystemIndexOutputs.NativeTable, SystemIndexOutputs.TableImage)], now, "Slack", canDraw: true, forWebhook: true);
    Check("heading: once in blocks too", richPlan.Segments[0].Messages.Count == 1 && Count(All(richPlan.Segments[0]), "Head") == 1 && richPlan.Segments[0].Pictures.Count == 1);

    var nothing = await renderer.PlanPostAsync([Index("Head", SystemIndexOutputs.Combined)], now, "Slack", canDraw: false, forWebhook: true);
    Check("heading: not when nothing prints: the picture cannot be sent", !nothing.AnyWords && nothing.Skipped == 1 && !nothing.AnyPictures);
    var noHistory = await renderer.PlanPostAsync([new MessageBlock { Type = MessageBlock.TypeSystemIndex, SectionTitle = "Head", IndexSystemIds = [503], IndexOutputs = [SystemIndexOutputs.Combined] }], now, "Slack", canDraw: true, forWebhook: true);
    Check("heading: not when nothing prints: no history to draw", !noHistory.AnyWords && !noHistory.AnyPictures);

    var onDiscord = await renderer.PlanPostAsync([Index("Head", SystemIndexOutputs.NativeTable, SystemIndexOutputs.Combined), Words("after")], now, "Discord", canDraw: true, forWebhook: false);
    Check("discord: the Slack table's text form, in order, picture with it", onDiscord.Segments.Count == 2 && onDiscord.Segments[0].Text.Contains("```") && onDiscord.Segments[0].Messages.Count == 0 && onDiscord.Segments[0].Pictures.Count == 1 && onDiscord.Segments[1].Text.Contains("after"));

    Check("preview: names pictures for what they are", ScheduledBlockRenderer.PictureTitles(Index("Head", SystemIndexOutputs.Combined, "manufacturing")).All(t => !t.Contains("Head")));

    var parkPicturesOnly = new MessageBlock { Type = MessageBlock.TypeParkCompare, SectionTitle = "Head", CompareOutputs = [ParkCompareOutputs.SummaryJobsImage] };
    var parkWithFollow = await renderer.RenderAsync([parkPicturesOnly], now, default, "Slack", new HashSet<MessageBlock> { parkPicturesOnly });
    Check("heading: a park section of only a picture still has one", Count(parkWithFollow.Text, "Head") == 1, parkWithFollow.Text);
    var parkNoFollow = await renderer.RenderAsync([parkPicturesOnly], now);
    Check("heading: not over a park picture that did not come", Count(parkNoFollow.Text, "Head") == 0, parkNoFollow.Text);

    // Slack refused the format, so the words go as text, and a picture's section keeps its heading.
    var (_, saidBackHead) = await Run([Top10("kills"), Index("Head", SystemIndexOutputs.Combined)], answer: "invalid_blocks");
    Check("wire: format refused, the text keeps the heading over the picture", fake.Posts.Count(p => !p.Contains("\"blocks\"") && p.Contains("Head")) == 1, saidBackHead);

    // And on the wire, a whole run.
    var (okOrder, saidOrder) = await Run([Words("first"), Index("Head", SystemIndexOutputs.NativeTable, SystemIndexOutputs.Combined), Words("second")]);
    var wire = fake.Posts.Where(p => p.Contains("\"blocks\"") || p.Contains("first") || p.Contains("second")).ToList();
    Check("wire: words, then its picture, then the next section", okOrder && wire.Count == 3 && wire[0].Contains("first") && wire[0].Contains("Head") && wire[1].Contains("hosted.invalid") && wire[2].Contains("second"), saidOrder + " | " + string.Join(" || ", wire.Select(w => w[..Math.Min(60, w.Length)])));
    Check("wire: says what it posted", saidOrder.Contains(string.Format(EveConsole.Localization.AlarmsText.RunChartsUploaded, 1)), saidOrder);

    // Post 0 is the first section's words, post 1 its picture, so the second section's words are refused.
    var (okGap, saidGap) = await Run([Words("first"), Index("Head", SystemIndexOutputs.Combined), Words("second"), Index("Later", SystemIndexOutputs.Combined), Words("third")], answer: "rate_limited", failFrom: 2);
    Check("wire: a refusal part way counts as a run, so a retry does not repeat it", okGap && saidGap.Contains("rate_limited"), saidGap);
    Check("wire: and nothing goes after the gap", fake.Posts.Count == 3 && !fake.Posts.Any(p => p.Contains("third")), string.Join(" || ", fake.Posts.Select(p => p[..Math.Min(60, p.Length)])));

    var (okFirst, saidFirst) = await Run([Words("first"), Index("Head", SystemIndexOutputs.Combined)], answer: "no_service");
    Check("wire: refused with nothing sent is a failed run, to try again", !okFirst && fake.Posts.Count == 1, saidFirst);

    // Discord carries the same order, each picture a message under its title.
    await using (var db = factory.CreateDbContext())
    {
        db.DiscordWebhooks.Add(new DiscordWebhook { Id = 1, Name = "fake", Url = "https://discord.com/api/webhooks/1/fake-token" });
        await db.SaveChangesAsync();
    }

    fake.Reset(null, int.MaxValue);
    var discordTask = new ScheduledTaskConfig { DestinationKind = SlackDestination.KindDiscord, DestinationId = "1", Blocks = [Words("first"), Index("Head", SystemIndexOutputs.Combined), Words("second")] };
    var (okDiscord, saidDiscord) = await runner.RunOneAsync(new ScheduledTask { Id = 1, Name = "t", TaskType = ScheduledTaskType.SlackPost, Config = discordTask.ToJson() }, now);
    Check("discord wire: words, then its picture, then the next section",
        okDiscord && fake.Posts.Count == 3 && fake.Posts[0].Contains("first") && fake.Posts[0].Contains("Head") && fake.Posts[1].Contains("Index history") && fake.Posts[2].Contains("second"),
        saidDiscord + " | " + string.Join(" || ", fake.Posts.Select(p => p[..Math.Min(60, p.Length)])));
    Check("discord wire: heading once", fake.Posts.Count(p => p.Contains("Head")) == 1);

    // Sections past one message's limit: two messages, and the second refused, twice.
    List<MessageBlock> many = [Top10("kills"), .. Enumerable.Range(0, 60).Select(i => new MessageBlock { Type = MessageBlock.TypeText, Text = $"Line {i}" })];
    var (okPart, saidPart) = await Run(many, answer: "rate_limited", failFrom: 1);
    Check("run: part sent is a run that counts, so a retry does not repeat it", okPart && saidPart.Contains("rate_limited") && saidPart.Contains('1'), saidPart);
    Check("run: the refused message was tried twice", fake.Posts.Count == 3, fake.Posts.Count.ToString());
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { File.Delete(dbPath); } catch (IOException) { /* a temp file, and the run's result is what matters */ }
}

// ── Result ────────────────────────────────────────────────────────────────────

if (failures.Count > 0)
{
    Console.WriteLine($"Slack rich format check: {failures.Count} of {checks} FAILED");
    foreach (var f in failures) Console.WriteLine(f);
    return 1;
}

Console.WriteLine($"Slack rich format check: all {checks} passed.");
return 0;

sealed class Factory(DbContextOptions<AppDbContext> o) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(o);
}

/// <summary>A webhook that keeps what it is sent and answers as told. Nothing leaves the machine.</summary>
sealed class FakeSlack : IHttpClientFactory
{
    public List<string> Posts { get; } = [];
    private string? _answer;
    private int _failFrom;

    public void Reset(string? answer, int failFrom)
    {
        Posts.Clear();
        _answer   = answer;
        _failFrom = answer is null ? int.MaxValue : Math.Min(failFrom, int.MaxValue);
        if (answer is not null && failFrom == int.MaxValue) _failFrom = 0;
    }

    public HttpClient CreateClient(string name) => new(new Handler(this)) { BaseAddress = new Uri("https://api.hosted.invalid/") };

    sealed class Handler(FakeSlack slack) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // The image service, which answers with where the picture now is.
            if (request.RequestUri!.Host == "api.hosted.invalid")
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new StringContent("""{"id":"abc","url":"https://img.hosted.invalid/abc.png"}""") };

            var body  = await request.Content!.ReadAsStringAsync(ct);
            var index = slack.Posts.Count;
            slack.Posts.Add(body);

            // A refusal of the format only refuses blocks: the same webhook takes plain text.
            var refused = slack._answer is not null && index >= slack._failFrom
                       && (slack._answer != "invalid_blocks" || body.Contains("\"blocks\""));

            return refused
                ? new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent(slack._answer!) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }
}

sealed class NoNetwork : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new Refuse());

    sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No network in this check.");
    }
}
