namespace EveConsole.Services;

/// <summary>
/// How loud speech is, and bringing a voice server's speech to the level Kokoro speaks at.
///
/// <para>⚠️ A server of our own speaks at whatever level its model makes, and every engine plays
/// at the same volume, so the difference comes through untouched: Chatterbox's voices came out
/// well above Kokoro, which sits at the level of the system's other sounds. It has to come out
/// of the audio itself — measured as broadcasters measure it (ITU-R BS.1770: loudness as the ear
/// weighs it, not peaks) and brought to Kokoro's.</para>
/// </summary>
public static class SpeechLoudness
{
    /// <summary>
    /// Kokoro's speech, as measured (LUFS): what a server voice is brought to. Its default voice,
    /// af_heart, measured −22.2 over three typical replies, and its voices −24 to −19 (median
    /// −21.6); Chatterbox's −16 to −14, peaking at full scale.
    /// </summary>
    public const double TargetLufs = -22.0;

    /// <summary>How far a quiet voice may be raised: until its loudest sample is a decibel under full scale.</summary>
    private const double CeilingDb = -1.0;

    /// <summary>
    /// <paramref name="wav"/> brought to <paramref name="targetLufs"/> — but never raised to within
    /// a decibel of full scale. Returned unchanged when it is not a WAV this reads (16-bit PCM or
    /// 32-bit float), when it is silence, or when it is already within a fraction of a decibel.
    /// </summary>
    public static byte[] Level(byte[] wav, double targetLufs = TargetLufs)
    {
        if (Read(wav) is not { } pcm) return wav;
        var samples = Samples(wav, pcm);
        if (Integrated(samples, pcm.Channels, pcm.SampleRate) is not { } lufs) return wav;

        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));

        var gainDb = Math.Clamp(targetLufs - lufs, -30, 12);
        if (peak > 0) gainDb = Math.Min(gainDb, CeilingDb - 20 * Math.Log10(peak));
        if (Math.Abs(gainDb) < 0.25) return wav;

        var output = (byte[])wav.Clone();
        Write(output, pcm, samples, (float)Math.Pow(10, gainDb / 20));
        return output;
    }

    /// <summary>The integrated loudness of a WAV, in LUFS; null when it cannot be read or is silence.</summary>
    public static double? Measure(byte[] wav) =>
        Read(wav) is { } pcm ? Integrated(Samples(wav, pcm), pcm.Channels, pcm.SampleRate) : null;

    /// <summary>
    /// Integrated loudness (ITU-R BS.1770-4) of interleaved samples: K-weighted, in 400 ms blocks
    /// every 100 ms, gated at −70 LUFS and then at 10 LU under the level of what passed. A clip
    /// shorter than a block is one block. Null for silence.
    /// </summary>
    public static double? Integrated(float[] interleaved, int channels, int sampleRate)
    {
        var frames = channels > 0 ? interleaved.Length / channels : 0;
        if (frames == 0 || sampleRate <= 0) return null;

        // K-weighting — a high shelf for the head, then a high pass — designed for this rate as
        // libebur128 does, so 24 kHz speech is weighed as 48 kHz would be.
        var (sb, sa) = Shelf(sampleRate);
        var (hb, ha) = HighPass(sampleRate);
        var power = new double[frames];
        for (var c = 0; c < channels; c++)
        {
            double s1 = 0, s2 = 0, h1 = 0, h2 = 0;
            for (var i = 0; i < frames; i++)
            {
                double x = interleaved[i * channels + c];
                var y = sb[0] * x + s1; s1 = sb[1] * x - sa[1] * y + s2; s2 = sb[2] * x - sa[2] * y;
                var z = hb[0] * y + h1; h1 = hb[1] * y - ha[1] * z + h2; h2 = hb[2] * y - ha[2] * z;
                power[i] += z * z;           // every channel weighs 1: mono or stereo speech
            }
        }

        var prefix = new double[frames + 1];
        for (var i = 0; i < frames; i++) prefix[i + 1] = prefix[i] + power[i];
        var block  = Math.Min(frames, (int)(sampleRate * 0.4));
        var hop    = Math.Max(1, sampleRate / 10);
        var blocks = new List<double>();
        for (var start = 0; start + block <= frames; start += hop)
            blocks.Add((prefix[start + block] - prefix[start]) / block);

        static double Loudness(double meanSquare) => -0.691 + 10 * Math.Log10(meanSquare);
        var heard = blocks.Where(z => z > 0 && Loudness(z) > -70).ToList();
        if (heard.Count == 0) return null;
        var relative = Loudness(heard.Average()) - 10;
        var kept = heard.Where(z => Loudness(z) > relative).ToList();
        return kept.Count == 0 ? null : Loudness(kept.Average());
    }

    private static (double[] B, double[] A) Shelf(int rate)
    {
        const double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
        var k  = Math.Tan(Math.PI * f0 / rate);
        var vh = Math.Pow(10, g / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / q + k * k;
        return ([(vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0],
                [1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0]);
    }

    private static (double[] B, double[] A) HighPass(int rate)
    {
        const double f0 = 38.13547087602444, q = 0.5003270373238773;
        var k  = Math.Tan(Math.PI * f0 / rate);
        var a0 = 1 + k / q + k * k;
        return ([1, -2, 1], [1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0]);
    }

    // ── WAV ──────────────────────────────────────────────────────────────────────

    private readonly record struct Pcm(int Channels, int SampleRate, int Offset, int Length, bool IsFloat);

    /// <summary>Where a WAV's samples are and what they are; null for anything but 16-bit PCM or
    /// 32-bit float, the two a speech server sends.</summary>
    private static Pcm? Read(byte[] b)
    {
        if (b.Length < 12 || !Tag(b, 0, "RIFF") || !Tag(b, 8, "WAVE")) return null;
        int channels = 0, rate = 0, bits = 0, format = 0;
        long pos = 12;
        while (pos + 8 <= b.Length)
        {
            var at   = (int)pos;
            var size = BitConverter.ToUInt32(b, at + 4);
            var body = at + 8;
            if (Tag(b, at, "fmt ") && body + 16 <= b.Length)
            {
                format   = BitConverter.ToUInt16(b, body);
                channels = BitConverter.ToUInt16(b, body + 2);
                rate     = BitConverter.ToInt32(b, body + 4);
                bits     = BitConverter.ToUInt16(b, body + 14);
                // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of its sub-format.
                if (format == 0xFFFE && size >= 40 && body + 26 <= b.Length) format = BitConverter.ToUInt16(b, body + 24);
            }
            else if (Tag(b, at, "data"))
            {
                var isFloat = format == 3 && bits == 32;
                if (channels <= 0 || rate <= 0 || !(isFloat || (format == 1 && bits == 16))) return null;
                // A server that streams its WAV writes the header before it knows the length, and
                // some put 0 or 0xFFFFFFFF there: the samples are then simply the rest.
                var length = size == 0 || size > b.Length - body ? b.Length - body : (int)size;
                var frame  = channels * bits / 8;
                return new Pcm(channels, rate, body, length - length % frame, isFloat);
            }
            pos = body + (long)size + (size & 1);      // chunks are padded to an even length
        }
        return null;
    }

    private static bool Tag(byte[] b, int at, string tag) =>
        b[at] == tag[0] && b[at + 1] == tag[1] && b[at + 2] == tag[2] && b[at + 3] == tag[3];

    private static float[] Samples(byte[] b, Pcm pcm)
    {
        var n = pcm.Length / (pcm.IsFloat ? 4 : 2);
        var samples = new float[n];
        for (var i = 0; i < n; i++)
            samples[i] = pcm.IsFloat
                ? BitConverter.ToSingle(b, pcm.Offset + i * 4)
                : BitConverter.ToInt16(b, pcm.Offset + i * 2) / 32768f;
        return samples;
    }

    private static void Write(byte[] b, Pcm pcm, float[] samples, float gain)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i] * gain;
            if (pcm.IsFloat)
                BitConverter.TryWriteBytes(b.AsSpan(pcm.Offset + i * 4, 4), v);
            else
                BitConverter.TryWriteBytes(b.AsSpan(pcm.Offset + i * 2, 2),
                    (short)Math.Clamp(MathF.Round(v * 32768f), short.MinValue, short.MaxValue));
        }
    }
}
