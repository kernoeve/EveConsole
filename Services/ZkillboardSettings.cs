namespace EveConsole.Services;

/// <summary>Which kills zKillboard integration captures. Determines both which live
/// mechanism runs (targeted interval poll vs the R2Z2 firehose) and what the daily-dump
/// backfill filters to — see ZkillboardPollingService / ZkillboardFirehoseService.</summary>
public enum ZkbScope
{
    /// <summary>Only kills/losses involving a tracked character or corp. Live capture
    /// uses a lightweight interval poll of zKillboard's per-entity API.</summary>
    MineAndCorp,

    /// <summary>Every kill in New Eden. Live capture uses the R2Z2 firehose instead of
    /// the interval poll, since zKillboard has no "everything, unfiltered" poll endpoint.
    /// Database growth is substantially higher in this mode.</summary>
    All,
}

/// <summary>
/// Typed view over the zKillboard integration's preference keys, wrapping
/// AppPreferencesService the same way MonitoringSettings does for the log importers.
/// </summary>
public sealed class ZkillboardSettings(AppPreferencesService prefs)
{
    public const string KeyEnabled           = "zkb.enabled";
    public const string KeyScope             = "zkb.scope";
    public const string KeyPollIntervalSecs  = "zkb.poll_interval_seconds";
    public const string KeyBackfillDays      = "zkb.backfill_days";
    public const string KeyLastFullDay       = "zkb.last_full_day";
    public const string KeyR2Z2LastSequence  = "zkb.r2z2_last_sequence";
    public const string KeyR2Z2SequenceSaved = "zkb.r2z2_sequence_saved_at";
    public const string KeyPostEnabled       = "zkb.post_enabled";

    /// <summary>
    /// ON by default. ESI's own killmail endpoints only return a kill when the tracked character
    /// or corporation was the victim or landed the final blow, so participation without the final
    /// blow — routine in fleet fights — is invisible to them. zKillboard closes that gap, which
    /// makes it the normal way to have complete kill data rather than an opt-in extra.
    ///
    /// ⚠️ Defaulting to on means an install that has never touched this setting starts polling
    /// zKillboard on upgrade, because an absent preference reads as the default. That is the
    /// intent; the traffic is the targeted per-character/corp API in the default Mine + Corp
    /// scope, not the firehose.
    /// </summary>
    public bool Enabled
    {
        get => prefs.GetBool(KeyEnabled, true);
        set => _ = prefs.SetBoolAsync(KeyEnabled, value);
    }

    public ZkbScope Scope
    {
        get => prefs.Get(KeyScope) == "All" ? ZkbScope.All : ZkbScope.MineAndCorp;
        set => _ = prefs.SetAsync(KeyScope, value.ToString());
    }

    /// <summary>How often the Mine+Corp interval poll runs. Meaningless in All scope —
    /// the R2Z2 firehose paces itself instead.</summary>
    public int PollIntervalSeconds
    {
        get => Math.Clamp((int)prefs.GetLong(KeyPollIntervalSecs, 300), 60, 3600);
        set => _ = prefs.SetLongAsync(KeyPollIntervalSecs, Math.Clamp(value, 60, 3600));
    }

    /// <summary>Day window most recently chosen for a manual backfill. Only a
    /// remembered UI value — the backfill itself is explicit and user-initiated.</summary>
    public int BackfillDays
    {
        get => Math.Clamp((int)prefs.GetLong(KeyBackfillDays, 30), 1, 3650);
        set => _ = prefs.SetLongAsync(KeyBackfillDays, Math.Clamp(value, 1, 3650));
    }

    /// <summary>The last calendar day (UTC) whose daily dump has been fully imported by
    /// the automatic gap-fill. Null means gap-fill has never run — the first run seeds
    /// this to yesterday without importing anything, since there is no prior session to
    /// have missed.</summary>
    public DateOnly? LastFullDay
    {
        get => DateOnly.TryParseExact(prefs.Get(KeyLastFullDay), "yyyyMMdd", out var d) ? d : null;
        set => _ = prefs.SetAsync(KeyLastFullDay, value?.ToString("yyyyMMdd"));
    }

    /// <summary>Submit kills zKillboard doesn't have back to zKillboard. OFF by default.
    /// See ZkillboardPostService for what "doesn't have" means and why it is bounded by
    /// <see cref="CoverageFrom"/>.</summary>
    public bool PostEnabled
    {
        get => prefs.GetBool(KeyPostEnabled, false);
        set => _ = prefs.SetBoolAsync(KeyPostEnabled, value);
    }

    /// <summary>R2Z2 stream position to resume from. 0 = unset (start at "now" via the
    /// sequence endpoint).
    ///
    /// No expiry judgement is made here any more. This used to be paired with a 23-hour
    /// staleness rule based on the documented ~24h retention, which measured badly wrong:
    /// on 2026-08-02 the oldest retained entry was 7.86 days behind the head. That rule
    /// threw away perfectly resumable streams after a single overnight outage and fell
    /// back to daily dumps that had not been published yet, which is how a whole day of
    /// kills went missing. ZkillboardFirehoseService now decides where to start by
    /// measuring the retained range directly (ZkillboardApiClient.FindSequenceAtAsync)
    /// rather than assuming a window.</summary>
    public long R2Z2LastSequence
    {
        get => prefs.GetLong(KeyR2Z2LastSequence, 0);
        set => _ = prefs.SetLongAsync(KeyR2Z2LastSequence, value);
    }

    public DateTimeOffset? R2Z2SequenceSavedAt
    {
        get => DateTimeOffset.TryParse(prefs.Get(KeyR2Z2SequenceSaved), out var t) ? t : null;
        set => _ = prefs.SetAsync(KeyR2Z2SequenceSaved, value?.ToString("O"));
    }

    /// <summary>Persist the current stream position. The timestamp is kept for display
    /// and diagnostics only — nothing branches on it.</summary>
    public void SaveR2Z2Position(long sequenceId)
    {
        R2Z2LastSequence     = sequenceId;
        R2Z2SequenceSavedAt  = DateTimeOffset.UtcNow;
    }
}
