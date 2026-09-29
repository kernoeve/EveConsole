using System.Reactive;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One line of the zKillboard panel: destroyed and lost, where each ranks, and the
/// efficiency between them. Every field is display text; a rank zKillboard does not give is "".</summary>
public sealed record ZkbStatRow(string Destroyed, string DestroyedRank, string Lost, string LostRank, string Efficiency);

/// <summary>
/// The zKillboard panel in an entity's header: ships, points and ISK destroyed and lost, with
/// ranks, for all time, the last 90 days or the last 7 — and under them the danger and gang
/// ratios for the same period.
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

    // ── Danger and gang, for the period shown ─────────────────────────────────
    //
    // Worked out as zKillboard's own entity page works them out (its view/overview.php), which
    // draws both bars for each period. For all time they match the dangerRatio and gangRatio its
    // API publishes (34 and 92 for a large alliance, 68 and 99 for a pilot), bar the one case
    // noted under gang below.

    private bool _hasDanger;
    public bool HasDanger { get => _hasDanger; private set => this.RaiseAndSetIfChanged(ref _hasDanger, value); }

    private double _dangerValue;
    public double DangerValue { get => _dangerValue; private set => this.RaiseAndSetIfChanged(ref _dangerValue, value); }

    private string _dangerText = "", _snugglyText = "";
    public string DangerText  { get => _dangerText;  private set => this.RaiseAndSetIfChanged(ref _dangerText, value); }
    public string SnugglyText { get => _snugglyText; private set => this.RaiseAndSetIfChanged(ref _snugglyText, value); }

    private bool _hasGang;
    public bool HasGang { get => _hasGang; private set => this.RaiseAndSetIfChanged(ref _hasGang, value); }

    private double _gangValue;
    public double GangValue { get => _gangValue; private set => this.RaiseAndSetIfChanged(ref _gangValue, value); }

    private string _gangText = "", _soloText = "", _gangDetail = "";
    public string GangText   { get => _gangText;   private set => this.RaiseAndSetIfChanged(ref _gangText, value); }
    public string SoloText   { get => _soloText;   private set => this.RaiseAndSetIfChanged(ref _soloText, value); }
    public string GangDetail { get => _gangDetail; private set => this.RaiseAndSetIfChanged(ref _gangDetail, value); }

    private void ShowRatios(ZkillboardApiClient.ZkbPeriod p)
    {
        // Dangerous: ships and points destroyed, against those and ships and points lost, rounded
        // down. The division comes first, as in zKillboard, so a share on a whole number rounds
        // down the same way: 29 of 100 is 28.999… that way, and 28%.
        HasDanger = p.ShipsDestroyed + p.ShipsLost > 0;
        var destroyed = (double)p.ShipsDestroyed + p.PointsDestroyed;
        var lost      = (double)p.ShipsLost + p.PointsLost;
        var danger = HasDanger ? (int)Math.Floor(destroyed / (lost + destroyed) * 100) : 0;
        DangerValue = danger;
        DangerText  = $"Dangerous {danger}%";
        SnugglyText = $"{100 - danger}% Snuggly";

        // Gang: the share of the period's kills that were not solo; no solo kill is all gang, as
        // zKillboard's page shows it. (The all-time gangRatio its API publishes guesses from the
        // points a kill was worth instead, for an entity with no solo kill ever.) A period with
        // no kills has no gang bar here; zKillboard's page draws one anyway, at 100% for 90 days
        // and 0% for 7.
        HasGang = p.ShipsDestroyed > 0;
        var gang = HasGang ? 100 - (int)Math.Floor(100 * ((double)p.SoloKills / p.ShipsDestroyed)) : 0;
        GangValue = gang;
        GangText  = $"Gang {gang}%";
        SoloText  = $"{100 - gang}% Solo";

        GangDetail = (p.AvgGangSize is { } avg ? $"average gang {avg:0.#} · " : "")
                   + $"{p.SoloKills:N0} solo kill(s), {p.SoloLosses:N0} solo loss(es)";
    }

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
        ShowRatios(p);

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
