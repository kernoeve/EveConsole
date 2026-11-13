using System.Globalization;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace EveConsole.Services;

/// <summary>
/// A picture this person chooses for themselves on Jabber or Mumble: from a file, or an EVE character's portrait. Made
/// square and small before it goes anywhere, as every server takes that and some take nothing bigger.
/// </summary>
public static class OwnPicture
{
    /// <summary>The picture's side, in pixels: what the screens draw at most, and well under any server's limit.</summary>
    public const int Side = 128;

    /// <summary>Most a file chosen may be before it is even read: a photo straight from a camera is well under this.</summary>
    public const long MostFileBytes = 20 * 1024 * 1024;

    /// <summary>A picture made square (the middle of it) and <see cref="Side"/> across, as a JPEG. Null when it is not a picture.</summary>
    public static byte[]? Prepare(byte[] input)
    {
        try
        {
            using var original = SKBitmap.Decode(input);
            if (original is null || original.Width <= 0 || original.Height <= 0) return null;

            var side = Math.Min(original.Width, original.Height);
            var crop = new SKRectI((original.Width - side) / 2, (original.Height - side) / 2, (original.Width + side) / 2, (original.Height + side) / 2);

            using var square = new SKBitmap(Side, Side, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(square))
            {
                // A see-through picture is put on white: a JPEG has no see-through, and black behind it reads as a hole.
                canvas.Clear(SKColors.White);
                using var image = SKImage.FromBitmap(original);
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                canvas.DrawImage(image, SKRect.Create(crop.Left, crop.Top, crop.Width, crop.Height), SKRect.Create(0, 0, Side, Side), paint);
            }
            using var data = square.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data?.ToArray();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>An EVE character's portrait from the game's own image server, made ready. Null when it could not be had.</summary>
    public static async Task<byte[]?> FromEvePortraitAsync(long characterId, CancellationToken ct)
    {
        var link = $"https://images.evetech.net/characters/{characterId.ToString(CultureInfo.InvariantCulture)}/portrait?size=256";
        return await WebPictures.FetchAsync(link, ct) is { } bytes ? Prepare(bytes) : null;
    }

    /// <summary>The EVE characters added to this app, by name, to offer their portraits.</summary>
    public static async Task<List<(long Id, string Name)>> EveCharactersAsync(IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        return [.. rows.Where(r => r.Name.Length > 0).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => (r.Id, r.Name))];
    }
}
