using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

// Per-type computed values for a posting: quantities (assets / industry jobs / order
// tracker) plus the three reference prices and the configured sale price.
public record SalePostingCalc(
    string  Name,
    long    InStock,
    long    InBuild,
    long    Reserved,
    double? BuildCost,
    double? MarketValue,
    double? ContractValue,
    double? SalePrice,
    DateTimeOffset? EarliestJobEnd);

// Backs the Sale Posting tool. Persistence + computation for postings → sections → items.
// Reuses InvLevelService for the (non-trivial) location-scope → assets/industry-jobs
// aggregation, type metadata, and the type/location search pickers, so the two tools stay
// consistent. Reserved comes from the Order Tracker; contract value from ContractPricing;
// the "Specific Market" basis is priced off a region's 30-day average via MarketHistoryService.
public class SalePostingService(
    IDbContextFactory<AppDbContext> dbFactory,
    InvLevelService                 inv)
{
    // ── Posting CRUD ──────────────────────────────────────────────────────────

    public async Task<List<SalePosting>> LoadPostingsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        return await db.SalePostings.OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<SalePosting> AddPostingAsync(PostingDialogResult r, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var p = new SalePosting
        {
            Name             = r.Name,
            Scope            = r.Scope,
            LocationId       = r.LocationId,
            LocationName     = r.LocationName,
            PricingBasis      = r.PricingBasis,
            PricePercent      = r.PricePercent,
            MarketStationId   = r.MarketStationId,
            MarketStationName = r.MarketStationName,
            MarketPriceType   = r.MarketPriceType,
            ShowInStock       = r.ShowInStock,
            ShowInBuild       = r.ShowInBuild,
            ShowReserved      = r.ShowReserved,
            IncludeCompletionDate = r.IncludeCompletionDate,
            OnlyPackaged      = r.OnlyPackaged,
            ColorByState      = r.ColorByState,
            ColorInStock      = r.ColorInStock,
            ColorInBuild      = r.ColorInBuild,
            ColorNone         = r.ColorNone,
        };
        db.SalePostings.Add(p);
        await db.SaveChangesAsync(ct);
        return p;
    }

    public async Task UpdatePostingAsync(int postingId, PostingDialogResult r, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var p = await db.SalePostings.FindAsync([postingId], ct);
        if (p is null) return;
        p.Name             = r.Name;
        p.Scope            = r.Scope;
        p.LocationId       = r.LocationId;
        p.LocationName     = r.LocationName;
        p.PricingBasis      = r.PricingBasis;
        p.PricePercent      = r.PricePercent;
        p.MarketStationId   = r.MarketStationId;
        p.MarketStationName = r.MarketStationName;
        p.MarketPriceType   = r.MarketPriceType;
        p.ShowInStock       = r.ShowInStock;
        p.ShowInBuild       = r.ShowInBuild;
        p.ShowReserved      = r.ShowReserved;
        p.IncludeCompletionDate = r.IncludeCompletionDate;
        p.OnlyPackaged      = r.OnlyPackaged;
        p.ColorByState      = r.ColorByState;
        p.ColorInStock      = r.ColorInStock;
        p.ColorInBuild      = r.ColorInBuild;
        p.ColorNone         = r.ColorNone;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeletePostingAsync(int postingId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var sectionIds = await db.SalePostingSections
            .Where(s => s.PostingId == postingId).Select(s => s.Id).ToListAsync(ct);
        await db.SalePostingItems.Where(i => sectionIds.Contains(i.SectionId)).ExecuteDeleteAsync(ct);
        await db.SalePostingSections.Where(s => s.PostingId == postingId).ExecuteDeleteAsync(ct);
        await db.SalePostingPosts.Where(p => p.PostingId == postingId).ExecuteDeleteAsync(ct);
        await db.SalePostings.Where(p => p.Id == postingId).ExecuteDeleteAsync(ct);
    }

    // ── Post blocks (the Summary/Detail/Static blocks a posting renders as) ──────

    public async Task<List<SalePostingPost>> LoadPostsAsync(int postingId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        return await db.SalePostingPosts.Where(p => p.PostingId == postingId)
            .OrderBy(p => p.Ordinal).ToListAsync(ct);
    }

    // Replace a posting's post blocks wholesale (handles add/edit/delete/reorder in one shot).
    // Ordinal is the list index; the first block is the parent.
    public async Task ReplacePostsAsync(int postingId, IReadOnlyList<PostBlockDraft> posts, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.SalePostingPosts.Where(p => p.PostingId == postingId).ExecuteDeleteAsync(ct);
        for (int i = 0; i < posts.Count; i++)
        {
            var d = posts[i];
            bool isStatic = d.PostType == "Static";
            db.SalePostingPosts.Add(new SalePostingPost
            {
                PostingId     = postingId,
                Ordinal       = i,
                PostType      = d.PostType,
                Name          = d.Name,
                StaticContent = isStatic ? d.StaticContent : null,
                Header        = isStatic ? "" : d.Header,
                Footer        = isStatic ? "" : d.Footer,
                // HeaderColor survives on a Static block — there it colours the content.
                HeaderColor   = d.HeaderColor,
                FooterColor   = isStatic ? "" : d.FooterColor,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    // ── Section CRUD ──────────────────────────────────────────────────────────

    public async Task<List<SalePostingSection>> LoadSectionsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        return await db.SalePostingSections.OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<SalePostingSection> AddSectionAsync(int postingId, SectionDialogResult r, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var s = new SalePostingSection { PostingId = postingId };
        ApplySection(s, r);
        db.SalePostingSections.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    public async Task UpdateSectionAsync(int sectionId, SectionDialogResult r, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var s = await db.SalePostingSections.FindAsync([sectionId], ct);
        if (s is null) return;
        ApplySection(s, r);
        await db.SaveChangesAsync(ct);
    }

    private static void ApplySection(SalePostingSection s, SectionDialogResult r)
    {
        s.Name              = r.Name;
        s.Prefix            = r.Prefix;
        s.HeaderColor       = r.HeaderColor;
        s.RowColor          = r.RowColor;
        s.OverrideScope     = r.OverrideScope;
        s.Scope             = r.Scope;
        s.LocationId        = r.LocationId;
        s.LocationName      = r.LocationName;
        s.OverridePricing   = r.OverridePricing;
        s.PricingBasis      = r.PricingBasis;
        s.PricePercent      = r.PricePercent;
        s.MarketStationId   = r.MarketStationId;
        s.MarketStationName = r.MarketStationName;
        s.MarketPriceType   = r.MarketPriceType;
        s.OverrideOnlyPackaged = r.OverrideOnlyPackaged;
        s.OnlyPackaged      = r.OnlyPackaged;
    }

    public async Task DeleteSectionAsync(int sectionId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.SalePostingItems.Where(i => i.SectionId == sectionId).ExecuteDeleteAsync(ct);
        await db.SalePostingSections.Where(s => s.Id == sectionId).ExecuteDeleteAsync(ct);
    }

    // ── Item CRUD ─────────────────────────────────────────────────────────────

    public async Task<List<SalePostingItem>> LoadItemsAsync(int sectionId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        return await db.SalePostingItems.Where(i => i.SectionId == sectionId).ToListAsync(ct);
    }

    public async Task<SalePostingItem?> AddItemAsync(int sectionId, int typeId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        if (await db.SalePostingItems.AnyAsync(i => i.SectionId == sectionId && i.TypeId == typeId, ct))
            return null;
        var item = new SalePostingItem { SectionId = sectionId, TypeId = typeId };
        db.SalePostingItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task DeleteItemAsync(int itemId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.SalePostingItems.Where(i => i.Id == itemId).ExecuteDeleteAsync(ct);
    }

    public async Task UpdateItemNameOverrideAsync(int itemId, string? value, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.SalePostingItems.Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.NameOverride, value), ct);
    }

    public async Task UpdateItemColorAsync(int itemId, string? value, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var item = await db.SalePostingItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return;
        item.Color = value ?? "";
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateItemNamePrefixAsync(int itemId, string? value, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.SalePostingItems.Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.NamePrefix, value), ct);
    }

    public async Task UpdateItemInStockOverrideAsync(int itemId, int? value, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.SalePostingItems.Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.InStockOverride, value), ct);
    }

    public async Task UpdateItemInBuildOverrideAsync(int itemId, int? value, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.SalePostingItems.Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.InBuildOverride, value), ct);
    }

    public async Task UpdateItemReservedOverrideAsync(int itemId, int? value, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.SalePostingItems.Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ReservedOverride, value), ct);
    }

    // ── Search (reuse InvLevelService) ────────────────────────────────────────

    public Task<IReadOnlyList<InvTypeResult>> SearchTypesAsync(string text, CancellationToken ct = default)
        => inv.SearchTypesAsync(text, ct);

    public Task<IReadOnlyList<LocationOption>> SearchLocationsAsync(string scope, string text, CancellationToken ct = default)
        => inv.SearchLocationsAsync(scope, text, ct);

    // Stations the app has current order data for (from polled Market configs) — the same set
    // the Trade Opportunities from/to dropdowns offer. This is what "Specific Market" picks from.
    public async Task<List<StationOption>> GetMarketStationsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var locIds = await db.MarketRawOrders.Select(o => o.LocationId).Distinct().ToListAsync(ct);
        if (locIds.Count == 0) return [];

        var stationInts = locIds.Where(id => id <= int.MaxValue).Select(id => (int)id).ToList();
        var stationNames = await db.SdeStations.Where(s => stationInts.Contains(s.StationId))
            .ToDictionaryAsync(s => (long)s.StationId, s => s.Name, ct);
        var structNames = await db.EsiStructureNames.Where(s => locIds.Contains(s.StructureId))
            .ToDictionaryAsync(s => s.StructureId, s => s.Name, ct);

        return locIds
            .Select(id => new StationOption(id,
                stationNames.GetValueOrDefault(id) ?? structNames.GetValueOrDefault(id) ?? $"Unknown ({id})"))
            .OrderBy(s => s.Name)
            .ToList();
    }

    // ── Computation ───────────────────────────────────────────────────────────

    // Compute quantities + prices for a posting's items. In Stock (assets) and In Build
    // (industry jobs) reuse InvLevelService's scope aggregation; Reserved sums pending
    // Order Tracker units; contract value uses ContractPricing.EffectivePrice; sale price
    // applies the posting's basis × percent (region 30-day average for the Market basis).
    /// <summary>
    /// A section's effective settings: its own overrides where set, otherwise the posting's.
    ///
    /// <para>⚠️ The one place this is decided. The tool and the headless render both price
    /// through it, so a section that overrides its scope or its basis cannot come out one way on
    /// screen and another way in a mail.</para>
    /// </summary>
    public static SalePosting EffectiveFor(SalePosting p, SalePostingSection s) => new()
    {
        Scope                 = s.OverrideScope   ? s.Scope             : p.Scope,
        LocationId            = s.OverrideScope   ? s.LocationId        : p.LocationId,
        LocationName          = s.OverrideScope   ? s.LocationName      : p.LocationName,
        PricingBasis          = s.OverridePricing ? s.PricingBasis      : p.PricingBasis,
        PricePercent          = s.OverridePricing ? s.PricePercent      : p.PricePercent,
        MarketStationId       = s.OverridePricing ? s.MarketStationId   : p.MarketStationId,
        MarketStationName     = s.OverridePricing ? s.MarketStationName : p.MarketStationName,
        MarketPriceType       = s.OverridePricing ? s.MarketPriceType   : p.MarketPriceType,
        OnlyPackaged          = s.OverrideOnlyPackaged ? s.OnlyPackaged  : p.OnlyPackaged,
        IncludeCompletionDate = p.IncludeCompletionDate,
    };

    // ── Export / import ───────────────────────────────────────────────────────
    //
    // A posting is a lot of setup — sections with their own scope and pricing overrides, every
    // item with its name tweaks, and the post blocks with their header and footer text. Building
    // a second one that differs in a few places means rebuilding all of it by hand, which is why
    // this exists: export the one that works, import it, change what differs.
    //
    // ⚠️ Everything in the file is an EVE id or a plain setting — type ids, station ids, region
    // ids. Nothing carries a row id from this database, so a file is portable between installs
    // and can be shared. Nesting and ordinals carry the structure instead.

    public sealed record PostingItemExportDto(
        int TypeId, string? NameOverride, string? NamePrefix,
        int? InStockOverride, int? InBuildOverride, int? ReservedOverride,
        string? Color = null);

    public sealed record PostingSectionExportDto(
        string Name, string Prefix,
        bool OverrideScope, string Scope, long? LocationId, string LocationName,
        bool OverridePricing, string PricingBasis, double PricePercent,
        long? MarketStationId, string MarketStationName, string MarketPriceType,
        bool OverrideOnlyPackaged, bool OnlyPackaged,
        List<PostingItemExportDto> Items,
        string? Color = null,          // ⚠️ the old single colour; kept so older files still load
        string? HeaderColor = null,
        string? RowColor = null);

    public sealed record PostingPostExportDto(
        int Ordinal, string PostType, string Name,
        string? StaticContent, string Header, string Footer,
        string? HeaderColor = null, string? FooterColor = null);

    public sealed record PostingExportDto(
        string Name, string Scope, long? LocationId, string LocationName,
        string PricingBasis, double PricePercent,
        long? MarketStationId, string MarketStationName, string MarketPriceType,
        bool ShowInStock, bool ShowInBuild, bool ShowReserved,
        bool IncludeCompletionDate, bool OnlyPackaged,
        List<PostingSectionExportDto> Sections,
        List<PostingPostExportDto> Posts,
        // ⚠️ Defaulted, so a file written before colour existed still imports. Every field added
        // here from now on has to be, or an older export becomes unreadable the day it is needed.
        bool ColorByState = false,
        string ColorInStock = "#4a9a5a",
        string ColorInBuild = "#c8a84b",
        string ColorNone    = "#888899");

    private static readonly JsonSerializerOptions ExportJson = new() { WriteIndented = true };

    /// <summary>Writes one posting, whole, as JSON.</summary>
    public async Task ExportPostingAsync(int postingId, Stream stream, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();

        var p = await db.SalePostings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == postingId, ct);
        if (p is null) return;

        var sections = await db.SalePostingSections.AsNoTracking()
            .Where(s => s.PostingId == postingId).OrderBy(s => s.Name).ToListAsync(ct);
        var sectionIds = sections.Select(s => s.Id).ToList();

        var items = await db.SalePostingItems.AsNoTracking()
            .Where(i => sectionIds.Contains(i.SectionId)).ToListAsync(ct);

        var posts = await db.SalePostingPosts.AsNoTracking()
            .Where(x => x.PostingId == postingId).OrderBy(x => x.Ordinal).ToListAsync(ct);

        var dto = new PostingExportDto(
            p.Name, p.Scope, p.LocationId, p.LocationName,
            p.PricingBasis, p.PricePercent,
            p.MarketStationId, p.MarketStationName, p.MarketPriceType,
            p.ShowInStock, p.ShowInBuild, p.ShowReserved,
            p.IncludeCompletionDate, p.OnlyPackaged,
            sections.Select(s => new PostingSectionExportDto(
                s.Name, s.Prefix,
                s.OverrideScope, s.Scope, s.LocationId, s.LocationName,
                s.OverridePricing, s.PricingBasis, s.PricePercent,
                s.MarketStationId, s.MarketStationName, s.MarketPriceType,
                s.OverrideOnlyPackaged, s.OnlyPackaged,
                items.Where(i => i.SectionId == s.Id)
                     .Select(i => new PostingItemExportDto(
                         i.TypeId, i.NameOverride, i.NamePrefix,
                         i.InStockOverride, i.InBuildOverride, i.ReservedOverride, i.Color))
                     .ToList(),
                null, s.HeaderColor, s.RowColor))
                .ToList(),
            posts.Select(x => new PostingPostExportDto(
                x.Ordinal, x.PostType, x.Name, x.StaticContent, x.Header, x.Footer,
                x.HeaderColor, x.FooterColor)).ToList(),
            p.ColorByState, p.ColorInStock, p.ColorInBuild, p.ColorNone);

        await JsonSerializer.SerializeAsync(stream, dto, ExportJson, ct);
    }

    /// <summary>
    /// Reads a posting file and creates a new posting from it. Null if the file is not one of ours.
    ///
    /// <para>⚠️ Always a new posting, never an overwrite of one with the same name. Import is used
    /// to make a variant of something that already works, so silently replacing the original would
    /// destroy the very thing being copied. A clashing name is suffixed instead — visible, and
    /// renamed in one edit.</para>
    /// </summary>
    public async Task<SalePosting?> ImportPostingAsync(Stream stream, CancellationToken ct = default)
    {
        PostingExportDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<PostingExportDto>(stream, cancellationToken: ct); }
        catch { return null; }
        if (dto is null || string.IsNullOrWhiteSpace(dto.Name)) return null;

        await using var db = dbFactory.CreateDbContext();

        var name     = dto.Name.Trim();
        var existing = await db.SalePostings.AsNoTracking().Select(x => x.Name).ToListAsync(ct);
        if (existing.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var n = 2;
            while (existing.Contains($"{name} ({n})", StringComparer.OrdinalIgnoreCase)) n++;
            name = $"{name} ({n})";
        }

        var posting = new SalePosting
        {
            Name                  = name,
            Scope                 = dto.Scope,
            LocationId            = dto.LocationId,
            LocationName          = dto.LocationName ?? "",
            PricingBasis          = dto.PricingBasis,
            PricePercent          = dto.PricePercent,
            MarketStationId       = dto.MarketStationId,
            MarketStationName     = dto.MarketStationName ?? "",
            MarketPriceType       = dto.MarketPriceType,
            ShowInStock           = dto.ShowInStock,
            ShowInBuild           = dto.ShowInBuild,
            ShowReserved          = dto.ShowReserved,
            IncludeCompletionDate = dto.IncludeCompletionDate,
            OnlyPackaged          = dto.OnlyPackaged,
            ColorByState          = dto.ColorByState,
            ColorInStock          = dto.ColorInStock,
            ColorInBuild          = dto.ColorInBuild,
            ColorNone             = dto.ColorNone,
        };
        db.SalePostings.Add(posting);
        await db.SaveChangesAsync(ct);

        // Sections in one write, then every item in one more — not a save per section. A posting
        // with a dozen sections would take the write lock a dozen times in a row, and anything
        // else saving during that stretch queues behind all of them.
        var dtoSections = dto.Sections ?? [];
        var sections = dtoSections.Select(s => new SalePostingSection
        {
            PostingId            = posting.Id,
            Name                 = s.Name,
            Prefix               = s.Prefix ?? "",
            OverrideScope        = s.OverrideScope,
            Scope                = s.Scope,
            LocationId           = s.LocationId,
            LocationName         = s.LocationName ?? "",
            OverridePricing      = s.OverridePricing,
            PricingBasis         = s.PricingBasis,
            PricePercent         = s.PricePercent,
            MarketStationId      = s.MarketStationId,
            MarketStationName    = s.MarketStationName ?? "",
            MarketPriceType      = s.MarketPriceType,
            OverrideOnlyPackaged = s.OverrideOnlyPackaged,
            OnlyPackaged         = s.OnlyPackaged,
            // An older file carries one colour, which was the heading's.
            HeaderColor          = s.HeaderColor ?? s.Color ?? "",
            RowColor             = s.RowColor ?? "",
        }).ToList();

        db.SalePostingSections.AddRange(sections);
        await db.SaveChangesAsync(ct);   // assigns the ids the items need

        var items = new List<SalePostingItem>();
        for (var i = 0; i < sections.Count; i++)
            foreach (var it in dtoSections[i].Items ?? [])
                items.Add(new SalePostingItem
                {
                    SectionId        = sections[i].Id,
                    TypeId           = it.TypeId,
                    NameOverride     = it.NameOverride,
                    NamePrefix       = it.NamePrefix,
                    InStockOverride  = it.InStockOverride,
                    InBuildOverride  = it.InBuildOverride,
                    ReservedOverride = it.ReservedOverride,
                    Color            = it.Color ?? "",
                });
        db.SalePostingItems.AddRange(items);

        db.SalePostingPosts.AddRange((dto.Posts ?? []).Select(x => new SalePostingPost
        {
            PostingId     = posting.Id,
            Ordinal       = x.Ordinal,
            PostType      = x.PostType,
            Name          = x.Name,
            StaticContent = x.StaticContent,
            Header        = x.Header ?? "",
            Footer        = x.Footer ?? "",
            HeaderColor   = x.HeaderColor ?? "",
            FooterColor   = x.FooterColor ?? "",
        }));

        await db.SaveChangesAsync(ct);
        return posting;
    }

    // ── Headless render ───────────────────────────────────────────────────────
    //
    // What the Sale Posting tab does when it draws itself, without the tab. Needed because a
    // mailed request for prices is answered by a background service, which has no view models
    // and no dispatcher — see SalePostingRenderer for why the rendering moved out of one.

    /// <summary>
    /// The posting as plain data, priced and counted, ready to render.
    ///
    /// <para>Sections and items are ordered exactly as the tool orders them — sections by name,
    /// items by type name — because a buyer comparing a mailed list against the one posted in
    /// chat should not have to wonder whether they are looking at the same thing.</para>
    /// </summary>
    internal async Task<PostingView?> BuildViewAsync(int postingId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();

        var posting = await db.SalePostings.FirstOrDefaultAsync(p => p.Id == postingId, ct);
        if (posting is null) return null;

        var sections = await db.SalePostingSections
            .Where(s => s.PostingId == postingId).OrderBy(s => s.Name).ToListAsync(ct);

        var views = new List<PostingSectionView>();

        foreach (var section in sections)
        {
            var items = await db.SalePostingItems
                .Where(i => i.SectionId == section.Id).ToListAsync(ct);
            if (items.Count == 0)
            {
                views.Add(new PostingSectionView(
                    section.Name, section.Prefix, section.HeaderColor, section.RowColor, []));
                continue;
            }

            // Per section, because each resolves its own scope and pricing.
            var calc = await ComputeAsync(
                EffectiveFor(posting, section),
                items.Select(i => i.TypeId).Distinct().ToList(), ct);

            var rows = items
                .Select(i =>
                {
                    calc.TryGetValue(i.TypeId, out var c);
                    return new PostingItemView(
                        i.TypeId,
                        c?.Name ?? $"Type {i.TypeId}",
                        i.NameOverride, i.NamePrefix, i.Color,
                        c?.InStock  ?? 0, c?.InBuild ?? 0, c?.Reserved ?? 0,
                        i.InStockOverride, i.InBuildOverride, i.ReservedOverride,
                        c?.SalePrice, c?.EarliestJobEnd);
                })
                .OrderBy(r => r.TypeName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            views.Add(new PostingSectionView(
                section.Name, section.Prefix, section.HeaderColor, section.RowColor, rows));
        }

        return new PostingView(
            posting.ShowInStock, posting.ShowInBuild, posting.ShowReserved,
            posting.IncludeCompletionDate,
            posting.ColorByState, posting.ColorInStock, posting.ColorInBuild, posting.ColorNone,
            views);
    }

    /// <summary>
    /// Every post block of a posting, rendered in one output format.
    ///
    /// <para>Returns them in Ordinal order, the same sequence the tool previews and Slack posts
    /// in — block 0 is the parent and the rest are supporting detail.</para>
    /// </summary>
    internal async Task<List<RenderedPost>> RenderAsync(
        int postingId, string formatName, CancellationToken ct = default)
    {
        var view = await BuildViewAsync(postingId, ct);
        if (view is null) return [];

        var fmt   = OutputFormat.ByName(formatName);
        var posts = await LoadPostsAsync(postingId, ct);

        return posts.OrderBy(p => p.Ordinal)
            .Select(p => new RenderedPost(
                p.Name, p.PostType,
                fmt.Finalize(SalePostingRenderer.Render(view, fmt, p))))
            .ToList();
    }

    public async Task<Dictionary<int, SalePostingCalc>> ComputeAsync(
        SalePosting posting, IReadOnlyList<int> typeIds, CancellationToken ct = default)
    {
        var ids = typeIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        // Assets (In Stock) + industry jobs (In Build), scoped like an InvLevel group.
        var scopeGroup = new InvLevelGroup
        {
            Scope                  = posting.Scope,
            LocationId             = posting.LocationId,
            IncludeAssets          = true,
            IncludeIndustryJobs    = true,
            IncludeMarketBuyOrders = false,
        };
        var avail    = await inv.LoadAvailableAsync(scopeGroup, ids, ct, packagedOnly: posting.OnlyPackaged);
        var meta     = await inv.GetTypeMetaAsync(ids, ct);
        var earliest = posting.IncludeCompletionDate
            ? await inv.LoadEarliestJobEndAsync(scopeGroup, ids, ct)
            : [];

        await using var db = dbFactory.CreateDbContext();

        // Reserved = pending Order Tracker units (global; no location/owner dimension).
        var reserved = (await db.TrackedOrders
                .Where(o => o.Status == "pending" && ids.Contains(o.TypeId))
                .GroupBy(o => o.TypeId)
                .Select(g => new { TypeId = g.Key, Units = g.Sum(o => o.Units) })
                .ToListAsync(ct))
            .ToDictionary(x => x.TypeId, x => (long)x.Units);

        // Contract value per type via the shared best/avg reduction rule.
        var contract = (await db.ContractPrices
                .Where(c => ids.Contains(c.TypeId))
                .ToListAsync(ct))
            .ToDictionary(c => c.TypeId, c => ContractPricing.EffectivePrice(c));

        // Current price for the Market basis: best sell / best buy / midpoint at the chosen
        // station, from the polled order book (MarketRawOrders), per the posting's price type.
        var stationPrice = new Dictionary<int, double>();
        if (posting.PricingBasis == "Market" && posting.MarketStationId is long stationId)
        {
            var orders = await db.MarketRawOrders
                .Where(o => o.LocationId == stationId && ids.Contains(o.TypeId))
                .Select(o => new { o.TypeId, o.IsBuyOrder, o.Price })
                .ToListAsync(ct);

            foreach (var g in orders.GroupBy(o => o.TypeId))
            {
                var sells = g.Where(o => !o.IsBuyOrder).Select(o => o.Price).ToList();
                var buys  = g.Where(o =>  o.IsBuyOrder).Select(o => o.Price).ToList();
                double? bestSell = sells.Count > 0 ? sells.Min() : null;
                double? bestBuy  = buys.Count  > 0 ? buys.Max()  : null;
                double? v = posting.MarketPriceType switch
                {
                    "Buy"      => bestBuy,
                    "Midpoint" => bestBuy.HasValue && bestSell.HasValue ? (bestBuy.Value + bestSell.Value) / 2 : bestSell ?? bestBuy,
                    _          => bestSell,
                };
                if (v.HasValue) stationPrice[g.Key] = v.Value;
            }
        }

        double pct = posting.PricePercent / 100.0;

        return ids.ToDictionary(id => id, id =>
        {
            meta.TryGetValue(id, out var m);
            double? build    = m?.BuildPrice;
            double? market   = m?.MarketPrice;
            double? contractV = contract.TryGetValue(id, out var cv) && cv.HasValue ? (double)cv.Value : null;

            double? basisVal = posting.PricingBasis switch
            {
                "Contract" => contractV,
                "Market"   => stationPrice.TryGetValue(id, out var sp) ? sp : null,
                _          => build,
            };
            double? sale = basisVal.HasValue ? basisVal.Value * pct : null;

            avail.TryGetValue(id, out var a);
            DateTimeOffset? ej = earliest.TryGetValue(id, out var e) ? e : null;
            return new SalePostingCalc(
                m?.Name ?? $"Type {id}",
                a?.Assets ?? 0,
                a?.IndustryJobs ?? 0,
                reserved.GetValueOrDefault(id),
                build, market, contractV, sale, ej);
        });
    }
}
