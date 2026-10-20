using System.Reflection;

namespace EveConsole.Services;

/// <summary>
/// What kind of binary this is.
///
/// <para>⚠️ "Not installed" is not "development build". Velopack's IsInstalled is false for a
/// build run from source, and equally for the released Portable zip, the Linux tarball and the
/// Docker image built from it. Asking IsInstalled to mean "dev build" labelled those releases
/// "dev build" in the title bar, and anything that switched dev-only behaviour on from it would
/// have switched it on for every user of them.</para>
///
/// <para>So the release workflow says so instead: release-build.yml publishes with
/// <c>-p:OfficialRelease=true</c>, which stamps an assembly attribute (EveConsole.csproj). A
/// binary without it — a local build, a PR or develop artifact — is a development build.</para>
/// </summary>
public static class BuildInfo
{
    public static bool IsOfficialRelease { get; } = typeof(BuildInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "OfficialRelease" && a.Value == "true");

    public static bool IsDevelopmentBuild => !IsOfficialRelease;
}
