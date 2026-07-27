using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Generic decoder counterpart to <see cref="AnalogFmSstvEncoder"/>, using a ported
/// <see cref="PllFmDemodulator"/> (see that type's doc comment) run continuously over the incoming
/// sample stream. Once VIS reveals the mode, per-line decoding is delegated to a
/// <see cref="IScanlineDecoder"/> selected via <see cref="ScanlineCodecFactory"/> — the decoder
/// can't know the family upfront the way the encoder does, since VIS detection is itself part of
/// this shared, family-agnostic shell. Scope note (Phase 1): this reconstructs scanlines using the
/// mode's *nominal* timing — it does not independently re-search for each line's sync pulse (the
/// legacy AFC/sync state machine in `CSSTVDEM` is a separate, larger piece of work not yet ported),
/// and does not yet implement the clock-drift/slant correction described in spec/06-sstv-dsp.md.
/// That's fine for the same-process, no-channel-noise round-trip this proves; real captured audio
/// (with clock drift between transmitter and receiver sound cards) needs both of those added
/// before this is usable on the air.
/// </summary>
public sealed class AnalogFmSstvDecoder : ISstvDecoder
{
    // Covers every frequency this decoder needs to track: VIS tones (1100-1900Hz) and per-line
    // sync/porch/separator/luminance (1200-2300Hz). See PllFmDemodulator's doc comment — legacy
    // switches tracking bandwidth between VIS detection and image data; this port uses one fixed
    // range for both, which is simpler and a documented Phase 1 simplification.
    private const double DemodulatorLowHz = 1100;
    private const double DemodulatorHighHz = 2300;

    private readonly int _sampleRate;
    private readonly List<double> _demodulatedFrequencies = [];
    private readonly PllFmDemodulator _demodulator;

    private int _consumedSamples;
    private SstvModeDefinition? _mode;
    private IScanlineDecoder? _lineDecoder;
    private Rgb24[]? _pixels;
    private int _nextLine;

    public AnalogFmSstvDecoder(int sampleRate = 11025)
    {
        _sampleRate = sampleRate;
        _demodulator = new PllFmDemodulator(sampleRate, DemodulatorLowHz, DemodulatorHighHz);
    }

    public event Action<DecodedImageUpdate>? LineDecoded;

    public event Action<SstvModeDefinition>? ModeDetected;

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
        var span = samples.Span;
        for (var i = 0; i < span.Length; i++)
        {
            _demodulatedFrequencies.Add(_demodulator.ProcessSample(span[i]));
        }

        TryProcessBuffer();
    }

    private void TryProcessBuffer()
    {
        if (_mode is null && !TryDecodeVisHeader())
        {
            return;
        }

        var mode = _mode!;
        var lineDecoder = _lineDecoder!;
        var pixels = _pixels!;
        var lineSampleCount = (int)Math.Round(mode.LineDurationMs / 1000.0 * _sampleRate);

        while (_nextLine < mode.ImageHeight && _demodulatedFrequencies.Count - _consumedSamples >= lineSampleCount)
        {
            lineDecoder.DecodeLine(mode, _sampleRate, _consumedSamples, _nextLine, AverageFrequencyInWindow, pixels);
            _consumedSamples += lineSampleCount;

            LineDecoded?.Invoke(new DecodedImageUpdate(_nextLine, new MutableImageSource(mode.ImageWidth, mode.ImageHeight, pixels)));
            _nextLine++;
        }
    }

    private bool TryDecodeVisHeader()
    {
        // Prefix (leader/break/leader/start-bit + first 7 data bits) is the same length whether
        // this is a normal single-byte VIS code or an "extended" MR/MP/ML one (see VisHeader) — we
        // don't know which until those 7 bits are decoded, so read the prefix first, then decide.
        var prefixSampleCount = (int)Math.Round(VisHeader.PrefixDurationMs / 1000.0 * _sampleRate);
        if (_demodulatedFrequencies.Count - _consumedSamples < prefixSampleCount)
        {
            return false;
        }

        var headerStart = _consumedSamples;
        var bitMidpointHz = (VisHeader.Bit1FrequencyHz + VisHeader.Bit0FrequencyHz) / 2;
        var prefixIdealSamples = (VisHeader.LeaderDurationMs + VisHeader.BreakDurationMs + VisHeader.LeaderDurationMs + VisHeader.BitDurationMs)
            / 1000.0 * _sampleRate;

        var firstByteBits = new int[VisHeader.DataBitCount];
        for (var bitIndex = 0; bitIndex < VisHeader.DataBitCount; bitIndex++)
        {
            var startSample = headerStart + (int)Math.Round(prefixIdealSamples);
            prefixIdealSamples += VisHeader.BitDurationMs / 1000.0 * _sampleRate;
            var endSample = headerStart + (int)Math.Round(prefixIdealSamples);

            var avgFreq = AverageFrequencyInWindow(startSample, endSample);
            firstByteBits[bitIndex] = avgFreq < bitMidpointHz ? 1 : 0; // closer to Bit1FrequencyHz (1100) => 1
        }

        var firstByteValue = VisHeader.DecodeVisCode(firstByteBits);
        var isExtended = firstByteValue == VisHeader.ExtendedVisEscapeCode;
        var tailDurationMs = isExtended ? VisHeader.ExtendedTailDurationMs : VisHeader.NormalTailDurationMs;
        var totalHeaderSampleCount = (int)Math.Round((VisHeader.PrefixDurationMs + tailDurationMs) / 1000.0 * _sampleRate);

        if (_demodulatedFrequencies.Count - headerStart < totalHeaderSampleCount)
        {
            return false; // wait for the rest of the header before consuming/deciding
        }

        _consumedSamples = headerStart + totalHeaderSampleCount;

        SstvModeDefinition? mode;
        if (isExtended)
        {
            // 1 leftover bit from the escape byte (its bit 7, unused) + all 8 bits of the real
            // extended-mode byte = 9 more bit-slots before the stop bit.
            var remainingBits = new int[9];
            for (var bitIndex = 0; bitIndex < remainingBits.Length; bitIndex++)
            {
                var startSample = headerStart + (int)Math.Round(prefixIdealSamples);
                prefixIdealSamples += VisHeader.BitDurationMs / 1000.0 * _sampleRate;
                var endSample = headerStart + (int)Math.Round(prefixIdealSamples);

                var avgFreq = AverageFrequencyInWindow(startSample, endSample);
                remainingBits[bitIndex] = avgFreq < bitMidpointHz ? 1 : 0;
            }

            var extendedCode = VisHeader.DecodeRawByte(remainingBits.AsSpan(1, 8));
            mode = SstvModeRegistry.FindByExtendedCode(extendedCode);
        }
        else
        {
            mode = SstvModeRegistry.FindByVisCode(firstByteValue);
        }

        if (mode is null)
        {
            // Unknown VIS code. A fuller implementation would keep scanning for a valid header
            // instead of giving up — out of scope for this Phase 1 proof.
            return false;
        }

        _mode = mode;
        _lineDecoder = ScanlineCodecFactory.CreateDecoder(mode.ColorEncoding);
        _pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        ModeDetected?.Invoke(mode);
        return true;
    }

    /// <summary>Averages the (already fully demodulated) frequency stream over [startSample,
    /// endSample), skipping a settling margin at the start for the PLL loop's transient response
    /// after the preceding frequency change.</summary>
    private double AverageFrequencyInWindow(int startSample, int endSample)
    {
        var sampleCount = Math.Max(1, endSample - startSample);
        var settleSamples = sampleCount / 4;

        var from = Math.Clamp(startSample + settleSamples, 0, _demodulatedFrequencies.Count);
        var to = Math.Clamp(endSample, 0, _demodulatedFrequencies.Count);
        if (to <= from)
        {
            from = Math.Clamp(startSample, 0, _demodulatedFrequencies.Count);
            to = Math.Clamp(endSample, 0, _demodulatedFrequencies.Count);
        }

        if (to <= from)
        {
            return 0;
        }

        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += _demodulatedFrequencies[i];
        }

        return sum / (to - from);
    }

    private sealed class MutableImageSource(int width, int height, Rgb24[] pixels) : IImageSource
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => pixels.AsSpan(y * Width, Width);
    }
}
