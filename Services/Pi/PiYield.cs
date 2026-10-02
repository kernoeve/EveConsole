namespace EveConsole.Services.Pi;

/// <summary>
/// An extractor program's output per cycle, by CCP's published formula
/// (developers.eveonline.com/docs/guides/pi/ — "Extraction calculation"), written from the
/// formula as documented there.
///
/// <para>Per cycle i, from 0: with bar width w = cycleSeconds / 900 and t = (i + 0.5) × w,
/// decay = q / (1 + t × decayFactor), phase = q^0.7, and the mean of cos(phase + t/12),
/// cos(phase/2 + t/5) and cos(t/2), floored at zero, as the noise term n: the cycle yields
/// w × decay × (1 + noiseFactor × n), rounded down. q is ESI's qty_per_cycle; the factors are
/// dogma 1683 and 1687 (0.012 and 0.8).</para>
///
/// <para>⚠️ Rounding. The page's listings disagree at one edge: its C# truncates, while its Kotlin
/// rounds an exact whole number down by one more ("123.0 → 122"). This follows the Kotlin rule —
/// it is the only one of the three written as a deliberate statement about the result. The two
/// differ only when the product lands exactly on an integer, which in double arithmetic is rare.
/// Doubles throughout, as the Python and Kotlin listings use; the C# listing's single-precision
/// intermediates can move a cycle by one unit.</para>
///
/// <para>⚠️ <c>cycle_time</c> is in SECONDS on ESI. The page's C# comment multiplies it by 60;
/// that is a remnant of the old XML API, which gave minutes.</para>
/// </summary>
public static class PiYield
{
    /// <summary>Cycles in a program: whole cycles between install and expiry.</summary>
    public static int TotalCycles(DateTimeOffset install, DateTimeOffset expiry, int cycleSeconds)
    {
        if (cycleSeconds <= 0 || expiry <= install) return 0;
        return (int)((long)(expiry - install).TotalSeconds / cycleSeconds);
    }

    /// <summary>Output of cycle <paramref name="cycle"/> (0-based) of a program.</summary>
    public static long CycleOutput(int qtyPerCycle, int cycleSeconds, int cycle,
                                   double decayFactor = PiStaticData.DefaultDecayFactor,
                                   double noiseFactor = PiStaticData.DefaultNoiseFactor)
    {
        if (qtyPerCycle <= 0 || cycleSeconds <= 0 || cycle < 0) return 0;

        var barWidth = cycleSeconds / 900.0;
        var t        = (cycle + 0.5) * barWidth;
        var decay    = qtyPerCycle / (1 + t * decayFactor);
        var phase    = Math.Pow(qtyPerCycle, 0.7);

        var a = Math.Cos(phase + t * (1.0 / 12.0));
        var b = Math.Cos(phase / 2.0 + t * 0.2);
        var c = Math.Cos(t * 0.5);
        var noise = Math.Max((a + b + c) / 3.0, 0);

        var output = barWidth * decay * (1 + noiseFactor * noise);
        var whole  = (long)Math.Floor(output);
        // The Kotlin listing's edge: an exact whole number counts one less.
        if (output == whole) whole--;
        return Math.Max(0, whole);
    }

    /// <summary>Every cycle's output for a whole program.</summary>
    public static long[] ProgramOutputs(int qtyPerCycle, int cycleSeconds, int totalCycles,
                                        double decayFactor = PiStaticData.DefaultDecayFactor,
                                        double noiseFactor = PiStaticData.DefaultNoiseFactor)
    {
        if (totalCycles <= 0) return [];
        var values = new long[totalCycles];
        for (var i = 0; i < totalCycles; i++)
            values[i] = CycleOutput(qtyPerCycle, cycleSeconds, i, decayFactor, noiseFactor);
        return values;
    }
}
