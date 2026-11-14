using SoundFlow.Extensions.WebRtc.Apm;

namespace EveConsole.Services.Mumble;

/// <summary>A Fourier transform of a fixed size. Allocates nothing once made: this runs on the sound device's thread.</summary>
internal sealed class MumbleFft
{
    private readonly int _size;
    private readonly int[] _reversed;
    private readonly double[] _cos, _sin;

    public MumbleFft(int size)
    {
        _size = size;
        _reversed = new int[size];
        _cos = new double[size / 2];
        _sin = new double[size / 2];

        var bits = (int)Math.Log2(size);
        for (var i = 0; i < size; i++)
        {
            int r = 0, v = i;
            for (var b = 0; b < bits; b++) { r = r << 1 | v & 1; v >>= 1; }
            _reversed[i] = r;
        }
        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = Math.Cos(2 * Math.PI * i / size);
            _sin[i] = Math.Sin(2 * Math.PI * i / size);
        }
    }

    public void Forward(double[] re, double[] im) => Transform(re, im, 1);

    /// <summary>The way back, scaled so that going there and back changes nothing.</summary>
    public void Inverse(double[] re, double[] im)
    {
        Transform(re, im, -1);
        for (var i = 0; i < _size; i++) { re[i] /= _size; im[i] /= _size; }
    }

    private void Transform(double[] re, double[] im, int direction)
    {
        for (var i = 0; i < _size; i++)
        {
            var j = _reversed[i];
            if (j <= i) continue;
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        for (var length = 2; length <= _size; length <<= 1)
        {
            int half = length / 2, step = _size / length;
            for (var start = 0; start < _size; start += length)
                for (var k = 0; k < half; k++)
                {
                    double wr = _cos[k * step], wi = -direction * _sin[k * step];
                    int a = start + k, b = a + half;
                    double tr = wr * re[b] - wi * im[b], ti = wr * im[b] + wi * re[b];
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
    }
}

/// <summary>
/// Takes what came out of the speakers back out of what the microphone heard: WebRTC's echo canceller, the one browsers
/// use. ⚠️ Not our own: a filter of ours, built the way Speex's is, was tried first and on a real room with the speakers
/// loud it took 5 seconds to learn, let bursts through, and cost someone talking over it their words. This learned in a
/// second, and the user could hear the difference on the same recordings.
///
/// <para>⚠️ What it ships as has a native library that may not load: the Linux one needs a recent glibc. So nothing
/// is made until <see cref="Available"/> says so, and without it voice carries on with no echo cancelling.</para>
/// </summary>
public sealed class MumbleEchoCanceller
{
    /// <summary>What it works in: 10 ms, the only size it takes.</summary>
    public const int FrameSamples = MumbleMixer.SamplesPer10Ms;

    private const float Scale = 32768f;

    private static readonly Lazy<Exception?> LoadProblem = new(TryLoad);

    private readonly AudioProcessingModule _apm = new();
    private readonly StreamConfig _stream = new(MumbleMixer.SampleRate, 1);
    private readonly float[][] _far = [new float[FrameSamples]], _near = [new float[FrameSamples]], _clean = [new float[FrameSamples]];

    /// <summary>Whether its native library loads on this machine.</summary>
    public static bool Available => LoadProblem.Value is null;

    /// <summary>What stopped it loading, if anything did.</summary>
    public static Exception? WhyNot => LoadProblem.Value;

    public MumbleEchoCanceller()
    {
        using var config = new ApmConfig();
        config.SetEchoCanceller(true, false);
        config.SetNoiseSuppression(false, NoiseSuppressionLevel.Moderate);
        config.SetGainController2(false);
        config.SetHighPassFilter(true);
        _apm.ApplyConfig(config);
        _apm.Initialize();
    }

    private static Exception? TryLoad()
    {
        try
        {
            using var apm = new AudioProcessingModule();
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
                                     or TypeInitializationException or PlatformNotSupportedException or FileNotFoundException or FileLoadException)
        {
            return e;
        }
    }

    /// <summary>One frame of what was played and the frame of microphone that goes with it, which is changed in place.</summary>
    public void Process(ReadOnlySpan<short> played, Span<short> mic)
    {
        for (var i = 0; i < FrameSamples; i++)
        {
            _far[0][i] = played[i] / Scale;
            _near[0][i] = mic[i] / Scale;
        }

        // ⚠️ What was played first, and the delay every frame, as it forgets. Nought because it finds the real one itself.
        _apm.AnalyzeReverseStream(_far, _stream);
        _apm.SetStreamDelayMs(0);

        // What it could not work on goes out as it came in.
        if (_apm.ProcessStream(_near, _stream, _stream, _clean) != ApmError.NoError) return;

        for (var i = 0; i < FrameSamples; i++)
            mic[i] = (short)Math.Clamp(_clean[0][i] * Scale, short.MinValue, short.MaxValue);
    }
}

/// <summary>
/// Quietens steady background noise, frequency by frequency, leaving through what stands well above it. A Wiener filter
/// over overlapping windows.
/// </summary>
public sealed class MumbleNoiseSuppressor
{
    public const int BlockSize = 512;
    private const int Size = BlockSize * 2, Bins = BlockSize + 1;

    /// <summary>How far anything is turned down at most. Further than this and what is left starts to warble.</summary>
    private const double FloorDb = -20;

    /// <summary>What is carried over from the last block's reading. The higher, the less warble, and the slower to follow.</summary>
    private const double Carry = 0.97;

    private const double NoiseSmoothing = 0.8;

    /// <summary>How quickly the idea of how loud the noise is rises, about 1.5 dB a second, so speech is never taken for it.</summary>
    private const double NoiseRise = 1.0037;

    /// <summary>The lowest points of a noise sit well under its average.</summary>
    private const double MinimumToAverage = 1.8;

    private readonly MumbleFft _fft = new(Size);
    private readonly double[] _window = new double[Size];
    private readonly double[] _before = new double[BlockSize], _tail = new double[BlockSize];
    private readonly double[] _re = new double[Size], _im = new double[Size];
    private readonly double[] _smoothed = new double[Bins], _least = new double[Bins];
    private readonly double[] _gain = new double[Bins], _lastRatio = new double[Bins];
    private bool _started;

    public MumbleNoiseSuppressor()
    {
        for (var i = 0; i < Size; i++) _window[i] = Math.Sin(Math.PI * (i + 0.5) / Size);
        Array.Fill(_gain, 1.0);
    }

    /// <summary>One block, in place. What comes out is one block behind what went in.</summary>
    public void Process(Span<double> block)
    {
        _before.AsSpan().CopyTo(_re);
        block.CopyTo(_re.AsSpan(BlockSize));
        block.CopyTo(_before);
        for (var i = 0; i < Size; i++) _re[i] *= _window[i];
        Array.Clear(_im);
        _fft.Forward(_re, _im);

        var floor = Math.Pow(10, FloorDb / 20);
        for (var b = 0; b < Bins; b++)
        {
            var power = _re[b] * _re[b] + _im[b] * _im[b];

            _smoothed[b] = _started ? _smoothed[b] * NoiseSmoothing + power * (1 - NoiseSmoothing) : power;
            _least[b] = !_started || _smoothed[b] < _least[b] ? _smoothed[b] : _least[b] * NoiseRise;

            var noise = _least[b] * MinimumToAverage;

            if (noise <= 0)
            {
                _gain[b] = 1;
                _lastRatio[b] = 0;
            }
            else
            {
                var ratio = power / noise;
                var clean = Carry * _gain[b] * _gain[b] * _lastRatio[b] + (1 - Carry) * Math.Max(ratio - 1, 0);
                _gain[b] = Math.Max(clean / (1 + clean), floor);
                _lastRatio[b] = ratio;
            }

            _re[b] *= _gain[b];
            _im[b] *= _gain[b];
        }
        _started = true;

        for (var b = 1; b < BlockSize; b++)
        {
            _re[Size - b] = _re[b];
            _im[Size - b] = -_im[b];
        }
        _fft.Inverse(_re, _im);

        for (var i = 0; i < BlockSize; i++)
        {
            block[i] = _tail[i] + _re[i] * _window[i];
            _tail[i] = _re[BlockSize + i] * _window[BlockSize + i];
        }
    }

    /// <summary>A fresh press: what was being said is over, though what the noise sounds like has not changed.</summary>
    public void StartTalk()
    {
        Array.Clear(_before);
        Array.Clear(_tail);
        Array.Fill(_gain, 1.0);
        Array.Clear(_lastRatio);
    }
}

/// <summary>
/// Decides from how loud the microphone is whether someone is talking: the standard client's voice activation. Open at
/// the first frame over the level, and held open a moment after the last, or the ends of words get cut off.
/// </summary>
public sealed class MumbleVoiceGate
{
    /// <summary>400 ms of frames: longer than the gaps between words, shorter than a pause between thoughts.</summary>
    public const int HoldFrames = 20;

    private int _sinceLoud = HoldFrames;

    public bool IsOpen => _sinceLoud < HoldFrames;

    /// <summary>Takes one frame's level, and says whether it is open now.</summary>
    public bool Update(double levelDb, double thresholdDb)
    {
        _sinceLoud = levelDb >= thresholdDb ? 0 : Math.Min(_sinceLoud + 1, HoldFrames);
        return IsOpen;
    }

    public void Close() => _sinceLoud = HoldFrames;

    public static double LevelOf(ReadOnlySpan<short> frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += (double)s * s;
        return sum <= frame.Length ? -96 : 10 * Math.Log10(sum / frame.Length / (32768.0 * 32768.0));
    }
}

/// <summary>
/// What runs on the microphone before anything else does: echo cancelling and noise suppression, as asked for. Noise
/// suppression puts it about 20 ms later than otherwise, which is why it is not in the way at all when off.
/// </summary>
public sealed class MumbleVoiceCleanup
{
    private const int Block = MumbleNoiseSuppressor.BlockSize, Frame = MumbleEchoCanceller.FrameSamples;

    /// <summary>A second and a bit of what was played: far more than is ever waiting, and a margin for a device's hiccups.</summary>
    private const int FarCapacity = 1 << 16;

    /// <summary>Where reading starts when the microphone opens, until there has been time to see how the devices deliver.</summary>
    private const int FarLag = Frame * 2;

    /// <summary>
    /// How much has been played beyond where reading is, at the tightest moment, before a frame is taken. A frame, and what
    /// a real microphone's tightest second was seen to vary by, handing over 50 ms at a time. ⚠️ Not much more: what is heard
    /// has to be compared with what came out before it, and reading further back than the devices' own delay puts an echo
    /// ahead of the sound that made it, where nothing can reach it.
    /// </summary>
    private const int FarAhead = Frame + 1024;

    /// <summary>The first look at that, about a second: enough to start from, and soon.</summary>
    private const int FirstLookFrames = 100;

    /// <summary>
    /// Later looks, about 16 seconds. ⚠️ Short ones vary by far more than clocks part: on a real microphone, 1000 samples
    /// from one second to the next with nothing wrong. Moving for that would disturb it over nothing.
    /// </summary>
    private const int LaterLookFrames = 1600;

    /// <summary>Moved from where the looks began by more than this, in two looks running, and it is real: two clocks parting, or samples lost.</summary>
    private const int FarMoved = 480;

    private readonly MumbleNoiseSuppressor _noise = new();
    private MumbleEchoCanceller? _echo;
    private bool _cancelEcho;

    private readonly object _farGate = new();
    private readonly short[] _farRing = new short[FarCapacity];
    private readonly short[] _farFrame = new short[Frame];
    private long _farWritten, _farRead, _leastAhead = long.MaxValue, _settledAhead = long.MinValue, _lastMoved;
    private int _framesLooked;
    private bool _inStep;

    private readonly double[] _in = new double[Block + 4096];
    private readonly double[] _out = new double[Block * 8 + 4096];
    private readonly double[] _block = new double[Block];
    private int _inCount, _outCount;

    /// <summary>Whether what was played is taken out of the microphone. Stays off where <see cref="MumbleEchoCanceller.Available"/> says it must.</summary>
    public bool CancelEcho
    {
        get => _cancelEcho;
        set
        {
            _cancelEcho = value && MumbleEchoCanceller.Available;
            if (_cancelEcho) _echo ??= new MumbleEchoCanceller();
        }
    }

    public bool ReduceNoise { get; set; }

    public bool IsOn => CancelEcho || ReduceNoise;

    /// <summary>
    /// How much has to have gone from a frame for what is left not to be anyone talking: 20 dB. ⚠️ Well clear of a voice,
    /// which it leaves, even one quieter than the echo over it: a looser test of ours, on a filter of our own, took a real
    /// voice for echo and chopped it.
    /// </summary>
    private const double MostlyEchoRatio = 0.01;

    /// <summary>
    /// Frames an echo is taken to linger after, 200 ms: the room's tail is too quiet to show as much taken out, and on its
    /// own would look like someone talking. Ended at once if what is left gets louder, which an echo's tail does not.
    /// </summary>
    private const int EchoHoldFrames = 10;
    private const double VoiceRise = 4;   // 6 dB

    private double _leftByEcho;
    private int _echoHold;

    /// <summary>Whether the frame just handed back had most of it taken out as echo, or was the tail of that, with no one talking under it.</summary>
    public bool LastFrameWasEcho { get; private set; }

    /// <summary>What has just gone to the speakers. On the sound device's thread, whether anyone is talking or not.</summary>
    public void Played(ReadOnlySpan<short> samples)
    {
        if (!CancelEcho) return;

        lock (_farGate)
        {
            foreach (var s in samples)
            {
                _farRing[(int)(_farWritten & (FarCapacity - 1))] = s;
                _farWritten++;
            }
        }
    }

    /// <summary>
    /// A press of a talk key. ⚠️ With echo cancelling on, nothing starts over: the microphone has been running all along,
    /// sample for sample in step with what was played, and what was learned depends on that staying exactly as it is.
    /// </summary>
    public void NewPress()
    {
        if (!CancelEcho) StartTalk();
    }

    /// <summary>The microphone starting.</summary>
    public void StartTalk()
    {
        _inCount = 0;
        _echoHold = 0;
        LastFrameWasEcho = false;
        _noise.StartTalk();

        // A block ahead, so a whole frame can always be handed back: frames and blocks never fall further apart than that.
        Array.Clear(_out, 0, Block);
        _outCount = Block;

        if (!CancelEcho) return;

        lock (_farGate)
        {
            _farRead = Math.Max(0, _farWritten - FarLag);
            _leastAhead = long.MaxValue;
            _settledAhead = long.MinValue;
            _lastMoved = 0;
            _framesLooked = 0;
            _inStep = false;
        }
    }

    /// <summary>One frame of microphone, in place, a whole number of 10 ms. Fast, as it runs on the sound device's thread.</summary>
    public void Process(Span<short> frame)
    {
        LastFrameWasEcho = false;
        if (CancelEcho && _echo is { } echo)
        {
            double before = 0, after = 0;
            for (var at = 0; at + Frame <= frame.Length; at += Frame)
            {
                var part = frame.Slice(at, Frame);
                foreach (var s in part) before += (double)s * s;
                lock (_farGate) ReadFar();
                echo.Process(_farFrame, part);
                foreach (var s in part) after += (double)s * s;
            }

            if (after < before * MostlyEchoRatio)
            {
                _echoHold = EchoHoldFrames;
                _leftByEcho = Math.Max(after, 1);
                LastFrameWasEcho = true;
            }
            else if (_echoHold > 0 && after < _leftByEcho * VoiceRise)
            {
                _echoHold--;
                LastFrameWasEcho = true;
            }
            else _echoHold = 0;
        }

        if (ReduceNoise) ReduceNoiseIn(frame);
    }

    private void ReduceNoiseIn(Span<short> frame)
    {
        foreach (var s in frame) _in[_inCount++] = s / 32768.0;

        var taken = 0;
        while (_inCount - taken >= Block)
        {
            _in.AsSpan(taken, Block).CopyTo(_block);
            taken += Block;

            _noise.Process(_block);

            _block.AsSpan().CopyTo(_out.AsSpan(_outCount));
            _outCount += Block;
        }

        _in.AsSpan(taken, _inCount - taken).CopyTo(_in);
        _inCount -= taken;

        var count = Math.Min(frame.Length, _outCount);
        for (var i = 0; i < count; i++) frame[i] = (short)Math.Clamp(Math.Round(_out[i] * 32768.0), short.MinValue, short.MaxValue);
        frame[count..].Clear();

        _out.AsSpan(count, _outCount - count).CopyTo(_out);
        _outCount -= count;
    }

    /// <summary>
    /// The next frame of what was played, under the lock. ⚠️ One for each of microphone, always, even if some of it has not
    /// been played yet, which is silence: holding back would move every later frame for good.
    /// </summary>
    private void ReadFar()
    {
        if (_farWritten - _farRead > FarCapacity - Frame) _farRead = _farWritten - FarAhead;

        _leastAhead = Math.Min(_leastAhead, _farWritten - _farRead);
        if (++_framesLooked >= (_inStep ? LaterLookFrames : FirstLookFrames)) KeepInStep();

        for (var i = 0; i < Frame; i++)
        {
            var at = _farRead + i;
            _farFrame[i] = at >= 0 && at < _farWritten ? _farRing[(int)(at & (FarCapacity - 1))] : (short)0;
        }
        _farRead += Frame;
    }

    /// <summary>
    /// Speakers and microphone are two streams, each at its own pace and in its own bursts, on clocks that are never quite
    /// the same. So every so often: how far ahead was what was played, at the tightest moment? ⚠️ Moving disturbs what was
    /// learned, so it is done first, then only for what has really changed.
    /// </summary>
    private void KeepInStep()
    {
        var least = _leastAhead;
        _leastAhead = long.MaxValue;
        _framesLooked = 0;

        if (!_inStep)
        {
            _farRead += least - FarAhead;
            _inStep = true;
            return;
        }

        if (_settledAhead == long.MinValue)
        {
            _settledAhead = least;
            return;
        }

        var moved = least - _settledAhead;
        if (Math.Abs(moved) > FarMoved && Math.Abs(_lastMoved) > FarMoved && Math.Sign(moved) == Math.Sign(_lastMoved))
        {
            _farRead += moved;
            moved = 0;
        }
        _lastMoved = moved;
    }
}
