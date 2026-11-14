using EveConsole.Localization;

namespace EveConsole.ViewModels;

/// <summary>The IRC server programs whose channel modes are known here. Each gives some letters its own meaning.</summary>
public enum IrcServerKind { Unknown, Ergo, Solanum, InspIrcd, Unreal, Hybrid, NgIrcd }

/// <summary>What a channel mode does, in words: a label, a longer tip, and for one with a value, what to type.</summary>
/// <param name="ServerOnly">Set by the network's operators or services, not a channel operator: shown when set, never changed here.</param>
public sealed record IrcModeMeaning(string Label, string Tip, string Hint = "", bool ServerOnly = false);

/// <summary>
/// Channel modes in words, by server program. A server names its program in 004 (e.g. "ergo-2.19.1"); the same
/// letter means different things on different programs (r, u, z, S, Q, L, f...), so only the letters every server
/// shares are named when the program is not known. A letter with no meaning here is not shown as a setting: it can
/// still be set from the window's command line by someone who knows it.
/// </summary>
public static class IrcModeMeanings
{
    public static IrcServerKind KindOf(string software)
    {
        var s = software.ToLowerInvariant();
        if (s.Contains("ergo") || s.Contains("oragono")) return IrcServerKind.Ergo;
        if (s.Contains("solanum") || s.Contains("charybdis") || s.Contains("ircd-seven")) return IrcServerKind.Solanum;
        if (s.Contains("inspircd")) return IrcServerKind.InspIrcd;
        if (s.Contains("unreal")) return IrcServerKind.Unreal;
        if (s.Contains("hybrid") || s.Contains("plexus")) return IrcServerKind.Hybrid;
        if (s.Contains("ngircd")) return IrcServerKind.NgIrcd;
        return IrcServerKind.Unknown;
    }

    private static IrcModeMeaning M(string label, string tip, string hint = "") => new(label, tip, hint);
    private static IrcModeMeaning ByNetwork(string label, string tip) => new(label, tip, ServerOnly: true);

    /// <summary>What <paramref name="letter"/> does on this kind of server, or null when not known.</summary>
    public static IrcModeMeaning? Of(char letter, IrcServerKind kind)
    {
        // The letters every server shares (RFC 1459 / 2811).
        switch (letter)
        {
            case 'i': return M(IrcText.ModeInviteOnly, IrcText.ModeInviteOnlyTip);
            case 'm': return M(IrcText.ModeModerated, IrcText.ModeModeratedTip);
            case 'n': return M(IrcText.ModeNoOutside, IrcText.ModeNoOutsideTip);
            case 't': return M(IrcText.ModeTopicLock, IrcText.ModeTopicLockTip);
            case 's': return M(IrcText.ModeSecret, IrcText.ModeSecretTip);
            case 'p': return M(IrcText.ModePrivate, IrcText.ModePrivateTip);
            case 'l': return M(IrcText.ModeLimit, IrcText.ModeLimitTip, IrcText.ModeLimitHint);
        }

        return (kind, letter) switch
        {
            // Ergo: its own HELP CMODES.
            (IrcServerKind.Ergo, 'C') => M(IrcText.ModeNoCtcp, IrcText.ModeNoCtcpTip),
            (IrcServerKind.Ergo, 'E') => M(IrcText.ModeRoleplay, IrcText.ModeRoleplayTip),
            (IrcServerKind.Ergo, 'M') => M(IrcText.ModeRegisteredSpeak, IrcText.ModeRegisteredSpeakTip),
            (IrcServerKind.Ergo, 'R') => M(IrcText.ModeRegisteredOnly, IrcText.ModeRegisteredOnlyTip),
            (IrcServerKind.Ergo, 'u') => M(IrcText.ModeAuditorium, IrcText.ModeAuditoriumTip),
            (IrcServerKind.Ergo, 'U') => M(IrcText.ModeOpModerated, IrcText.ModeOpModeratedTip),
            (IrcServerKind.Ergo, 'f') => M(IrcText.ModeForward, IrcText.ModeForwardTip, IrcText.ModeChannelHint),

            // Solanum, and charybdis and ircd-seven it came from.
            (IrcServerKind.Solanum, 'c') => M(IrcText.ModeStripColour, IrcText.ModeStripColourTip),
            (IrcServerKind.Solanum, 'C') => M(IrcText.ModeNoCtcp, IrcText.ModeNoCtcpTip),
            (IrcServerKind.Solanum, 'g') => M(IrcText.ModeFreeInvite, IrcText.ModeFreeInviteTip),
            (IrcServerKind.Solanum, 'z') => M(IrcText.ModeOpModerated, IrcText.ModeOpModeratedTip),
            (IrcServerKind.Solanum, 'r') => M(IrcText.ModeRegisteredOnly, IrcText.ModeRegisteredOnlyTip),
            (IrcServerKind.Solanum, 'S') => M(IrcText.ModeTlsOnly, IrcText.ModeTlsOnlyTip),
            (IrcServerKind.Solanum, 'T') => M(IrcText.ModeNoNotice, IrcText.ModeNoNoticeTip),
            (IrcServerKind.Solanum, 'F') => M(IrcText.ModeFreeTarget, IrcText.ModeFreeTargetTip),
            (IrcServerKind.Solanum, 'Q') => M(IrcText.ModeNoForward, IrcText.ModeNoForwardTip),
            (IrcServerKind.Solanum, 'L') => ByNetwork(IrcText.ModeLargeLists, IrcText.ModeLargeListsTip),
            (IrcServerKind.Solanum, 'P') => ByNetwork(IrcText.ModePermanent, IrcText.ModePermanentTip),
            (IrcServerKind.Solanum, 'f') => M(IrcText.ModeForward, IrcText.ModeForwardTip, IrcText.ModeChannelHint),
            (IrcServerKind.Solanum, 'j') => M(IrcText.ModeJoinThrottle, IrcText.ModeJoinThrottleTip, IrcText.ModeJoinThrottleHint),

            // InspIRCd, with its usual modules.
            (IrcServerKind.InspIrcd, 'A') => M(IrcText.ModeFreeInvite, IrcText.ModeFreeInviteTip),
            (IrcServerKind.InspIrcd, 'B') => M(IrcText.ModeBlockCaps, IrcText.ModeBlockCapsTip),
            (IrcServerKind.InspIrcd, 'c') => M(IrcText.ModeNoColour, IrcText.ModeNoColourTip),
            (IrcServerKind.InspIrcd, 'C') => M(IrcText.ModeNoCtcp, IrcText.ModeNoCtcpTip),
            (IrcServerKind.InspIrcd, 'D') => M(IrcText.ModeDelayJoin, IrcText.ModeDelayJoinTip),
            (IrcServerKind.InspIrcd, 'G') => M(IrcText.ModeCensor, IrcText.ModeCensorTip),
            (IrcServerKind.InspIrcd, 'K') => M(IrcText.ModeNoKnock, IrcText.ModeNoKnockTip),
            (IrcServerKind.InspIrcd, 'M') => M(IrcText.ModeRegisteredSpeak, IrcText.ModeRegisteredSpeakTip),
            (IrcServerKind.InspIrcd, 'N') => M(IrcText.ModeNoNickChange, IrcText.ModeNoNickChangeTip),
            (IrcServerKind.InspIrcd, 'O') => ByNetwork(IrcText.ModeOpersOnly, IrcText.ModeOpersOnlyTip),
            (IrcServerKind.InspIrcd, 'P') => ByNetwork(IrcText.ModePermanent, IrcText.ModePermanentTip),
            (IrcServerKind.InspIrcd, 'Q') => M(IrcText.ModeNoKick, IrcText.ModeNoKickTip),
            (IrcServerKind.InspIrcd, 'r') => ByNetwork(IrcText.ModeRegisteredChannel, IrcText.ModeRegisteredChannelTip),
            (IrcServerKind.InspIrcd, 'R') => M(IrcText.ModeRegisteredOnly, IrcText.ModeRegisteredOnlyTip),
            (IrcServerKind.InspIrcd, 'S') => M(IrcText.ModeStripColour, IrcText.ModeStripColourTip),
            (IrcServerKind.InspIrcd, 'T') => M(IrcText.ModeNoNotice, IrcText.ModeNoNoticeTip),
            (IrcServerKind.InspIrcd, 'u') => M(IrcText.ModeAuditorium, IrcText.ModeAuditoriumTip),
            (IrcServerKind.InspIrcd, 'z') => M(IrcText.ModeTlsOnly, IrcText.ModeTlsOnlyTip),
            (IrcServerKind.InspIrcd, 'd') => M(IrcText.ModeMessageDelay, IrcText.ModeMessageDelayTip, IrcText.ModeSecondsHint),
            (IrcServerKind.InspIrcd, 'f') => M(IrcText.ModeFlood, IrcText.ModeFloodTip, IrcText.ModeFloodHint),
            (IrcServerKind.InspIrcd, 'F') => M(IrcText.ModeNickFlood, IrcText.ModeNickFloodTip, IrcText.ModeNickFloodHint),
            (IrcServerKind.InspIrcd, 'H') => M(IrcText.ModeHistory, IrcText.ModeHistoryTip, IrcText.ModeHistoryHint),
            (IrcServerKind.InspIrcd, 'j') => M(IrcText.ModeJoinThrottle, IrcText.ModeJoinThrottleTip, IrcText.ModeJoinThrottleHint),
            (IrcServerKind.InspIrcd, 'J') => M(IrcText.ModeRejoinDelay, IrcText.ModeRejoinDelayTip, IrcText.ModeSecondsHint),
            (IrcServerKind.InspIrcd, 'L') => M(IrcText.ModeOverflow, IrcText.ModeOverflowTip, IrcText.ModeChannelHint),

            // UnrealIRCd 6.
            (IrcServerKind.Unreal, 'c') => M(IrcText.ModeNoColour, IrcText.ModeNoColourTip),
            (IrcServerKind.Unreal, 'C') => M(IrcText.ModeNoCtcp, IrcText.ModeNoCtcpTip),
            (IrcServerKind.Unreal, 'D') => M(IrcText.ModeDelayJoin, IrcText.ModeDelayJoinTip),
            (IrcServerKind.Unreal, 'G') => M(IrcText.ModeCensor, IrcText.ModeCensorTip),
            (IrcServerKind.Unreal, 'K') => M(IrcText.ModeNoKnock, IrcText.ModeNoKnockTip),
            (IrcServerKind.Unreal, 'M') => M(IrcText.ModeRegisteredSpeak, IrcText.ModeRegisteredSpeakTip),
            (IrcServerKind.Unreal, 'N') => M(IrcText.ModeNoNickChange, IrcText.ModeNoNickChangeTip),
            (IrcServerKind.Unreal, 'O') => ByNetwork(IrcText.ModeOpersOnly, IrcText.ModeOpersOnlyTip),
            (IrcServerKind.Unreal, 'P') => ByNetwork(IrcText.ModePermanent, IrcText.ModePermanentTip),
            (IrcServerKind.Unreal, 'Q') => M(IrcText.ModeNoKick, IrcText.ModeNoKickTip),
            (IrcServerKind.Unreal, 'r') => ByNetwork(IrcText.ModeRegisteredChannel, IrcText.ModeRegisteredChannelTip),
            (IrcServerKind.Unreal, 'R') => M(IrcText.ModeRegisteredOnly, IrcText.ModeRegisteredOnlyTip),
            (IrcServerKind.Unreal, 'S') => M(IrcText.ModeStripColour, IrcText.ModeStripColourTip),
            (IrcServerKind.Unreal, 'T') => M(IrcText.ModeNoNotice, IrcText.ModeNoNoticeTip),
            (IrcServerKind.Unreal, 'V') => M(IrcText.ModeNoInvite, IrcText.ModeNoInviteTip),
            (IrcServerKind.Unreal, 'z') => M(IrcText.ModeTlsOnly, IrcText.ModeTlsOnlyTip),
            (IrcServerKind.Unreal, 'Z') => ByNetwork(IrcText.ModeAllTls, IrcText.ModeAllTlsTip),
            (IrcServerKind.Unreal, 'f') => M(IrcText.ModeFloodRules, IrcText.ModeFloodRulesTip, IrcText.ModeFloodRulesHint),
            (IrcServerKind.Unreal, 'F') => M(IrcText.ModeFloodProfile, IrcText.ModeFloodProfileTip, IrcText.ModeFloodProfileHint),
            (IrcServerKind.Unreal, 'H') => M(IrcText.ModeHistory, IrcText.ModeHistoryTip, IrcText.ModeHistoryHint),
            (IrcServerKind.Unreal, 'L') => M(IrcText.ModeOverflow, IrcText.ModeOverflowTip, IrcText.ModeChannelHint),

            // ircd-hybrid and those built on it.
            (IrcServerKind.Hybrid, 'c') => M(IrcText.ModeNoColour, IrcText.ModeNoColourTip),
            (IrcServerKind.Hybrid, 'C') => M(IrcText.ModeNoCtcp, IrcText.ModeNoCtcpTip),
            (IrcServerKind.Hybrid, 'M') => M(IrcText.ModeRegisteredSpeak, IrcText.ModeRegisteredSpeakTip),
            (IrcServerKind.Hybrid, 'N') => M(IrcText.ModeNoNickChange, IrcText.ModeNoNickChangeTip),
            (IrcServerKind.Hybrid, 'O') => ByNetwork(IrcText.ModeOpersOnly, IrcText.ModeOpersOnlyTip),
            (IrcServerKind.Hybrid, 'r') => ByNetwork(IrcText.ModeRegisteredChannel, IrcText.ModeRegisteredChannelTip),
            (IrcServerKind.Hybrid, 'R') => M(IrcText.ModeRegisteredOnly, IrcText.ModeRegisteredOnlyTip),
            (IrcServerKind.Hybrid, 'S') => M(IrcText.ModeTlsOnly, IrcText.ModeTlsOnlyTip),
            (IrcServerKind.Hybrid, 'T') => M(IrcText.ModeNoNotice, IrcText.ModeNoNoticeTip),

            // ngIRCd.
            (IrcServerKind.NgIrcd, 'M') => M(IrcText.ModeRegisteredSpeak, IrcText.ModeRegisteredSpeakTip),
            (IrcServerKind.NgIrcd, 'N') => M(IrcText.ModeNoNickChange, IrcText.ModeNoNickChangeTip),
            (IrcServerKind.NgIrcd, 'O') => ByNetwork(IrcText.ModeOpersOnly, IrcText.ModeOpersOnlyTip),
            (IrcServerKind.NgIrcd, 'P') => ByNetwork(IrcText.ModePermanent, IrcText.ModePermanentTip),
            (IrcServerKind.NgIrcd, 'Q') => M(IrcText.ModeNoKick, IrcText.ModeNoKickTip),
            (IrcServerKind.NgIrcd, 'r') => ByNetwork(IrcText.ModeRegisteredChannel, IrcText.ModeRegisteredChannelTip),
            (IrcServerKind.NgIrcd, 'R') => M(IrcText.ModeRegisteredOnly, IrcText.ModeRegisteredOnlyTip),
            (IrcServerKind.NgIrcd, 'V') => M(IrcText.ModeNoInvite, IrcText.ModeNoInviteTip),
            (IrcServerKind.NgIrcd, 'z') => M(IrcText.ModeTlsOnly, IrcText.ModeTlsOnlyTip),

            _ => null,
        };
    }
}
