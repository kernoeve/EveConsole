using System.Text.Json;
using EveConsole.Data;
using Microsoft.Data.Sqlite;

namespace EveConsole.Agent.Tools.Data;

public sealed class GetIndustryJobsTool : IAgentTool
{
    private readonly string _connString;

    public string Name        => "get_industry_jobs";
    public string Description => "Returns industry jobs (manufacturing, invention, research, reactions) from the local database. " +
                                 "Includes time remaining. " +
                                 "IMPORTANT: 'active' status only covers jobs still running. " +
                                 "Jobs that have finished but not yet been delivered have status 'ready'. " +
                                 "Use 'in_progress' to get both active and ready jobs together, or omit status to see everything.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            status = new
            {
                type        = "string",
                description = "Filter by job status: 'in_progress' (active + ready — use this for 'current' jobs), " +
                              "'active' (still running), 'ready' (finished, awaiting delivery), " +
                              "'delivered', 'all' (default: all).",
            },
            owner_name = new
            {
                type        = "string",
                description = "Filter by character or corporation name (partial match). Omit for all owners.",
            },
            limit = new { type = "integer", description = "Max results (default 50, max 200)." },
        },
    };

    private static readonly string[] ActivityNames =
    [
        "Unknown", "Manufacturing", "Unknown", "Time Efficiency Research",
        "Material Efficiency Research", "Copying", "Unknown", "Reverse Engineering",
        "Invention", "Reactions",
    ];

    public GetIndustryJobsTool(string connString) => _connString = connString;

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var statusRaw  = input.TryGetProperty("status",     out var s) ? s.GetString() : "all";
        var ownerFilter = input.TryGetProperty("owner_name", out var n) ? n.GetString() : null;
        // Also accept legacy "character_name" param if the model sends it
        if (ownerFilter is null)
            ownerFilter = input.TryGetProperty("character_name", out var cn) ? cn.GetString() : null;
        var limit = input.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 200) : 50;

        // "in_progress" is a convenience alias for both active and ready jobs
        bool inProgress = statusRaw == "in_progress";
        string? status = (statusRaw is null or "all" or "in_progress") ? null : statusRaw;

        const string sql = """
            SELECT
                j."JobId",
                j."ActivityId",
                j."Runs",
                j."Status",
                j."EndDate",
                j."Cost",
                COALESCE(c."Name",  corp."Name",  CAST(j."OwnerId"   AS TEXT)) AS owner,
                COALESCE(inst."Name", CAST(j."InstallerId" AS TEXT))          AS installer,
                COALESCE(bp_st."Name",  un_bp."Name",  CAST(j."BlueprintTypeId" AS TEXT))  AS blueprint,
                COALESCE(prod_st."Name", un_p."Name",  CAST(j."ProductTypeId"   AS TEXT))  AS product,
                COALESCE(NULLIF(sn_f."Name",''), ss_f."Name", CAST(j."FacilityId" AS TEXT)) AS facility
            FROM  "EsiIndustryJobs"  j
            LEFT JOIN "Characters"   c    ON c."Id"    = j."OwnerId" AND j."OwnerType" = 'character'
            LEFT JOIN "Corporations" corp ON corp."Id" = j."OwnerId" AND j."OwnerType" = 'corporation'
            LEFT JOIN "Characters"   inst ON inst."Id" = j."InstallerId"
            LEFT JOIN "SdeTypes"     bp_st   ON bp_st."TypeId"   = j."BlueprintTypeId"
            LEFT JOIN "SdeTypes"     prod_st ON prod_st."TypeId" = j."ProductTypeId"
            LEFT JOIN "UniverseNames" un_bp  ON un_bp."EntityId"  = j."BlueprintTypeId"
            LEFT JOIN "UniverseNames" un_p   ON un_p."EntityId"   = j."ProductTypeId"
            LEFT JOIN "SdeStations"       ss_f ON ss_f."StationId"   = j."FacilityId"
            LEFT JOIN "EsiStructureNames" sn_f ON sn_f."StructureId" = j."FacilityId"
            WHERE  (@status IS NULL OR j."Status" = @status)
              AND  (@inProgress = 0 OR j."Status" IN ('active', 'ready'))
              AND  (@owner IS NULL OR c."Name" LIKE @owner OR corp."Name" LIKE @owner)
            ORDER BY
                CASE j."Status" WHEN 'ready' THEN 0 WHEN 'active' THEN 1 WHEN 'paused' THEN 2 ELSE 3 END,
                j."EndDate"
            LIMIT @limit
            """;

        var rows = new List<object>();
        await using var conn = AppDb.Connect();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddWithValue("@status",     status is null ? (object)DBNull.Value : status);
        cmd.AddWithValue("@inProgress", inProgress ? 1 : 0);
        cmd.AddWithValue("@owner",      ownerFilter is null ? (object)DBNull.Value : $"%{ownerFilter}%");
        cmd.AddWithValue("@limit",      limit);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var activityId   = rdr.GetInt32(1);
            var activityName = activityId < ActivityNames.Length ? ActivityNames[activityId] : $"Activity {activityId}";
            var endDateRaw   = rdr.IsDBNull(4) ? null : rdr.GetString(4);
            var timeRemaining = ComputeTimeRemaining(rdr.GetString(3), endDateRaw);

            rows.Add(new
            {
                job_id          = rdr.GetInt32(0),
                activity        = activityName,
                runs            = rdr.GetInt32(2),
                status          = rdr.GetString(3),
                time_remaining  = timeRemaining,
                cost            = rdr.GetDouble(5),
                owner           = rdr.IsDBNull(6)  ? "Unknown" : rdr.GetString(6),
                installer       = rdr.IsDBNull(7)  ? "Unknown" : rdr.GetString(7),
                blueprint       = rdr.IsDBNull(8)  ? "Unknown" : rdr.GetString(8),
                product         = rdr.IsDBNull(9)  ? ""        : rdr.GetString(9),
                facility        = rdr.IsDBNull(10) ? "Unknown" : rdr.GetString(10),
            });
        }

        return rows.Count == 0
            ? "No industry jobs found matching the specified filters."
            : JsonSerializer.Serialize(rows);
    }

    private static string ComputeTimeRemaining(string status, string? endDateRaw)
    {
        if (status is "delivered" or "cancelled" or "reverted") return "Completed";
        if (status == "ready") return "Ready to deliver";
        if (endDateRaw is null) return "Unknown";
        if (!DateTimeOffset.TryParse(endDateRaw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var end))
            return "Unknown";
        var remaining = end.ToUniversalTime() - DateTimeOffset.UtcNow;
        if (remaining.TotalSeconds <= 0) return "Ready";
        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h {remaining.Minutes}m";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"{remaining.Minutes}m {remaining.Seconds}s";
    }
}
