using System.Text.Json;
using System.Text.Json.Nodes;
using EveConsole.Models;

namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// One layout written to a file and read back, to keep it or hand it to another desktop or another
/// person. The file holds the layout's name and its shape, as the database row does.
///
/// <para>⚠️ A Mumble control names its server by the database's own id, which means nothing in another
/// database. So the file also lists each server a control uses by its address, and reading it ties
/// the control to the server at that address here, or to none, for the person to pick.</para>
///
/// <para>Area ids are kept: a Tool Container is named by its id in a menu control's choices, and a new
/// layout has no monitors, so nothing is assigned to them yet.</para>
/// </summary>
public static class ClientLayoutFile
{
    /// <summary>What the file says it is, so another JSON file isn't read as a layout.</summary>
    public const string Format = "eveconsole-client-layout";
    public const int Version = 1;

    /// <summary>A Mumble server as the file names it.</summary>
    public sealed record ServerAddress(int Id, string Name, string Host, int Port);

    public static async Task WriteAsync(Stream stream, string name, LayoutArea root, IReadOnlyCollection<MumbleServer> servers,
        CancellationToken ct = default)
    {
        var used = ClientLayoutEditing.Controls(root)
            .Select(c => c.Options?.MumbleServerId).OfType<int>().ToHashSet();
        var listed = servers.Where(s => used.Contains(s.Id))
            .Select(s => new ServerAddress(s.Id, s.Name, s.Host, s.Port)).ToList();
        var file = new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["name"] = name,
            ["layout"] = JsonNode.Parse(ClientLayoutJson.Write(root)),
            ["mumbleServers"] = JsonSerializer.SerializeToNode(listed),
        };
        await JsonSerializer.SerializeAsync(stream, file, new JsonSerializerOptions { WriteIndented = true }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The name and layout in a file, with each Mumble control tied to the server here at the same
    /// address. Throws <see cref="LayoutFileException"/> for a file that isn't a layout.
    /// </summary>
    public static async Task<(string Name, LayoutArea Root)> ReadAsync(Stream stream, IReadOnlyCollection<MumbleServer> servers,
        CancellationToken ct = default)
    {
        JsonNode? file;
        try { file = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false); }
        catch (JsonException) { throw new LayoutFileException(newer: false); }

        if (file is not JsonObject obj || (string?)obj["format"] != Format || obj["layout"] is not JsonObject layout)
            throw new LayoutFileException(newer: false);
        int version;
        try { version = (int?)obj["version"] ?? 0; }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { throw new LayoutFileException(newer: false); }
        if (version < 1) throw new LayoutFileException(newer: false);
        if (version > Version) throw new LayoutFileException(newer: true);

        // Read as a saved row is, so a file from an older version is brought up to date the same way.
        var root = ClientLayoutJson.Read(layout.ToJsonString());
        var name = ((string?)obj["name"])?.Trim() is { Length: > 0 } n ? n : "";

        List<ServerAddress> named;
        try { named = obj["mumbleServers"]?.Deserialize<List<ServerAddress>>() ?? []; }
        catch (JsonException) { named = []; }
        foreach (var (_, _, _, options) in ClientLayoutEditing.Controls(root))
        {
            if (options?.MumbleServerId is not { } id) continue;
            var address = named.FirstOrDefault(s => s.Id == id);
            options.MumbleServerId = address is null ? null
                : servers.FirstOrDefault(s => s.Port == address.Port
                    && string.Equals(s.Host.Trim(), address.Host.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
        }
        return (name, root);
    }
}

/// <summary>A file that can't be read as a layout: not one at all, or made by a newer version.</summary>
public sealed class LayoutFileException(bool newer)
    : Exception(newer ? "The layout file was made by a newer version." : "The file isn't a layout.")
{
    public bool Newer => newer;
}
