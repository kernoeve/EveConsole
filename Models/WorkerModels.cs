namespace EveConsole.Models;

/// <summary>
/// Who is doing the background work, as that process reports it. Single row, always Id = 1.
///
/// <para>⚠️ Descriptive, never authoritative. The PostgreSQL advisory lock decides who holds the
/// lease; this row only says who <em>claimed</em> to. Nothing cleans it up — a worker killed with
/// the power switch leaves its row behind exactly as it was — so a reader must never take the
/// presence of a row as "a worker is running". Ownership comes from the lock,
/// liveness from the age of <see cref="HeartbeatUtc"/>, and the two are separate questions.</para>
///
/// <para>The reason it exists at all is version skew. Several clients can now be pointed at one
/// database, and the one holding the lease is writing to it with <em>its</em> build's idea of the
/// schema. A client on a different build needs to know that before it starts — and, more sharply,
/// before it migrates anything.</para>
/// </summary>
public class BackgroundWorkerStatus
{
    public int    Id       { get; set; } = 1;

    /// <summary>Major.Minor.Build of the process holding the lease, e.g. <c>0.9.13</c>.</summary>
    public string Version  { get; set; } = "";

    /// <summary>Machine name, so "which box is polling?" is answerable without guessing.</summary>
    public string HostName { get; set; } = "";

    /// <summary>PID on that host. Only useful alongside HostName, which is why both are here.</summary>
    public int    ProcessId { get; set; }

    /// <summary>True when the holder is running with --headless and has no window.</summary>
    public bool   Headless { get; set; }

    /// <summary>When this holder took the lease. Distinct from the heartbeat: together they say
    /// "up for three days" rather than only "alive ten seconds ago".</summary>
    public DateTimeOffset LeaseTakenUtc { get; set; }

    /// <summary>⚠️ Stamped by the same statement that proves the lock connection is still alive.
    /// A stale value means the holder is gone or unreachable, not merely idle.</summary>
    public DateTimeOffset HeartbeatUtc  { get; set; }
}
