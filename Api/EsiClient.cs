using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveCortex.Auth;
using EveCortex.Models;

namespace EveCortex.Api;

/// <summary>
/// Typed HTTP client for the Eve ESI REST API.
/// Singleton — holds per-character token state; tokens are lazy-refreshed on first use.
/// </summary>
public class EsiClient
{
    private readonly HttpClient      _http;
    private readonly EsiAuthService  _auth;
    private readonly ConcurrentDictionary<long, TokenSet> _tokens     = new();
    private readonly ConcurrentDictionary<long, TokenSet> _corpTokens = new();

    // Limits simultaneous ESI HTTP calls app-wide; prevents request bursts from exhausting
    // ESI's per-client error limit when many characters/corps poll in parallel.
    private readonly SemaphoreSlim _httpGate = new(2, 2);

    // ESI global error limit block — set on HTTP 420 or when error budget is nearly exhausted.
    // Written by authenticated-endpoint responses; checked by all callers including market refresh.
    private long _errorLimitBlockedTicks; // UTC ticks; 0 = not blocked

    internal bool IsErrorLimitBlocked
    {
        get
        {
            var ticks = Interlocked.Read(ref _errorLimitBlockedTicks);
            return ticks > 0 && DateTimeOffset.UtcNow.UtcTicks < ticks;
        }
    }

    private void UpdateErrorLimitState(int statusCode, int? errorLimitRemain, int? errorLimitReset)
    {
        if (statusCode == 420)
            Interlocked.Exchange(ref _errorLimitBlockedTicks,
                DateTimeOffset.UtcNow.AddSeconds((errorLimitReset ?? 30) + 1).UtcTicks);
        else if (errorLimitRemain.HasValue && errorLimitRemain.Value < 20 && errorLimitReset.HasValue)
            Interlocked.Exchange(ref _errorLimitBlockedTicks,
                DateTimeOffset.UtcNow.AddSeconds(errorLimitReset.Value).UtcTicks);
        else if (errorLimitRemain.HasValue && errorLimitRemain.Value > 30)
            Interlocked.Exchange(ref _errorLimitBlockedTicks, 0L);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public EsiClient(IHttpClientFactory httpFactory, EsiAuthService auth)
    {
        _http = httpFactory.CreateClient("esi");
        _auth = auth;
    }

    // -----------------------------------------------------------------------
    // Token management
    // -----------------------------------------------------------------------

    /// <summary>
    /// Register a character whose token will be lazy-refreshed on first API call.
    /// Use this on startup to restore characters from DB without hitting the network.
    /// </summary>
    public void RegisterCharacter(long characterId, string refreshToken)
        => _tokens[characterId] = new TokenSet("", refreshToken, DateTimeOffset.UnixEpoch);

    /// <summary>Register a freshly-obtained token set (e.g. after a live login).</summary>
    public void SetTokens(long characterId, TokenSet tokens)
        => _tokens[characterId] = tokens;

    /// <summary>
    /// Register a corporation whose token will be lazy-refreshed on first API call.
    /// The refresh token must belong to a character with the director/accountant role.
    /// </summary>
    public void RegisterCorporation(long corpId, string refreshToken)
        => _corpTokens[corpId] = new TokenSet("", refreshToken, DateTimeOffset.UnixEpoch);

    /// <summary>Register a freshly-obtained corp token set.</summary>
    public void SetCorpTokens(long corpId, TokenSet tokens)
        => _corpTokens[corpId] = tokens;

    // -----------------------------------------------------------------------
    // Character endpoints
    // -----------------------------------------------------------------------

    public Task<EsiCharacterPublic?> GetCharacterPublicAsync(long characterId, CancellationToken ct = default)
        => GetAsync<EsiCharacterPublic>($"characters/{characterId}/", ct);

    public Task<EsiSkills?> GetSkillsAsync(long characterId, CancellationToken ct = default)
        => GetAuthAsync<EsiSkills>(characterId, $"characters/{characterId}/skills/", ct);

    public Task<List<EsiSkillQueueItem>?> GetSkillQueueAsync(long characterId, CancellationToken ct = default)
        => GetAuthAsync<List<EsiSkillQueueItem>>(characterId, $"characters/{characterId}/skillqueue/", ct);

    public Task<double> GetWalletBalanceAsync(long characterId, CancellationToken ct = default)
        => GetAuthAsync<double>(characterId, $"characters/{characterId}/wallet/", ct);

    // -----------------------------------------------------------------------
    // Universe / static data endpoints (no auth)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves up to 1000 IDs (types, characters, corps, etc.) to their names.
    /// Results are keyed by id in the returned list.
    /// </summary>
    public async Task<List<EsiUniverseName>> GetNamesAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var response = await _http.PostAsJsonAsync("universe/names/", ids, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<EsiUniverseName>>(JsonOptions, ct) ?? [];
    }

    /// <summary>
    /// Resolves up to 1000 int64 IDs to names — handles character IDs above int.MaxValue.
    /// </summary>
    public async Task<List<EsiUniverseNameLong>> GetNamesAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var response = await _http.PostAsJsonAsync("universe/names/", ids, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<EsiUniverseNameLong>>(JsonOptions, ct) ?? [];
    }

    private sealed record EsiSearchResult(
        [property: JsonPropertyName("character")] List<int>? Character);

    private sealed record EsiUniverseIdsResult(
        [property: JsonPropertyName("characters")]   List<EsiIdItem>? Characters,
        [property: JsonPropertyName("corporations")] List<EsiIdItem>? Corporations,
        [property: JsonPropertyName("alliances")]    List<EsiIdItem>? Alliances);

    private sealed record EsiIdItem(
        [property: JsonPropertyName("id")]   int    Id,
        [property: JsonPropertyName("name")] string Name);

    /// <summary>
    /// Resolves an exact name to its EVE entity via the public POST /universe/ids/ endpoint.
    /// Returns a flat list of (Id, Name, Category) — category is "character", "corporation", or "alliance".
    /// </summary>
    public async Task<List<(long Id, string Name, string Category)>> LookupEntityIdsAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        if (names.Count == 0) return [];
        try
        {
            var response = await _http.PostAsJsonAsync("universe/ids/", names, JsonOptions, ct);
            if (!response.IsSuccessStatusCode) return [];
            var result = await response.Content.ReadFromJsonAsync<EsiUniverseIdsResult>(JsonOptions, ct);
            if (result is null) return [];
            var all = new List<(long, string, string)>();
            foreach (var c in result.Characters   ?? []) all.Add((c.Id, c.Name, "character"));
            foreach (var c in result.Corporations ?? []) all.Add((c.Id, c.Name, "corporation"));
            foreach (var c in result.Alliances    ?? []) all.Add((c.Id, c.Name, "alliance"));
            return all;
        }
        catch { return []; }
    }

    public async Task<List<int>> SearchCharacterIdsAsync(long charId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var url = $"characters/{charId}/search/?categories=character&search={Uri.EscapeDataString(name)}&strict=false";
        try
        {
            var result = await GetAuthAsync<EsiSearchResult>(charId, url, ct);
            return result?.Character ?? [];
        }
        catch { return []; }
    }

    // -----------------------------------------------------------------------
    // Universe location endpoints
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves an NPC station ID to its detail (no auth required).
    /// Use for station IDs below ~1,000,000,000,000.
    /// </summary>
    public async Task<EsiStationDetail?> GetStationAsync(long stationId, CancellationToken ct = default)
    {
        try { return await GetAsync<EsiStationDetail>($"universe/stations/{stationId}/", ct); }
        catch { return null; }
    }

    /// <summary>
    /// Resolves a player-owned structure ID (>= 1,000,000,000,000) to its detail (auth required).
    /// The character must have docking access to the structure.
    /// </summary>
    public Task<EsiCallResult<EsiStructureDetail>> GetStructureAsync(long charId, long structureId, CancellationToken ct = default)
        => ExecuteAuthAsync<EsiStructureDetail>(charId, $"universe/structures/{structureId}/", ct);

    // -----------------------------------------------------------------------
    // Sovereignty endpoints (no auth)
    // -----------------------------------------------------------------------

    public async Task<List<EsiSovStructure>?> GetSovStructuresAsync(CancellationToken ct = default)
    {
        try { return await GetAsync<List<EsiSovStructure>>("sovereignty/structures/", ct); }
        catch { return null; }
    }

    public sealed class EsiSovStructure
    {
        [System.Text.Json.Serialization.JsonPropertyName("solar_system_id")]
        public int     SolarSystemId                 { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("structure_type_id")]
        public int     StructureTypeId               { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("vulnerability_occupancy_level")]
        public double? VulnerabilityOccupancyLevel   { get; set; }
    }

    /// <summary>
    /// Searches for stations and structures matching the query string.
    /// Requires a character with access to the relevant structures.
    /// </summary>
    public async Task<EsiLocationSearch?> SearchLocationsAsync(long charId, string query, CancellationToken ct = default)
    {
        try
        {
            return await GetAuthAsync<EsiLocationSearch>(charId,
                $"characters/{charId}/search/?categories=station,structure&search={Uri.EscapeDataString(query)}",
                ct);
        }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    // Corporation endpoints
    // -----------------------------------------------------------------------

    public Task<EsiCorporation?> GetCorporationPublicAsync(int corpId, CancellationToken ct = default)
        => GetAsync<EsiCorporation>($"corporations/{corpId}/", ct);

    /// <summary>
    /// Returns null on HTTP error (caller should not cache the result).
    /// Returns an empty list when ESI responds 200 with no data (item has no history in region).
    /// </summary>
    // Returns (data, statusCode). data is null on any non-success; statusCode is 0 when the call
    // was skipped because we're error-limit blocked. Callers use the status to tell a terminal 4xx
    // (this type simply has no market history here) from a transient failure worth retrying.
    public async Task<(List<EsiMarketHistoryEntry>? Data, int Status)> GetMarketHistoryAsync(
        int regionId, int typeId, CancellationToken ct = default)
    {
        if (IsErrorLimitBlocked) return (null, 0);

        await _httpGate.WaitAsync(ct);
        HttpResponseMessage response;
        try { response = await _http.GetAsync($"markets/{regionId}/history/?type_id={typeId}", ct); }
        finally { _httpGate.Release(); }

        // Feed the shared error-limit tracker so the background history sweep self-throttles
        // and can never push us over ESI's error limit — even when it runs on its own.
        var headers = response.Headers;
        int? remain = headers.TryGetValues("X-Esi-Error-Limit-Remain", out var rv)
                      && int.TryParse(rv.FirstOrDefault(), out var r) ? r : null;
        int? reset  = headers.TryGetValues("X-Esi-Error-Limit-Reset", out var sv)
                      && int.TryParse(sv.FirstOrDefault(), out var s) ? s : null;
        UpdateErrorLimitState((int)response.StatusCode, remain, reset);

        if (!response.IsSuccessStatusCode) return (null, (int)response.StatusCode);
        var data = await response.Content.ReadFromJsonAsync<List<EsiMarketHistoryEntry>>(JsonOptions, ct) ?? [];
        return (data, (int)response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // HTTP helpers
    // -----------------------------------------------------------------------

    // Kill mail detail — public endpoint, no auth required
    public Task<EsiKillMailFull?> GetKillMailAsync(int killMailId, string hash, CancellationToken ct = default)
        => GetAsync<EsiKillMailFull>($"killmails/{killMailId}/{hash}/", ct);

    // Moon detail (public). Returns null on error. name is e.g. "X-1QGA VI - Moon 3".
    public async Task<EsiMoonDetail?> GetMoonAsync(int moonId, CancellationToken ct = default)
    {
        try { return await GetAsync<EsiMoonDetail>($"universe/moons/{moonId}/", ct); }
        catch { return null; }
    }

    private async Task<T?> GetAsync<T>(string endpoint, CancellationToken ct)
    {
        var response = await _http.GetAsync(endpoint, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    private async Task<T?> GetAuthAsync<T>(long characterId, string endpoint, CancellationToken ct)
    {
        var token = await EnsureValidTokenAsync(characterId, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    // -----------------------------------------------------------------------
    // Internal polling API — returns rich result with rate-limit headers.
    // Does not throw on non-2xx; caller inspects EsiCallResult.IsSuccess.
    // -----------------------------------------------------------------------

    internal async Task<EsiCallResult<T>> ExecuteAuthAsync<T>(
        long characterId, string path, CancellationToken ct, int page = 0)
    {
        try
        {
            var token = await EnsureValidTokenAsync(characterId, ct);
            var url = page > 0
                ? $"{path}{(path.Contains('?') ? '&' : '?')}page={page}"
                : path;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            await _httpGate.WaitAsync(ct);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, ct); }
            finally { _httpGate.Release(); }
            var headers  = response.Headers;

            int? TryGetInt(string name) =>
                headers.TryGetValues(name, out var vals)
                && int.TryParse(vals.FirstOrDefault(), out var v) ? v : null;

            string? TryGetStr(string name) =>
                headers.TryGetValues(name, out var vals) ? vals.FirstOrDefault() : null;

            int totalPages = TryGetInt("X-Pages") ?? 1;
            int statusCode = (int)response.StatusCode;

            T? data = default;
            string? error = null;
            if (response.IsSuccessStatusCode)
                data = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
            else
                error = await response.Content.ReadAsStringAsync(ct);

            var errorLimitRemain = TryGetInt("X-Esi-Error-Limit-Remain");
            var errorLimitReset  = TryGetInt("X-Esi-Error-Limit-Reset");
            UpdateErrorLimitState(statusCode, errorLimitRemain, errorLimitReset);

            return new EsiCallResult<T>
            {
                Data               = data,
                StatusCode         = statusCode,
                TotalPages         = totalPages,
                RateLimitGroup     = TryGetStr("X-Ratelimit-Group"),
                RateLimitRemaining = TryGetInt("X-Ratelimit-Remaining"),
                RateLimitLimit     = TryGetInt("X-Ratelimit-Limit"),
                ErrorLimitRemain   = errorLimitRemain,
                ErrorLimitReset    = errorLimitReset,
                RetryAfterSeconds  = TryGetInt("Retry-After"),
                Error              = error,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new EsiCallResult<T> { StatusCode = 0, Error = ex.Message };
        }
    }

    /// <summary>
    /// Fetches all pages of a public (no-auth) ESI endpoint.
    /// Used for regional market orders: GET /markets/{region_id}/orders/
    /// </summary>
    internal async Task<EsiCallResult<List<T>>> ExecutePublicAllPagesAsync<T>(
        string path, CancellationToken ct)
    {
        var firstPage = await ExecutePublicAsync<List<T>>(path, ct, page: 1);
        if (!firstPage.IsSuccess || firstPage.TotalPages <= 1)
        {
            return new EsiCallResult<List<T>>
            {
                Data       = firstPage.Data ?? [],
                StatusCode = firstPage.StatusCode,
                TotalPages = firstPage.TotalPages,
                Error      = firstPage.Error,
            };
        }

        var allItems = new List<T>(firstPage.Data ?? []);
        bool complete = true;
        for (int p = 2; p <= firstPage.TotalPages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ExecutePublicAsync<List<T>>(path, ct, page: p);
            if (page.IsSuccess && page.Data is not null)
                allItems.AddRange(page.Data);
            else
                complete = false;   // a page dropped — Data is now an incomplete set
        }

        return new EsiCallResult<List<T>>
        {
            Data       = allItems,
            StatusCode = firstPage.StatusCode,
            TotalPages = firstPage.TotalPages,
            Complete   = complete,
        };
    }

    private async Task<EsiCallResult<T>> ExecutePublicAsync<T>(
        string path, CancellationToken ct, int page = 0)
    {
        try
        {
            var url      = page > 0 ? $"{path}?page={page}" : path;
            var response = await _http.GetAsync(url, ct);

            int? TryGetInt(string name) =>
                response.Headers.TryGetValues(name, out var vals)
                && int.TryParse(vals.FirstOrDefault(), out var v) ? v : null;

            int totalPages = TryGetInt("X-Pages") ?? 1;
            int statusCode = (int)response.StatusCode;

            T?      data  = default;
            string? error = null;
            if (response.IsSuccessStatusCode)
            {
                // A 204, or a success with an empty body, is a valid "no content" response (e.g. a
                // public contract with no retrievable items). Treat it as empty rather than letting
                // the JSON reader throw "input does not contain any JSON tokens".
                if (statusCode != 204)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    if (!string.IsNullOrWhiteSpace(body))
                        data = JsonSerializer.Deserialize<T>(body, JsonOptions);
                }
            }
            else
                error = await response.Content.ReadAsStringAsync(ct);

            return new EsiCallResult<T>
            {
                Data       = data,
                StatusCode = statusCode,
                TotalPages = totalPages,
                Error      = error,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new EsiCallResult<T> { StatusCode = 0, Error = ex.Message };
        }
    }

    internal async Task<EsiCallResult<List<T>>> ExecuteAllPagesAsync<T>(
        long characterId, string path, CancellationToken ct)
    {
        var firstPage = await ExecuteAuthAsync<List<T>>(characterId, path, ct, page: 1);
        if (!firstPage.IsSuccess || firstPage.TotalPages <= 1)
        {
            return new EsiCallResult<List<T>>
            {
                Data               = firstPage.Data ?? [],
                StatusCode         = firstPage.StatusCode,
                TotalPages         = firstPage.TotalPages,
                RateLimitGroup     = firstPage.RateLimitGroup,
                RateLimitRemaining = firstPage.RateLimitRemaining,
                RateLimitLimit     = firstPage.RateLimitLimit,
                ErrorLimitRemain   = firstPage.ErrorLimitRemain,
                ErrorLimitReset    = firstPage.ErrorLimitReset,
                RetryAfterSeconds  = firstPage.RetryAfterSeconds,
                Error              = firstPage.Error,
            };
        }

        var allItems = new List<T>(firstPage.Data ?? []);
        for (int p = 2; p <= firstPage.TotalPages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ExecuteAuthAsync<List<T>>(characterId, path, ct, page: p);
            if (page.IsSuccess && page.Data is not null)
                allItems.AddRange(page.Data);
        }

        return new EsiCallResult<List<T>>
        {
            Data               = allItems,
            StatusCode         = firstPage.StatusCode,
            TotalPages         = firstPage.TotalPages,
            RateLimitGroup     = firstPage.RateLimitGroup,
            RateLimitRemaining = firstPage.RateLimitRemaining,
            RateLimitLimit     = firstPage.RateLimitLimit,
            ErrorLimitRemain   = firstPage.ErrorLimitRemain,
            ErrorLimitReset    = firstPage.ErrorLimitReset,
        };
    }

    // -----------------------------------------------------------------------
    // Internal polling API — corporation (separate token dict; IDs can overlap)
    // -----------------------------------------------------------------------

    internal async Task<EsiCallResult<T>> ExecuteCorpAuthAsync<T>(
        long corpId, string path, CancellationToken ct, int page = 0,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        try
        {
            var token = await EnsureValidCorpTokenAsync(corpId, ct);
            var url = page > 0
                ? $"{path}{(path.Contains('?') ? '&' : '?')}page={page}"
                : path;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            if (extraHeaders is not null)
                foreach (var (k, v) in extraHeaders)
                    request.Headers.TryAddWithoutValidation(k, v);

            await _httpGate.WaitAsync(ct);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, ct); }
            finally { _httpGate.Release(); }
            var headers  = response.Headers;

            int? TryGetInt(string name) =>
                headers.TryGetValues(name, out var vals)
                && int.TryParse(vals.FirstOrDefault(), out var v) ? v : null;

            string? TryGetStr(string name) =>
                headers.TryGetValues(name, out var vals) ? vals.FirstOrDefault() : null;

            int totalPages = TryGetInt("X-Pages") ?? 1;
            int statusCode = (int)response.StatusCode;

            T? data = default;
            string? error = null;
            if (response.IsSuccessStatusCode)
                data = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
            else
                error = await response.Content.ReadAsStringAsync(ct);

            var esiErrorRemain = TryGetInt("X-Esi-Error-Limit-Remain");
            var esiErrorReset  = TryGetInt("X-Esi-Error-Limit-Reset");
            UpdateErrorLimitState(statusCode, esiErrorRemain, esiErrorReset);

            return new EsiCallResult<T>
            {
                Data               = data,
                StatusCode         = statusCode,
                TotalPages         = totalPages,
                RateLimitGroup     = TryGetStr("X-Ratelimit-Group"),
                RateLimitRemaining = TryGetInt("X-Ratelimit-Remaining"),
                RateLimitLimit     = TryGetInt("X-Ratelimit-Limit"),
                ErrorLimitRemain   = esiErrorRemain,
                ErrorLimitReset    = esiErrorReset,
                RetryAfterSeconds  = TryGetInt("Retry-After"),
                Error              = error,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new EsiCallResult<T> { StatusCode = 0, Error = ex.Message };
        }
    }

    internal async Task<EsiCallResult<List<T>>> ExecuteCorpAllPagesAsync<T>(
        long corpId, string path, CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var firstPage = await ExecuteCorpAuthAsync<List<T>>(corpId, path, ct, page: 1, extraHeaders: extraHeaders);
        if (!firstPage.IsSuccess || firstPage.TotalPages <= 1)
        {
            return new EsiCallResult<List<T>>
            {
                Data               = firstPage.Data ?? [],
                StatusCode         = firstPage.StatusCode,
                TotalPages         = firstPage.TotalPages,
                RateLimitGroup     = firstPage.RateLimitGroup,
                RateLimitRemaining = firstPage.RateLimitRemaining,
                RateLimitLimit     = firstPage.RateLimitLimit,
                ErrorLimitRemain   = firstPage.ErrorLimitRemain,
                ErrorLimitReset    = firstPage.ErrorLimitReset,
                RetryAfterSeconds  = firstPage.RetryAfterSeconds,
                Error              = firstPage.Error,
            };
        }

        var allItems = new List<T>(firstPage.Data ?? []);
        for (int p = 2; p <= firstPage.TotalPages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ExecuteCorpAuthAsync<List<T>>(corpId, path, ct, page: p, extraHeaders: extraHeaders);
            if (page.IsSuccess && page.Data is not null)
                allItems.AddRange(page.Data);
        }

        return new EsiCallResult<List<T>>
        {
            Data               = allItems,
            StatusCode         = firstPage.StatusCode,
            TotalPages         = firstPage.TotalPages,
            RateLimitGroup     = firstPage.RateLimitGroup,
            RateLimitRemaining = firstPage.RateLimitRemaining,
            RateLimitLimit     = firstPage.RateLimitLimit,
            ErrorLimitRemain   = firstPage.ErrorLimitRemain,
            ErrorLimitReset    = firstPage.ErrorLimitReset,
        };
    }

    // -----------------------------------------------------------------------
    // Authenticated write endpoints (POST / PUT) — used for Eve Mail
    // -----------------------------------------------------------------------

    /// <summary>
    /// Authenticated POST returning a typed response body.
    /// Returns (statusCode, data) — does not throw on non-2xx.
    /// </summary>
    internal async Task<(int StatusCode, T? Data)> PostAuthAsync<T>(
        long characterId, string path, object body, CancellationToken ct)
    {
        try
        {
            var token = await EnsureValidTokenAsync(characterId, ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            request.Content = JsonContent.Create(body, options: JsonOptions);

            await _httpGate.WaitAsync(ct);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, ct); }
            finally { _httpGate.Release(); }

            var statusCode = (int)response.StatusCode;
            T? data = default;
            if (response.IsSuccessStatusCode)
                data = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
            return (statusCode, data);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (0, default); }
    }

    /// <summary>
    /// Authenticated PUT with JSON body. Returns true on 2xx.
    /// </summary>
    internal async Task<bool> PutAuthAsync(
        long characterId, string path, object body, CancellationToken ct)
    {
        try
        {
            var token = await EnsureValidTokenAsync(characterId, ct);
            using var request = new HttpRequestMessage(HttpMethod.Put, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            request.Content = JsonContent.Create(body, options: JsonOptions);

            await _httpGate.WaitAsync(ct);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, ct); }
            finally { _httpGate.Release(); }

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private async Task<TokenSet> EnsureValidCorpTokenAsync(long corpId, CancellationToken ct)
    {
        if (!_corpTokens.TryGetValue(corpId, out var tokens))
            throw new InvalidOperationException(
                $"No token registered for corporation {corpId}. Call RegisterCorporation() first.");

        if (tokens.IsExpired)
        {
            tokens = await _auth.RefreshAsync(tokens.RefreshToken, ct);
            _corpTokens[corpId] = tokens;
        }
        return tokens;
    }

    private async Task<TokenSet> EnsureValidTokenAsync(long characterId, CancellationToken ct)
    {
        if (!_tokens.TryGetValue(characterId, out var tokens))
            throw new InvalidOperationException(
                $"No token registered for character {characterId}. Call RegisterCharacter() or SetTokens() first.");

        if (tokens.IsExpired)
        {
            tokens = await _auth.RefreshAsync(tokens.RefreshToken, ct);
            _tokens[characterId] = tokens;
        }
        return tokens;
    }
}
