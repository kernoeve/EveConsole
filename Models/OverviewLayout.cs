using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Localization;

namespace EveConsole.Models;

// One section's placement within the Overview grid. Row/Col are 1-based start cells;
// RowSpan/ColSpan are how many cells the section covers.
public sealed class OverviewPlacement
{
    public string Key     { get; set; } = "";
    public bool   Enabled { get; set; } = true;
    public int    Row     { get; set; } = 1;
    public int    Col     { get; set; } = 1;
    public int    RowSpan { get; set; } = 1;
    public int    ColSpan { get; set; } = 1;
}

// User-customizable layout of the Overview tab: a Rows×Cols grid of equally-sized cells,
// with each section placed into it. Persisted as JSON in AppPreferences ("overview.layout").
public sealed class OverviewLayout
{
    public int Rows { get; set; } = 2;
    public int Cols { get; set; } = 3;
    public List<OverviewPlacement> Sections { get; set; } = [];

    // Section key → display title. Order here is the order shown in the customize dialog.
    // ⚠️ The key is what the layout saves and what everything matches on; the title is only shown,
    // in the interface's language — the same words as the section's own heading.
    public static readonly (string Key, string Title)[] KnownSections =
    [
        ("ActivitySummary",   OverviewText.ActivitySummary),
        ("Alerts",            OverviewText.Alerts),
        ("Notifications",     OverviewText.Notifications),
        ("News",              OverviewText.EveOnlineNews),
        ("PersonalKillmails", OverviewText.PersonalKillmails),
        ("SaleListingBuild",  ShellText.TabSaleListingBuild),
        ("SaleListingMarket", ShellText.TabSaleListingMarket),
        ("IncomePie",         OverviewText.SectionIncomePie),
        ("ExpensePie",        OverviewText.SectionExpensePie),
        ("IncomeExpense",     ShellText.NavIncomeExpense),
        ("Orders",            OverviewText.Orders),
        ("StandingProjects",  OverviewText.StandingProjects),
        ("StandingBuyOrders", OverviewText.StandingBuyOrders),
        ("WorklistAll",       OverviewText.WorklistAll),
        ("WorklistBuy",       OverviewText.WorklistBuy),
        ("WorklistHaul",      OverviewText.WorklistHaul),
        ("WorklistJobs",      OverviewText.WorklistJobs),
        ("WorklistNeeds",     OverviewText.WorklistStationNeeds),
    ];

    // Default layout: Sale Listing (Build) / Income & Expense / Personal Killmails across the top
    // row, Alerts / Notifications / News across the bottom row — all single cells.
    public static OverviewLayout Default() => new()
    {
        Rows = 2,
        Cols = 3,
        Sections =
        [
            new() { Key = "SaleListingBuild",  Enabled = true, Row = 1, Col = 1, RowSpan = 1, ColSpan = 1 },
            new() { Key = "IncomeExpense",     Enabled = true, Row = 1, Col = 2, RowSpan = 1, ColSpan = 1 },
            new() { Key = "PersonalKillmails", Enabled = true, Row = 1, Col = 3, RowSpan = 1, ColSpan = 1 },
            new() { Key = "Alerts",            Enabled = true, Row = 2, Col = 1, RowSpan = 1, ColSpan = 1 },
            new() { Key = "Notifications",     Enabled = true, Row = 2, Col = 2, RowSpan = 1, ColSpan = 1 },
            new() { Key = "News",              Enabled = true, Row = 2, Col = 3, RowSpan = 1, ColSpan = 1 },
        ],
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static OverviewLayout FromJsonOrDefault(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default();
        try
        {
            var layout = JsonSerializer.Deserialize<OverviewLayout>(json!, JsonOpts);
            if (layout is null || layout.Rows < 1 || layout.Cols < 1) return Default();
            // Ensure every known section has a placement (new sections may be added over time).
            foreach (var (key, _) in KnownSections)
                if (!layout.Sections.Any(s => s.Key == key))
                    layout.Sections.Add(new OverviewPlacement { Key = key, Enabled = false });
            return layout;
        }
        catch { return Default(); }
    }
}
