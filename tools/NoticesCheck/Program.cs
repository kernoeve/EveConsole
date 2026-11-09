using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EveConsole.Tools.NoticesCheck;

/// <summary>
/// Builds THIRD-PARTY-NOTICES.txt and the licenses folder from what each release ships, or (with no
/// arguments) checks that the committed copies still match. `--write` writes them.
/// </summary>
/// <remarks>
/// ⚠️ What ships differs by platform (LibVLC for Windows or macOS, ANGLE only on Windows, each platform's
/// own natives), so the app's project is restored once per release platform, self-contained as the
/// releases are, each into a scratch folder of its own: the repository's obj folder is never touched.
/// A package counts as shipped on a platform when its restore gives it anything that reaches the output.
/// </remarks>
static class Program
{
    private static readonly (string Rid, string Name)[] Platforms =
        [("win-x64", "Windows"), ("linux-x64", "Linux"), ("osx-arm64", "macOS"), ("osx-x64", "macOS")];

    private static readonly string[] AllPlatformNames = [.. Platforms.Select(p => p.Name).Distinct()];

    private const string NoticesFile = "THIRD-PARTY-NOTICES.txt";
    private const string LicensesDir = "licenses";

    private sealed class Package
    {
        public required string Id;
        public required string Version;
        public required string Dir;
        public readonly SortedSet<string> PlatformNames = new(StringComparer.Ordinal);
    }

    private static int Main(string[] args)
    {
        var write = args.Contains("--write");
        var repo = FindRepo();
        if (repo is null) { Console.Error.WriteLine("Run from inside the repository: EveConsole.csproj not found."); return 2; }

        var scratch = Path.Combine(Path.GetTempPath(), "noticescheck-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var packages = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
            var runtimes = new List<(string Rid, string Version, string Dir)>();

            foreach (var (rid, name) in Platforms)
            {
                var obj = Path.Combine(scratch, rid) + Path.DirectorySeparatorChar;
                if (!Restore(repo, rid, obj)) return 2;
                ReadAssets(Path.Combine(obj, "project.assets.json"), rid, name, packages, runtimes);
            }

            var output = Build(repo, packages, runtimes, out var problems);
            if (problems.Count > 0)
            {
                foreach (var p in problems) Console.Error.WriteLine("FAIL " + p);
                return 1;
            }

            return write ? Write(repo, output) : Compare(repo, output);
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? FindRepo()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "EveConsole.csproj"))) return dir.FullName;
        return null;
    }

    private static bool Restore(string repo, string rid, string obj)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "restore", "EveConsole.csproj", $"-p:RuntimeIdentifier={rid}", "-p:SelfContained=true",
                                  $"-p:MSBuildProjectExtensionsPath={obj}", "-v", "q" })
            start.ArgumentList.Add(a);

        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode == 0) return true;

        Console.Error.WriteLine($"FAIL restore for {rid}:\n{stdout.Result}{stderr.Result}");
        return false;
    }

    private static void ReadAssets(string path, string rid, string platform, Dictionary<string, Package> packages,
                                   List<(string, string, string)> runtimes)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var folder = root.GetProperty("packageFolders").EnumerateObject().First().Name;
        var libraries = root.GetProperty("libraries");

        foreach (var target in root.GetProperty("targets").EnumerateObject())
        {
            if (!target.Name.EndsWith("/" + rid, StringComparison.Ordinal)) continue;

            foreach (var lib in target.Value.EnumerateObject())
            {
                if (lib.Value.GetProperty("type").GetString() != "package") continue;
                var (id, version) = Split(lib.Name);
                if (Known.IsNotShipped(id) || !ReachesOutput(lib.Value)) continue;

                if (!packages.TryGetValue(id, out var pkg))
                {
                    var rel = libraries.GetProperty(lib.Name).GetProperty("path").GetString()!;
                    packages[id] = pkg = new Package { Id = id, Version = version, Dir = Path.Combine(folder, rel) };
                }
                pkg.PlatformNames.Add(platform);
            }
        }

        // The runtime itself, which a self-contained release carries in full.
        var frameworks = root.GetProperty("project").GetProperty("frameworks");
        foreach (var fw in frameworks.EnumerateObject())
            if (fw.Value.TryGetProperty("downloadDependencies", out var deps))
                foreach (var d in deps.EnumerateArray())
                {
                    var name = d.GetProperty("name").GetString()!;
                    if (!name.StartsWith("Microsoft.NETCore.App.Runtime.", StringComparison.OrdinalIgnoreCase)) continue;
                    var version = d.GetProperty("version").GetString()!.Trim('[', ']').Split(',')[0].Trim();
                    runtimes.Add((rid, version, Path.Combine(folder, name.ToLowerInvariant(), version.ToLowerInvariant())));
                }
    }

    private static (string Id, string Version) Split(string name)
    {
        var slash = name.IndexOf('/');
        return (name[..slash], name[(slash + 1)..]);
    }

    /// <summary>
    /// Whether a package's restore gives it anything copied into the app: assemblies, natives, content, or
    /// build targets (LibVLC and the Whisper runtimes arrive through their own targets). "_._" is NuGet's
    /// placeholder for "nothing for this platform".
    /// </summary>
    private static bool ReachesOutput(JsonElement lib)
    {
        foreach (var kind in new[] { "runtime", "native", "runtimeTargets", "resource", "contentFiles", "build" })
            if (lib.TryGetProperty(kind, out var files))
                foreach (var f in files.EnumerateObject())
                    if (!f.Name.EndsWith("_._", StringComparison.Ordinal)) return true;
        return false;
    }

    // ═══ Building the files ═══════════════════════════════════════════════════════════════════

    private sealed class Texts(string repo)
    {
        private readonly string _dir = Path.Combine(repo, "tools", "NoticesCheck", "texts");
        private readonly string _repo = repo;
        public readonly List<(string Key, string Text)> Used = [];

        /// <summary>The key a text is printed under. An identical text already printed is shared rather than repeated.</summary>
        public string? Add(string key, string? text)
        {
            if (text is null) return null;
            text = Normalise(text);
            var squeezed = Squeeze(text);
            foreach (var (k, t) in Used)
                if (Squeeze(t) == squeezed) return k;
            Used.Add((key, text));
            return key;
        }

        public string? Named(string key, Func<string, string?> packageText)
        {
            if (key.StartsWith("pkg:", StringComparison.Ordinal))
                return Add(key[4..], packageText(key[4..]));
            var path = key == "GPL-3.0-or-later" ? Path.Combine(_repo, "LICENSE") : Path.Combine(_dir, key + ".txt");
            return File.Exists(path) ? Add(key, File.ReadAllText(path)) : null;
        }

        public bool Exists(string key) => key == "GPL-3.0-or-later" || File.Exists(Path.Combine(_dir, key + ".txt"));

        private static string Squeeze(string s) => Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', '﻿') + "\n";

    private sealed record Meta(string License, string[] TextKeys, string Copyright, string Source, string? Note, string[] Notices);

    private static Dictionary<string, string> Build(string repo, Dictionary<string, Package> packages,
                                                    List<(string Rid, string Version, string Dir)> runtimes, out List<string> problems)
    {
        problems = [];
        var texts = new Texts(repo);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // licenses/ name -> text
        var noticeNames = new Dictionary<string, string>();                              // squeezed text -> file name

        string NoticeFile(string owner, string path)
        {
            var text = Normalise(File.ReadAllText(path));
            var key = Regex.Replace(text, @"\s+", " ");
            if (noticeNames.TryGetValue(key, out var existing)) return existing;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var name = $"{LicensesDir}/{owner}-{Path.GetFileNameWithoutExtension(path)}{(ext.Length == 0 ? ".txt" : ext)}";
            noticeNames[key] = name;
            files[name] = text;
            return name;
        }

        string? LicenseFileText(string id) =>
            packages.TryGetValue(id, out var p) && LicenseFile(p) is { } f ? File.ReadAllText(f) : null;

        // ── The .NET runtime ──
        string? runtimeText = null;
        var runtimeNotices = new SortedSet<string>(StringComparer.Ordinal);
        var runtimeVersions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (rid, version, dir) in runtimes)
        {
            runtimeVersions.Add(version);
            var license = Path.Combine(dir, "LICENSE.TXT");
            var noticesPath = Path.Combine(dir, "THIRD-PARTY-NOTICES.TXT");
            if (!File.Exists(license) || !File.Exists(noticesPath)) { problems.Add($".NET runtime pack for {rid} has no LICENSE.TXT or notices at {dir}"); continue; }
            runtimeText ??= texts.Add(".NET runtime", File.ReadAllText(license));
            runtimeNotices.Add(NoticeFile("dotnet-runtime", noticesPath));
        }
        if (runtimes.Count == 0) problems.Add("no .NET runtime pack in the restore: the releases are self-contained, so one is expected");

        // ── Notice files: named after the shortest package id that carries each, read before anything uses them ──
        var notices = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in packages.Values.OrderBy(p => p.Id.Length).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            notices[pkg.Id] = Known.NoticeSkip.Contains(pkg.Id) ? [] : [.. NoticePaths(pkg.Dir).Select(f => NoticeFile(pkg.Id, f))];

        // ── Packages ──
        var metas = new List<(Package Pkg, Meta Meta)>();
        foreach (var pkg in packages.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var meta = ReadMeta(pkg, texts, notices[pkg.Id], LicenseFileText, problems);
            if (meta is not null) metas.Add((pkg, meta));
        }

        // ── Components that are not packages ──
        var components = new List<(Component C, string[] Keys)>();
        foreach (var c in Known.Components)
        {
            if (c.Requires is not null && !packages.ContainsKey(c.Requires))
            {
                problems.Add($"\"{c.Name}\" is listed as inside {c.Requires}, which no longer ships: update Components.cs");
                continue;
            }
            var keys = new List<string>();
            foreach (var t in c.Texts)
            {
                var k = texts.Named(t, LicenseFileText);
                if (k is null) problems.Add($"\"{c.Name}\": no license text \"{t}\"");
                else keys.Add(k);
            }
            components.Add((c, [.. keys]));
        }

        foreach (var id in Known.Overrides.Keys)
            if (!packages.ContainsKey(id))
                problems.Add($"Components.cs has an override for {id}, which no longer ships: remove it");

        // ── The notices file ──
        var sb = new StringBuilder();
        sb.Append("EVE Console: third-party notices\n");
        sb.Append("================================\n\n");
        Para(sb, "EVE Console is free software under the GNU General Public License, version 3 (the LICENSE file). " +
                 "Its releases include the software below, each under its own license. Every entry gives that license, " +
                 "who holds the copyright, and where the source is; the license texts follow the list. The larger notice " +
                 "files some packages carry are in the licenses folder, named in their entry.");
        Para(sb, "An entry is in every release (Windows, Linux and macOS) unless it says otherwise.");
        Para(sb, "This file is generated by tools/NoticesCheck from what each release restores. Do not edit it by hand: " +
                 "run `dotnet run --project tools/NoticesCheck -- --write`.");

        Heading(sb, "Packages");
        var groups = metas
            .GroupBy(m => (m.Meta.License, Keys: string.Join("|", m.Meta.TextKeys), m.Meta.Copyright, m.Meta.Source, m.Meta.Note,
                           Notices: string.Join("|", m.Meta.Notices), Where: PlatformLabel(m.Pkg.PlatformNames)))
            .OrderBy(g => g.First().Pkg.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var items = g.Select(m => m.Pkg).ToList();
            var versions = items.Select(p => p.Version).Distinct().ToList();
            var title = versions.Count == 1
                ? string.Join(", ", items.Select(p => p.Id)) + " " + versions[0]
                : string.Join(", ", items.Select(p => p.Id + " " + p.Version));
            var m = g.First().Meta;
            Entry(sb, title, m.License, m.TextKeys, m.Copyright, m.Source, g.Key.Where, m.Note, m.Notices, where: null);
        }

        Heading(sb, "The .NET runtime");
        Entry(sb, ".NET runtime " + string.Join(", ", runtimeVersions), "MIT", runtimeText is null ? [] : [runtimeText],
              "Copyright (c) .NET Foundation and Contributors", "https://github.com/dotnet/runtime", null,
              "Every release is self-contained, so it carries the runtime.", [.. runtimeNotices], where: null);

        Heading(sb, "Inside the packages, or added by the build");
        foreach (var (c, keys) in components)
            Entry(sb, c.Name, c.License, keys, c.Copyright, c.Source, c.Platforms is null ? null : c.Platforms + " only", c.Note, [], c.Where);

        Heading(sb, "Downloaded when first used");
        Para(sb, "Not part of any release. Each is fetched the first time the feature that needs it is turned on, and is used under its own license:");
        foreach (var d in Known.DownloadedLater) Bullet(sb, d);
        sb.Append('\n');

        Heading(sb, "Used from the system");
        Para(sb, "Not part of any release:");
        foreach (var d in Known.FromTheSystem) Bullet(sb, d);
        sb.Append('\n');

        Heading(sb, "Trademarks");
        foreach (var t in Known.Trademarks) Para(sb, t);

        Heading(sb, "License texts");
        foreach (var (key, text) in texts.Used)
        {
            sb.Append($"──── [{key}] ").Append(new string('─', Math.Max(4, 96 - key.Length))).Append("\n\n");
            sb.Append(text).Append('\n');
        }

        files[NoticesFile] = sb.ToString();
        return files;
    }

    private static string? PlatformLabel(SortedSet<string> names) =>
        names.Count == AllPlatformNames.Length ? null : string.Join(" and ", names) + " only";

    private static Meta? ReadMeta(Package pkg, Texts texts, string[] notices,
                                  Func<string, string?> licenseFileText, List<string> problems)
    {
        var nuspecPath = Directory.EnumerateFiles(pkg.Dir, "*.nuspec").FirstOrDefault();
        if (nuspecPath is null) { problems.Add($"{pkg.Id} {pkg.Version}: no .nuspec in {pkg.Dir}"); return null; }

        var md = XDocument.Load(nuspecPath).Root!.Elements().First(e => e.Name.LocalName == "metadata");
        string? Get(string name) => md.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var licenseElement = md.Elements().FirstOrDefault(e => e.Name.LocalName == "license");

        Known.Overrides.TryGetValue(pkg.Id, out var over);
        string? label = over?.License;
        var keys = new List<string>();

        if (over?.Texts is { } named)
        {
            foreach (var t in named)
            {
                var k = texts.Named(t, licenseFileText);
                if (k is null) problems.Add($"{pkg.Id}: no license text \"{t}\"");
                else keys.Add(k);
            }
        }
        else if (licenseElement?.Attribute("type")?.Value == "expression")
        {
            label ??= licenseElement.Value.Trim();
            foreach (var id in Regex.Split(licenseElement.Value, @"\s+(?:OR|AND|WITH)\s+|[()]").Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                if (!texts.Exists(id)) { problems.Add($"{pkg.Id}: license {id} has no text in tools/NoticesCheck/texts"); continue; }
                keys.Add(texts.Named(id, licenseFileText)!);
            }
        }
        else if (LicenseFile(pkg) is { } file)
        {
            var text = File.ReadAllText(file);
            label ??= Recognise(text);
            keys.Add(texts.Add(pkg.Id, text)!);
        }
        else
        {
            problems.Add($"{pkg.Id} {pkg.Version}: its package names no license; add an override in Components.cs");
            return null;
        }

        var copyright = over?.Copyright ?? Get("copyright") ?? "";
        var authors = Get("authors");
        if (authors is not null && !copyright.Contains(authors, StringComparison.OrdinalIgnoreCase) && authors != pkg.Id)
            copyright = copyright.Length == 0 ? "Authors: " + authors : copyright + " (authors: " + authors + ")";

        var source = over?.Source ?? Get("projectUrl") ?? md.Elements().FirstOrDefault(e => e.Name.LocalName == "repository")?.Attribute("url")?.Value ?? "";
        source = Regex.Replace(source, @"\?utm_[^ ]*$", "");

        return new Meta(label ?? "see its license text", [.. keys], copyright, source, over?.Note, notices);
    }

    /// <summary>The larger notice files a package carries beside its license, at its root or in a notices folder.</summary>
    private static IEnumerable<string> NoticePaths(string packageDir) =>
        new[] { packageDir, Path.Combine(packageDir, "notices") }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d).Order(StringComparer.Ordinal))
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^(third[-_ ]?party[-_ ]?notices?|notice)(\.\w+)?$", RegexOptions.IgnoreCase));

    private static string? LicenseFile(Package pkg)
    {
        var nuspec = Directory.EnumerateFiles(pkg.Dir, "*.nuspec").FirstOrDefault();
        if (nuspec is null) return null;
        var md = XDocument.Load(nuspec).Root!.Elements().First(e => e.Name.LocalName == "metadata");
        var el = md.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
        if (el?.Attribute("type")?.Value != "file") return null;
        var path = Path.Combine(pkg.Dir, el.Value.Trim().Replace('\\', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    /// <summary>The license a package's own license file is, by its wording, for the entry's one-line label.</summary>
    private static string Recognise(string text)
    {
        if (text.Contains("Apache License") && text.Contains("Version 2.0")) return "Apache-2.0";
        if (text.Contains("GNU LESSER GENERAL PUBLIC LICENSE")) return "LGPL";
        if (text.Contains("Permission is hereby granted, free of charge")) return "MIT";
        if (text.Contains("Redistribution and use in source and binary forms"))
            return text.Contains("Neither the name") ? "BSD-3-Clause" : "BSD-2-Clause";
        return "its own license";
    }

    // ── Layout ──

    private const int Width = 100;

    private static void Heading(StringBuilder sb, string title) =>
        sb.Append('\n').Append(title.ToUpperInvariant()).Append('\n').Append(new string('=', title.Length)).Append("\n\n");

    private static void Para(StringBuilder sb, string text)
    {
        Wrap(sb, text, "", "");
        sb.Append('\n');
    }

    private static void Bullet(StringBuilder sb, string text) => Wrap(sb, text, "  - ", "    ");

    private static void Entry(StringBuilder sb, string title, string license, string[] keys, string copyright, string source,
                              string? platforms, string? note, string[] notices, string? where)
    {
        Wrap(sb, title, "* ", "  ");
        if (where is not null) Wrap(sb, "In " + where + ".", "    ", "    ");
        var textRef = keys.Length == 0 ? "" : ", text " + string.Join(" and ", keys.Select(k => $"[{k}]")) + " below";
        Wrap(sb, "License: " + license + textRef, "    ", "      ");
        if (copyright.Length > 0) Wrap(sb, copyright, "    ", "      ");
        if (source.Length > 0) Wrap(sb, "Source: " + source, "    ", "      ");
        if (platforms is not null) Wrap(sb, platforms + ".", "    ", "      ");
        if (note is not null) Wrap(sb, note, "    ", "      ");
        foreach (var n in notices) Wrap(sb, "More notices: " + n, "    ", "      ");
        sb.Append('\n');
    }

    private static void Wrap(StringBuilder sb, string text, string first, string rest)
    {
        var line = new StringBuilder(first);
        var empty = true;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!empty && line.Length + 1 + word.Length > Width)
            {
                sb.Append(line).Append('\n');
                line.Clear().Append(rest);
                empty = true;
            }
            if (!empty) line.Append(' ');
            line.Append(word);
            empty = false;
        }
        sb.Append(line).Append('\n');
    }

    // ═══ Writing and comparing ═══════════════════════════════════════════════════════════════

    private static int Write(string repo, Dictionary<string, string> output)
    {
        var dir = Path.Combine(repo, LicensesDir);
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir))
                if (!output.ContainsKey($"{LicensesDir}/{Path.GetFileName(f)}")) File.Delete(f);
        Directory.CreateDirectory(dir);

        foreach (var (name, text) in output)
            File.WriteAllText(Path.Combine(repo, name), text.Replace("\n", Environment.NewLine), new UTF8Encoding(false));

        Console.WriteLine($"Wrote {NoticesFile} and {output.Count - 1} file(s) in {LicensesDir}/.");
        return 0;
    }

    private static int Compare(string repo, Dictionary<string, string> output)
    {
        var stale = new List<string>();
        foreach (var (name, text) in output)
        {
            var path = Path.Combine(repo, name);
            if (!File.Exists(path)) stale.Add(name + " is missing");
            else if (Normalise(File.ReadAllText(path)) != Normalise(text)) stale.Add(name + " differs from what the packages now say");
        }

        var dir = Path.Combine(repo, LicensesDir);
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir))
                if (!output.ContainsKey($"{LicensesDir}/{Path.GetFileName(f)}")) stale.Add($"{LicensesDir}/{Path.GetFileName(f)} is no longer needed");

        if (stale.Count == 0)
        {
            Console.WriteLine($"OK: {NoticesFile} and {LicensesDir}/ match what the releases ship.");
            return 0;
        }

        foreach (var s in stale) Console.Error.WriteLine("FAIL " + s);
        Console.Error.WriteLine("The notices are out of date: run `dotnet run --project tools/NoticesCheck -- --write` and commit the result.");
        return 1;
    }
}
