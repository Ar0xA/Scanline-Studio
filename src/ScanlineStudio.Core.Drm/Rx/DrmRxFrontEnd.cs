namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>Receives one 1024-sample 12 kHz stripe. The span is only valid during the call.</summary>
internal delegate void DrmStripeHandler(ReadOnlySpan<double> stripe);

/// <summary>Which rate path a <see cref="DrmRxFrontEnd"/> uses.</summary>
internal enum DrmRxFrontEndPath
{
    /// <summary>48000 Hz: the ported QSSTV decimator.</summary>
    Decimate48k,

    /// <summary>12000 Hz: no rate change.</summary>
    Passthrough12k,

    /// <summary>Any other supported rate: <see cref="PolyphaseResampler"/>.</summary>
    Resample,
}

/// <summary>The DRM receiver's front end: float capture at the capture rate to QSSTV's 12 kHz int16-scale
/// 1024-sample stripes (sound/soundbase.cpp:42,187-189; mainwidgets/rxfunctions.cpp:53-76).</summary>
/// <remarks>
/// <para>The capture rate is fixed per instance. A rate change means building a new instance; the old
/// one's history belongs to the old rate and must not be reused.</para>
/// <para>Samples are scaled ×32768 to QSSTV's int16 scale and not quantized. The Hilbert stage
/// (<see cref="DrmHilbertFilter"/>) is applied by the demodulator to each stripe, as in drmRx::run.</para>
/// <para>Not thread-safe: one instance per receive worker. No allocation after construction.</para>
/// </remarks>
internal sealed class DrmRxFrontEnd
{
    /// <summary>The lowest supported capture rate: the 3.5 kHz stop-band edge needs a Nyquist above it.</summary>
    public const int MinCaptureRate = 8000;

    /// <summary>The highest supported capture rate; above it the resampler kernel grows without benefit.</summary>
    public const int MaxCaptureRate = 192000;

    /// <summary>SAMPLERATE (appdefs.h:17).</summary>
    public const int OutputRate = 12000;

    /// <summary>RXSTRIPE (appdefs.h:13).</summary>
    public const int StripeSize = 1024;

    /// <summary>Float full scale to QSSTV's int16 capture scale.</summary>
    public const double Int16Scale = 32768.0;

    private const int ChunkSize = 4096;

    private readonly QsstvDecimator4? _decimator;
    private readonly PolyphaseResampler? _resampler;
    private readonly double[] _in = new double[ChunkSize];
    private readonly double[] _out;
    private readonly double[] _stripe = new double[StripeSize];
    private int _stripeCount;

    /// <exception cref="ArgumentOutOfRangeException">The rate is below <see cref="MinCaptureRate"/>
    /// or above <see cref="MaxCaptureRate"/>.</exception>
    public DrmRxFrontEnd(int captureRate)
    {
        if (!IsSupportedRate(captureRate))
            throw new ArgumentOutOfRangeException(nameof(captureRate), captureRate,
                $"DRM receive needs a capture rate of {MinCaptureRate}-{MaxCaptureRate} Hz.");
        CaptureRate = captureRate;
        switch (captureRate)
        {
            case 48000:
                Path = DrmRxFrontEndPath.Decimate48k;
                _decimator = new QsstvDecimator4();
                _out = new double[QsstvDecimator4.MaxOutput(ChunkSize)];
                break;
            case OutputRate:
                Path = DrmRxFrontEndPath.Passthrough12k;
                _out = _in;
                break;
            default:
                Path = DrmRxFrontEndPath.Resample;
                _resampler = new PolyphaseResampler(captureRate, OutputRate);
                _out = new double[_resampler.MaxOutput(ChunkSize)];
                break;
        }
    }

    public static bool IsSupportedRate(int captureRate) => captureRate is >= MinCaptureRate and <= MaxCaptureRate;

    public int CaptureRate { get; }

    public DrmRxFrontEndPath Path { get; }

    /// <summary>Clears all filter history and any partial stripe, for a capture restart only (soundbase.cpp:332-334); a gap is zero-filled, not reset.</summary>
    public void Reset()
    {
        _decimator?.Reset();
        _resampler?.Reset();
        Array.Clear(_stripe);
        _stripeCount = 0;
    }

    /// <summary>Feeds capture samples (float full scale ±1); calls <paramref name="onStripe"/> for every
    /// completed stripe, in order, on the calling thread.</summary>
    public void Push(ReadOnlySpan<float> samples, DrmStripeHandler onStripe)
    {
        ArgumentNullException.ThrowIfNull(onStripe);
        while (!samples.IsEmpty)
        {
            int n = Math.Min(samples.Length, ChunkSize);
            for (int i = 0; i < n; i++)
                _in[i] = samples[i] * Int16Scale;
            samples = samples[n..];
            int produced = Path switch
            {
                DrmRxFrontEndPath.Decimate48k => _decimator!.Process(_in.AsSpan(0, n), _out),
                DrmRxFrontEndPath.Resample => _resampler!.Process(_in.AsSpan(0, n), _out),
                _ => n,
            };
            Emit(_out.AsSpan(0, produced), onStripe);
        }
    }

    private void Emit(ReadOnlySpan<double> data, DrmStripeHandler onStripe)
    {
        while (!data.IsEmpty)
        {
            int take = Math.Min(data.Length, StripeSize - _stripeCount);
            data[..take].CopyTo(_stripe.AsSpan(_stripeCount));
            _stripeCount += take;
            data = data[take..];
            if (_stripeCount < StripeSize)
                continue;
            _stripeCount = 0;
            onStripe(_stripe);
        }
    }
}
