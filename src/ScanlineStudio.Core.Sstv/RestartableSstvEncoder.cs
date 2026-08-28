using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Restart-required-settings backlog item 4 (sample-rate live-apply, 2026-08-27): wraps
/// <see cref="AnalogFmSstvEncoder"/> with a live-swappable sample rate. Much lighter than
/// <see cref="RestartableSstvDecoder"/>'s own equivalent -- <see cref="AnalogFmSstvEncoder"/> is
/// stateless and trivially cheap to construct (its constructor is one field assignment,
/// <see cref="AnalogFmSstvEncoder(int)"/>), so this wrapper needs no periodic-maintenance swap
/// machinery, no threshold recompute, no scratch-file disposal chain -- just a plain instance
/// replace under a lock.
///
/// <see cref="EncodeAsync"/>/<see cref="EstimateSampleCount"/> are PLAIN DELEGATING METHODS, not
/// iterators -- <see cref="ISstvEncoder"/> requires the dimension-mismatch
/// <see cref="ArgumentException"/> to throw synchronously from the call, not deferred to enumeration
/// (<see cref="AnalogFmSstvEncoder.EncodeAsync"/> is itself split for exactly this reason), and
/// `SstvSessionService.TransmitAsync` calls <see cref="EncodeAsync"/> before keying PTT -- an
/// iterator-shaped wrapper would move that throw to mid-playback, PTT already keyed. Each call
/// resolves which inner instance it's delegating to ONCE, into a local, not per-<c>MoveNextAsync</c>
/// -- defense in depth on top of the <see cref="BeginTransmission"/>/<see cref="EndTransmission"/>
/// bracket (see <see cref="ISstvEncoderReconfiguration"/>'s own doc comment for the real
/// correctness contract that bracket provides).</summary>
public sealed class RestartableSstvEncoder : ISstvEncoder, ISstvEncoderReconfiguration
{
    private readonly object _gate = new();
    private AnalogFmSstvEncoder _inner;
    private int? _pendingSampleRate;
    private int _transmissionRefCount;

    public RestartableSstvEncoder(int sampleRate = SstvSampleRate.Default)
    {
        _inner = new AnalogFmSstvEncoder(sampleRate);
    }

    public int SampleRate
    {
        get
        {
            lock (_gate)
            {
                return _inner.SampleRate;
            }
        }
    }

    public IAsyncEnumerable<float> EncodeAsync(
        SstvModeDefinition mode,
        IImageSource image,
        StationIdTransmitOptions? stationId = null,
        double sampleRateOffsetHz = 0.0,
        CancellationToken ct = default)
    {
        AnalogFmSstvEncoder inner;
        lock (_gate)
        {
            inner = _inner;
        }

        return inner.EncodeAsync(mode, image, stationId, sampleRateOffsetHz, ct);
    }

    public long EstimateSampleCount(
        SstvModeDefinition mode,
        IImageSource image,
        StationIdTransmitOptions? stationId = null,
        double sampleRateOffsetHz = 0.0)
    {
        AnalogFmSstvEncoder inner;
        lock (_gate)
        {
            inner = _inner;
        }

        return inner.EstimateSampleCount(mode, image, stationId, sampleRateOffsetHz);
    }

    /// <summary>See <see cref="ISstvEncoderReconfiguration.RequestSampleRate"/>.</summary>
    public void RequestSampleRate(int sampleRate)
    {
        if (!SstvSampleRate.IsSupported(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, $"Sample rate must be between {SstvSampleRate.Minimum} and {SstvSampleRate.Maximum} Hz inclusive.");
        }

        lock (_gate)
        {
            if (sampleRate == _inner.SampleRate)
            {
                _pendingSampleRate = null;
                return;
            }

            if (_transmissionRefCount == 0)
            {
                _inner = new AnalogFmSstvEncoder(sampleRate);
                _pendingSampleRate = null;
            }
            else
            {
                _pendingSampleRate = sampleRate;
            }
        }
    }

    /// <summary>See <see cref="ISstvEncoderReconfiguration.BeginTransmission"/>.</summary>
    public void BeginTransmission()
    {
        lock (_gate)
        {
            _transmissionRefCount++;
        }
    }

    /// <summary>See <see cref="ISstvEncoderReconfiguration.EndTransmission"/>.</summary>
    public void EndTransmission()
    {
        lock (_gate)
        {
            _transmissionRefCount--;
            if (_transmissionRefCount == 0 && _pendingSampleRate is { } sampleRate)
            {
                _inner = new AnalogFmSstvEncoder(sampleRate);
                _pendingSampleRate = null;
            }
        }
    }
}
