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
/// Takes what came out of the speakers back out of what the microphone heard.
///
/// <para>A filter learns the way sound gets from the speakers to the microphone, and subtracts its guess. It is split
/// into blocks and worked in frequency, the way Speex and the standard client's own do it, as a filter this long
/// would otherwise be far too slow to run sample by sample.</para>
///
/// <para>⚠️ Someone talking over the echo looks to a learning filter like echo it got wrong, and left to itself it goes
/// astray and starts adding echo. Two things stop that. How far it moves depends on how much of what it got wrong is
/// really echo (see <see cref="Learn"/>), and of two copies, the one heard only takes on what the other has learned when
/// that is far better (see <see cref="ChooseFilter"/>). The way Speex's echo canceller does it, on both counts.</para>
/// </summary>
public sealed class MumbleEchoCanceller
{
    public const int BlockSize = 512;
    private const int Size = BlockSize * 2, Bins = BlockSize + 1;

    /// <summary>About 430 ms: from speakers to microphone by way of both sound devices' own buffering, which is most of it.</summary>
    private const int Partitions = 40;

    /// <summary>How quickly the error's and the echo's spectra are followed: about the block's own length in seconds.</summary>
    private const double SpectrumFollow = (double)BlockSize / MumbleMixer.SampleRate;

    private const double LeakFollow = 2.0 * BlockSize / MumbleMixer.SampleRate, LeakMost = 0.5 * BlockSize / MumbleMixer.SampleRate;

    /// <summary>However good the filter looks, some echo is always assumed left: real rooms never hold still.</summary>
    private const double LeastLeak = 0.005;

    /// <summary>How quickly it learns before it knows anything. A filter this long takes seconds to learn at Speex's quarter.</summary>
    private const double FirstLearningRate = 0.4;

    private const double Tiny = 1e-12, FarFloor = 1e-4 * Size;

    /// <summary>Below this nothing is playing, about -70 dBFS, and there is nothing to learn from.</summary>
    private const double SilentPower = 1e-7;

    private readonly MumbleFft _fft = new(Size);

    // One spectrum per block of what was played, newest at _newest. Real and imaginary, by partition, by bin.
    private readonly double[][] _farRe = NewPartitions(), _farIm = NewPartitions();
    private readonly double[][] _learnRe = NewPartitions(), _learnIm = NewPartitions();
    private readonly double[][] _heardRe = NewPartitions(), _heardIm = NewPartitions();
    private readonly double[] _farPower = new double[Bins];
    private readonly double[] _farOld = new double[BlockSize];
    private readonly double[] _re = new double[Size], _im = new double[Size];
    private readonly double[] _echoPower = new double[Bins];
    private readonly double[] _echoNow = new double[Bins], _errorSlow = new double[Bins], _echoSlow = new double[Bins], _rate = new double[Bins];
    private int _newest, _quietBlocks = Partitions + 1, _rotating;
    private double _betterFast, _betterSlow, _spreadFast, _spreadSlow;
    private double _leakTogether, _leakMoves, _leak = 1, _learned;
    private bool _settled;

    private static double[][] NewPartitions()
    {
        var parts = new double[Partitions][];
        for (var i = 0; i < Partitions; i++) parts[i] = new double[Bins];
        return parts;
    }

    /// <summary>How strong the echo was thought to be just now, bin by bin, for what follows to take out what this could not.</summary>
    public ReadOnlySpan<double> EchoPower => _echoPower;

    /// <summary>Takes one block of what was played, in step with the block of microphone that goes with it.</summary>
    public void Far(ReadOnlySpan<double> block)
    {
        double energy = 0;
        foreach (var s in block) energy += s * s;
        var silent = energy / BlockSize < SilentPower;
        _quietBlocks = silent ? Math.Min(_quietBlocks + 1, Partitions + 1) : 0;

        // Taking the oldest out of the running total of how much there is to learn from, and the newest in.
        _newest = (_newest + Partitions - 1) % Partitions;
        double[] re = _farRe[_newest], im = _farIm[_newest];
        for (var b = 0; b < Bins; b++) _farPower[b] = Math.Max(0, _farPower[b] - (re[b] * re[b] + im[b] * im[b]));

        if (_quietBlocks > Partitions)
        {
            Array.Clear(re);
            Array.Clear(im);
            block.CopyTo(_farOld);
            return;
        }

        _farOld.AsSpan().CopyTo(_re);
        block.CopyTo(_re.AsSpan(BlockSize));
        Array.Clear(_im);
        block.CopyTo(_farOld);
        _fft.Forward(_re, _im);

        for (var b = 0; b < Bins; b++)
        {
            re[b] = _re[b];
            im[b] = _im[b];
            _farPower[b] += re[b] * re[b] + im[b] * im[b];
        }
    }

    /// <summary>Takes the echo out of one block of microphone, in place.</summary>
    public void Near(Span<double> block)
    {
        // Nothing has been played for as long as an echo could last: there is nothing to take out.
        if (_quietBlocks > Partitions)
        {
            Array.Clear(_echoPower);
            return;
        }

        Span<double> heardEcho = stackalloc double[BlockSize];
        Span<double> learnEcho = stackalloc double[BlockSize];

        Estimate(_heardRe, _heardIm, heardEcho, keepPower: true);
        Estimate(_learnRe, _learnIm, learnEcho, keepPower: false);

        double heardError = 0, learnError = 0, between = 0, echoEnergy = 0, errorWithEcho = 0, farEnergy = 0;
        for (var i = 0; i < BlockSize; i++)
        {
            var a = block[i] - heardEcho[i];
            var b = block[i] - learnEcho[i];
            heardError += a * a;
            learnError += b * b;
            between += (a - b) * (a - b);
            echoEnergy += learnEcho[i] * learnEcho[i];
            errorWithEcho += b * learnEcho[i];
            farEnergy += _farOld[i] * _farOld[i];
        }

        if (_quietBlocks == 0) Learn(block, learnEcho, learnError, echoEnergy, errorWithEcho, farEnergy);

        for (var i = 0; i < BlockSize; i++) block[i] -= heardEcho[i];

        // What is heard now would be what a little less of the voice is, so what the leak says is left goes to the next stage.
        var left = Math.Min(2 * _leak, 0.5);
        for (var b = 0; b < Bins; b++) _echoPower[b] *= left;

        ChooseFilter(heardError, learnError, between);
    }

    /// <summary>
    /// ⚠️ Whether the learning copy really is better. Not simply which has less error: with someone talking the error is
    /// mostly them, and a filter bent out of shape can fit a little of a voice and come out ahead, then be heard adding
    /// echo. The difference has to be large against how far apart the two are, over a short time or a longer one.
    /// </summary>
    private void ChooseFilter(double heardError, double learnError, double between)
    {
        var better = heardError - learnError;
        var spread = heardError * between;

        _betterFast = 0.6 * _betterFast + 0.4 * better;
        _betterSlow = 0.85 * _betterSlow + 0.15 * better;
        _spreadFast = 0.36 * _spreadFast + 0.16 * spread;
        _spreadSlow = 0.7225 * _spreadSlow + 0.0225 * spread;

        if (better * Math.Abs(better) > spread
            || _betterFast * Math.Abs(_betterFast) > 0.5 * _spreadFast
            || _betterSlow * Math.Abs(_betterSlow) > 0.25 * _spreadSlow)
        {
            Copy(_learnRe, _learnIm, _heardRe, _heardIm);
            _betterFast = _betterSlow = _spreadFast = _spreadSlow = 0;
        }
        else if (-better * Math.Abs(better) > 4 * spread
                 || -_betterFast * Math.Abs(_betterFast) > 4 * _spreadFast
                 || -_betterSlow * Math.Abs(_betterSlow) > 4 * _spreadSlow)
        {
            // Gone astray: back to what is working.
            Copy(_heardRe, _heardIm, _learnRe, _learnIm);
            _betterFast = _betterSlow = _spreadFast = _spreadSlow = 0;
        }
    }

    private void Estimate(double[][] filterRe, double[][] filterIm, Span<double> echo, bool keepPower)
    {
        Array.Clear(_re);
        Array.Clear(_im);

        for (var p = 0; p < Partitions; p++)
        {
            var at = (_newest + p) % Partitions;
            double[] xr = _farRe[at], xi = _farIm[at], wr = filterRe[p], wi = filterIm[p];
            for (var b = 0; b < Bins; b++)
            {
                _re[b] += wr[b] * xr[b] - wi[b] * xi[b];
                _im[b] += wr[b] * xi[b] + wi[b] * xr[b];
            }
        }

        if (keepPower)
            for (var b = 0; b < Bins; b++) _echoPower[b] = _re[b] * _re[b] + _im[b] * _im[b];

        // Only half is worked out above: the rest of a real signal's spectrum mirrors it.
        for (var b = 1; b < BlockSize; b++)
        {
            _re[Size - b] = _re[b];
            _im[Size - b] = -_im[b];
        }

        _fft.Inverse(_re, _im);
        _re.AsSpan(BlockSize, BlockSize).CopyTo(echo);
    }

    /// <summary>
    /// ⚠️ How far to move is the whole of it. What is left after the filter is echo it got wrong plus anyone talking, and
    /// only the first says anything about the filter. So first: how much of the error rises and falls with the echo, which
    /// a voice does not. From that, each frequency moves by the share of its error that is really left-over echo, and
    /// hardly at all while someone talks, which is the way Speex's echo canceller goes about it. Before that, it is only
    /// held back by how much was played against how much was heard.
    /// </summary>
    private void Learn(ReadOnlySpan<double> block, ReadOnlySpan<double> echo, double errorEnergy, double echoEnergy, double errorWithEcho, double farEnergy)
    {
        // The echo's own spectrum, then the error's, each from just this block.
        _re.AsSpan(0, BlockSize).Clear();
        echo.CopyTo(_re.AsSpan(BlockSize));
        Array.Clear(_im);
        _fft.Forward(_re, _im);
        for (var b = 0; b < Bins; b++) _echoNow[b] = _re[b] * _re[b] + _im[b] * _im[b];

        _re.AsSpan(0, BlockSize).Clear();
        for (var i = 0; i < BlockSize; i++) _re[BlockSize + i] = block[i] - echo[i];
        Array.Clear(_im);
        _fft.Forward(_re, _im);

        var errorRe = new Span<double>(_re, 0, Bins);
        var errorIm = new Span<double>(_im, 0, Bins);

        // How the error moves with the echo, from block to block.
        double together = 0, echoMoves = 0;
        for (var b = 0; b < Bins; b++)
        {
            var error = errorRe[b] * errorRe[b] + errorIm[b] * errorIm[b];
            _errorSlow[b] += SpectrumFollow * (error - _errorSlow[b]);
            _echoSlow[b] += SpectrumFollow * (_echoNow[b] - _echoSlow[b]);
            var e = error - _errorSlow[b];
            var y = _echoNow[b] - _echoSlow[b];
            together += e * y;
            echoMoves += y * y;
        }
        echoMoves = Math.Sqrt(echoMoves);
        if (echoMoves > 0) together /= echoMoves;

        var follow = Math.Min(LeakFollow * echoEnergy, LeakMost * errorEnergy) / (errorEnergy + Tiny);
        _leakTogether += follow * (together - _leakTogether);
        _leakMoves += follow * (echoMoves - _leakMoves);

        var leak = _leakMoves > Tiny ? Math.Clamp(_leakTogether / _leakMoves, LeastLeak, 1) : LeastLeak;
        _leak = leak;

        // The share of the whole error thought to be echo. At least as much as lines up with it right now.
        var share = (0.0001 * farEnergy + 3 * leak * echoEnergy) / (errorEnergy + Tiny);
        share = Math.Min(Math.Max(share, errorWithEcho * errorWithEcho / (Tiny + errorEnergy * echoEnergy)), 0.5);

        // Getting started, with nothing yet to judge by. Held back only by how much was played against how much was heard.
        var starting = 0.0;
        if (!_settled)
        {
            starting = Math.Min(FirstLearningRate, FirstLearningRate * farEnergy / (errorEnergy + Tiny));
            _learned += starting;
            if (_learned > Partitions && leak > 0.03) _settled = true;
        }

        for (var b = 0; b < Bins; b++)
        {
            double rate;
            if (_settled)
            {
                var error = errorRe[b] * errorRe[b] + errorIm[b] * errorIm[b] + Tiny;
                var left = Math.Min(leak * _echoNow[b], 0.5 * error);
                rate = (0.7 * left + 0.3 * share * error) / error;
            }
            else rate = starting;

            _rate[b] = rate / (_farPower[b] + FarFloor);
        }

        for (var p = 0; p < Partitions; p++)
        {
            var at = (_newest + p) % Partitions;
            double[] xr = _farRe[at], xi = _farIm[at], wr = _learnRe[p], wi = _learnIm[p];
            for (var b = 0; b < Bins; b++)
            {
                wr[b] += _rate[b] * (xr[b] * errorRe[b] + xi[b] * errorIm[b]);
                wi[b] += _rate[b] * (xr[b] * errorIm[b] - xi[b] * errorRe[b]);
            }
        }

        KeepLength(_learnRe[_rotating], _learnIm[_rotating]);
        _rotating = (_rotating + 1) % Partitions;
    }

    /// <summary>
    /// A block's filter can only be half as long as the transform it is worked in, and learning drifts away from that.
    /// One partition per block is enough to hold it to that, which is what makes a filter this long affordable.
    /// </summary>
    private void KeepLength(double[] wr, double[] wi)
    {
        wr.AsSpan().CopyTo(_re);
        wi.AsSpan().CopyTo(_im);
        for (var b = 1; b < BlockSize; b++)
        {
            _re[Size - b] = _re[b];
            _im[Size - b] = -_im[b];
        }
        _fft.Inverse(_re, _im);
        _re.AsSpan(BlockSize, BlockSize).Clear();
        Array.Clear(_im);
        _fft.Forward(_re, _im);
        _re.AsSpan(0, Bins).CopyTo(wr);
        _im.AsSpan(0, Bins).CopyTo(wi);
    }

    private static void Copy(double[][] fromRe, double[][] fromIm, double[][] toRe, double[][] toIm)
    {
        for (var p = 0; p < Partitions; p++)
        {
            fromRe[p].AsSpan().CopyTo(toRe[p]);
            fromIm[p].AsSpan().CopyTo(toIm[p]);
        }
    }
}

/// <summary>
/// Quietens what is left, frequency by frequency, leaving through what stands well above it: steady background noise,
/// and with echo cancelling on, what the filter could not remove, as speakers distort in ways no filter can follow.
/// A Wiener filter over overlapping windows.
/// </summary>
public sealed class MumbleNoiseSuppressor
{
    public const int BlockSize = MumbleEchoCanceller.BlockSize;
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

    /// <summary>How far what is only echo is turned down. Further than noise, as nobody wants to hear any of it.</summary>
    private const double EchoFloorDb = -40;

    /// <summary>How quickly what is thought to be left of an echo fades: echoes tail off rather than stop.</summary>
    private const double EchoFade = 0.6;

    private readonly MumbleFft _fft = new(Size);
    private readonly double[] _window = new double[Size];
    private readonly double[] _before = new double[BlockSize], _tail = new double[BlockSize];
    private readonly double[] _re = new double[Size], _im = new double[Size];
    private readonly double[] _smoothed = new double[Bins], _least = new double[Bins];
    private readonly double[] _gain = new double[Bins], _lastRatio = new double[Bins];
    private readonly double[] _echoLeft = new double[Bins];
    private bool _started;

    /// <summary>Whether steady background noise is taken out. Without it only what is left of an echo is.</summary>
    public bool ReduceNoise { get; set; } = true;

    public MumbleNoiseSuppressor()
    {
        for (var i = 0; i < Size; i++) _window[i] = Math.Sin(Math.PI * (i + 0.5) / Size);
        Array.Fill(_gain, 1.0);
    }

    /// <summary>One block, in place. What comes out is one block behind what went in.</summary>
    /// <param name="echoPower">What echo cancelling thought it removed, bin by bin. Empty when it is off.</param>
    public void Process(Span<double> block, ReadOnlySpan<double> echoPower)
    {
        _before.AsSpan().CopyTo(_re);
        block.CopyTo(_re.AsSpan(BlockSize));
        block.CopyTo(_before);
        for (var i = 0; i < Size; i++) _re[i] *= _window[i];
        Array.Clear(_im);
        _fft.Forward(_re, _im);

        double noiseFloor = Math.Pow(10, FloorDb / 20), echoFloor = Math.Pow(10, EchoFloorDb / 20);
        for (var b = 0; b < Bins; b++)
        {
            var power = _re[b] * _re[b] + _im[b] * _im[b];

            _smoothed[b] = _started ? _smoothed[b] * NoiseSmoothing + power * (1 - NoiseSmoothing) : power;
            _least[b] = !_started || _smoothed[b] < _least[b] ? _smoothed[b] : _least[b] * NoiseRise;

            _echoLeft[b] = Math.Max(_echoLeft[b] * EchoFade, b < echoPower.Length ? echoPower[b] : 0);

            var noise = ReduceNoise ? _least[b] * MinimumToAverage : 0;
            var unwanted = noise + _echoLeft[b];

            if (unwanted <= 0)
            {
                _gain[b] = 1;
                _lastRatio[b] = 0;
            }
            else
            {
                var ratio = power / unwanted;
                var clean = Carry * _gain[b] * _gain[b] * _lastRatio[b] + (1 - Carry) * Math.Max(ratio - 1, 0);
                var floor = (noise * noiseFloor + _echoLeft[b] * echoFloor) / unwanted;
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
        Array.Clear(_echoLeft);
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
/// What runs on the microphone before anything else does: echo cancelling and noise suppression, as asked for, 20 ms in
/// and 20 ms out. About 20 ms later than if neither were on, which is why it is not in the way at all when neither is.
/// </summary>
public sealed class MumbleVoiceCleanup
{
    private const int Block = MumbleEchoCanceller.BlockSize;

    /// <summary>A second and a bit of what was played: far longer than the filter can use, and a margin for a device's hiccups.</summary>
    private const int FarCapacity = 1 << 16;

    /// <summary>How far behind the newest played sound the filter reads, so it is rarely ahead of a sound device delivering in bursts.</summary>
    private const int FarLag = Block * 2;

    /// <summary>How many blocks of history a press starts with: an echo of what was playing just before is still on its way.</summary>
    private const int HistoryBlocks = 41;

    private readonly MumbleEchoCanceller _echo = new();
    private readonly MumbleNoiseSuppressor _noise = new();

    private readonly object _farGate = new();
    private readonly double[] _farRing = new double[FarCapacity];
    private long _farWritten, _farRead;

    private readonly double[] _in = new double[Block + 4096];
    private readonly double[] _out = new double[Block * 8 + 4096];
    private readonly double[] _block = new double[Block];
    private readonly double[] _farBlock = new double[Block];
    private int _inCount, _outCount;

    public bool CancelEcho { get; set; }
    public bool ReduceNoise { get; set; }

    public bool IsOn => CancelEcho || ReduceNoise;

    /// <summary>What has just gone to the speakers. On the sound device's thread, whether anyone is talking or not.</summary>
    public void Played(ReadOnlySpan<short> samples)
    {
        if (!CancelEcho) return;

        lock (_farGate)
        {
            foreach (var s in samples)
            {
                _farRing[(int)(_farWritten & (FarCapacity - 1))] = s / 32768.0;
                _farWritten++;
            }
        }
    }

    /// <summary>A fresh press. Starts from what has been playing, so echo of it is caught from the first word.</summary>
    public void StartTalk()
    {
        _inCount = 0;
        _noise.StartTalk();

        // A block ahead, so a whole frame can always be handed back: frames and blocks never fall further apart than that.
        Array.Clear(_out, 0, Block);
        _outCount = Block;

        if (!CancelEcho) return;

        lock (_farGate)
        {
            _farRead = Math.Max(0, _farWritten - FarLag - Block * HistoryBlocks);
            while (_farWritten - _farRead - FarLag >= Block)
            {
                ReadFar();
                _echo.Far(_farBlock);
            }
        }
    }

    /// <summary>One frame of microphone, in place. Fast, as it runs on the sound device's thread.</summary>
    public void Process(Span<short> frame)
    {
        foreach (var s in frame) _in[_inCount++] = s / 32768.0;

        var taken = 0;
        while (_inCount - taken >= Block)
        {
            _in.AsSpan(taken, Block).CopyTo(_block);
            taken += Block;

            var echoPower = ReadOnlySpan<double>.Empty;
            if (CancelEcho)
            {
                lock (_farGate) ReadFar();
                _echo.Far(_farBlock);
                _echo.Near(_block);
                echoPower = _echo.EchoPower;
            }

            _noise.ReduceNoise = ReduceNoise;
            _noise.Process(_block, echoPower);

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

    /// <summary>The next block of what was played. Silence for what has not been played yet, and a jump forward if far behind. Under the lock.</summary>
    private void ReadFar()
    {
        var waiting = _farWritten - _farRead;
        if (waiting > FarCapacity - Block) _farRead = _farWritten - FarLag - Block;

        if (_farWritten - _farRead < Block)
        {
            Array.Clear(_farBlock);
            return;
        }

        for (var i = 0; i < Block; i++) _farBlock[i] = _farRing[(int)((_farRead + i) & (FarCapacity - 1))];
        _farRead += Block;
    }
}
