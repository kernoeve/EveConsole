using Velopack;
using Velopack.Sources;
using EveConsole.Localization;

namespace EveConsole.Services;

/// <summary>
/// Where releases come from, for both ways the app updates itself: the check in Settings and at
/// startup, and a client that finds its database ahead of it and would otherwise stop with no way
/// forward (see <see cref="Views.UpdateRequiredDialog"/>).
/// </summary>
public static class AppUpdater
{
    public const string RepoUrl     = "https://github.com/kernoeve/EveConsole";
    public const string ReleasesUrl = RepoUrl + "/releases/latest";

    public static UpdateManager CreateManager() => new(new GithubSource(RepoUrl, null, false));

    /// <summary>A release's version as Major.Minor.Patch, comparable with
    /// <see cref="AppVersion"/> and with the version a database was last opened by.</summary>
    public static Version VersionOf(UpdateInfo info)
    {
        var v = info.TargetFullRelease.Version;
        return new Version(v.Major, v.Minor, v.Patch);
    }

    /// <summary>The arguments this process was started with, so a restart after an update comes
    /// back the way it went down — in the same <c>--profile</c> above all, which a restart without
    /// them would quietly leave for the default one.</summary>
    public static string[] RestartArgs() => Environment.GetCommandLineArgs().Skip(1).ToArray();

    // ── A database ahead of this build ──────────────────────────────────────

    /// <summary>What a client can do about a database a newer build has already changed.</summary>
    public enum AheadRemedy
    {
        /// <summary>Not an installed build (a development run, an unpacked archive): nothing to update.</summary>
        NotInstalled,
        /// <summary>The release feed could not be read.</summary>
        CheckFailed,
        /// <summary>Nothing newer than this build is released: the database was opened by an
        /// unreleased build, or the release is still being published.</summary>
        NoRelease,
        /// <summary>A newer release exists, but it is still older than the database.</summary>
        ReleaseTooOld,
        /// <summary>A release at or past the database's version: updating fixes it.</summary>
        UpdateAvailable,
    }

    public static AheadRemedy RemedyFor(bool installed, bool checkFailed, Version? latest, Version database) =>
        !installed        ? AheadRemedy.NotInstalled
        : checkFailed     ? AheadRemedy.CheckFailed
        : latest is null  ? AheadRemedy.NoRelease
        : latest < database ? AheadRemedy.ReleaseTooOld
        : AheadRemedy.UpdateAvailable;

    /// <summary>The remedy in a sentence — the dialog's status line, and the log line a headless
    /// client leaves behind, so the two can never tell a person different things. The log names
    /// the releases page by its address; the dialog has a link to it beside the text.
    ///
    /// <para>⚠️ So each sentence is written twice: the dialog's is read from the resources, in the
    /// interface's language, and the log's stays English here, as every log line does. A change to
    /// one is a change to the other.</para></summary>
    public static string Describe(AheadRemedy remedy, Version database, Version? latest, string? error = null,
                                  bool forLog = false)
    {
        if (!forLog)
            return remedy switch
            {
            AheadRemedy.NotInstalled =>
                string.Format(ShellText.UpdateAheadNotInstalled, database),
            AheadRemedy.CheckFailed when string.IsNullOrWhiteSpace(error) =>
                string.Format(ShellText.UpdateAheadCheckFailedNoReason, database),
            AheadRemedy.CheckFailed =>
                string.Format(ShellText.UpdateAheadCheckFailed, WithoutFullStop(error!), database),
            AheadRemedy.NoRelease =>
                string.Format(ShellText.UpdateAheadNoRelease, database),
            AheadRemedy.ReleaseTooOld =>
                string.Format(ShellText.UpdateAheadReleaseTooOld, latest, database),
            _ =>
                string.Format(ShellText.UpdateAheadAvailable, latest),
            };

        var where = ReleasesUrl;
        return remedy switch
        {
        AheadRemedy.NotInstalled =>
            $"This copy was not set up by the installer, so it cannot update itself. Install {database} or later from {where}.",
        AheadRemedy.CheckFailed =>
            $"Could not check for an update: {Sentence(error)} Get {database} or later from {where}.",
        AheadRemedy.NoRelease =>
            $"No newer release is out yet. {database} has not been released: the database was opened by a development build, or the release is still being published.",
        AheadRemedy.ReleaseTooOld =>
            $"The newest release, {latest}, is still older than this database ({database}), which was opened by a build that has not been released.",
        _ =>
            $"Version {latest} is available. Updating installs it and restarts EVE Console on this database.",
        };

        static string Sentence(string? text)
        {
            var t = string.IsNullOrWhiteSpace(text) ? "no reason given" : text.Trim();
            return t.EndsWith('.') ? t : t + ".";
        }

        // The error as the dialog's sentence takes it: the sentence supplies the full stop, so a
        // translation can use its own.
        static string WithoutFullStop(string text)
        {
            var t = text.Trim();
            return t.EndsWith('.') ? t[..^1] : t;
        }
    }

    /// <summary>
    /// The remedy for a client with nowhere to show a dialog — a headless one — as a line for its
    /// log. The check is bounded, since the process is on its way out either way.
    /// </summary>
    public static async Task<string> DescribeForLogAsync(Version database, AppErrorLogger log)
    {
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled) return Describe(AheadRemedy.NotInstalled, database, null, forLog: true);
            var check = mgr.CheckForUpdatesAsync();
            if (await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(30))) != check)
                return Describe(AheadRemedy.CheckFailed, database, null, "no answer in 30 seconds", forLog: true);
            var found  = await check;
            var latest = found is null ? null : VersionOf(found);
            return Describe(RemedyFor(true, false, latest, database), database, latest, forLog: true);
        }
        catch (Exception ex)
        {
            log.Log("Startup", "update check for a database ahead of this build", ex);
            return Describe(AheadRemedy.CheckFailed, database, null, ex.Message, forLog: true);
        }
    }
}
