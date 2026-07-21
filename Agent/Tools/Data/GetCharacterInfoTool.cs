using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EveConsole.Agent.Tools.Data;

public sealed class GetCharacterInfoTool : IAgentTool
{
    private readonly string _connString;

    public string Name        => "get_character_info";
    public string Description => "Returns character data: corporation, wallet balance, total skill points. " +
                                 "When character_name is provided, ALSO returns the full skill training queue " +
                                 "(all queued skills, levels, and finish dates). " +
                                 "Always supply character_name when the user asks about skills or training.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            character_name = new { type = "string", description = "Optional character name to filter by (partial match). Omit to get all." },
        },
    };

    public GetCharacterInfoTool(string connString) => _connString = connString;

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var nameFilter = input.TryGetProperty("character_name", out var n) ? n.GetString() : null;

        const string sql = """
            SELECT c.Name,
                   c.TotalSp,
                   c.UnallocatedSp,
                   c.SecurityStatus,
                   corp.Name  AS CorpName,
                   corp.Ticker,
                   wb.Balance
            FROM   Characters c
            LEFT JOIN Corporations corp ON corp.Id = c.CorporationId
            LEFT JOIN EsiWalletBalances wb ON wb.OwnerId = c.Id AND wb.OwnerType = 'character' AND wb.Division = 0
            WHERE  (@name IS NULL OR c.Name LIKE @name)
            ORDER  BY c.Name
            """;

        var rows = new List<object>();
        await using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@name", nameFilter is null ? (object)DBNull.Value : $"%{nameFilter}%");
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new
            {
                name             = rdr.GetString(0),
                total_sp         = rdr.GetInt64(1),
                unallocated_sp   = rdr.GetInt32(2),
                security_status  = Math.Round(rdr.GetDouble(3), 1),
                corporation      = rdr.IsDBNull(4) ? "Unknown" : rdr.GetString(4),
                ticker           = rdr.IsDBNull(5) ? ""        : rdr.GetString(5),
                wallet_balance   = rdr.IsDBNull(6) ? 0m        : rdr.GetDecimal(6),
            });
        }

        if (rows.Count == 0) return "No characters found.";

        // When querying a specific character, also attach the training queue.
        if (nameFilter is not null && rows.Count == 1)
        {
            const string queueSql = """
                SELECT sq.QueuePosition,
                       COALESCE(st.Name, 'Unknown Skill') AS SkillName,
                       sq.FinishedLevel   AS TargetLevel,
                       COALESCE(sk.ActiveSkillLevel, 0) AS CurrentLevel,
                       sq.StartDate,
                       sq.FinishDate
                FROM   EsiSkillQueue sq
                JOIN   Characters c ON c.Id = sq.CharacterId
                LEFT   JOIN SdeTypes st ON st.TypeId  = sq.SkillId
                LEFT   JOIN EsiSkills sk ON sk.CharacterId = sq.CharacterId
                                        AND sk.SkillId     = sq.SkillId
                WHERE  c.Name LIKE @name
                ORDER  BY sq.QueuePosition
                LIMIT  50
                """;

            var queue = new List<object>();
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = queueSql;
            cmd2.Parameters.AddWithValue("@name", $"%{nameFilter}%");
            await using var rdr2 = await cmd2.ExecuteReaderAsync(ct);
            while (await rdr2.ReadAsync(ct))
            {
                queue.Add(new
                {
                    position      = rdr2.GetInt32(0),
                    skill         = rdr2.GetString(1),
                    target_level  = rdr2.GetInt32(2),
                    current_level = rdr2.GetInt32(3),
                    start_date    = rdr2.IsDBNull(4) ? null : rdr2.GetString(4),
                    finish_date   = rdr2.IsDBNull(5) ? null : rdr2.GetString(5),
                });
            }

            var result = new { character = rows[0], training_queue = queue };
            return JsonSerializer.Serialize(result);
        }

        return JsonSerializer.Serialize(rows);
    }
}
