using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// One entry on the Item Browser's quickbar: a folder (<see cref="Id"/>, <see cref="Name"/>,
/// <see cref="Children"/>) or an item (<see cref="TypeId"/>).
/// </summary>
public sealed class QuickbarEntry
{
    [JsonPropertyName("id")]       public string? Id       { get; set; }
    [JsonPropertyName("name")]     public string? Name     { get; set; }
    [JsonPropertyName("type")]     public int?    TypeId   { get; set; }
    [JsonPropertyName("children")] public List<QuickbarEntry>? Children { get; set; }

    [JsonIgnore] public bool IsFolder => TypeId is null;
}

/// <summary>
/// The Item Browser's quickbar: the items someone keeps an eye on, in folders, the way the game's
/// market window keeps them.
///
/// <para>Shared: one row in AppPreferences, so every app on the database sees the same quickbar.
/// ⚠️ Read from the database for every change and written straight back, never kept in memory
/// and saved whole — another app may have changed it since, and saving a copy held here would
/// undo that.</para>
///
/// <para>An item is on it once at most. A folder deleted gives its contents to the folder it was
/// in, so deleting one never loses an item.</para>
/// </summary>
public static class ItemQuickbar
{
    public const string PreferenceKey = "items.quickbar";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static List<QuickbarEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<QuickbarEntry>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Serialize(List<QuickbarEntry> root) => JsonSerializer.Serialize(root, Json);

    public static async Task<List<QuickbarEntry>> LoadAsync(IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.Key == PreferenceKey, ct);
        return Parse(row?.Value);
    }

    /// <summary>
    /// Applies one change to the quickbar as the database has it now, and saves it when the change
    /// says it did something. Returns the quickbar after it, and what the change said.
    /// </summary>
    public static async Task<(List<QuickbarEntry> Root, bool Changed)> ChangeAsync(
        IDbContextFactory<AppDbContext> dbFactory, Func<List<QuickbarEntry>, bool> change, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row  = await db.AppPreferences.FirstOrDefaultAsync(p => p.Key == PreferenceKey, ct);
        var root = Parse(row?.Value);
        if (!change(root)) return (root, false);

        var json = Serialize(root);
        if (row is null) db.AppPreferences.Add(new AppPreference { Key = PreferenceKey, Value = json });
        else             row.Value = json;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (root, true);
    }

    // ── The changes ──────────────────────────────────────────────────────────

    /// <summary>Puts an item in a folder (null: the top level). False when it is already on the
    /// quickbar, wherever it is.</summary>
    public static bool AddItem(List<QuickbarEntry> root, int typeId, string? folderId = null)
    {
        if (Find(root, typeId) is not null) return false;
        if (ListOf(root, folderId) is not { } into) return false;
        into.Add(new QuickbarEntry { TypeId = typeId });
        return true;
    }

    /// <summary>A new, empty folder in a folder (null: the top level); its id, or null when that
    /// folder is gone.</summary>
    public static string? NewFolder(List<QuickbarEntry> root, string name, string? parentId = null)
    {
        if (ListOf(root, parentId) is not { } into) return null;
        var id = Guid.NewGuid().ToString("N");
        into.Add(new QuickbarEntry { Id = id, Name = name, Children = [] });
        return id;
    }

    public static bool RenameFolder(List<QuickbarEntry> root, string folderId, string name)
    {
        if (FindFolder(root, folderId) is not { } folder || folder.Name == name) return false;
        folder.Name = name;
        return true;
    }

    public static bool RemoveItem(List<QuickbarEntry> root, int typeId)
    {
        if (Find(root, typeId) is not { } at) return false;
        at.List.RemoveAt(at.Index);
        return true;
    }

    /// <summary>Deletes a folder; what was in it goes where the folder was.</summary>
    public static bool DeleteFolder(List<QuickbarEntry> root, string folderId)
    {
        if (FindFolderAt(root, folderId) is not { } at) return false;
        var folder = at.List[at.Index];
        at.List.RemoveAt(at.Index);
        at.List.InsertRange(at.Index, folder.Children ?? []);
        return true;
    }

    /// <summary>Moves an item to a folder (null: the top level).</summary>
    public static bool MoveItem(List<QuickbarEntry> root, int typeId, string? folderId)
    {
        if (Find(root, typeId) is not { } at || ListOf(root, folderId) is not { } into) return false;
        if (ReferenceEquals(at.List, into)) return false;
        var entry = at.List[at.Index];
        at.List.RemoveAt(at.Index);
        into.Add(entry);
        return true;
    }

    /// <summary>Moves a folder into another (null: the top level). Never into itself or anything
    /// inside it.</summary>
    public static bool MoveFolder(List<QuickbarEntry> root, string folderId, string? intoId)
    {
        if (FindFolderAt(root, folderId) is not { } at) return false;
        var folder = at.List[at.Index];
        if (intoId is not null && (intoId == folderId || FindFolder(folder.Children ?? [], intoId) is not null)) return false;
        if (ListOf(root, intoId) is not { } into || ReferenceEquals(at.List, into)) return false;
        at.List.RemoveAt(at.Index);
        into.Add(folder);
        return true;
    }

    // ── Reading it ───────────────────────────────────────────────────────────

    public static bool Contains(List<QuickbarEntry> root, int typeId) => Find(root, typeId) is not null;

    /// <summary>Every item on it, at any depth.</summary>
    public static IEnumerable<int> TypeIds(IEnumerable<QuickbarEntry> entries)
    {
        foreach (var e in entries)
        {
            if (e.TypeId is { } id) yield return id;
            else foreach (var inner in TypeIds(e.Children ?? [])) yield return inner;
        }
    }

    /// <summary>Every folder, depth first, with the names of the folders it is in.</summary>
    public static IEnumerable<(QuickbarEntry Folder, IReadOnlyList<string> Path)> Folders(IEnumerable<QuickbarEntry> entries, IReadOnlyList<string>? path = null)
    {
        path ??= [];
        foreach (var e in entries.Where(e => e.IsFolder))
        {
            IReadOnlyList<string> mine = [.. path, e.Name ?? ""];
            yield return (e, mine);
            foreach (var inner in Folders(e.Children ?? [], mine)) yield return inner;
        }
    }

    private static List<QuickbarEntry>? ListOf(List<QuickbarEntry> root, string? folderId)
        => folderId is null ? root : FindFolder(root, folderId) is { } f ? f.Children ??= [] : null;

    private static QuickbarEntry? FindFolder(List<QuickbarEntry> list, string folderId)
        => FindFolderAt(list, folderId) is { } at ? at.List[at.Index] : null;

    private static (List<QuickbarEntry> List, int Index)? FindFolderAt(List<QuickbarEntry> list, string folderId)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].IsFolder && list[i].Id == folderId) return (list, i);
            if (list[i].Children is { } inner && FindFolderAt(inner, folderId) is { } found) return found;
        }
        return null;
    }

    private static (List<QuickbarEntry> List, int Index)? Find(List<QuickbarEntry> list, int typeId)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].TypeId == typeId) return (list, i);
            if (list[i].Children is { } inner && Find(inner, typeId) is { } found) return found;
        }
        return null;
    }
}
