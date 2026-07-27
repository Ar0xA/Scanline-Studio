using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Generic decoder counterpart to <see cref="AnalogFmSstvEncoder"/>, using a ported
/// <see cref="PllFmDemodulator"/> (see that type's doc comment) run continuously over the incoming
/// sample stream. Scope note (Phase 1): this decodes the VIS-detected mode and then reconstructs
/// scanlines using the mode's *nominal* timing — it does not independently re-search for each
/// line's sync pulse (the legacy AFC/sync state machine in `CSSTVDEM` is a separate, larger piece
/// of work not yet ported), and does not yet implement the clock-drift/slant correction described
/// in spec/06-sstv-dsp.md. That's fine for the same-process, no-channel-noise round-trip this
/// proves; real captured audio (with clock drift between transmitter and receiver sound cards)
/// needs both of those added before this is usable on the air.
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
        var pixels = _pixels!;
        var lineSampleCount = (int)Math.Round(mode.LineDurationMs / 1000.0 * _sampleRate);

        while (_nextLine < mode.ImageHeight && _demodulatedFrequencies.Count - _consumedSamples >= lineSampleCount)
        {
            DecodeLine(mode, _consumedSamples, _nextLine, pixels);
            _consumedSamples += lineSampleCount;

            LineDecoded?.Invoke(new DecodedImageUpdate(_nextLine, new MutableImageSource(mode.ImageWidth, mode.ImageHeight, pixels)));
            _nextLine++;
        }
    }

    private bool TryDecodeVisHeader()
    {
        var headerSampleCount = (int)Math.Round(VisHeader.TotalDurationMs / 1000.0 * _sampleRate);
        if (_demodulatedFrequencies.Count - _consumedSamples < headerSampleCount)
        {
            return false;
        }

        var headerStart = _consumedSamples;
        _consumedSamples += headerSampleCount;

        var idealSamplesSoFar = (VisHeader.LeaderDurationMs + VisHeader.BreakDurationMs + VisHeader.LeaderDurationMs + VisHeader.BitDurationMs)
            / 1000.0 * _sampleRate;
        var bits = new int[VisHeader.DataBitCount];
        var bitMidpointHz = (VisHeader.Bit1FrequencyHz + VisHeader.Bit0FrequencyHz) / 2;

        for (var bitIndex = 0; bitIndex < VisHeader.DataBitCount; bitIndex++)
        {
            var startSample = headerStart + (int)Math.Round(idealSamplesSoFar);
            idealSamplesSoFar += VisHeader.BitDurationMs / 1000.0 * _sampleRate;
            var endSample = headerStart + (int)Math.Round(idealSamplesSoFar);

            var avgFreq = AverageFrequencyInWindow(startSample, endSample);
            bits[bitIndex] = avgFreq < bitMidpointHz ? 1 : 0; // closer to Bit1FrequencyHz (1100) => 1
        }

        var visCode = VisHeader.DecodeVisCode(bits);
        var mode = SstvModeRegistry.FindByVisCode(visCode);
        if (mode is null)
        {
            // Unknown VIS code. A fuller implementation would keep scanning for a valid header
            // instead of giving up — out of scope for this Phase 1 proof.
            return false;
        }

        _mode = mode;
        _pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        ModeDetected?.Invoke(mode);
        return true;
    }

    private void DecodeLine(SstvModeDefinition mode, int lineStartSample, int lineIndex, Rgb24[] pixels)
    {
        // Mirrors the encoder's running-accumulator approach (see AnalogFmSstvEncoder) so pixel
        // window boundaries line up with where the encoder actually placed them, rather than each
        // side independently rounding ms->samples and drifting apart over 320 pixels x 3 channels.
        var idealSamplesSoFar = 0.0;

        foreach (var segment in mode.LineSegments)
        {
            if (segment is ScanSegment scan)
            {
                var perPixelDurationMs = scan.DurationMs / mode.ImageWidth;
                for (var x = 0; x < mode.ImageWidth; x++)
                {
                    var startSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);
                    idealSamplesSoFar += perPixelDurationMs / 1000.0 * _sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);

                    var avgFreq = AverageFrequencyInWindow(startSample, endSample);
                    var value = (byte)Math.Clamp(
                        (avgFreq - mode.LuminanceMinHz) / (mode.LuminanceMaxHz - mode.LuminanceMinHz) * 255.0,
                        0,
                        255);

                    var index = lineIndex * mode.ImageWidth + x;
                    pixels[index] = SetChannel(pixels[index], scan.ChannelName, value);
                }
            }
            else
            {
                idealSamplesSoFar += segment.DurationMs / 1000.0 * _sampleRate;
            }
        }
    }

    private static Rgb24 SetChannel(Rgb24 pixel, string channelName, byte value) => channelName switch
    {
        "R" => pixel with { R = value },
        "G" => pixel with { G = value },
        "B" => pixel with { B = value },
        _ => throw new NotSupportedException($"Unknown channel '{channelName}'."),
    };

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
