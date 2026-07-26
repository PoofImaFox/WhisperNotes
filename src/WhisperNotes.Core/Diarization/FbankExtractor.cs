using System.Numerics;

namespace WhisperNotes.Core.Diarization;

/// <summary>
/// The taper multiplied into each frame before the transform. Kaldi offers half a dozen; only the
/// two that speaker-embedding models are actually trained against are worth carrying.
/// </summary>
internal enum FbankWindow
{
    /// <summary>
    /// Kaldi's own default, and what sherpa-onnx configures for every speaker model it runs. A Hann
    /// window raised to a fractional power: narrower than Hann, but unlike Hamming it still reaches
    /// exactly zero at both edges, which is the property it was invented for.
    /// </summary>
    Povey,

    /// <summary>
    /// What WeSpeaker's own training pipeline asks torchaudio for, and what its command line uses
    /// for its own checkpoints. Carried because WeSpeaker and sherpa-onnx genuinely disagree here
    /// and a model is best fed the features it was trained on.
    /// </summary>
    Hamming,
}

/// <summary>
/// The kaldi fbank conventions a speaker-embedding model was trained under.
/// </summary>
/// <remarks>
/// Every default here was read off a primary source rather than recalled: kaldi-native-fbank's
/// <c>FrameExtractionOptions</c> / <c>MelBanksOptions</c> / <c>FbankOptions</c>, sherpa-onnx's
/// <c>FeatureExtractorConfig</c> and its <c>scripts/wespeaker/test.py</c>, and WeSpeaker's own
/// <c>dataset/processor.py</c>. The two knobs that actually differ between model families are
/// <see cref="SampleScale"/> and <see cref="SubtractMeanOverTime"/>; the rest are shared.
/// </remarks>
internal sealed record FbankOptions
{
    /// <summary>
    /// WeSpeaker (ResNet34/CAM++ from the sherpa-onnx speaker-recognition zoo). Its ONNX metadata
    /// carries <c>normalize_samples=0</c>, which is sherpa-onnx's flag for "this model was trained
    /// on int16-range audio", and its training pipeline ran mean normalisation over time.
    /// </summary>
    public static FbankOptions WeSpeaker { get; } = new();

    /// <summary>
    /// 3D-Speaker / CAM++ as exported by sherpa-onnx. Its metadata carries
    /// <c>normalize_samples=1</c> — samples stay in -1..1 — together with
    /// <c>feature_normalize_type=global-mean</c>, so the two families differ in exactly two places.
    /// </summary>
    public static FbankOptions ThreeDSpeaker { get; } = new()
    {
        SampleScale = 1f,
    };

    /// <summary>
    /// 16 kHz because every speaker model in the sherpa-onnx zoo declares <c>sample_rate=16000</c>
    /// in its metadata and the extractor is fed by a pipeline that has already resampled.
    /// </summary>
    public int SampleRate { get; init; } = 16000;

    /// <summary>
    /// 80 filters. Not a taste decision: the exported graphs pin their input to <c>[B, T, 80]</c>,
    /// so any other value is rejected by the model rather than merely degrading it.
    /// </summary>
    public int MelBins { get; init; } = 80;

    /// <summary>
    /// 20 Hz, kaldi's default. Discards the rumble below the lowest voiced pitch, which carries no
    /// speaker information but plenty of handling noise.
    /// </summary>
    public float LowFrequency { get; init; } = 20f;

    /// <summary>
    /// Kaldi's sign convention: positive is a literal cutoff in Hz, zero or negative is added to
    /// Nyquist. Zero — the full 8 kHz — is kaldi's default, is what WeSpeaker trained under, and is
    /// what sherpa-onnx's own WeSpeaker validation script uses. sherpa-onnx's C++ runtime instead
    /// defaults to -400 (7600 Hz), a convention inherited from lhotse's ASR recipes; that value
    /// belongs to a different lineage than these models, and measured against a three-speaker set
    /// it separates slightly worse, so it is not reproduced here.
    /// </summary>
    public float HighFrequency { get; init; }

    /// <summary>25 ms, kaldi's default: 400 samples at 16 kHz, long enough to resolve pitch.</summary>
    public float FrameLengthMs { get; init; } = 25f;

    /// <summary>10 ms, kaldi's default: 160 samples at 16 kHz, so frames overlap by 60%.</summary>
    public float FrameShiftMs { get; init; } = 10f;

    /// <summary>
    /// 0.97, kaldi's default. A one-tap high-pass that flattens the roughly -6 dB/octave tilt of
    /// voiced speech so the upper filters are not starved of energy.
    /// </summary>
    public float PreemphasisCoefficient { get; init; } = 0.97f;

    /// <summary>
    /// True, kaldi's default. Subtracting the frame's own mean stops a DC bias in the capture chain
    /// from leaking into the lowest mel filters as if it were signal.
    /// </summary>
    public bool RemoveDcOffset { get; init; } = true;

    /// <summary>
    /// Povey, which is kaldi's default and what sherpa-onnx sets for every speaker model. WeSpeaker
    /// trained under Hamming, so <see cref="FbankWindow.Hamming"/> is the more faithful choice on
    /// paper — but swapping them moves speaker separation by less than the measurement noise of a
    /// three-speaker set, and where the evidence is a tie the convention of the surrounding
    /// ecosystem wins.
    /// </summary>
    public FbankWindow Window { get; init; } = FbankWindow.Povey;

    /// <summary>
    /// False, matching sherpa-onnx, which overrides kaldi's default of true for every model it runs
    /// — both in its C++ config and in the validation script it checks these very models with. With
    /// snip-edges off the frame count is a plain rounding of length over shift, frames are centred
    /// on their shift rather than starting at it, and the two frames that overhang either end are
    /// filled by reflecting the audio back on itself instead of being dropped.
    /// </summary>
    public bool SnipEdges { get; init; }

    /// <summary>
    /// What the caller's -1..1 samples are multiplied by before framing. Kaldi has always operated
    /// on int16-range audio, and WeSpeaker's training pipeline duly multiplies by 1 &lt;&lt; 15
    /// before extracting features; sherpa-onnx does the same, spelling it <c>waveform[i] * 32768</c>
    /// for models whose metadata says <c>normalize_samples=0</c>. 32768 rather than 32767 because
    /// that is literally the constant both of them use. Getting it wrong shifts every log-mel value
    /// by 2*ln(32768) ~ 20.8; <see cref="SubtractMeanOverTime"/> happens to absorb exactly that, so
    /// the mistake is invisible with normalisation on and ruinous with it off.
    /// </summary>
    public float SampleScale { get; init; } = 32768f;

    /// <summary>
    /// Mean normalisation over the time axis, per mel bin — mean only, never variance. WeSpeaker's
    /// training pipeline applies <c>apply_cmvn(norm_mean=True, norm_var=False)</c> to every
    /// utterance and its own inference repeats it, so the model has never seen features without it.
    /// sherpa-onnx does the same for 3D-Speaker models but skips it for WeSpeaker ones, because its
    /// WeSpeaker export never writes the <c>feature_normalize_type</c> metadata key that turns it
    /// on. That omission is emphatically not worth reproducing: on three speakers cut into two
    /// second windows, leaving it off moves the equal error rate from 3.8% to 38.7% — every voice
    /// scores about 0.75 against every other, which is exactly the failure that looks like a
    /// working pipeline right up until the clustering has to choose a threshold.
    /// </summary>
    /// <remarks>
    /// A side effect worth knowing: because the scaling in <see cref="SampleScale"/> adds a
    /// constant to every element of the log-mel matrix, this subtraction removes it exactly. With
    /// this on, the two families' conventions converge; with it off, they do not.
    /// </remarks>
    public bool SubtractMeanOverTime { get; init; } = true;
}

/// <summary>
/// Kaldi-style log mel filterbank features, ported from kaldi-native-fbank as sherpa-onnx
/// configures it, because that is the input contract of every speaker-embedding model in the
/// sherpa-onnx zoo. They do not take audio; they take this.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline per frame, in the order kaldi applies it and for the reasons kaldi applies it in
/// that order: lift the frame out of the waveform, remove its DC offset, pre-emphasise, taper,
/// zero-pad to a power of two, transform, take the power spectrum, project onto triangular mel
/// filters, take the natural log with a floor. Then, once, mean-normalise down the time axis.
/// </para>
/// <para>
/// This class is <b>not thread-safe</b>. It keeps the frame, transform and spectrum buffers on the
/// instance so that a several-hundred-frame window costs one allocation rather than several
/// thousand, which is only sound because the calling code embeds one window at a time.
/// </para>
/// </remarks>
internal sealed class FbankExtractor
{
    /// <summary>
    /// The floor kaldi puts under a mel energy before taking its log, so that a digitally silent
    /// frame yields ln(eps) rather than negative infinity. This is C's <c>FLT_EPSILON</c>, the gap
    /// between 1f and the next float — <em>not</em> <see cref="float.Epsilon"/>, which in .NET is
    /// the smallest denormal and would floor at ln(1.4e-45) = -103 instead of -15.94.
    /// </summary>
    private const float MelEnergyFloor = 1.1920928955078125e-7f;

    /// <summary>
    /// The constant in kaldi's mel scale, <c>mel = 1127 * ln(1 + f/700)</c>. Kaldi writes the round
    /// 1127, not the 1127.01048 that falls out of converting HTK's base-10 form exactly, and not
    /// the 2595*log10 form at all. The difference is small but it moves every filter edge.
    /// </summary>
    private const double MelScaleFactor = 1127.0;

    /// <summary>
    /// The exponent kaldi raises its Hann window to: <c>(0.5 - 0.5*cos(2*pi*n/(N-1)))^0.85</c>.
    /// N-1, not N — kaldi's periodic-Hann variant uses N but the Povey window does not.
    /// </summary>
    private const double PoveyExponent = 0.85;

    private readonly FbankOptions _options;
    private readonly int _melBins;
    private readonly int _frameLength;
    private readonly int _frameShift;
    private readonly int _fftLength;
    private readonly int _spectrumBins;
    private readonly double _sampleScale;

    private readonly double[] _window;

    /// <summary>
    /// The filterbank the way kaldi stores it: each mel bin knows the first FFT bin it touches and
    /// carries only the handful of weights that are non-zero. A dense 80x256 matrix would be 95%
    /// zeros and would turn the innermost loop into mostly multiplying by nothing.
    /// </summary>
    private readonly int[] _melOffsets;

    private readonly double[][] _melWeights;

    private readonly int[] _bitReversal;
    private readonly double[] _twiddleReal;
    private readonly double[] _twiddleImaginary;

    private readonly double[] _real;
    private readonly double[] _imaginary;
    private readonly double[] _power;
    private readonly double[] _binMeans;

    public FbankExtractor(FbankOptions? options = null)
    {
        _options = options ?? FbankOptions.WeSpeaker;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.SampleRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MelBins, 3);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.FrameLengthMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.FrameShiftMs);

        _melBins = _options.MelBins;
        _sampleScale = _options.SampleScale;

        // Kaldi truncates rather than rounds here, which at 16 kHz gives the familiar 400 and 160.
        _frameLength = (int)(_options.SampleRate * 0.001 * _options.FrameLengthMs);
        _frameShift = (int)(_options.SampleRate * 0.001 * _options.FrameShiftMs);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_frameLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_frameShift);

        // round_to_power_of_two, kaldi's default: 400 samples become a 512-point transform. The
        // extra 112 samples stay zero, which costs a little frequency smearing and buys a radix-2
        // FFT instead of a 400-point mixed-radix one.
        _fftLength = RoundUpToPowerOfTwo(_frameLength);

        // Kaldi's mel banks span bins 0..N/2-1 and never touch the Nyquist bin, so neither do we.
        _spectrumBins = _fftLength / 2;

        _window = BuildWindow(_options.Window, _frameLength);
        (_melOffsets, _melWeights) = BuildMelBanks(_options, _fftLength, _spectrumBins);
        (_bitReversal, _twiddleReal, _twiddleImaginary) = BuildTransform(_fftLength);

        _real = new double[_fftLength];
        _imaginary = new double[_fftLength];
        _power = new double[_spectrumBins];
        _binMeans = new double[_melBins];
    }

    /// <summary>Number of mel bins per frame.</summary>
    public int Dimensions => _melBins;

    /// <summary>
    /// Computes the feature matrix for one window of 16 kHz mono PCM in -1..1.
    /// Returns [frames][bins] flattened row-major, plus the frame count.
    /// </summary>
    public float[] Compute(ReadOnlySpan<float> pcm16kMono, out int frames)
    {
        frames = FrameCount(pcm16kMono.Length);
        if (frames <= 0)
        {
            frames = 0;
            return [];
        }

        float[] features = new float[frames * _melBins];

        for (int frame = 0; frame < frames; frame++)
        {
            FillFrame(pcm16kMono, frame);
            Transform();
            PowerSpectrum();
            ProjectOntoMel(features.AsSpan(frame * _melBins, _melBins));
        }

        if (_options.SubtractMeanOverTime)
        {
            SubtractPerBinMean(features, frames);
        }

        return features;
    }

    /// <summary>
    /// How many frames kaldi would emit for this many samples, after the caller has said there is
    /// no more audio coming — which for us is always, since we are handed a whole window at once.
    /// </summary>
    private int FrameCount(int samples)
    {
        // Kaldi with snip-edges off would happily emit frames for a 20 ms clip, built almost
        // entirely out of reflected padding. That is a worse answer than none: the embedding would
        // be of the reflection, not of the speaker. Callers filter short segments already, so this
        // only fires on data that was never going to produce a usable vector.
        if (samples < _frameLength)
        {
            return 0;
        }

        return _options.SnipEdges
            ? 1 + ((samples - _frameLength) / _frameShift)
            : (samples + (_frameShift / 2)) / _frameShift;
    }

    /// <summary>
    /// Lifts one frame out of the waveform and runs kaldi's per-frame preparation over it, leaving
    /// the result in the real half of the transform buffer.
    /// </summary>
    private void FillFrame(ReadOnlySpan<float> pcm, int frame)
    {
        // With snip-edges off, frame f is centred on the midpoint of its shift rather than starting
        // at it, which puts the first frame's start at a negative index on purpose.
        int start = _options.SnipEdges
            ? frame * _frameShift
            : (frame * _frameShift) + (_frameShift / 2) - (_frameLength / 2);

        for (int i = 0; i < _frameLength; i++)
        {
            _real[i] = _sampleScale * Sample(pcm, start + i);
        }

        // Kaldi zeroes the whole padded buffer and fills only the first frameLength entries, so the
        // tail is genuine zero-padding rather than whatever the previous frame left behind.
        Array.Clear(_real, _frameLength, _fftLength - _frameLength);
        Array.Clear(_imaginary);

        if (_options.RemoveDcOffset)
        {
            double sum = 0;
            for (int i = 0; i < _frameLength; i++)
            {
                sum += _real[i];
            }

            double mean = sum / _frameLength;
            for (int i = 0; i < _frameLength; i++)
            {
                _real[i] -= mean;
            }
        }

        double preemphasis = _options.PreemphasisCoefficient;
        if (preemphasis != 0)
        {
            // Backwards so each sample sees its untouched predecessor. The first sample has no
            // predecessor inside the frame and kaldi deliberately does not reach into the previous
            // one — it uses the sample itself, i.e. x[0] - c*x[0], so the filter stays a pure
            // function of the frame and frames stay independent of each other.
            for (int i = _frameLength - 1; i > 0; i--)
            {
                _real[i] -= preemphasis * _real[i - 1];
            }

            _real[0] -= preemphasis * _real[0];
        }

        for (int i = 0; i < _frameLength; i++)
        {
            _real[i] *= _window[i];
        }
    }

    /// <summary>
    /// One sample, with kaldi's edge handling: indices off either end reflect back inside, so a
    /// frame that overhangs the window is filled with a mirror image rather than with silence.
    /// A non-finite sample becomes zero, because one NaN would otherwise spread through the FFT
    /// into all eighty bins of the frame and then, via mean normalisation, into the whole matrix.
    /// </summary>
    private static double Sample(ReadOnlySpan<float> pcm, int index)
    {
        int count = pcm.Length;

        while (index < 0 || index >= count)
        {
            index = index < 0 ? -index - 1 : (2 * count) - 1 - index;
        }

        float value = pcm[index];
        return float.IsFinite(value) ? value : 0d;
    }

    /// <summary>
    /// In-place radix-2 decimation-in-time FFT over the whole padded frame, treating it as complex
    /// with a zero imaginary part.
    /// </summary>
    /// <remarks>
    /// A real input of length 512 could be packed into a 256-point complex transform and unpacked
    /// afterwards, for roughly half the work. That trick is not used: it doubles the amount of
    /// index arithmetic that has to be right, and this is not the expensive part of the pipeline —
    /// the model that consumes these features costs far more than the transform that produces them.
    /// A plain complex FFT is the version that can be checked against a brute-force DFT and seen
    /// to be correct.
    /// </remarks>
    private void Transform()
    {
        for (int i = 0; i < _fftLength; i++)
        {
            int j = _bitReversal[i];
            if (j > i)
            {
                (_real[i], _real[j]) = (_real[j], _real[i]);
                (_imaginary[i], _imaginary[j]) = (_imaginary[j], _imaginary[i]);
            }
        }

        for (int length = 2; length <= _fftLength; length <<= 1)
        {
            int half = length >> 1;
            int stride = _fftLength / length;

            for (int start = 0; start < _fftLength; start += length)
            {
                for (int k = 0; k < half; k++)
                {
                    int twiddle = k * stride;
                    double weightReal = _twiddleReal[twiddle];
                    double weightImaginary = _twiddleImaginary[twiddle];

                    int even = start + k;
                    int odd = even + half;

                    double productReal = (_real[odd] * weightReal) - (_imaginary[odd] * weightImaginary);
                    double productImaginary = (_real[odd] * weightImaginary) + (_imaginary[odd] * weightReal);

                    _real[odd] = _real[even] - productReal;
                    _imaginary[odd] = _imaginary[even] - productImaginary;
                    _real[even] += productReal;
                    _imaginary[even] += productImaginary;
                }
            }
        }
    }

    /// <summary>
    /// Squared magnitude, not magnitude: kaldi's <c>use_power</c> defaults to true and every
    /// speaker model in the zoo is trained that way.
    /// </summary>
    private void PowerSpectrum()
    {
        for (int i = 0; i < _spectrumBins; i++)
        {
            _power[i] = (_real[i] * _real[i]) + (_imaginary[i] * _imaginary[i]);
        }
    }

    private void ProjectOntoMel(Span<float> destination)
    {
        for (int bin = 0; bin < _melBins; bin++)
        {
            double[] weights = _melWeights[bin];
            int offset = _melOffsets[bin];
            double energy = 0;

            for (int k = 0; k < weights.Length; k++)
            {
                energy += weights[k] * _power[offset + k];
            }

            // Natural log, floored rather than offset: kaldi's use_log_fbank with its energy_floor
            // left at zero, which means the only protection is the epsilon clamp.
            destination[bin] = MathF.Log(Math.Max((float)energy, MelEnergyFloor));
        }
    }

    /// <summary>
    /// Mean normalisation down the time axis, one mean per mel bin, variance untouched. Traversed
    /// row-major in both passes because the alternative walks an 80-float stride through the whole
    /// matrix eighty times.
    /// </summary>
    private void SubtractPerBinMean(float[] features, int frames)
    {
        Array.Clear(_binMeans);

        for (int frame = 0; frame < frames; frame++)
        {
            int row = frame * _melBins;
            for (int bin = 0; bin < _melBins; bin++)
            {
                _binMeans[bin] += features[row + bin];
            }
        }

        for (int bin = 0; bin < _melBins; bin++)
        {
            _binMeans[bin] /= frames;
        }

        for (int frame = 0; frame < frames; frame++)
        {
            int row = frame * _melBins;
            for (int bin = 0; bin < _melBins; bin++)
            {
                features[row + bin] -= (float)_binMeans[bin];
            }
        }
    }

    private static double[] BuildWindow(FbankWindow shape, int length)
    {
        double[] window = new double[length];

        // Kaldi's single angular step, shared by every window type it offers except the periodic
        // Hann: the denominator is length - 1, so the taper is symmetric and closes on both edges.
        double step = 2 * Math.PI / (length - 1);

        for (int i = 0; i < length; i++)
        {
            double hann = 0.5 - (0.5 * Math.Cos(step * i));
            window[i] = shape switch
            {
                FbankWindow.Hamming => 0.54 - (0.46 * Math.Cos(step * i)),
                _ => Math.Pow(hann, PoveyExponent),
            };
        }

        return window;
    }

    /// <summary>
    /// Kaldi's triangular filterbank: centres evenly spaced in mel space, edges at the neighbouring
    /// centres, weights read off in mel space rather than in Hz so the triangles are symmetric
    /// where it matters. Deliberately not area-normalised — that is HTK's convention and kaldi
    /// only reproduces it under <c>htk_mode</c>, which nothing in this zoo sets.
    /// </summary>
    private static (int[] Offsets, double[][] Weights) BuildMelBanks(
        FbankOptions options,
        int fftLength,
        int spectrumBins)
    {
        double nyquist = 0.5 * options.SampleRate;
        double lowFrequency = options.LowFrequency;

        // Kaldi's sign convention: a positive cutoff is literal, anything else is relative to
        // Nyquist, so the default of 0 means "all the way up".
        double highFrequency = options.HighFrequency > 0
            ? options.HighFrequency
            : nyquist + options.HighFrequency;

        if (lowFrequency < 0 || lowFrequency >= nyquist || highFrequency > nyquist || highFrequency <= lowFrequency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Mel range {lowFrequency}..{highFrequency} Hz does not fit under Nyquist {nyquist} Hz.");
        }

        double binWidth = (double)options.SampleRate / fftLength;
        double melLow = MelScale(lowFrequency);
        double melHigh = MelScale(highFrequency);

        // Divided by bins + 1 rather than bins - 1: the outermost triangles spill past the first
        // and last centre, so there is one more gap than there are filters.
        double melDelta = (melHigh - melLow) / (options.MelBins + 1);

        int[] offsets = new int[options.MelBins];
        double[][] weights = new double[options.MelBins][];
        double[] scratch = new double[spectrumBins];

        for (int bin = 0; bin < options.MelBins; bin++)
        {
            double leftMel = melLow + (bin * melDelta);
            double centreMel = melLow + ((bin + 1) * melDelta);
            double rightMel = melLow + ((bin + 2) * melDelta);

            int first = -1;
            int last = -1;

            for (int i = 0; i < spectrumBins; i++)
            {
                double mel = MelScale(binWidth * i);
                if (mel <= leftMel || mel >= rightMel)
                {
                    continue;
                }

                scratch[i] = mel <= centreMel
                    ? (mel - leftMel) / (centreMel - leftMel)
                    : (rightMel - mel) / (rightMel - centreMel);

                if (first < 0)
                {
                    first = i;
                }

                last = i;
            }

            if (first < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"Mel bin {bin} covers no FFT bin; there are too many bins for this frequency range.");
            }

            offsets[bin] = first;
            weights[bin] = scratch[first..(last + 1)];
            Array.Clear(scratch, first, last + 1 - first);
        }

        return (offsets, weights);
    }

    /// <summary>Kaldi's mel scale, the HTK-style natural-log form.</summary>
    private static double MelScale(double frequency) => MelScaleFactor * Math.Log(1 + (frequency / 700));

    private static (int[] BitReversal, double[] Real, double[] Imaginary) BuildTransform(int length)
    {
        int[] reversal = new int[length];
        int bits = BitOperations.Log2((uint)length);

        for (int i = 0; i < length; i++)
        {
            int reversed = 0;
            for (int bit = 0; bit < bits; bit++)
            {
                reversed |= ((i >> bit) & 1) << (bits - 1 - bit);
            }

            reversal[i] = reversed;
        }

        // Forward transform, so the twiddles run clockwise: exp(-2*pi*i*k/N).
        double[] real = new double[length / 2];
        double[] imaginary = new double[length / 2];

        for (int k = 0; k < length / 2; k++)
        {
            double angle = -2 * Math.PI * k / length;
            real[k] = Math.Cos(angle);
            imaginary[k] = Math.Sin(angle);
        }

        return (reversal, real, imaginary);
    }

    private static int RoundUpToPowerOfTwo(int value) => (int)BitOperations.RoundUpToPowerOf2((uint)value);
}
