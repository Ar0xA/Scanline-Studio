using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Drm;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>The public DRM receive chain: <see cref="DrmRxFrontEnd"/> → <see cref="DrmDemodulator"/> (with
/// <see cref="DrmRxFrameStage"/>: channel estimation, <see cref="DrmChannelDecoder"/>) → <see cref="DrmSourceDecoder"/>.</summary>
/// <remarks>Not thread-safe: one instance per receive worker. All events are raised on the calling thread.</remarks>
public sealed class DrmReceiver : IDrmReceiver
{
    // 400 ms of consumed audio, counted in 12 kHz stripes.
    private const int StateIntervalSamples = DrmRxFrontEnd.OutputRate * 400 / 1000;

    private const int ZeroBlock = 4096;

    private readonly DrmRxFrontEnd _frontEnd;
    private readonly DrmDemodulator _demodulator;
    private readonly DrmChannelDecoder _channelDecoder;
    private readonly DrmSourceDecoder _sourceDecoder;
    private readonly DrmStripeHandler _onStripe;
    private readonly float[] _zeros = new float[ZeroBlock];
    private DrmSignalState? _lastState;
    private int _samplesSinceState;
    private string _receivingName = string.Empty;

    /// <summary>A receiver at <paramref name="captureRate"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not supported.</exception>
    public DrmReceiver(int captureRate, ILoggerFactory? loggerFactory = null)
        : this(captureRate, DrmRxOptions.Default, loggerFactory)
    {
    }

    /// <summary>A receiver with explicit behaviour switches (baselines and measurements).</summary>
    internal DrmReceiver(int captureRate, DrmRxOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        loggerFactory ??= NullLoggerFactory.Instance;
        _frontEnd = new DrmRxFrontEnd(captureRate);
        _channelDecoder = new DrmChannelDecoder();
        _sourceDecoder = new DrmSourceDecoder(loggerFactory.CreateLogger<DrmSourceDecoder>())
        {
            ReproduceQsstvPpiWrap = options.ReproduceQsstvPpiWrap,
            ReproduceQsstvSignedSurplus = options.ReproduceQsstvSignedSurplus,
        };
        _demodulator = new DrmDemodulator(new DrmRxFrameStage(new DrmChannelEstimator(), _channelDecoder, _sourceDecoder));
        _onStripe = OnStripe;
        _sourceDecoder.FileReceived += f => FileReceived?.Invoke(f);
        _sourceDecoder.ReceivingNameChanged += n =>
        {
            _receivingName = n;
            ReceivingNameChanged?.Invoke(n);
        };
        _sourceDecoder.AlreadyReceived += n => AlreadyReceived?.Invoke(n);
        _sourceDecoder.BsrReceived += b => BsrReceived?.Invoke(b);
    }

    /// <inheritdoc/>
    public event Action<DrmReceivedFile>? FileReceived;

    /// <inheritdoc/>
    public event Action<string>? ReceivingNameChanged;

    /// <inheritdoc/>
    public event Action<string>? AlreadyReceived;

    /// <inheritdoc/>
    public event Action<ReadOnlyMemory<byte>>? BsrReceived;

    /// <inheritdoc/>
    public event Action<DrmSignalState>? SignalStateChanged;

    /// <inheritdoc/>
    public int CaptureRate => _frontEnd.CaptureRate;

    /// <summary>The demodulator state, for tests.</summary>
    internal DrmDemodState State => _demodulator.State;

    /// <summary>Whether <paramref name="captureRate"/> is supported by the front end.</summary>
    public static bool IsSupportedRate(int captureRate) => DrmRxFrontEnd.IsSupportedRate(captureRate);

    /// <inheritdoc/>
    public void Push(ReadOnlySpan<float> samples) => _frontEnd.Push(samples, _onStripe);

    /// <inheritdoc/>
    public void ZeroFill(int samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(samples);
        while (samples > 0)
        {
            int n = Math.Min(samples, ZeroBlock);
            _frontEnd.Push(_zeros.AsSpan(0, n), _onStripe);
            samples -= n;
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _frontEnd.Reset();
        _demodulator.Reset();
        PublishState();
    }

    private void OnStripe(ReadOnlySpan<double> stripe)
    {
        _demodulator.RunStripe(stripe);
        _samplesSinceState += stripe.Length;
        PublishState();
    }

    private void PublishState()
    {
        DrmSignalState state = Snapshot();
        DrmSignalState? last = _lastState;
        bool syncChanged = last is null || last.TimeSync != state.TimeSync || last.FrequencySync != state.FrequencySync
            || last.FrameSync != state.FrameSync;
        if (!syncChanged && (_samplesSinceState < StateIntervalSamples || state == last))
            return;
        _lastState = state;
        _samplesSinceState = 0;
        SignalStateChanged?.Invoke(state);
    }

    private DrmSignalState Snapshot()
    {
        DrmDemodState st = _demodulator.State;
        bool frame = st.FrameSyncFlag;
        return new DrmSignalState(
            IsActive: true,
            TimeSync: st.TimeSyncFlag,
            FrequencySync: st.FrequencySyncFlag,
            FrameSync: frame,
            RobustnessMode: frame ? st.RobustnessMode : -1,
            SpectrumOccupancy: frame ? _channelDecoder.SpectrumOccupancyNew : -1,
            MscMode: frame ? _channelDecoder.MscModeNew : -1,
            Callsign: _channelDecoder.CallsignValid ? _channelDecoder.Callsign : string.Empty,
            SegmentsReceived: _sourceDecoder.BlockList.Count,
            TotalSegments: _sourceDecoder.BodyTotalSegments,
            ReceivingName: _receivingName,
            Generation: 0);
    }
}
