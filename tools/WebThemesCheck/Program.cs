using EveConsole.Services.WebStore;

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Does the web store's copy of the palette still match the palette?
//
// A store's site is themed from WebThemes, a table of the colours in Themes/Palette.axaml —
// a copy, because the site is themed by the STORE's setting resolved by key, never by what the
// desktop is showing, and the client pushing it may be headless with no resource dictionary
// loaded at all. A copy drifts the first time somebody adjusts a colour in the XAML and not
// here; this reads the XAML as XML and fails the build on the first difference.
// ═══════════════════════════════════════════════════════════════════════════════════════════

var dir = AppContext.BaseDirectory;
string? palette = null;
for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
{
    var candidate = Path.Combine(d.FullName, "Themes", "Palette.axaml");
    if (File.Exists(candidate) && File.Exists(Path.Combine(d.FullName, "EveConsole.csproj")))
    {
        palette = candidate;
        break;
    }
}

if (palette is null)
{
    Console.Error.WriteLine("Themes/Palette.axaml not found above " + dir);
    return 2;
}

var diffs = WebThemes.Differences(File.ReadAllText(palette));
if (diffs.Count == 0)
{
    Console.WriteLine($"WebThemes matches {palette}: {WebThemes.All.Count} themes, {WebThemes.Tokens.Length} tokens each.");
    return 0;
}

Console.Error.WriteLine($"WebThemes disagrees with {palette} in {diffs.Count} place(s):");
foreach (var d in diffs) Console.Error.WriteLine("  " + d);
return 1;
