using System.Reflection;

namespace EveConsole.Services;

/// <summary>
/// This build's version, in one place.
///
/// <para>⚠️ Read once into a static. It is a constant for the life of the process, and it is now
/// asked for on a timer — the worker stamps it into the database on every heartbeat — so
/// re-reflecting over the assembly each time would be work with a known answer.</para>
///
/// <para>Major.Minor.Build, dropping Revision. That matches what the csproj sets
/// (<c>&lt;Version&gt;0.9.13&lt;/Version&gt;</c>) and what the release tags say, so a version
/// compared against another client's, or shown next to an update prompt, is the number a person
/// would recognise rather than a four-part one they have never seen.</para>
/// </summary>
public static class AppVersion
{
    /// <summary>e.g. <c>0.9.13</c>, or <c>unknown</c> if the assembly carries no version.</summary>
    public static string Number { get; } = Read();

    /// <summary>e.g. <c>v0.9.13</c>. The same string, spelled the way releases are tagged.</summary>
    public static string Display { get; } = Number == Unknown ? Unknown : $"v{Number}";

    private const string Unknown = "unknown";

    private static string Read()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? Unknown : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
