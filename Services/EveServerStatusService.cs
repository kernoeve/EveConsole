using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ReactiveUI;
using Avalonia.Media;

namespace EveConsole.Services;

/// <summary>
/// Tracks whether Tranquility is up, and how many players are on it, from ESI's public
/// <c>/status/</c> endpoint. No authentication, and it sits in its own rate-limit bucket
/// (600 per 15 minutes), so a short poll interval costs nothing that matters.
///
/// Deliberately uses its own HttpClient rather than EsiClient: this is the one call that
/// must keep working while everything else is paused, and routing it through the shared
/// client would put it behind the same gates it is responsible for lifting.
///
/// Downtime is reported by ESI as a 5xx (503 with a "datasource ... offline" body) rather
/// than a 200 with a flag, so any non-success response is treated as "not up". A network
/// failure is NOT treated as downtime — that means our connection is broken, not the
/// server, and pausing all polling on a local network blip would be worse than useless.
/// </summary>
public sealed class EveServerStatusService(
    IHttpClientFactory httpClientFactory,
    EveConsole.Api.EsiClient esi,
    AppErrorLogger     errorLogger) : ReactiveObject
{
    private const int PollSeconds        = 30;   // ESI caches /status/ for ~30s
    private const int OfflinePollSeconds = 20;   // check back a little sooner during downtime

    // Tranquility's daily downtime is short; requiring two consecutive failures avoids
    // flapping the whole app's polling off on a single unlucky response.
    private const int FailuresBeforeOffline = 2;

    // ⚠️ The calls know before the status endpoint does. ESI caches /status/ for thirty seconds,
    // so for that long after the server goes down it can still answer "online", while every
    // data call is already coming back 502, 503 or 504 — two and a half minutes of them a night,
    // logged as errors, before the old thirty-second check plus two strikes caught up. So a
    // gateway failure on any call is downtime's first strike and asks for a check at once, and a
    // burst of them stands the client down on its own, cached status or not. Once down, the
    // client stays down until the status endpoint says online and the failures have stopped for
    // long enough that its answer cannot be the cache talking.
    private const int      BurstCount        = 3;
    private static readonly TimeSpan BurstWindow      = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PromptGap        = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HoldAfterFailure = TimeSpan.FromSeconds(45);

    private readonly System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset> _gatewayFailures = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private DateTimeOffset _lastGatewayFailure = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPromptedCheck  = DateTimeOffset.MinValue;

    private readonly HttpClient _http = httpClientFactory.CreateClient("esi-public");

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;
    private int                      _consecutiveFailures;

    private sealed record EsiStatus(
        [property: JsonPropertyName("players")]        int     Players,
        [property: JsonPropertyName("server_version")] string? ServerVersion,
        [property: JsonPropertyName("start_time")]     DateTimeOffset? StartTime);

    /// <summary>True until proven otherwise: if we have never managed to check, holding
    /// everything back would be worse than letting calls try and fail on their own.</summary>
    private bool _isOnline = true;
    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isOnline, value);
            // One flag, read by every ESI caller — see EsiClient.IsErrorLimitBlocked.
            esi.ServerOffline = !value;
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(StatusColor));
        }
    }

    private int _players;
    public int Players
    {
        get => _players;
        private set
        {
            this.RaiseAndSetIfChanged(ref _players, value);
            this.RaisePropertyChanged(nameof(PlayersText));
        }
    }

    private DateTimeOffset? _lastChecked;
    public DateTimeOffset? LastChecked
    {
        get => _lastChecked;
        private set => this.RaiseAndSetIfChanged(ref _lastChecked, value);
    }

    public string StatusText  => IsOnline ? "Online" : "Offline";
    public IBrush StatusColor => IsOnline ? Palette.Good : Palette.Bad;

    /// <summary>Blank while offline — a stale player count next to an "Offline" badge
    /// reads as though people are still logged in.</summary>
    public string PlayersText => IsOnline && Players > 0 ? $"{Players:N0} online" : "";

    public void Start()
    {
        if (_cts is not null) return;
        _cts     = new CancellationTokenSource();
        esi.GatewayFailure += OnGatewayFailure;
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>A 5xx from a data call: the first strike, a check at once, and a stand-down
    /// outright when they come in a burst.</summary>
    private void OnGatewayFailure(int statusCode)
    {
        var now = DateTimeOffset.UtcNow;
        _lastGatewayFailure = now;
        _gatewayFailures.Enqueue(now);
        while (_gatewayFailures.TryPeek(out var oldest) && now - oldest > BurstWindow)
            _gatewayFailures.TryDequeue(out _);

        if (!IsOnline) return;

        if (_gatewayFailures.Count >= BurstCount)
        {
            _consecutiveFailures = FailuresBeforeOffline;
            Players  = 0;
            IsOnline = false;
            return;
        }

        _consecutiveFailures = Math.Max(_consecutiveFailures, 1);
        if (now - _lastPromptedCheck < PromptGap) return;
        _lastPromptedCheck = now;
        _ = Task.Run(() => CheckOnceAsync(_cts?.Token ?? CancellationToken.None));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        esi.GatewayFailure -= OnGatewayFailure;
        await _cts.CancelAsync();
        if (_runTask is not null)
            try { await _runTask; } catch (OperationCanceledException) { }

        _cts     = null;
        _runTask = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await CheckOnceAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(IsOnline ? PollSeconds : OfflinePollSeconds), ct);
        }
    }

    public async Task CheckOnceAsync(CancellationToken ct = default)
    {
        // One at a time: the loop's check and a prompted one can land together.
        if (!await _checking.WaitAsync(0, ct)) return;
        try
        {
            using var response = await _http.GetAsync("status/", ct);

            if (response.IsSuccessStatusCode)
            {
                var status = await response.Content.ReadFromJsonAsync<EsiStatus>(cancellationToken: ct);

                // ⚠️ "Online" from the status endpoint is not taken at its word while the calls
                // were failing moments ago: it caches for thirty seconds, and that is exactly the
                // window in which it says online about a server that has just gone down.
                if (!IsOnline && DateTimeOffset.UtcNow - _lastGatewayFailure < HoldAfterFailure)
                    return;

                _consecutiveFailures = 0;
                Players  = status?.Players ?? 0;
                IsOnline = true;
            }
            else if (response.StatusCode is HttpStatusCode.ServiceUnavailable
                                         or HttpStatusCode.GatewayTimeout
                                         or HttpStatusCode.BadGateway)
            {
                RegisterFailure();
            }
            // Any other status (429, 4xx) says something about our request, not about
            // Tranquility — leave the current state alone.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Local connectivity problem: explicitly NOT downtime. See class remarks.
            errorLogger.Log(nameof(EveServerStatusService), nameof(CheckOnceAsync), ex);
        }
        finally
        {
            LastChecked = DateTimeOffset.UtcNow;
            _checking.Release();
        }
    }

    private void RegisterFailure()
    {
        if (++_consecutiveFailures < FailuresBeforeOffline) return;
        Players  = 0;
        IsOnline = false;
    }
}
