using System.Reactive;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One line of the zKillboard panel: destroyed and lost, where each ranks, and the
/// efficiency between them. Every field is display text; a rank zKillboard does not give is "".</summary>
public sealed record ZkbStatRow(string Destroyed, string DestroyedRank, string Lost, string LostRank, string Efficiency);

/// <summary>
/// The zKillboard panel in an entity's header: ships, points and ISK destroyed and lost, with
/// ranks, for all time, the last 90 days or the last 7 — and the danger and gang ratios under it.
///
/// <para>zKillboard's own figures, never counted here: the database holds only the kills something
/// brought in, which for anyone but our own is a sample, and a header counting that sample read
/// as the entity's record.</para>
/// </summary>
public class ZkbStatsVm : ReactiveObject
{
    private enum Span { AllTime, Recent, Weekly }

    private readonly ZkillboardApiClient.ZkbStats _stats;
    private Span _span;

    public ZkbStatsVm(ZkillboardApiClient.ZkbStats stats, string url)
    {
        _stats = stats;
        Url    = url;
        OpenCommand = ReactiveCommand.Create(() => ExternalLinks.Launch(Url));
        Show(Span.AllTime);
    }

    /// <summary>The entity's own page on zKillboard.</summary>
    public string Url { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    // ── The period ────────────────────────────────────────────────────────────
    // Three radio buttons bound two ways; only a button being checked moves the period, so the
    // one being unchecked as another is picked changes nothing.

    public bool IsAllTime { get => _span == Span.AllTime; set { if (value) Show(Span.AllTime); } }
    public bool IsRecent  { get => _span == Span.Recent;  set { if (value) Show(Span.Recent); } }
    public bool IsWeekly  { get => _span == Span.Weekly;  set { if (value) Show(Span.Weekly); } }

    private ZkbStatRow _ships = Blank, _points = Blank, _isk = Blank;
    public ZkbStatRow Ships  { get => _ships;  private set => this.RaiseAndSetIfChanged(ref _ships, value); }
    public ZkbStatRow Points { get => _points; private set => this.RaiseAndSetIfChanged(ref _points, value); }
    public ZkbStatRow Isk    { get => _isk;    private set => this.RaiseAndSetIfChanged(ref _isk, value); }

    /// <summary>"Rank #10" overall for the period, or "" when zKillboard gives none.</summary>
    private string _rankText = "";
    public string RankText { get => _rankText; private set => this.RaiseAndSetIfChanged(ref _rankText, value); }

    /// <summary>Said instead of a table of zeros when the period has no kills or losses.</summary>
    private string _emptyText = "";
    public string EmptyText { get => _emptyText; private set => this.RaiseAndSetIfChanged(ref _emptyText, value); }

    private bool _hasFigures;
    public bool HasFigures { get => _hasFigures; private set => this.RaiseAndSetIfChanged(ref _hasFigures, value); }

    // ── Danger and gang — zKillboard gives these for all time only ─────────────

    public bool   HasDanger   => _stats.DangerRatio is not null;
    public double DangerValue => _stats.DangerRatio ?? 0;
    public string DangerText  => $"Dangerous {_stats.DangerRatio ?? 0}%";
    public string SnugglyText => $"{100 - (_stats.DangerRatio ?? 0)}% Snuggly";

    public bool   HasGang   => _stats.GangRatio is not null;
    public double GangValue => _stats.GangRatio ?? 0;
    public string GangText  => $"Gang {_stats.GangRatio ?? 0}%";
    public string SoloText  => $"{100 - (_stats.GangRatio ?? 0)}% Solo";

    public string GangDetail =>
        (_stats.AvgGangSize is { } avg ? $"average gang {avg:0.#} · " : "")
        + $"{_stats.SoloKills:N0} solo kill(s), {_stats.SoloLosses:N0} solo loss(es)";

    private static readonly ZkbStatRow Blank = new("", "", "", "", "");

    private void Show(Span span)
    {
        _span = span;
        var p = span switch
        {
            Span.Recent => _stats.Recent,
            Span.Weekly => _stats.Weekly,
            _           => _stats.AllTime,
        };
        var r = p.Ranks;

        Ships  = new(Count(p.ShipsDestroyed),  RankOf(r?.ShipsDestroyed),  Count(p.ShipsLost),  RankOf(r?.ShipsLost),
                     Efficiency(p.ShipsDestroyed, p.ShipsLost));
        Points = new(Count(p.PointsDestroyed), RankOf(r?.PointsDestroyed), Count(p.PointsLost), RankOf(r?.PointsLost),
                     Efficiency(p.PointsDestroyed, p.PointsLost));
        Isk    = new(ShortIsk(p.IskDestroyed), RankOf(r?.IskDestroyed),    ShortIsk(p.IskLost), RankOf(r?.IskLost),
                     Efficiency(p.IskDestroyed, p.IskLost));

        RankText   = r?.Overall is long overall ? $"Rank #{overall:N0}" : "";
        EmptyText  = !p.IsEmpty ? ""
                   : span switch
                   {
                       Span.Recent => "No kills or losses in the last 90 days.",
                       Span.Weekly => "No kills or losses in the last 7 days.",
                       _           => "No kills or losses on zKillboard.",
                   };
        HasFigures = !p.IsEmpty;

        this.RaisePropertyChanged(nameof(IsAllTime));
        this.RaisePropertyChanged(nameof(IsRecent));
        this.RaisePropertyChanged(nameof(IsWeekly));
    }

    private static string Count(long n) => n.ToString("N0");
    private static string RankOf(long? rank) => rank is long r ? $"#{r:N0}" : "";

    /// <summary>Destroyed as a share of both, zKillboard's "Eff. %". A dash when there is neither.</summary>
    private static string Efficiency(double destroyed, double lost) =>
        destroyed + lost > 0 ? $"{100 * destroyed / (destroyed + lost):0.0}%" : "—";

    /// <summary>ISK in the short form zKillboard uses: 1.49T, 2.23B.</summary>
    internal static string ShortIsk(double v) => Math.Abs(v) switch
    {
        >= 1e12 => $"{v / 1e12:0.##}T",
        >= 1e9  => $"{v / 1e9:0.##}B",
        >= 1e6  => $"{v / 1e6:0.##}M",
        >= 1e3  => $"{v / 1e3:0.#}K",
        _       => $"{v:N0}",
    };
}
