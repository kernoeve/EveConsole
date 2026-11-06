using System.Text.Json;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Finds what a section's picture can be: an entity through the entity tools' own search, or a type
/// by its name as the interface shows it or in English.
/// </summary>
public sealed class SlackPictureSearch(EntityBrowserService entities, IDbContextFactory<AppDbContext> dbFactory)
{
    private const int MinSearch = 2;
    private const int MaxTypes  = 100;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<List<EntityMatch>> SearchAsync(string kind, string text, CancellationToken ct = default)
    {
        await SdeNames.EnsureLoadedAsync(ct);

        if (kind == SlackPicture.Type) return await SearchTypesAsync(text, ct);

        return EntityKindOf(kind) is { } entity ? await entities.SearchShownAsync(entity, text, ct) : [];
    }

    private static EntityKind? EntityKindOf(string kind) => kind switch
    {
        SlackPicture.Character      => EntityKind.Pilot,
        SlackPicture.Corporation    => EntityKind.PlayerCorp,
        SlackPicture.Alliance       => EntityKind.Alliance,
        SlackPicture.Faction        => EntityKind.Faction,
        SlackPicture.NpcCorporation => EntityKind.NpcCorp,
        SlackPicture.Agent          => EntityKind.Agent,
        _                           => null,
    };

    private async Task<List<EntityMatch>> SearchTypesAsync(string text, CancellationToken ct)
    {
        var q = (text ?? "").Trim();
        if (q.Length < MinSearch) return [];

        var shown = SdeNames.Find(SdeNameKind.Type, q).Select(id => (int)id).ToList();
        var lower = q.ToLowerInvariant();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var found = await db.SdeTypes
            .Where(t => t.Published && (t.Name.ToLower().Contains(lower) || shown.Contains(t.TypeId)))
            .OrderBy(t => t.Name.Length)
            .ThenBy(t => t.Name)
            .Take(MaxTypes)
            .Select(t => new { t.TypeId, t.Name })
            .ToListAsync(ct);

        return [.. found.Select(t => new EntityMatch(t.TypeId, SdeNames.Type(t.TypeId, t.Name), ""))];
    }

    /// <summary>
    /// Which of a type's pictures to use, from what the image server holds for it.
    ///
    /// <para>Blank when it cannot be asked, which posts the type's icon: most types have one, and a
    /// post that fails over it falls back to its text.</para>
    /// </summary>
    public static async Task<string> TypeVariantAsync(long typeId, CancellationToken ct = default)
    {
        try
        {
            await using var body = await _http.GetStreamAsync($"https://images.evetech.net/types/{typeId}", ct);
            var held = await JsonSerializer.DeserializeAsync<List<string>>(body, cancellationToken: ct);
            return held is { Count: > 0 } ? SlackPicture.BestVariant(held) : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "";
        }
    }
}
