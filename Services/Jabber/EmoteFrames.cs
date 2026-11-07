using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace EveConsole.Services.Jabber;

/// <summary>
/// A picture ready to draw: one frame for a still, every frame of an animated GIF with how long each shows. Avalonia
/// draws only the first frame of a GIF, so the frames are decoded here and the control showing them steps through.
/// </summary>
public sealed class EmoteFrames
{
    /// <summary>What browsers show a frame for when the file says none, or next to none.</summary>
    private const int DefaultFrameMs = 100;

    private readonly Bitmap[] _frames;
    private readonly int[] _ends;

    private EmoteFrames(Bitmap[] frames, int[] ends, PixelSize size)
    {
        _frames = frames;
        _ends = ends;
        Size = size;
        Bytes = (long)size.Width * size.Height * 4 * frames.Length;
    }

    public PixelSize Size { get; }
    public int Count => _frames.Length;
    public bool IsAnimated => _frames.Length > 1;

    /// <summary>One loop, in milliseconds.</summary>
    public int LoopMs => _ends[^1];

    /// <summary>Memory the frames take, for keeping the decoded ones within bounds.</summary>
    public long Bytes { get; }

    public Bitmap First => _frames[0];

    /// <summary>The frame showing this far into the animation. Every animation loops.</summary>
    public Bitmap At(long ms)
    {
        if (!IsAnimated) return _frames[0];

        var t = (int)(ms % LoopMs);
        var i = Array.BinarySearch(_ends, t + 1);
        if (i < 0) i = ~i;
        return _frames[Math.Min(i, _frames.Length - 1)];
    }

    /// <summary>Every frame, or only the first. Null when the bytes are not a picture.</summary>
    public static EmoteFrames? Decode(byte[] bytes, bool firstOnly)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (info.Width < 1 || info.Height < 1) return null;

        var frameInfo = codec.FrameInfo;
        var count = firstOnly ? 1 : Math.Clamp(codec.FrameCount, 1, EmotePackReader.MostFrames);
        var decoded = new SKBitmap?[count];
        var bitmaps = new List<Bitmap>(count);
        var ends = new List<int>(count);
        var elapsed = 0;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var frame = new SKBitmap(info);
                var prior = i < frameInfo.Length ? frameInfo[i].RequiredFrame : -1;

                // ⚠️ A GIF frame is often only what changed: it is drawn over the one it says it needs.
                if (prior >= 0 && prior < i && decoded[prior] is { } under) CopyPixels(under, frame);
                else frame.Erase(SKColors.Transparent);

                var result = codec.GetPixels(info, frame.GetPixels(), new SKCodecOptions(i, prior < i ? prior : -1));
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    frame.Dispose();
                    if (i == 0) return null;
                    break;
                }

                decoded[i] = frame;
                bitmaps.Add(new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, frame.GetPixels(),
                    new PixelSize(info.Width, info.Height), new Vector(96, 96), frame.RowBytes));

                var ms = i < frameInfo.Length ? frameInfo[i].Duration : 0;
                elapsed += ms <= 10 ? DefaultFrameMs : ms;
                ends.Add(elapsed);
            }
        }
        finally
        {
            foreach (var frame in decoded) frame?.Dispose();
        }

        return bitmaps.Count == 0 ? null : new EmoteFrames([.. bitmaps], [.. ends], new PixelSize(info.Width, info.Height));
    }

    private static unsafe void CopyPixels(SKBitmap from, SKBitmap to)
    {
        var length = Math.Min(from.ByteCount, to.ByteCount);
        Buffer.MemoryCopy((void*)from.GetPixels(), (void*)to.GetPixels(), to.ByteCount, length);
    }
}
