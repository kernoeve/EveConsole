using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

// MarketFmt formats ISK the way every other surface in the app does. It lives with the view
// models because that is where it was first needed; a scheduled post quoting different figures
// from the same numbers would be worse than the tidier namespace.
using EveConsole.ViewModels;
using EveConsole.Localization;

namespace EveConsole.Services;

/// <summary>One piece of a composed message.</summary>
public sealed class MessageBlock
{
    public const string TypeText    = "text";
    public const string TypeTop10   = "corp_top10";
    public const string TypeMonthly = "corp_monthly";
    public const string TypeSale    = "sale_posting";
    public const string TypeProjects = "standing_projects";
    public const string TypeIndustryOpps = "industry_opps";
    public const string TypeParkCompare  = "park_compare";
    public const string TypeSystemIndex  = "system_index";

    /// <summary>
    /// The monthly trend charts. Pictures, not text.
    ///
    /// <para>Kills and Mining split what the Activity chart plots on one pair of axes. Activity
    /// is kept because it is what already-scheduled posts are set to, and quietly dropping the
    /// mined line out of somebody's Monday post is not a rename.</para>
    /// </summary>
    public const string TypeIskChart      = "corp_isk_chart";
    public const string TypeActivityChart = "corp_activity_chart";
    public const string TypeKillChart     = "corp_kill_chart";
    public const string TypeMiningChart   = "corp_mining_chart";

    public string Type { get; set; } = TypeText;

    /// <summary>
    /// Whether this section's content comes from the data rather than from what was typed.
    ///
    /// <para>Text is the only section that always has something to say. Every other kind can
    /// render to nothing — an empty month, a posting with no stock, no project needing anything
    /// — which is what "do not post if the dynamic sections are empty" is about.</para>
    /// </summary>
    public bool IsDynamic => Type != TypeText;

    /// <summary>
    /// Whether this section is an image rather than a line of text.
    ///
    /// <para>⚠️ A webhook cannot carry one. It posts JSON and has no way to attach a file, so a
    /// chart section only renders where the destination is a channel and the token can upload.</para>
    /// </summary>
    public bool IsChart => Type is TypeIskChart or TypeActivityChart or TypeKillChart or TypeMiningChart;

    /// <summary>
    /// Whether this section sends any picture: a chart section always, an Indy Park Compare or
    /// System Index section when one of its chosen outputs is a picture. A webhook cannot carry
    /// them, as it cannot carry a chart.
    /// </summary>
    public bool HasPictures => IsChart
        || (Type == TypeParkCompare && CompareOutputs.Any(ParkCompareOutputs.IsPicture))
        || (Type == TypeSystemIndex && IndexOutputs.Any(SystemIndexOutputs.IsPicture));

    /// <summary>
    /// Top 10 and standing project blocks: post to Slack with images and lined-up columns rather
    /// than as a fenced text table. Discord always gets the text.
    ///
    /// <para>⚠️ Off unless it was ticked. A task written yesterday has to keep saying what it said
    /// yesterday, and a long project list as rich rows is longer in the channel than the same list
    /// as a table, so it is a choice each section makes.</para>
    /// </summary>
    public bool SlackRich { get; set; }

    /// <summary>Whether this section is one of the kinds that has a rich format, and asks for it.</summary>
    [JsonIgnore]
    public bool UsesSlackRich => Type switch
    {
        TypeTop10 or TypeProjects => SlackRich,

        // These two choose their outputs from a list, so the choice is already there.
        TypeSystemIndex => IndexOutputs.Contains(SystemIndexOutputs.NativeTable),
        TypeParkCompare => CompareOutputs.Any(ParkCompareOutputs.IsRich),
        _               => false,
    };

    /// <summary>Static text blocks: what to say.</summary>
    public string Text { get; set; } = "";

    /// <summary>Corp blocks: whose figures.</summary>
    public long CorpId { get; set; }

    /// <summary>
    /// Corp blocks: how many months back, 0 being the month in progress.
    ///
    /// <para>⚠️ Relative, never a fixed month. A task that posts "last month" has to keep meaning
    /// last month every time it runs; a stored year and month would say January forever.</para>
    /// </summary>
    public int MonthsBack { get; set; } = 1;

    /// <summary>Top 10 blocks: which of the five lists to include, by key.</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>
    /// Top 10 blocks: print the share of the total, and not what it was worth.
    ///
    /// <para>The same switch the Corp Activity tab offers, for the same reason: a ranking is
    /// often the point where the ISK is nobody else's business.</para>
    ///
    /// <para>⚠️ Kills keeps its number either way. Its "amount" is a count of kills, not a
    /// value, so there is nothing to withhold and dropping it would leave a list of names.</para>
    /// </summary>
    public bool HideIsk { get; set; }

    /// <summary>Sale posting blocks: which defined posting to render.</summary>
    public int PostingId { get; set; }

    /// <summary>Standing project blocks: "deliver_item" or "destroy_npc".</summary>
    public string ProjectType { get; set; } = "destroy_npc";

    /// <summary>
    /// Standing project blocks: how much of the list to report.
    ///
    /// <para>⚠️ Defaults to All, which is what these sections did before there was a choice. A
    /// task written yesterday has to keep saying what it said yesterday.</para>
    /// </summary>
    public string ProjectFilter { get; set; } = ProjectFilters.All;

    /// <summary>
    /// Standing project blocks: the heading over the table.
    ///
    /// <para>Empty means the one the section builds for itself. Stored empty rather than stamped
    /// with today's default, so a section that was never retitled keeps following the type and
    /// filter it is actually reporting on.</para>
    /// </summary>
    public string SectionTitle { get; set; } = "";

    /// <summary>Standing project blocks: print the ISK still payable for the outstanding work.</summary>
    public bool ShowIskLeft { get; set; } = true;

    /// <summary>Standing project blocks: print when a matching project last completed.</summary>
    public bool ShowLastCompleted { get; set; } = true;

    /// <summary>Standing project blocks: print a header row above the table.</summary>
    ///
    /// <para>On by default. A section saved before this existed stored its own false and keeps it;
    /// only a section written from here on starts with headers.</para>
    /// </summary>
    public bool ShowHeaders { get; set; } = true;

    /// <summary>
    /// Standing project blocks: exactly the projects to report on.
    ///
    /// <para>⚠️ Inclusions, not exclusions. A new section starts with everything ticked, but what
    /// gets stored is the list itself — so a project defined next month does NOT appear in a task
    /// written today. Nothing joins a saved post without somebody putting it there.</para>
    /// </summary>
    public List<long> IncludedProjectIds { get; set; } = [];

    // ── Industry Ops blocks ──────────────────────────────────────────────────
    //
    // ⚠️ Every filter the tool has, kept in the section itself. Nothing is read from the tool's
    // remembered choices: a post written today must not change because somebody narrowed the
    // tool to Ships tomorrow. SectionTitle and ShowHeaders above serve this table too.

    /// <summary>Industry Ops blocks: an <see cref="IndustryMode"/> name.</summary>
    public string OppsMode { get; set; } = nameof(IndustryMode.BuildAndSellOrder);

    /// <summary>Industry Ops blocks: the market config to price at ("Price At").</summary>
    public int OppsConfigId { get; set; }

    /// <summary>Industry Ops blocks: the four boxes, as typed — percent for the margins, ISK and
    /// units for the volumes. Null is a blank box: no filter.</summary>
    public double? OppsMinIskVol30d  { get; set; }
    public double? OppsMinUnitVol30d { get; set; }
    public double? OppsMinMarginPct   { get; set; }
    public double? OppsMinMargin30dPct { get; set; }

    /// <summary>Industry Ops blocks: the tool's two tick boxes, with its defaults.</summary>
    public bool OppsBpoOnly     { get; set; } = true;
    public bool OppsOwnedOnly   { get; set; }

    /// <summary>
    /// Industry Ops blocks: the meta groups ticked (see OppsMetaGroups).
    ///
    /// <para>⚠️ Null on a section saved before there was a choice, which had a "Skip faction"
    /// box instead: <see cref="OppsSkipFaction"/> then says what it meant.</para>
    /// </summary>
    public List<int>? OppsMetaGroups { get; set; }

    /// <summary>Read only from sections saved before Meta Groups; never written by the editor.</summary>
    public bool OppsSkipFaction { get; set; } = true;

    /// <summary>The meta groups this section keeps, old sections included.</summary>
    public List<int> MetaGroupsOrDefault() => OppsMetaGroups
        ?? [.. EveConsole.Services.OppsMetaGroups.All.Select(g => g.Id)
                .Where(id => !OppsSkipFaction || id != EveConsole.Services.OppsMetaGroups.Faction)];

    /// <summary>Industry Ops blocks: the market groups chosen, as chosen; what is nested under
    /// them is worked out when the post is made, so a group the SDE adds later is covered.</summary>
    public List<int> OppsExcludedGroupIds { get; set; } = [];
    public List<int> OppsIncludedGroupIds { get; set; } = [];

    /// <summary>Industry Ops blocks: the columns printed after the item, by key (see
    /// IndustryOppsReport.Columns), in the order printed; the sort column and direction; how
    /// many rows, 0 for all.</summary>
    public List<string> OppsColumns { get; set; } = [];

    /// <summary>
    /// Industry Ops blocks: headers renamed, by column key, the item ("item") included. A column
    /// not here prints its own header, in the interface language of the moment.
    /// </summary>
    public Dictionary<string, string> OppsHeaders { get; set; } = [];
    public string OppsSortBy        { get; set; } = IndustryOppsReport.DefaultSort;
    public bool   OppsSortDescending { get; set; } = true;
    public int    OppsRowLimit       { get; set; } = 10;

    // ── Indy Park Compare blocks ─────────────────────────────────────────────
    //
    // ⚠️ The parks, the jobs and what to post, all kept in the section. Nothing is read from the
    // Compare tab's remembered list: a post written today must not change because somebody tried
    // another item on the tab tomorrow. SectionTitle above heads the section.

    /// <summary>
    /// Indy Park Compare blocks: exactly the parks compared.
    ///
    /// <para>⚠️ Inclusions, as for standing projects. A new section ticks every park, but a park
    /// defined later does not join a post written before it.</para>
    /// </summary>
    public List<int> CompareParkIds { get; set; } = [];

    /// <summary>Indy Park Compare blocks: the job list, in order, as the Compare tab keeps one.</summary>
    public List<CompareJobSpec> CompareJobs { get; set; } = [];

    /// <summary>Indy Park Compare blocks: what to post, by <see cref="ParkCompareOutputs"/> key.</summary>
    public List<string> CompareOutputs { get; set; } = [];

    /// <summary>Indy Park Compare blocks: where a job box's market value is read, 0 for the market
    /// the Market defaults name.</summary>
    public int CompareMarketConfigId { get; set; }

    /// <summary>Indy Park Compare blocks: the price it reads, a <see cref="Models.MarketPriceType"/>,
    /// empty for the one the Market defaults name.</summary>
    public string ComparePriceType { get; set; } = "";

    // ── System Index blocks ──────────────────────────────────────────────────

    /// <summary>System Index blocks: the systems, in order; the order picks each line's colour.
    /// Kept in the section, never read from the tool's list.</summary>
    public List<int> IndexSystemIds { get; set; } = [];

    /// <summary>System Index blocks: the table and the activities charted, by
    /// <see cref="SystemIndexOutputs"/> key.</summary>
    public List<string> IndexOutputs { get; set; } = [];

    /// <summary>
    /// System Index blocks: the emoji the Slack table marks with, by name. Blank is the standard
    /// one. A custom emoji shows only in a workspace that has it, so none is assumed.
    /// </summary>
    public string IndexBestEmoji { get; set; } = "";
    public string IndexUpEmoji   { get; set; } = "";
    public string IndexDownEmoji { get; set; } = "";

    /// <summary>The calculation this section asks for.</summary>
    public IndustryOppsQuery ToOppsQuery() => new()
    {
        Mode             = Enum.TryParse<IndustryMode>(OppsMode, out var m) ? m : IndustryMode.BuildAndSellOrder,
        ConfigId         = OppsConfigId,
        MinIskVol30d     = OppsMinIskVol30d,
        MinUnitVol30d    = OppsMinUnitVol30d,
        MinMargin        = OppsMinMarginPct / 100,
        MinMargin30d     = OppsMinMargin30dPct / 100,
        MetaGroups       = MetaGroupsOrDefault(),
        BpoOnly          = OppsBpoOnly,
        OwnedOnly        = OppsOwnedOnly,
        ExcludedGroupIds = OppsExcludedGroupIds,
        IncludedGroupIds = OppsIncludedGroupIds,
    };
}

/// <summary>What an Indy Park Compare section can post: four pictures and three tables. The
/// tables say what the pictures show, for a reader who would rather copy the numbers.</summary>
public static class ParkCompareOutputs
{
    public const string SummaryJobsImage  = "summary_jobs_image";
    public const string SummaryParksImage = "summary_parks_image";
    public const string CostChart         = "cost_chart";
    public const string TimeChart         = "time_chart";
    public const string SummaryJobsTable  = "summary_jobs_table";
    public const string SummaryParksTable = "summary_parks_table";
    public const string DetailTable       = "detail_table";

    /// <summary>The summary as Slack's own table. Elsewhere, the summary's text table.</summary>
    public const string NativeTable = "summary_jobs_native";

    /// <summary>A box for each job, in Slack's blocks. Elsewhere, the summary's text table.</summary>
    public const string JobBoxes = "job_boxes";

    /// <summary>In the order offered. A post's blocks and text come first, then the pictures.</summary>
    public static readonly string[] All =
        [NativeTable, JobBoxes, SummaryJobsImage, SummaryParksImage, CostChart, TimeChart, SummaryJobsTable, SummaryParksTable, DetailTable];

    /// <summary>What a new section starts with: nothing that uses up uploads.</summary>
    public static readonly string[] Default = [NativeTable, JobBoxes];

    public static bool IsPicture(string key) => key is SummaryJobsImage or SummaryParksImage or CostChart or TimeChart;

    /// <summary>Built from Slack's own blocks, so it has a text form for everywhere else.</summary>
    public static bool IsRich(string key) => key is NativeTable or JobBoxes;

    public static string Label(string key) => key switch
    {
        NativeTable       => AlarmsText.CompareOutNative,
        JobBoxes          => AlarmsText.CompareOutBoxes,
        SummaryJobsImage  => AlarmsText.CompareOutSummaryJobsImage,
        SummaryParksImage => AlarmsText.CompareOutSummaryParksImage,
        CostChart         => AlarmsText.CompareOutCostChart,
        TimeChart         => AlarmsText.CompareOutTimeChart,
        SummaryJobsTable  => AlarmsText.CompareOutSummaryJobsTable,
        SummaryParksTable => AlarmsText.CompareOutSummaryParksTable,
        DetailTable       => AlarmsText.CompareOutDetailTable,
        _                 => key,
    };

    /// <summary>What a table or picture is headed with in the post: the choice's name without
    /// saying it is a picture or a table, which the post shows for itself.</summary>
    public static string Title(string key) => key switch
    {
        SummaryJobsImage or SummaryJobsTable   => AlarmsText.CompareTitleSummaryJobs,
        SummaryParksImage or SummaryParksTable => AlarmsText.CompareTitleSummaryParks,
        CostChart                              => IndustryText.CompareCostChartTitle,
        TimeChart                              => IndustryText.CompareTimeChartTitle,
        _                                      => AlarmsText.CompareTitleDetail,
    };
}

/// <summary>What a System Index section can post: today's table, and a chart per activity. On
/// screen the table's dots are the charts' key; posted, each chart carries its own legend.</summary>
public static class SystemIndexOutputs
{
    public const string Table = "table";

    /// <summary>Today's indices as Slack's own table, with the week's change. Elsewhere, the text table.</summary>
    public const string NativeTable = "native_table";

    /// <summary>The same table as a picture, for colours Slack's own table cannot have.</summary>
    public const string TableImage = "table_image";

    /// <summary>Every activity's chart in one picture: one upload rather than six.</summary>
    public const string Combined = "combined";

    public static readonly string[] All = [NativeTable, TableImage, Table, Combined, .. CostIndexHistoryService.Activities];

    /// <summary>What a new section starts with.</summary>
    public static readonly string[] Default = [NativeTable, Combined];

    public static bool IsPicture(string key) => key is not (Table or NativeTable);

    public static string Label(string key) => key switch
    {
        Table       => AlarmsText.IndexOutTable,
        NativeTable => AlarmsText.IndexOutNative,
        TableImage  => AlarmsText.IndexOutTableImage,
        Combined    => AlarmsText.IndexOutCombined,
        _           => string.Format(AlarmsText.IndexOutChart, SystemIndexReport.ActivityName(key)),
    };
}

/// <summary>How much of a standing project list to report.</summary>
public static class ProjectFilters
{
    /// <summary>Only what no active project is currently covering.</summary>
    public const string Missing = "missing";

    /// <summary>That, plus the ones nearly finished and about to need replacing.</summary>
    public const string MissingAndLow = "missing_low";

    public const string All = "all";

    /// <summary>The filter's name, in the interface language: the key above is what is stored.</summary>
    public static string Label(string key) => key switch
    {
        Missing       => AlarmsText.FilterMissingProjects,
        MissingAndLow => AlarmsText.FilterMissingAndLowProjects,
        _             => AlarmsText.FilterAllProjects,
    };
}

/// <summary>
/// What a task is configured to do.
///
/// <para>One config for every task type rather than one each. The blocks are the part both types
/// share — the same composed message goes to Slack or into an alert — and a type only reads the
/// fields it needs.</para>
/// </summary>
public sealed class ScheduledTaskConfig
{
    /// <summary>Slack posts: "chan" or "hook", matching SlackDestination.</summary>
    public string DestinationKind { get; set; } = "";
    public string DestinationId   { get; set; } = "";

    /// <summary>Alerts: the headline. Empty falls back to the task's own name.</summary>
    public string AlertTitle { get; set; } = "";

    /// <summary>Alerts: what the alert says, under the headline.</summary>
    public string AlertText { get; set; } = "";

    public List<MessageBlock> Blocks { get; set; } = [];

    /// <summary>
    /// Slack posts: stay silent unless a dynamic section actually said something.
    ///
    /// <para>Off by default, because a post whose static text is the point should still go out.
    /// On, a message of headings over empty data is not worth sending — and a channel that only
    /// hears from this task when there is something to hear is a channel people keep reading.</para>
    /// </summary>
    public bool SkipIfNoDynamicContent { get; set; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    /// <summary>⚠️ Never throws. A task whose configuration cannot be read is reported as a task
    /// with nothing to say, which the runner records — an exception here would take the whole
    /// scheduling loop down with it.</summary>
    public static ScheduledTaskConfig FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ScheduledTaskConfig();
        try   { return JsonSerializer.Deserialize<ScheduledTaskConfig>(json!, Opts) ?? new ScheduledTaskConfig(); }
        catch { return new ScheduledTaskConfig(); }
    }
}

/// <summary>
/// A rendered message, and whether any of the data-driven part of it had anything to say.
/// </summary>
/// <param name="Text">The whole message.</param>
/// <param name="AnyDynamicContent">
/// True when at least one dynamic section rendered to something. ⚠️ False also when there are no
/// dynamic sections at all: "post only when there is data" cannot be satisfied by a message that
/// never asks for any.
/// </param>
public sealed record RenderedMessage(string Text, bool AnyDynamicContent);

/// <summary>One Slack message in the rich format.</summary>
/// <param name="Blocks">What it shows.</param>
/// <param name="Fallback">What a notification says, where blocks cannot be shown.</param>
public sealed record SlackRichMessage(IReadOnlyList<object> Blocks, string Fallback);

/// <summary>A task's sections as rich Slack messages, in the order composed.</summary>
public sealed record SlackRichPost(IReadOnlyList<SlackRichMessage> Messages, bool AnyDynamicContent);

/// <summary>Sections that go out together, then the pictures of the last of them.</summary>
/// <param name="Text">What they say, when not in <paramref name="Messages"/>.</param>
/// <param name="Messages">What they say in Slack's blocks, when any of them asked for that.</param>
/// <param name="PicturesFollow">The section the pictures belong to, if it drew any.</param>
public sealed record PostSegment(
    IReadOnlyList<MessageBlock> Sections, string Text, IReadOnlyList<SlackRichMessage> Messages,
    IReadOnlyList<(byte[] Png, string Title)> Pictures, IReadOnlySet<MessageBlock> PicturesFollow);

/// <summary>What a task sends, in order.</summary>
/// <param name="Skipped">Sections whose pictures cannot be sent to this destination.</param>
public sealed record PostPlan(IReadOnlyList<PostSegment> Segments, bool AnyDynamicContent, int Skipped)
{
    public bool AnyWords    => Segments.Any(s => s.Text.Length > 0 || s.Messages.Count > 0);
    public bool AnyPictures => Segments.Any(s => s.Pictures.Count > 0);
}

/// <summary>
/// Turns message blocks into the text a scheduled post sends.
///
/// <para>⚠️ Renders without a view model. The same lists already appear on the Corp Activity
/// screen, but that export reads what the screen happens to have loaded — a selected corp, a
/// selected month, rows already fetched. A task firing at 00:01 has none of that, so it asks the
/// service for the figures it wants and formats them here.</para>
/// </summary>
public class ScheduledBlockRenderer(
    CorpActivityService     corp,
    SalePostingService      sales,
    CorpTop10ExcludeService excludes,
    CorpReportTitles         titles,
    IndustryOpportunitiesService opps,
    ParkComparisonService   parkCompare,
    CostIndexHistoryService indexHistory,
    IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>The five lists, in the order the exports print them. Defined by CorpReportTitles,
    /// which is also what the settings tab overrides and what names them on screen.</summary>
    public static (string Key, string Title)[] Top10Categories => CorpReportTitles.Top10Categories;

    /// <summary>
    /// One chart, drawn.
    ///
    /// <para>Returns null when the corp has no monthly rows to plot — an empty chart says less
    /// than no chart, and the caller drops the section rather than posting axes with nothing
    /// between them.</para>
    /// </summary>
    public async Task<(byte[] Png, string Title)?> RenderChartAsync(
        MessageBlock b, CancellationToken ct = default, int width = DefaultChartWidth)
    {
        if (!b.IsChart || b.CorpId <= 0) return null;

        var rows = await corp.GetMonthlyActivityAsync(b.CorpId, 12, ct);

        var chart = b.Type switch
        {
            MessageBlock.TypeIskChart    => CorpTrendChartReport.IskTrends(rows),
            MessageBlock.TypeKillChart   => CorpTrendChartReport.KillTrends(rows),
            MessageBlock.TypeMiningChart => CorpTrendChartReport.MiningTrends(rows),
            _                            => CorpTrendChartReport.ActivityTrends(rows),
        };

        if (chart is null) return null;

        return (CorpTrendChartReport.RenderPng(chart, width), chart.Title);
    }

    private const int DefaultChartWidth = 1100;

    /// <summary>
    /// ⚠️ Narrower for a webhook. Slack shows an image block at about 600 wide however big it is,
    /// and a click only opens it from there, so a chart drawn at 1100 has its labels halved in the
    /// channel. Summary tables keep their width: their columns need it.
    /// </summary>
    private const int WebhookChartWidth = 800;

    /// <summary>
    /// Every picture a section sends, with the title each is posted under: a chart section's
    /// chart, an Indy Park Compare section's summaries and charts, a System Index section's
    /// charts. Empty when there is nothing to draw.
    /// </summary>
    public async Task<List<(byte[] Png, string Title)>> RenderPicturesAsync(
        MessageBlock b, CancellationToken ct = default, bool forWebhook = false)
    {
        var width = forWebhook ? WebhookChartWidth : DefaultChartWidth;
        if (b.IsChart) return await RenderChartAsync(b, ct, width) is { } chart ? [chart] : [];
        if (b.Type == MessageBlock.TypeParkCompare) return await ParkComparePicturesAsync(b, width, ct);
        if (b.Type == MessageBlock.TypeSystemIndex) return await SystemIndexPicturesAsync(b, width, ct);
        return [];
    }

    /// <summary>The titles a section's pictures would be posted under, without drawing them:
    /// what a preview names.</summary>
    public static List<string> PictureTitles(MessageBlock b) => b.Type switch
    {
        MessageBlock.TypeParkCompare => [.. ParkCompareOutputs.All.Where(k => ParkCompareOutputs.IsPicture(k) && b.CompareOutputs.Contains(k))
                                              .Select(ParkCompareOutputs.Title)],
        MessageBlock.TypeSystemIndex => [.. SystemIndexOutputs.All.Where(k => SystemIndexOutputs.IsPicture(k) && b.IndexOutputs.Contains(k))
                                              .Select(IndexPictureTitle)],
        _ => [],
    };

    // ── Indy Park Compare ────────────────────────────────────────────────────

    private static string CompareHeading(MessageBlock b) =>
        b.SectionTitle.Trim().Length > 0 ? b.SectionTitle.Trim() : AlarmsText.HeadingParkCompare;

    /// <summary>One section's comparison, worked out once: its tables and its pictures both come
    /// from it within a post.</summary>
    private sealed record CompareRun(List<ParkCost> Costs, List<RankedCost> Ranked, List<string> Labels,
        Dictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>> History);

    /// <summary>
    /// ⚠️ Keyed by the section itself. A post renders its text and then its pictures from the
    /// same section objects, and a comparison of a capital build in three parks takes seconds;
    /// the entry goes when the section does.
    /// </summary>
    private readonly ConditionalWeakTable<MessageBlock, Task<CompareRun?>> _compareRuns = new();

    private Task<CompareRun?> CompareRunAsync(MessageBlock b, CancellationToken ct) =>
        _compareRuns.GetValue(b, block => RunCompareAsync(block, ct));

    private async Task<CompareRun?> RunCompareAsync(MessageBlock b, CancellationToken ct)
    {
        if (b.CompareJobs.Count == 0 || b.CompareParkIds.Count == 0) return null;

        // Each park keeps the colour the Compare tab gives it: its place among all parks.
        var all   = await parkCompare.ParksAsync(ct);
        var parks = all.Select((p, i) => (p.Id, p.Name, Slot: i)).Where(p => b.CompareParkIds.Contains(p.Id)).ToList();
        if (parks.Count == 0) return null;

        await SdeNames.EnsureLoadedAsync();
        var jobs  = b.CompareJobs.Select((j, i) => j.ToJob(i)).ToList();
        var costs = await parkCompare.CompareAsync(jobs, parks, ct);
        var history = await indexHistory.DailyAsync(
            [.. costs.SelectMany(c => c.Terms).Select(t => t.SystemId).Distinct()],
            [.. costs.SelectMany(c => c.Terms).Select(t => t.Activity).Distinct()], ct);

        return new CompareRun(costs, ParkCompareReport.Rank(costs),
            [.. b.CompareJobs.Select(j => ParkCompareReport.PostLabel(j.T, j.N, j.Operation))], history);
    }

    private static string ParkTextTable(CompareRun run, string key, string heading, string formatName) => key switch
    {
        ParkCompareOutputs.SummaryJobsTable  => ParkCompareReport.SummaryTable(
            ParkCompareReport.Pivot(run.Ranked, run.Labels, byPark: false), heading, formatName),
        ParkCompareOutputs.SummaryParksTable => ParkCompareReport.SummaryTable(
            ParkCompareReport.Pivot(run.Ranked, run.Labels, byPark: true), heading, formatName),
        _ => ParkCompareReport.DetailTable(run.Ranked, heading, formatName),
    };

    private static List<string> ParkTextKeys(MessageBlock b) =>
        [.. ParkCompareOutputs.All.Where(k => !ParkCompareOutputs.IsPicture(k) && !ParkCompareOutputs.IsRich(k) && b.CompareOutputs.Contains(k))];

    /// <param name="picturesFollow">Pictures come after this text, so with no table of its own the
    /// section still says its heading over them.</param>
    private async Task<string> ParkCompareAsync(MessageBlock b, string formatName, bool picturesFollow, CancellationToken ct)
    {
        var tables = ParkTextKeys(b);

        // Where Slack's blocks do not reach (Discord, a preview, a format Slack refused), a section
        // that asked for them still says its numbers, as the summary's text table.
        if (b.CompareOutputs.Any(ParkCompareOutputs.IsRich) && !tables.Contains(ParkCompareOutputs.SummaryJobsTable))
            tables.Insert(0, ParkCompareOutputs.SummaryJobsTable);

        if (tables.Count == 0) return picturesFollow ? OutputFormat.ByName(formatName).Bold(CompareHeading(b)) : "";
        if (await CompareRunAsync(b, ct) is not { } run) return "";

        // One table goes under the section's own title, which says enough; with more, the title
        // heads the section and each table is named under it.
        if (tables.Count == 1) return ParkTextTable(run, tables[0], CompareHeading(b), formatName);

        var parts = new List<string> { OutputFormat.ByName(formatName).Bold(CompareHeading(b)) };
        parts.AddRange(tables.Select(key => ParkTextTable(run, key, ParkCompareOutputs.Title(key), formatName)));
        return string.Join("\n\n", parts.Where(p => p.Length > 0));
    }

    /// <summary>The section in Slack's blocks, one heading over whatever it was asked for.</summary>
    private async Task<List<SlackRichItem>> ParkCompareRichAsync(MessageBlock b, CancellationToken ct)
    {
        var items = new List<SlackRichItem>();
        if (await CompareRunAsync(b, ct) is not { } run) return items;

        var pivot = ParkCompareReport.Pivot(run.Ranked, run.Labels, byPark: false);
        if (pivot.Rows.Count == 0) return items;

        var heading = OutputFormat.ByName("Slack").Bold(CompareHeading(b));
        items.Add(new SlackRichItem(SlackRichBlocks.TextSection(heading), Room: 3));

        if (b.CompareOutputs.Contains(ParkCompareOutputs.NativeTable))
            items.AddRange(SlackRichBlocks.ParkTable(
                "", IndustryText.CompareColJob, ParkCompareReport.TableRows(pivot), pivot.Groups,
                IndustryText.CompareColCost, IndustryText.CompareColTime, SlackRichBlocks.DefaultBestEmoji, AlarmsText.ParkRichNote));

        if (b.CompareOutputs.Contains(ParkCompareOutputs.JobBoxes))
        {
            var jobs  = b.CompareJobs.Select((j, i) => j.ToJob(i)).ToList();
            var facts = await parkCompare.JobFactsAsync(jobs, run.Costs[0].ParkId, b.CompareMarketConfigId, b.ComparePriceType, ct);

            var boxes = pivot.Rows.Select((row, line) =>
            {
                var f        = facts[line];
                var produced = run.Costs.FirstOrDefault(c => c.Job.Line == line && c.Buildable)?.Produced ?? f.Quantity;
                return new SlackJobBox(
                    ParkCompareReport.BoxTitle(f, row.Title), ParkCompareReport.BoxAbout(f, produced), ParkCompareReport.BoxImage(f),
                    [.. row.Cells.Select(c => c is null ? null : new SlackJobCell(c.TotalText, c.OverBestText, c.TimeText, c.SlowerText))]);
            }).ToList();

            items.AddRange(SlackRichBlocks.JobBoxes(boxes, pivot.Groups, AlarmsText.JobBoxesNote, AlarmsText.JobBoxMoreParks));
        }

        foreach (var key in ParkTextKeys(b))
            foreach (var part in SlackMessageSplitter.Split(ParkTextTable(run, key, ParkCompareOutputs.Title(key), "Slack"), SlackRichBlocks.MaxSectionText))
                items.Add(new SlackRichItem(SlackRichBlocks.TextSection(part), Room: 2));

        return items;
    }

    private async Task<List<(byte[] Png, string Title)>> ParkComparePicturesAsync(MessageBlock b, int width, CancellationToken ct)
    {
        var pictures = ParkCompareOutputs.All.Where(k => ParkCompareOutputs.IsPicture(k) && b.CompareOutputs.Contains(k)).ToList();
        if (pictures.Count == 0 || await CompareRunAsync(b, ct) is not { } run) return [];

        var (labels, separators) = ParkCompareReport.PostPaints();
        var drawn = new List<(byte[] Png, string Title)>();
        foreach (var key in pictures)
        {
            ct.ThrowIfCancellationRequested();
            var title = ParkCompareOutputs.Title(key);
            byte[]? png = key switch
            {
                ParkCompareOutputs.SummaryJobsImage  => ParkCompareReport.SummaryPng(
                    ParkCompareReport.Pivot(run.Ranked, run.Labels, byPark: false)),
                ParkCompareOutputs.SummaryParksImage => ParkCompareReport.SummaryPng(
                    ParkCompareReport.Pivot(run.Ranked, run.Labels, byPark: true)),
                ParkCompareOutputs.CostChart => ParkCompareReport.CostChart(run.Costs, run.History, null, labels, separators) is { } cost
                    ? ParkCompareReport.ChartPng(cost, width) : null,
                // Taller: the job names under the bars are tilted once there are a few.
                _ => ParkCompareReport.TimeChart(run.Costs, run.Labels, null, labels, separators) is { } time
                    ? ParkCompareReport.ChartPng(time, width, height: 540) : null,
            };
            if (png is not null) drawn.Add((png, title));
        }
        return drawn;
    }

    // ── System Index ─────────────────────────────────────────────────────────

    private static string IndexHeading(MessageBlock b) =>
        b.SectionTitle.Trim().Length > 0 ? b.SectionTitle.Trim() : AlarmsText.HeadingSystemIndex;

    /// <summary>A picture's name. Only what it is: the section's heading is above it.</summary>
    private static string IndexPictureTitle(string key) => key switch
    {
        SystemIndexOutputs.Combined   => AlarmsText.IndexCombinedTitle,
        SystemIndexOutputs.TableImage => AlarmsText.IndexTableImageTitle,
        _                             => SystemIndexReport.ActivityName(key),
    };

    /// <summary>Today's value and the week's change, by system then activity.</summary>
    private static (double?[][] Values, double?[][] Changes) IndexNumbers(
        IReadOnlyList<IndexSystem> systems,
        IReadOnlyDictionary<(int SystemId, string Activity), double> current,
        IReadOnlyDictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>> daily)
    {
        var acts   = CostIndexHistoryService.Activities;
        var values = systems.Select(s => acts.Select(a => current.TryGetValue((s.SystemId, a), out var v) ? v : (double?)null).ToArray()).ToArray();
        return (values, systems.Select((s, i) => acts.Select((a, n) => values[i][n] is { } now
            ? SystemIndexReport.WeekChange(daily.GetValueOrDefault((s.SystemId, a)), now) : null).ToArray()).ToArray());
    }

    /// <param name="picturesFollow">Pictures come after this text, so with no table of its own the
    /// section still says its heading over them.</param>
    private async Task<string> SystemIndexAsync(MessageBlock b, string formatName, bool picturesFollow, CancellationToken ct)
    {
        // The Slack table's text form, where Slack's blocks do not reach.
        var wanted = b.IndexOutputs.Contains(SystemIndexOutputs.Table) || b.IndexOutputs.Contains(SystemIndexOutputs.NativeTable);
        if (b.IndexSystemIds.Count == 0) return "";
        if (!wanted) return picturesFollow ? OutputFormat.ByName(formatName).Bold(IndexHeading(b)) : "";

        var systems = await SystemIndexReport.LookUpAsync(dbFactory, b.IndexSystemIds, ct);
        var current = await indexHistory.CurrentAsync([.. systems.Select(s => s.SystemId)], ct);
        return SystemIndexReport.Table(systems, current, IndexHeading(b), formatName);
    }

    /// <summary>The section's table in Slack's own block, and its text table too if that was also ticked.</summary>
    private async Task<List<SlackRichItem>> SystemIndexRichAsync(MessageBlock b, CancellationToken ct)
    {
        if (b.IndexSystemIds.Count == 0) return [];

        var systems = await SystemIndexReport.LookUpAsync(dbFactory, b.IndexSystemIds, ct);
        if (systems.Count == 0) return [];

        var ids     = systems.Select(s => s.SystemId).ToList();
        var acts    = CostIndexHistoryService.Activities;
        var current = await indexHistory.CurrentAsync(ids, ct);
        var daily   = await indexHistory.DailyAsync(ids, acts, ct);

        var (values, changes) = IndexNumbers(systems, current, daily);

        var items = SlackRichBlocks.IndexTable(
            OutputFormat.ByName("Slack").Bold(IndexHeading(b)),
            [.. systems.Select(s => s.Name)], [.. acts.Select(SystemIndexReport.ActivityName)], IndustryText.IndexColSystem,
            values, changes,
            SlackRichBlocks.EmojiNameOr(b.IndexBestEmoji, SlackRichBlocks.DefaultBestEmoji),
            SlackRichBlocks.EmojiNameOr(b.IndexUpEmoji, SlackRichBlocks.DefaultUpEmoji),
            SlackRichBlocks.EmojiNameOr(b.IndexDownEmoji, SlackRichBlocks.DefaultDownEmoji),
            AlarmsText.IndexRichNote);

        if (b.IndexOutputs.Contains(SystemIndexOutputs.Table))
            foreach (var part in SlackMessageSplitter.Split(SystemIndexReport.Table(systems, current, "", "Slack"), SlackRichBlocks.MaxSectionText))
                items.Add(new SlackRichItem(SlackRichBlocks.TextSection(part), Room: 2));

        return items;
    }

    private async Task<List<(byte[] Png, string Title)>> SystemIndexPicturesAsync(MessageBlock b, int width, CancellationToken ct)
    {
        var table      = b.IndexOutputs.Contains(SystemIndexOutputs.TableImage);
        var combined   = b.IndexOutputs.Contains(SystemIndexOutputs.Combined);
        var activities = CostIndexHistoryService.Activities.Where(b.IndexOutputs.Contains).ToList();
        if ((activities.Count == 0 && !combined && !table) || b.IndexSystemIds.Count == 0) return [];

        var systems = await SystemIndexReport.LookUpAsync(dbFactory, b.IndexSystemIds, ct);
        var ids     = systems.Select(s => s.SystemId).ToList();
        var daily   = await indexHistory.DailyAsync(ids, combined || table ? CostIndexHistoryService.Activities : activities, ct);
        var (labels, separators) = ParkCompareReport.PostPaints();

        var drawn = new List<(byte[] Png, string Title)>();
        if (table && systems.Count > 0)
        {
            var (values, changes) = IndexNumbers(systems, await indexHistory.CurrentAsync(ids, ct), daily);
            drawn.Add((SystemIndexReport.TablePng(
                [.. systems.Select(s => s.Name)], [.. CostIndexHistoryService.Activities.Select(SystemIndexReport.ActivityName)],
                IndustryText.IndexColSystem, values, changes), IndexPictureTitle(SystemIndexOutputs.TableImage)));
        }

        if (combined && SystemIndexReport.CombinedPng(systems, daily) is { } all)
            drawn.Add((all, IndexPictureTitle(SystemIndexOutputs.Combined)));

        foreach (var activity in activities)
            if (SystemIndexReport.Chart(activity, systems, daily, labels, separators, textSize: 10) is { } chart)
                drawn.Add((ParkCompareReport.ChartPng(chart, width), IndexPictureTitle(activity)));
        return drawn;
    }

    /// <summary>
    /// Renders every block, in order, into one message.
    ///
    /// <para><paramref name="formatName"/> is the markup the message is written in: "Slack" for a
    /// Slack post, "Discord" for a Discord one, as the export formats name them. Only headings and
    /// the reports' own exports change with it; a text section is sent as it was typed.</para>
    /// </summary>
    /// <param name="picturesFollow">The sections whose pictures are going out straight after this
    /// text, so that one with no table still says its heading.</param>
    public async Task<RenderedMessage> RenderAsync(
        IReadOnlyList<MessageBlock> blocks, DateTime nowUtc, CancellationToken ct = default,
        string formatName = "Slack", IReadOnlySet<MessageBlock>? picturesFollow = null)
    {
        var parts      = new List<string>();
        var anyDynamic = false;

        foreach (var b in blocks)
        {
            ct.ThrowIfCancellationRequested();

            var text = await RenderSectionAsync(b, nowUtc, formatName, picturesFollow?.Contains(b) == true, ct);

            if (text.Length == 0) continue;

            parts.Add(text);
            if (b.IsDynamic) anyDynamic = true;
        }

        return new RenderedMessage(string.Join("\n\n", parts), anyDynamic);
    }

    private async Task<string> RenderSectionAsync(MessageBlock b, DateTime nowUtc, string formatName, bool picturesFollow, CancellationToken ct) =>
        b.Type switch
        {
            MessageBlock.TypeText     => b.Text.Trim(),
            MessageBlock.TypeTop10    => await Top10Async(b, nowUtc, formatName, ct),
            MessageBlock.TypeMonthly  => await MonthlyAsync(b, nowUtc, formatName, ct),
            MessageBlock.TypeSale     => await SalePostingAsync(b, formatName, ct),
            MessageBlock.TypeProjects => await StandingProjectsAsync(b, formatName, ct),
            MessageBlock.TypeIndustryOpps => await IndustryOppsAsync(b, formatName, ct),
            MessageBlock.TypeParkCompare  => await ParkCompareAsync(b, formatName, picturesFollow, ct),
            MessageBlock.TypeSystemIndex  => await SystemIndexAsync(b, formatName, picturesFollow, ct),

            // A chart contributes no text. It is uploaded separately, by whoever is posting.
            _                         => "",
        };

    /// <summary>
    /// Renders every block, in order, as Slack messages in the rich format.
    ///
    /// <para>Sections that have no rich format, or did not ask for it, go in as their text. ⚠️ In
    /// the same message rather than one of their own: a line of explanation over a list has to
    /// arrive with the list.</para>
    /// </summary>
    public async Task<SlackRichPost> RenderSlackRichAsync(
        IReadOnlyList<MessageBlock> blocks, DateTime nowUtc, CancellationToken ct = default,
        IReadOnlySet<MessageBlock>? picturesFollow = null)
    {
        var items      = new List<SlackRichItem>();
        var said       = new List<string>();
        var anyDynamic = false;

        foreach (var b in blocks)
        {
            ct.ThrowIfCancellationRequested();

            var before = items.Count;

            if (b.UsesSlackRich && b.Type == MessageBlock.TypeTop10)
            {
                var lists = await Top10ListsAsync(b, nowUtc, ct);
                if (lists.Count > 0)
                {
                    var rich = await Top10RichListsAsync(lists, ct);
                    items.Add(new SlackRichItem(SlackRichBlocks.Top10Boxes(rich, AlarmsText.HeadingTop10, AlarmsText.RichTopLabel, AlarmsText.RichShareOfTotal)));
                    said.AddRange(lists.Select(l => l.Title));
                }
            }
            else if (b.UsesSlackRich && b.Type == MessageBlock.TypeProjects)
            {
                var table = await StandingProjectsTableAsync(b, ct);
                items.AddRange(table);
                if (table.Count > 0) said.Add(b.SectionTitle.Trim().Length > 0 ? b.SectionTitle.Trim() : AlarmsText.HeadingStandingProjects);
            }
            else if (b.UsesSlackRich && b.Type == MessageBlock.TypeSystemIndex)
            {
                var table = await SystemIndexRichAsync(b, ct);
                items.AddRange(table);
                if (table.Count > 0) said.Add(IndexHeading(b));
            }
            else if (b.UsesSlackRich && b.Type == MessageBlock.TypeParkCompare)
            {
                var section = await ParkCompareRichAsync(b, ct);
                items.AddRange(section);
                if (section.Count > 0) said.Add(CompareHeading(b));
            }
            else
            {
                var text = await RenderSectionAsync(b, nowUtc, "Slack", picturesFollow?.Contains(b) == true, ct);

                // Cut as a plain post is, so a code block closes at the end of one section and
                // opens at the start of the next.
                foreach (var part in SlackMessageSplitter.Split(text, SlackRichBlocks.MaxSectionText))
                    items.Add(new SlackRichItem(SlackRichBlocks.TextSection(part), Room: 2));

                if (text.Length > 0) said.Add(text.Split('\n')[0].Trim());
            }

            if (items.Count > before && b.IsDynamic) anyDynamic = true;
        }

        var fallback = string.Join(" - ", said.Where(s => s.Length > 0));
        if (fallback.Length > SlackRichBlocks.MaxTitle) fallback = fallback[..SlackRichBlocks.MaxTitle];

        return new SlackRichPost(
            [.. SlackRichBlocks.Pack(items).Select(m => new SlackRichMessage(m, fallback))],
            anyDynamic);
    }

    /// <summary>
    /// A task as what goes out, in order: each section's words, then that section's pictures, before
    /// the next section starts.
    ///
    /// <para>⚠️ Cut at each section with pictures. A message's text and a picture are separate posts,
    /// so keeping every picture to the end put a section's table and picture a whole message apart.
    /// Sections with no pictures between them stay in one message, so a line of explanation still
    /// arrives with the list it explains.</para>
    /// </summary>
    /// <param name="canDraw">False where pictures cannot be sent: they are counted, not drawn.</param>
    public async Task<PostPlan> PlanPostAsync(
        IReadOnlyList<MessageBlock> blocks, DateTime nowUtc, string formatName, bool canDraw, bool forWebhook,
        CancellationToken ct = default)
    {
        var segments   = new List<PostSegment>();
        var group      = new List<MessageBlock>();
        var anyDynamic = false;
        var skipped    = 0;

        async Task CloseAsync(IReadOnlyList<(byte[] Png, string Title)> pictures, IReadOnlySet<MessageBlock> follow)
        {
            if (group.Count == 0 && pictures.Count == 0) return;

            var sections = group.ToList();
            group.Clear();

            if (formatName == "Slack" && sections.Any(b => b.UsesSlackRich))
            {
                var rich = await RenderSlackRichAsync(sections, nowUtc, ct, follow);
                anyDynamic |= rich.AnyDynamicContent;
                segments.Add(new PostSegment(sections, "", rich.Messages, pictures, follow));
                return;
            }

            var render = await RenderAsync(sections, nowUtc, ct, formatName, follow);
            anyDynamic |= render.AnyDynamicContent;
            segments.Add(new PostSegment(sections, render.Text.Trim(), [], pictures, follow));
        }

        foreach (var b in blocks)
        {
            ct.ThrowIfCancellationRequested();
            group.Add(b);

            if (!b.HasPictures) continue;

            // No point drawing what cannot be sent. The count is still reported, so a task
            // pointed at a webhook says what it could not do rather than looking complete.
            if (!canDraw) { skipped++; continue; }

            var pictures = await RenderPicturesAsync(b, ct, forWebhook);
            await CloseAsync(pictures, pictures.Count > 0 ? new HashSet<MessageBlock> { b } : new HashSet<MessageBlock>());
        }

        await CloseAsync([], new HashSet<MessageBlock>());

        return new PostPlan([.. segments.Where(s => s.Text.Length > 0 || s.Messages.Count > 0 || s.Pictures.Count > 0)], anyDynamic, skipped);
    }

    /// <summary>The calendar month a "months back" block names.</summary>
    private static (int Year, int Month, string Label) Month(int monthsBack, DateTime nowUtc)
    {
        var first = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddMonths(-Math.Max(0, monthsBack));

        return (first.Year, first.Month, first.ToString(CommonText.DateMonthYear));
    }

    /// <summary>That same month as a half-open range, for the ranked lists.</summary>
    private static (DateTimeOffset From, DateTimeOffset To, string Label) Window(int monthsBack, DateTime nowUtc)
    {
        var (year, month, label) = Month(monthsBack, nowUtc);
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);

        return (new DateTimeOffset(first), new DateTimeOffset(first.AddMonths(1)), label);
    }

    /// <summary>One ranked entity in a Top 10 list.</summary>
    /// <param name="Value">The amount as printed, or null when the section hides it.</param>
    /// <param name="Share">Of the whole, as the Top 10 tab shows it.</param>
    private sealed record Top10Entry(long Id, string Name, string? Value, string? FeaturedValue, string Share);

    private sealed record Top10List(string Title, List<Top10Entry> Rows);

    /// <summary>The lists a Top 10 section asks for, ranked and named. Empty ones are left out.</summary>
    private async Task<List<Top10List>> Top10ListsAsync(MessageBlock b, DateTime nowUtc, CancellationToken ct)
    {
        var lists = new List<Top10List>();
        if (b.CorpId <= 0 || b.Categories.Count == 0) return lists;

        var (from, to, label) = Window(b.MonthsBack, nowUtc);

        // ⚠️ The same exclusions the Top 10 tab honours. These lists were being built with the
        // exclude argument left null, so a corp somebody had deliberately hidden on the settings
        // tab came back in the post — the one place nobody would think to check it.
        var hidden = excludes.GetExcludeIds();

        foreach (var key in b.Categories)
        {
            ct.ThrowIfCancellationRequested();

            // ⚠️ Project contributors come back already named and in a different shape. Every
            // other list is ids and amounts, so they are brought to one form here rather than
            // printed by a second copy of the loop below.
            List<(long Id, decimal Amount, double Percent)> rows;
            Dictionary<long, string> names;

            if (key == "projects")
            {
                var contrib = await corp.GetTopProjectContributorsAsync(b.CorpId, from, to, hidden, ct);
                rows  = [.. contrib.Select(c => (c.CharacterId, c.IskPayout, c.Percent))];
                names = contrib
                    .GroupBy(c => c.CharacterId)
                    .ToDictionary(g => g.Key, g => g.First().Name);
            }
            else
            {
                var ranked = key switch
                {
                    "ratting"   => await corp.GetTopRattersAsync(b.CorpId, from, to, hidden, ct),
                    "mining"    => await corp.GetTopMinersAsync(b.CorpId, from, to, hidden, ct),
                    "kills"     => await corp.GetTopKillersAsync(b.CorpId, from, to, hidden, ct),
                    "solokills" => await corp.GetTopSoloKillersAsync(b.CorpId, from, to, hidden, ct),
                    "industry"  => await corp.GetTopIndustryAsync(b.CorpId, from, to, hidden, ct),
                    _           => [],
                };

                rows  = [.. ranked.Select(x => (x.CharacterId, x.Amount, x.Percent))];
                names = await corp.ResolveNamesAsync(
                    rows.Select(x => x.Id).Distinct().ToList(), ct);
            }

            if (rows.Count == 0) continue;

            // Kills counts kills; everything else is ISK. Printing "1,204 ISK" for a kill count
            // would be a units error nobody would question in a Slack post — and it is why kills
            // keeps its number even when the ISK is suppressed.
            var isKills   = key is "kills" or "solokills";
            var showValue = isKills || !b.HideIsk;

            var entries = new List<Top10Entry>();

            foreach (var row in rows)
            {
                var who   = names.GetValueOrDefault(row.Id, row.Id.ToString());
                var value = isKills
                    ? ((long)row.Amount).ToString("N0")
                    : MarketFmt.Isk((double)row.Amount);

                // ⚠️ Of the whole list, everyone below the top ten included, as the Top 10 tab
                // shows it. This was once worked out from the rows printed, so a post gave a
                // bigger share than the tab for the same person in the same month.
                var share = row.Percent > 0 ? row.Percent.ToString("F1") + "%" : "";

                entries.Add(new Top10Entry(row.Id, who,
                    showValue ? value : null,
                    showValue ? (isKills ? value : $"{value} ISK") : null,
                    share));
            }

            lists.Add(new Top10List(titles.Top10TitleFor(key, label), entries));
        }

        return lists;
    }

    /// <summary>
    /// The lists with their images.
    ///
    /// <para>⚠️ Each row's own image, by what that row is. The industry tax list mixes pilots with
    /// corps, and a corp's id does not show a portrait.</para>
    /// </summary>
    private async Task<List<SlackRichList>> Top10RichListsAsync(List<Top10List> lists, CancellationToken ct)
    {
        var kinds = await corp.GetEntityKindsAsync([.. lists.SelectMany(l => l.Rows).Select(r => r.Id).Distinct()], ct);

        return [.. lists.Select(l => new SlackRichList(l.Title, [.. l.Rows.Select(r =>
        {
            var kind = kinds.GetValueOrDefault(r.Id, "character");
            return new SlackRichRow(r.Name,
                SlackRichBlocks.EntityImage(r.Id, kind, 64),
                SlackRichBlocks.EntityImage(r.Id, kind, 128),
                r.Value, r.FeaturedValue, r.Share);
        })]))];
    }

    private async Task<string> Top10Async(MessageBlock b, DateTime nowUtc, string formatName, CancellationToken ct)
    {
        // Gathered before anything is printed, so every list shares one column grid rather than
        // each finding its own widths and the sections stepping in and out.
        var lists = (await Top10ListsAsync(b, nowUtc, ct))
            .Select(l => (l.Title, Cells: l.Rows.Select((r, i) => r.Value is { } value
                ? new[] { $"{i + 1}.", r.Name, value, r.Share }
                : [$"{i + 1}.", r.Name, r.Share]).ToList()))
            .ToList();

        if (lists.Count == 0) return "";

        // ⚠️ No "Top 10 — August 2026" line above them. Each fenced list is its own box in
        // Slack, so a single date line travelled with none of them; the month is on every heading
        // instead, where a list pasted on its own still carries it.

        // One grid across every list in this section.
        var columns = lists.Max(l => l.Cells.Max(c => c.Length));
        var widths  = new int[columns];
        foreach (var l in lists)
            foreach (var c in l.Cells)
                for (var i = 0; i < c.Length; i++)
                    widths[i] = Math.Max(widths[i], MonoColumns.Width(c[i]));

        var sb   = new StringBuilder();
        var bold = OutputFormat.ByName(formatName);

        foreach (var (title, cells) in lists)
        {
            sb.AppendLine();
            sb.AppendLine(bold.Bold(title));
            sb.AppendLine("```");

            foreach (var c in cells)
            {
                var line = new StringBuilder();
                for (var i = 0; i < c.Length; i++)
                {
                    // Numbers read right-aligned, the name reads left. The last cell is not padded,
                    // so there is no trailing whitespace inside the fence.
                    if (i == c.Length - 1)      line.Append(MonoColumns.PadLeft(c[i], widths[i]));
                    else if (i == 1)            line.Append(MonoColumns.PadRight(c[i], widths[i] + 2));
                    else                        line.Append(MonoColumns.PadLeft(c[i], widths[i])).Append("  ");
                }
                sb.AppendLine(line.ToString());
            }

            sb.AppendLine("```");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The Corp Activity tool's Monthly Summary, rendered for a message.
    ///
    /// <para>⚠️ The same report the screen shows, not a second set of numbers. MonthlySummaryReport
    /// holds what that summary says; both the export button and this ask it.</para>
    /// </summary>
    private async Task<string> MonthlyAsync(MessageBlock b, DateTime nowUtc, string formatName, CancellationToken ct)
    {
        if (b.CorpId <= 0) return "";

        var (year, month, _) = Month(b.MonthsBack, nowUtc);

        var summary = await corp.GetMonthSummaryAsync(b.CorpId, year, month, ct);
        var lines   = MonthlySummaryReport.Build(summary, titles);

        // The month in the interface language, as the screen's own export names it: the header
        // around it is the interface's, and an English month read "월간 요약 — 2026년 September".
        var header = MonthlySummaryReport.Header(
            await CorpNameAsync(b.CorpId, ct),
            new DateTime(year, month, 1).ToString("MMMM", System.Globalization.CultureInfo.CurrentCulture),
            year,
            titles.HeaderPrefix);

        return MonthlySummaryReport.Export(lines, header, formatName);
    }

    /// <summary>
    /// A defined sale posting, rendered for a message.
    ///
    /// <para>⚠️ The same render the Sale Posting tool and the store mail use, so a scheduled
    /// listing prices an item exactly as the screen does. Nothing about pricing is decided here.</para>
    ///
    /// <para>The tool posts block 0 as a message and the rest as threaded replies. A scheduled
    /// block is one piece of one message, so they are joined in the same order instead — the
    /// thread is a shape the tool's own button owns, not the posting's.</para>
    /// </summary>
    private async Task<string> SalePostingAsync(MessageBlock b, string formatName, CancellationToken ct)
    {
        if (b.PostingId <= 0) return "";

        var posts = await sales.RenderAsync(b.PostingId, formatName, ct);

        return string.Join("\n\n", posts
            .Select(p => p.Text.Trim())
            .Where(t => t.Length > 0));
    }

    /// <summary>
    /// The standing projects of one type, expanded.
    ///
    /// <para>⚠️ Expanded here, chosen unexpanded. One definition scoped to a region becomes a row
    /// per qualifying system, which is the whole point of reporting it — but the person picking
    /// which projects to report on picked definitions, so the exclusions are matched against
    /// DbId, which every expanded row still carries.</para>
    /// </summary>
    private async Task<string> StandingProjectsAsync(MessageBlock b, string formatName, CancellationToken ct)
    {
        var wanted = await StandingProjectRowsAsync(b, ct);

        // ⚠️ Taken as written, blank included. The default lives in the editor now, filled into
        // the box when the section is built; substituting it here as well would make a title
        // impossible to remove.
        var heading = b.SectionTitle.Trim();

        return StandingProjectReport.Export(
            [.. wanted], heading, b.ProjectType, b.ShowHeaders, b.ShowIskLeft, b.ShowLastCompleted, formatName);
    }

    /// <summary>
    /// The same rows, columns and order as the text table, each with its item's icon where it has
    /// one. A destroy-NPC row is a place, and has none.
    /// </summary>
    private async Task<List<SlackRichItem>> StandingProjectsTableAsync(MessageBlock b, CancellationToken ct)
    {
        var wanted = await StandingProjectRowsAsync(b, ct);
        if (wanted.Count == 0) return [];

        var cols = StandingProjectReport.Columns(b.ProjectType, b.ShowIskLeft, b.ShowLastCompleted);

        return SlackRichBlocks.Table(
            b.SectionTitle.Trim(), b.ShowHeaders,
            [.. cols.Select(c => new SlackRichColumn(c.Header, c.Right))],
            [.. StandingProjectReport.Ordered(wanted, b.ProjectType).Select(r => new SlackRichTableRow(
                [.. cols.Select(c => c.Cell(r))],
                r.ItemTypeId is > 0 ? SlackRichBlocks.ItemIcon(r.ItemTypeId.Value) : null,
                r.ItemTypeName))]);
    }

    private async Task<List<StandingProjectGridRow>> StandingProjectRowsAsync(MessageBlock b, CancellationToken ct)
    {
        if (b.CorpId <= 0) return [];

        // ⚠️ The type comes from the DEFINITIONS, not from reading it back off an expanded row.
        // A row could only be classified by whether it names an item, and a delivery project saved
        // without one would then be filed under destroy-NPC — wrong, and silently so.
        //
        // Intersected with what still exists, so a deleted project drops out rather than being
        // looked for among rows that no longer mention it.
        var defs = await corp.GetStandingProjectsAsync(b.CorpId, ct);
        var want = b.IncludedProjectIds.ToHashSet();

        var keep = defs
            .Where(d => d.ProjectType == b.ProjectType && want.Contains(d.Id))
            .Select(d => d.Id)
            .ToHashSet();

        if (keep.Count == 0) return [];

        var rows = await corp.BuildMaintainGridRowsAsync(b.CorpId, ct);

        return [.. rows
            .Where(r => keep.Contains(r.DbId))
            .Where(r => StandingProjectReport.Wanted(r, b.ProjectFilter))];
    }

    /// <summary>
    /// An Industry Ops list, calculated now with the section's own filters, sorted and cut to
    /// its row limit.
    ///
    /// <para>Nothing when the market it prices at is gone or switched off, or a 30-day filter
    /// needs a region the market does not resolve to — the same answer the tool would give,
    /// which is no list.</para>
    /// </summary>
    private async Task<string> IndustryOppsAsync(MessageBlock b, string formatName, CancellationToken ct)
    {
        if (b.OppsConfigId <= 0) return "";

        var result = await opps.CalculateAsync(b.ToOppsQuery(), ct);
        if (result is null || result.NoRegion) return "";

        var top = IndustryOppsReport.Top(result.Rows, b.OppsSortBy, b.OppsSortDescending, b.OppsRowLimit);
        return IndustryOppsReport.Export(top, b.OppsColumns, b.SectionTitle, b.ShowHeaders, formatName, b.OppsHeaders);
    }

    /// <summary>The corp's name, or nothing — a header without one still reads correctly.</summary>
    private async Task<string?> CorpNameAsync(long corpId, CancellationToken ct)
    {
        var names = await corp.ResolveNamesAsync([corpId], ct);
        return names.GetValueOrDefault(corpId);
    }
}
