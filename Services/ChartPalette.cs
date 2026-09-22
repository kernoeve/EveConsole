using SkiaSharp;

namespace EveConsole.Services;

/// <summary>
/// The colours a pie's slices take, in order, and the grey of the slice that stands for the
/// rest. Data colours, not chrome: the same on every theme, so a chart reads the same on screen
/// as in anything exported from it.
/// </summary>
public static class ChartPalette
{
    public static readonly SKColor[] Pie =
    [
        new(0xc8, 0xa8, 0x4b), new(0x5b, 0x9b, 0xd5), new(0x70, 0xad, 0x47), new(0xed, 0x7d, 0x31),
        new(0xa8, 0x79, 0xd8), new(0x17, 0xbe, 0xcf), new(0xe7, 0x4c, 0x3c), new(0xf1, 0xc4, 0x0f),
        new(0x2e, 0xcc, 0x71), new(0xe8, 0x4d, 0x8a),
    ];

    public static readonly SKColor Other = new(0x55, 0x55, 0x66);
}
