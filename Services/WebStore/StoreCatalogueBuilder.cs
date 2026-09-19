using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.WebStore;

/// <summary>
/// The store's posting as the web site should show it.
///
/// <para>⚠️ Built from the same view the mail price list is rendered from —
/// <see cref="SalePostingService.BuildViewAsync"/> — so what a buyer sees on the site and what a
/// PRICES mail says cannot disagree: the same in-stock, in-build and reserved figures, the same
/// price rounded the same way, the same name the posting chose to show.</para>
///
/// <para>The reference slice the site needs for these items — their own names and groups —
/// travels with them, so the site holds nothing about an item the app did not send.</para>
/// </summary>
public sealed class StoreCatalogueBuilder(
    IDbContextFactory<AppDbContext> dbFactory,
    SalePostingService              postings)
{
    /// <summary>Null when the store has no posting, or the posting renders to nothing.</summary>
    public async Task<CatalogueDto?> BuildAsync(Store store, CancellationToken ct)
    {
        if (store.PostingId == 0) return null;

        var view = await postings.BuildViewAsync(store.PostingId, ct);
        if (view is null) return null;

        var typeIds = view.Sections.SelectMany(s => s.Items).Select(i => i.TypeId).Distinct().ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var groups = await (
                from t in db.SdeTypes.AsNoTracking()
                join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                where typeIds.Contains(t.TypeId)
                select new { t.TypeId, Group = g.Name })
            .ToDictionaryAsync(x => x.TypeId, x => x.Group, ct);

        var dto = new CatalogueDto
        {
            AsOf               = DateTimeOffset.UtcNow,
            ShowInStock        = view.ShowInStock,
            ShowInBuild        = view.ShowInBuild,
            ShowReserved       = view.ShowReserved,
            ShowCompletionDate = view.IncludeCompletionDate,
            ColourByState      = view.ColorByState,
            ColourInStock      = view.ColorInStock,
            ColourInBuild      = view.ColorInBuild,
            ColourNone         = view.ColorNone,
            Sections = view.Sections.Select(s => new CatalogueSectionDto
            {
                Name         = s.Name,
                Prefix       = s.Prefix,
                HeaderColour = Blank(s.HeaderColor),
                RowColour    = Blank(s.RowColor),
                Items = s.Items.Select(i => new CatalogueItemDto
                {
                    TypeId    = i.TypeId,
                    // The same rule the renderer applies: the override replaces the name, the
                    // prefix goes in front of whichever is shown.
                    Name      = (string.IsNullOrWhiteSpace(i.NamePrefix) ? "" : i.NamePrefix!.Trim() + " ")
                              + (string.IsNullOrWhiteSpace(i.NameOverride) ? i.TypeName : i.NameOverride!),
                    TypeName  = i.TypeName,
                    GroupName = groups.GetValueOrDefault(i.TypeId, ""),
                    // ⚠️ Rounded as the price list rounds it, so the number the site quotes is the
                    // number a buyer would be shown by mail and the one their order is booked at.
                    UnitPrice = i.SalePrice is { } p ? MarketFmt.RoundToDisplay(p) : null,
                    InStock   = i.EffectiveInStock,
                    InBuild   = i.EffectiveInBuild,
                    Reserved  = i.EffectiveReserved,
                    EarliestJobEnd = view.IncludeCompletionDate ? i.EarliestJobEnd : null,
                    Colour    = Blank(i.Color),
                }).ToList(),
            }).ToList(),
        };

        dto.Hash = HashOf(dto);
        return dto;
    }

    /// <summary>
    /// The catalogue's content hash: everything a buyer could act on, and not the as-of time,
    /// which changes every cycle without the catalogue having.
    /// </summary>
    public static string HashOf(CatalogueDto c)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            c.ShowInStock, c.ShowInBuild, c.ShowReserved, c.ShowCompletionDate,
            c.ColourByState, c.ColourInStock, c.ColourInBuild, c.ColourNone,
            c.Sections,
        }, WebStoreProtocol.Json);
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
