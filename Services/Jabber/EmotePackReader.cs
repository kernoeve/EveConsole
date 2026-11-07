using System.IO.Compression;
using SkiaSharp;

namespace EveConsole.Services.Jabber;

/// <summary>A line of a smiley theme: one picture, and the codes that stand for it.</summary>
public sealed record EmoteThemeLine(string File, IReadOnlyList<string> Codes);

/// <summary>What a Pidgin smiley theme file says, before any picture is read.</summary>
public sealed record EmoteTheme(string Name, string Description, string Author, string Icon, IReadOnlyList<EmoteThemeLine> Lines);

/// <summary>What a picture turned out to be, once read.</summary>
public sealed record EmotePictureInfo(int Width, int Height, int Frames);

/// <summary>A pack read from disk: the theme, and the bytes of every picture it names that could be used.</summary>
public sealed class EmotePackFile
{
    public required EmoteTheme Theme { get; init; }

    /// <summary>By the file name the theme gives, case ignored.</summary>
    public Dictionary<string, byte[]> Pictures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, EmotePictureInfo> Info { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files the theme names that are not in the pack.</summary>
    public List<string> Missing { get; } = [];

    /// <summary>Files that are there but are not a picture, or are too big to be an emote.</summary>
    public List<string> Unusable { get; } = [];
}

/// <summary>
/// Reads a Pidgin smiley theme: a zip, a folder, or the <c>theme</c> file in a folder.
///
/// <para>The theme file is a header of <c>Name=</c>, <c>Description=</c>, <c>Icon=</c> and <c>Author=</c>, then sections.
/// Each line of a section is a picture's file name and the codes for it. A line starting <c>!</c> is one Pidgin leaves
/// out of its own picker; it is read the same here, because this picker can be searched. Only the <c>[default]</c>
/// section and the ones for XMPP are read: the others are for other networks.</para>
///
/// <para>⚠️ A pack is someone else's file. Nothing is unpacked to disk, a name is only ever looked up and never used as a
/// path outside the pack's own folder, and sizes are capped before anything is read into memory.</para>
/// </summary>
public static class EmotePackReader
{
    public const long MostPackBytes    = 64L * 1024 * 1024;
    public const int  MostPictureBytes = 4 * 1024 * 1024;
    public const int  MostThemeBytes   = 2 * 1024 * 1024;
    public const int  MostPictures     = 5000;
    public const int  MostSide         = 1024;
    // A small picture with many frames is fine: what it costs is the pixels, capped below.
    public const int  MostFrames       = 2000;
    public const int  MostCodeLength   = 64;

    /// <summary>Pixels across every frame of one picture: what it costs to show it moving.</summary>
    public const long MostFramePixels  = 16L * 1024 * 1024;

    public const string ThemeFileName = "theme";

    private static readonly string[] SectionsRead = ["default", "xmpp", "jabber"];

    public static EmoteTheme ParseTheme(string text)
    {
        string name = "", description = "", author = "", icon = "";
        var lines = new List<EmoteThemeLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inSection = false;
        var reading = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                inSection = true;
                reading = SectionsRead.Contains(line[1..^1].Trim().ToLowerInvariant());
                continue;
            }

            if (!inSection)
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var value = line[(eq + 1)..].Trim();
                switch (line[..eq].Trim().ToLowerInvariant())
                {
                    case "name":        name = value; break;
                    case "description": description = value; break;
                    case "author":      author = value; break;
                    case "icon":        icon = value; break;
                }
                continue;
            }
            if (!reading) continue;

            // "! file code": the mark Pidgin hides a line from its picker with, alone or against the name.
            if (line[0] == '!') line = line[1..].TrimStart();

            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) continue;

            var codes = words.Skip(1).Where(c => c.Length <= MostCodeLength && seen.Add(c)).ToList();
            if (codes.Count > 0) lines.Add(new EmoteThemeLine(words[0], codes));
        }

        return new EmoteTheme(name, description, author, icon, lines);
    }

    /// <summary>Reads a pack from a zip, a folder, or a folder's theme file. Throws, with why, when it is not one.</summary>
    public static EmotePackFile Read(string path)
    {
        if (Directory.Exists(path)) return ReadFolder(path);
        if (string.Equals(Path.GetFileName(path), ThemeFileName, StringComparison.OrdinalIgnoreCase))
            return ReadFolder(Path.GetDirectoryName(path) ?? ".");
        return ReadZip(path);
    }

    private static EmotePackFile ReadFolder(string folder)
    {
        var root = Path.GetFullPath(folder);
        var themePath = Path.Combine(root, ThemeFileName);
        if (!File.Exists(themePath))
            themePath = Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, ThemeFileName)).FirstOrDefault(File.Exists)
                        ?? throw new InvalidDataException(Localization.JabberText.EmoteNoTheme);

        if (new FileInfo(themePath).Length > MostThemeBytes) throw new InvalidDataException(Localization.JabberText.EmoteThemeTooBig);
        var dir = Path.GetDirectoryName(themePath)!;
        var pack = new EmotePackFile { Theme = ParseTheme(File.ReadAllText(themePath)) };

        long total = 0;
        foreach (var file in Files(pack.Theme))
        {
            if (!SafeName(file)) { pack.Unusable.Add(file); continue; }

            var full = Path.GetFullPath(Path.Combine(dir, file));
            if (!full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { pack.Unusable.Add(file); continue; }
            if (!File.Exists(full)) { pack.Missing.Add(file); continue; }

            var length = new FileInfo(full).Length;
            if (length > MostPictureBytes) { pack.Unusable.Add(file); continue; }
            if ((total += length) > MostPackBytes) throw new InvalidDataException(Localization.JabberText.EmotePackTooBig);

            Take(pack, file, File.ReadAllBytes(full));
        }
        return pack;
    }

    private static EmotePackFile ReadZip(string path)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidDataException(Localization.JabberText.EmoteNotAPack); }

        using (zip)
        {
            if (zip.Entries.Count > MostPictures * 2) throw new InvalidDataException(Localization.JabberText.EmotePackTooBig);

            // The theme nearest the top: a pack zipped with its folder has it one down.
            var themeEntry = zip.Entries
                .Where(e => string.Equals(e.Name, ThemeFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
                .FirstOrDefault() ?? throw new InvalidDataException(Localization.JabberText.EmoteNoTheme);
            if (themeEntry.Length > MostThemeBytes) throw new InvalidDataException(Localization.JabberText.EmoteThemeTooBig);

            var folder = themeEntry.FullName[..^themeEntry.Name.Length].Replace('\\', '/');
            var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries) byName.TryAdd(entry.FullName.Replace('\\', '/'), entry);

            string themeText;
            using (var reader = new StreamReader(themeEntry.Open())) themeText = reader.ReadToEnd();
            var pack = new EmotePackFile { Theme = ParseTheme(themeText) };

            long total = 0;
            foreach (var file in Files(pack.Theme))
            {
                if (!SafeName(file)) { pack.Unusable.Add(file); continue; }
                if (!byName.TryGetValue(folder + file.Replace('\\', '/'), out var entry)) { pack.Missing.Add(file); continue; }
                if (entry.Length > MostPictureBytes) { pack.Unusable.Add(file); continue; }

                // ⚠️ The size a zip claims is not trusted: no more than the cap is ever read.
                var bytes = ReadAtMost(entry, MostPictureBytes);
                if (bytes is null) { pack.Unusable.Add(file); continue; }
                if ((total += bytes.Length) > MostPackBytes) throw new InvalidDataException(Localization.JabberText.EmotePackTooBig);

                Take(pack, file, bytes);
            }
            return pack;
        }
    }

    private static IEnumerable<string> Files(EmoteTheme theme)
    {
        var files = theme.Lines.Select(l => l.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count > MostPictures) throw new InvalidDataException(Localization.JabberText.EmotePackTooBig);
        return files;
    }

    /// <summary>A plain name in the pack's own folder, or one below it: never a way out of it.</summary>
    private static bool SafeName(string file) =>
        file.Length is > 0 and < 260 && !Path.IsPathRooted(file) && !file.Contains(':')
        && !file.Split('/', '\\').Any(part => part is "" or "." or "..");

    private static byte[]? ReadAtMost(ZipArchiveEntry entry, int most)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > most) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static void Take(EmotePackFile pack, string file, byte[] bytes)
    {
        if (Probe(bytes) is { } info) { pack.Pictures[file] = bytes; pack.Info[file] = info; }
        else pack.Unusable.Add(file);
    }

    /// <summary>What the bytes are, if they are a picture small enough to be an emote. Null for anything else.</summary>
    public static EmotePictureInfo? Probe(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null) return null;

            var (width, height) = (codec.Info.Width, codec.Info.Height);
            var frames = Math.Max(1, codec.FrameCount);
            if (width is < 1 or > MostSide || height is < 1 or > MostSide || frames > MostFrames) return null;
            if ((long)width * height * frames > MostFramePixels) return null;

            return new EmotePictureInfo(width, height, frames);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
