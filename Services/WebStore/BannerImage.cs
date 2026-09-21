using System;
using System.IO;
using SkiaSharp;

namespace EveConsole.Services.WebStore;

/// <summary>
/// The store's banner, made fit for the site.
///
/// <para>The site keeps the banner in its database, one row, under a hard limit; the page shows
/// it at most 1100 pixels wide. A picture that already fits — a common format, not too wide, not
/// too many bytes — goes as it is, animation and all. Anything else is scaled to the width the
/// page can use and encoded as WebP, which keeps transparency and is small; the quality steps
/// down until it fits.</para>
/// </summary>
public static class BannerImage
{
    /// <summary>Under the site's cap of 1,000,000 bytes, with room for the JSON around it.</summary>
    public const int MaxBytes = 950_000;

    /// <summary>Twice the page's width, so the picture stays sharp on a dense screen.</summary>
    public const int MaxWidth = 1800;

    public sealed record Prepared(byte[] Bytes, string ContentType, int Width, int Height);

    /// <summary>The picture as the site should have it. Throws when the bytes are not a picture
    /// the app can read, or cannot be made small enough.</summary>
    public static Prepared Prepare(byte[] source)
    {
        using var data  = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("not a picture the app can read");
        var info = codec.Info;
        var type = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png  => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Webp => "image/webp",
            SKEncodedImageFormat.Gif  => "image/gif",
            _                         => "",
        };
        if (type.Length > 0 && source.Length <= MaxBytes && info.Width <= MaxWidth)
            return new Prepared(source, type, info.Width, info.Height);

        using var bitmap = SKBitmap.Decode(source) ?? throw new InvalidDataException("not a picture the app can read");
        foreach (var (width, quality) in new[] { (MaxWidth, 85), (MaxWidth, 70), (1200, 75), (1200, 55), (900, 50) })
        {
            var w = Math.Min(width, bitmap.Width);
            var h = Math.Max(1, (int)Math.Round(bitmap.Height * (double)w / bitmap.Width));
            using var scaled = w == bitmap.Width ? bitmap.Copy() : bitmap.Resize(new SKImageInfo(w, h), SKFilterQuality.High);
            if (scaled is null) break;
            using var image   = SKImage.FromBitmap(scaled);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality);
            if (encoded is not null && encoded.Size <= MaxBytes)
                return new Prepared(encoded.ToArray(), "image/webp", w, h);
        }
        throw new InvalidDataException("the picture could not be made small enough for the site");
    }
}
