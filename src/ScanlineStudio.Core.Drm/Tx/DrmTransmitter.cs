using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Core.Drm.Tables;
using CoreSettings = ScanlineStudio.Core.Drm.Tx.DrmTxSettings;
using PublicSettings = ScanlineStudio.Abstractions.Drm.DrmTxSettings;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>The app-lifetime DRM transmitter. It owns QSSTV's two pieces of cross-transmission state, the
/// TX file-name stamp (<see cref="DrmTxNaming"/>) and the BSR transport-ID counter
/// (<see cref="BsrTransportIdCounter"/>), and builds a fresh source generator and channel per
/// transmission, as QSSTV's <c>drmTransmitter::init</c> does (drmtransmitter.cpp:37-39).
/// <see cref="Prepare"/> and <see cref="ForgetTxFileName"/> serialize on one lock.</summary>
public sealed class DrmTransmitter : IDrmTransmitter
{
    /// <summary>QSSTV's DRM output rate (CTransmitData writes 48 kHz, DRMSignalIO.cpp:40-96).</summary>
    public const int OutputSampleRate = 48_000;

    private readonly DrmTxNaming _naming;
    private readonly BsrTransportIdCounter _bsrCounter;
    private readonly object _gate = new();

    public DrmTransmitter(DrmTxNaming naming, BsrTransportIdCounter bsrCounter)
    {
        ArgumentNullException.ThrowIfNull(naming);
        ArgumentNullException.ThrowIfNull(bsrCounter);
        _naming = naming;
        _bsrCounter = bsrCounter;
    }

    public int SampleRate => OutputSampleRate;

    public void ForgetTxFileName()
    {
        lock (_gate)
            _naming.ForgetTxFileName();
    }

    public IDrmTransmission Prepare(ReadOnlySpan<byte> payload, string fileName, bool isImage, PublicSettings settings, bool newTxFileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Callsign);
        if (payload.IsEmpty)
            throw new ArgumentException("A DRM transmission needs a non-empty payload.", nameof(payload));

        CoreSettings core = ToCoreSettings(settings);
        lock (_gate)
        {
            if (newTxFileName)
                _naming.ForgetTxFileName();
            DrmTxFileName name = _naming.GetTxFileName(fileName, binary: !isImage);
            DrmTxObject txObject = DrmTxObject.ForFile(payload, name, core.ReedSolomon);

            // A dry run on a copy of the counter, so the real run below starts from the same state.
            int frameCount = new DrmSourceFrameGenerator(txObject, core, _bsrCounter.Clone()).Frames().Count(f => f.FacBits is not null);
            var generator = new DrmSourceFrameGenerator(txObject, core, _bsrCounter);
            var channel = new DrmTxChannel(core);
            return new DrmTransmission(() => channel.Modulate(generator.Frames()), channel.SamplesPerFrame, frameCount, generator.SegmentSchedule.Count, settings.VoxTone, txObject.MotName.Span);
        }
    }

    internal static CoreSettings ToCoreSettings(PublicSettings settings) => new(
        settings.Mode switch
        {
            DrmRobustnessMode.A => RobustnessMode.A,
            DrmRobustnessMode.B => RobustnessMode.B,
            DrmRobustnessMode.E => RobustnessMode.E,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Mode, "Unknown robustness mode."),
        },
        settings.Bandwidth switch
        {
            DrmBandwidth.Hz2200 => SpectrumOccupancy.So0,
            DrmBandwidth.Hz2500 => SpectrumOccupancy.So1,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Bandwidth, "Unknown bandwidth."),
        },
        settings.Qam switch
        {
            DrmQamOrder.Qam4 => DrmQam.Qam4,
            DrmQamOrder.Qam16 => DrmQam.Qam16,
            DrmQamOrder.Qam64 => DrmQam.Qam64,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Qam, "Unknown QAM order."),
        },
        settings.Protection switch
        {
            DrmProtectionLevel.High => DrmProtection.High,
            DrmProtectionLevel.Low => DrmProtection.Low,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Protection, "Unknown protection level."),
        },
        settings.Interleave switch
        {
            DrmInterleaving.LongInterleaver => DrmInterleave.Long,
            DrmInterleaving.ShortInterleaver => DrmInterleave.Short,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Interleave, "Unknown interleaving."),
        },
        settings.ReedSolomon switch
        {
            DrmReedSolomonLevel.None => DrmReedSolomon.None,
            DrmReedSolomonLevel.Rs1 => DrmReedSolomon.Rs1,
            DrmReedSolomonLevel.Rs2 => DrmReedSolomon.Rs2,
            DrmReedSolomonLevel.Rs3 => DrmReedSolomon.Rs3,
            DrmReedSolomonLevel.Rs4 => DrmReedSolomon.Rs4,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.ReedSolomon, "Unknown Reed-Solomon level."),
        },
        settings.Callsign);
}

/// <summary>One prepared transmission. <see cref="ReadSamplesAsync"/> runs the source generator and channel
/// (CPU-bound: per frame the MLC, interleavers and 15-25 IFFTs) on a thread-pool task, never on the
/// consumer's thread. The task writes finished frames into a bounded channel of
/// <see cref="BufferedFrames"/> frames and awaits (does not drop) while it is full, so a slow playback
/// consumer throttles the producer and every frame goes out in order. A producer fault completes the
/// channel with that exception, which the consumer's enumeration rethrows.</summary>
internal sealed class DrmTransmission : IDrmTransmission
{
    /// <summary>1.2 s of audio queued ahead of playback.</summary>
    public const int BufferedFrames = 3;

    private readonly Func<IEnumerable<short[]>> _frames;
    private readonly int _samplesPerFrame;
    private readonly bool _voxTone;
    private int _started;

    /// <param name="frames">Starts the lazy source → channel pipeline; called once, on the producer task.</param>
    public DrmTransmission(Func<IEnumerable<short[]>> frames, int samplesPerFrame, int frameCount, int numTxFrames, bool voxTone, ReadOnlySpan<byte> motName)
    {
        _frames = frames;
        _samplesPerFrame = samplesPerFrame;
        _voxTone = voxTone;
        FrameCount = frameCount;
        NumTxFrames = numTxFrames;
        OnAirName = Encoding.Latin1.GetString(motName);
    }

    public int FrameCount { get; }

    public long SampleCount => (_voxTone ? DrmVoxTone.SampleCount(DrmTransmitter.OutputSampleRate) : 0) + ((long)FrameCount * _samplesPerFrame);

    /// <summary>QSSTV's <c>numTxFrames</c>: the segment schedule length (DABMOT.cpp:631), fewer than the on-air frames.</summary>
    public int NumTxFrames { get; }

    /// <summary>drmtransmitter.cpp:116 uses <c>numTxFrames</c>, so it undershoots the real airtime slightly.</summary>
    public TimeSpan DisplayDuration => TimeSpan.FromSeconds(NumTxFrames * 0.4 * 1.005);

    public string OnAirName { get; }

    /// <summary>The running producer, for tests; null until the first frame is requested.</summary>
    internal Task? ProducerTask { get; private set; }

    public IAsyncEnumerable<ReadOnlyMemory<float>> ReadSamplesAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A DRM transmission can be read once only.");
        return ReadCoreAsync(ct);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<float>> ReadCoreAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (_voxTone)
            yield return DrmVoxTone.Generate(DrmTransmitter.OutputSampleRate);

        var frames = Channel.CreateBounded<float[]>(new BoundedChannelOptions(BufferedFrames)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task producer = Task.Run(() => ProduceAsync(frames.Writer, producerCts.Token), CancellationToken.None);
        ProducerTask = producer;
        try
        {
            await foreach (float[] frame in frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return frame;
        }
        finally
        {
            // Stops a producer the consumer abandoned; ProduceAsync never throws, so this await only waits.
            await producerCts.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }
    }

    private async Task ProduceAsync(ChannelWriter<float[]> writer, CancellationToken ct)
    {
        try
        {
            foreach (short[] pcm in _frames())
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteAsync(DrmTxChannel.ToFloat(pcm), ct).ConfigureAwait(false);
            }

            writer.TryComplete();
        }
#pragma warning disable CA1031 // Any producer fault must reach the consumer through the channel.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            writer.TryComplete(ex);
        }
    }
}
