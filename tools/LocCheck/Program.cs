using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Are the translations safe to ship?
//
// The interface text lives in Localization/*Text.resx (English, which the build turns into
// classes) and Localization/*Text.<language>.resx (the translations). Everything a translation
// can get wrong that the build cannot see fails here:
//
//   • a placeholder lost or added — "{0} jobs" as "工作" throws FormatException the moment the
//     label is shown, in that language only, so English testing never meets it;
//   • braces that do not parse as a format string;
//   • an entry the English does not have — a typo in a key, or one renamed since, which the
//     app would simply never read;
//   • an empty entry, which shows as a blank label instead of falling back to English;
//   • a file named for a culture .NET does not know, which the build quietly embeds as neither
//     a translation nor anything else.
//
// And it reports, without failing: how much of each language is translated, and how much
// interface text is still written straight into the XAML rather than moved into the resources.
// ═══════════════════════════════════════════════════════════════════════════════════════════

string? root = null;
for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
    if (File.Exists(Path.Combine(d.FullName, "EveConsole.csproj"))) { root = d.FullName; break; }
if (root is null)
{
    Console.Error.WriteLine("EveConsole.csproj not found above " + AppContext.BaseDirectory);
    return 2;
}

var dir = Path.Combine(root, "Localization");
var errors = new List<string>();

// A counted phrase is a family: ItemsOther in English, and ItemsOne/Few/Many beside it in
// whichever languages need them (see Localization/Plurals.cs).
string[] pluralForms = ["Zero", "One", "Two", "Few", "Many", "Other"];

static Dictionary<string, (string Value, string? Comment)> Load(string file, List<string> errors)
{
    var entries = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
    foreach (var data in XDocument.Load(file).Root!.Elements("data"))
    {
        if (data.Attribute("type") is not null || data.Attribute("mimetype") is not null) continue;   // not text
        var name = data.Attribute("name")?.Value ?? "";
        if (!entries.TryAdd(name, (data.Element("value")?.Value ?? "", data.Element("comment")?.Value)))
            errors.Add($"{Path.GetFileName(file)}: \"{name}\" appears twice");
    }
    return entries;
}

// The placeholders a string uses — "{0}" and "{1:N0}" as their indices, and the app's own
// template tokens such as "{alarm}" by name, which a user types into a message and the app
// fills in, so a translation must keep them word for word — or null when the braces do not
// parse at all.
static SortedSet<string>? Placeholders(string s)
{
    var found = new SortedSet<string>(StringComparer.Ordinal);
    for (var i = 0; i < s.Length; i++)
    {
        if (s[i] == '{')
        {
            if (i + 1 < s.Length && s[i + 1] == '{') { i++; continue; }
            var end = s.IndexOf('}', i);
            if (end < 0) return null;
            var body = s[(i + 1)..end];
            var m = Regex.Match(body, @"^\s*(\d+)\s*(,\s*-?\d+\s*)?(:[^{}]*)?$");
            if (m.Success) found.Add(m.Groups[1].Value);
            else if (Regex.IsMatch(body, @"^[A-Za-z_][A-Za-z0-9_]*$")) found.Add(body);
            else return null;
            i = end;
        }
        else if (s[i] == '}')
        {
            if (i + 1 < s.Length && s[i + 1] == '}') { i++; continue; }
            return null;
        }
    }
    return found;
}

var neutralFiles = Directory.GetFiles(dir, "*Text.resx").OrderBy(f => f).ToList();
var coverage = new SortedDictionary<string, (int Done, int Total)>(StringComparer.Ordinal);
var totalStrings = 0;
var everyEnglish = new Dictionary<string, string>(StringComparer.Ordinal);   // "ShellText.Key" → English

foreach (var neutralFile in neutralFiles)
{
    var baseName = Path.GetFileNameWithoutExtension(neutralFile);           // ShellText
    var english  = Load(neutralFile, errors);
    totalStrings += english.Count;

    foreach (var (key, (value, _)) in english)
    {
        everyEnglish[$"{baseName}.{key}"] = value;
        // The build makes each key a property name.
        if (!Regex.IsMatch(key, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            errors.Add($"{baseName}.resx: \"{key}\" is not a valid property name");
        if (Placeholders(value) is null)
            errors.Add($"{baseName}.resx: {key}: not a valid format string: \"{value}\"");
    }

    foreach (var file in Directory.GetFiles(dir, baseName + ".*.resx").OrderBy(f => f))
    {
        var code = Path.GetFileNameWithoutExtension(file)[(baseName.Length + 1)..];   // zh-Hans
        var name = Path.GetFileName(file);
        try { CultureInfo.GetCultureInfo(code, predefinedOnly: true); }
        catch (CultureNotFoundException)
        {
            errors.Add($"{name}: \"{code}\" is not a culture .NET knows, so the build would not treat this as a translation");
            continue;
        }

        var translated = Load(file, errors);
        var done = 0;
        foreach (var (key, (value, _)) in translated)
        {
            // What this entry must match: its English twin, or for an extra plural form the
            // family's Other entry.
            string? twin = english.ContainsKey(key) ? key : null;
            if (twin is null && pluralForms.FirstOrDefault(f => key.EndsWith(f, StringComparison.Ordinal)) is { } form
                && english.ContainsKey(key[..^form.Length] + "Other"))
                twin = key[..^form.Length] + "Other";

            if (twin is null) { errors.Add($"{name}: \"{key}\" is not in {baseName}.resx"); continue; }
            if (value.Length == 0) { errors.Add($"{name}: {key} is empty — leave it out to fall back to English"); continue; }

            var mine = Placeholders(value);
            var want = Placeholders(english[twin].Value);
            if (mine is null)
                errors.Add($"{name}: {key}: not a valid format string: \"{value}\"");
            else if (want is not null && !mine.SetEquals(want))
                errors.Add($"{name}: {key}: placeholders {{{string.Join("},{", mine)}}} but the English has {{{string.Join("},{", want)}}}");
            else if (twin == key)
                done++;
        }

        var (d0, t0) = coverage.GetValueOrDefault(code);
        coverage[code] = (d0 + done, t0 + english.Count);
    }
}

// Every string.Format / Plurals.Format of an entry gets the values its English needs.
var formatUses = FormatUses.Check(root, everyEnglish, errors);

// ── Report ──────────────────────────────────────────────────────────────────────────────
Console.WriteLine($"Interface text: {totalStrings} strings in {neutralFiles.Count} file(s); {formatUses} formatted use(s) checked.");
foreach (var (code, (done, total)) in coverage)
    Console.WriteLine($"  {code,-8} {done,5} of {total} translated ({100.0 * done / Math.Max(1, total):0}%)");

// Text still written into the XAML: attributes a person reads, holding words rather than a
// binding or a resource. Reported, not failed — the app is moving over screen by screen.
var readable = new Regex(@"\b(Text|Content|Header|Title|ToolTip\.Tip|Watermark|Label)=""([^""{][^""]*)""");
var byFile = new List<(string File, int Count)>();
foreach (var xaml in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
{
    var rel = Path.GetRelativePath(root, xaml).Replace('\\', '/');
    if (rel.StartsWith("tools/") || rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
    var n = readable.Matches(File.ReadAllText(xaml)).Count(m => m.Groups[2].Value.Any(char.IsLetter));
    if (n > 0) byFile.Add((rel, n));
}
Console.WriteLine($"Still written into the XAML: {byFile.Sum(f => f.Count)} attribute(s) in {byFile.Count} file(s); most:");
foreach (var (file, count) in byFile.OrderByDescending(f => f.Count).Take(8))
    Console.WriteLine($"  {count,5}  {file}");

if (errors.Count == 0)
{
    Console.WriteLine("Translations: no problems.");
    return 0;
}

Console.Error.WriteLine($"Translations: {errors.Count} problem(s):");
foreach (var e in errors) Console.Error.WriteLine("  " + e);
return 1;
