using SixLabors.ImageSharp;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Imaging;

/// <summary>The "application-layer adapter" spec/07-image-pipeline.md describes — subscribes to
/// <see cref="ISstvDecoder.LineDecoded"/> itself, rather than something in <c>ScanlineStudio.Application</c>
/// doing the wiring. Layering-legal despite living in <c>ScanlineStudio.Core.Imaging</c>: <see cref="ISstvDecoder"/>
/// is in <c>ScanlineStudio.Abstractions.Sstv</c>, below `ScanlineStudio.Application` in spec/01-architecture.md's
/// diagram, same as this project — no Core-to-Core edge is created. <c>ScanlineStudio.Core.Imaging</c> must
/// never gain a reference to <c>ScanlineStudio.Core.Sstv</c> itself; that (not this class) is what would make
/// this placement illegal.
///
/// <b>Whole-image snapshot, not per-line</b> (a real bug caught in plan review before this shipped):
/// <see cref="ISstvDecoder.LineDecoded"/> fires with only the *starting* row of a
/// multi-row transmission unit for the paired-line families (PD/MP, RM8/RM12 —
/// `IScanlineDecoder.RowsPerTransmissionLine` is 2 for those, not exposed on the public
/// <see cref="SstvModeDefinition"/>). <see cref="DecodedImageUpdate.Image"/> is always the *whole*
/// live canvas (`AnalogFmSstvDecoder`'s private `MutableImageSource` wraps the full
/// mode-sized pixel array), so snapshotting the entire image on every event — not just the reported
/// row — is both simplest and correct, and cheap (bounded by one video frame's own pixel count).
///
/// <b>Snapshot contract</b> (Phase-3 plan decision #5): <see cref="Current"/> always returns a
/// defensive copy, never a view into the decoder's own live/mutable buffer.</summary>
public sealed class ReceivedImageBuffer : IReceivedImageBuffer
{
    private static readonly IImageSource EmptyImage = new ArrayImageSource(1, 1, [new Rgb24(0, 0, 0)]);

    private readonly object _gate = new();
    private IImageSource _current = EmptyImage;
    private double? _progress;

    // Completion-detection state -- same technique, same field shapes as
    // ReceiveHistoryRecorder.cs's own (independently-solved) version of this exact problem: the
    // scanline-group step size (1 row per event for most families, 2 for PD/MP/RM8/RM12 per
    // IScanlineDecoder.RowsPerTransmissionLine, not exposed on the public SstvModeDefinition) isn't
    // known up front, so it's learned from the first two DecodedImageUpdate.Line deltas rather than
    // guessed.
    private int? _previousLine;
    private int? _observedStep;

    public ReceivedImageBuffer(ISstvDecoder decoder)
    {
        decoder.ModeDetected += OnModeDetected;
        decoder.LineDecoded += OnLineDecoded;
        decoder.DecodeRestarted += OnDecodeRestarted;
    }

    public IImageSource Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public double? Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    public event Action? Updated;

    public Task SaveAsync(string path, CancellationToken ct = default)
    {
        var snapshot = Current;
        return Task.Run(
            () =>
            {
                using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(snapshot.Width, snapshot.Height);
                image.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < snapshot.Height; y++)
                    {
                        var sourceRow = snapshot.GetScanline(y);
                        var destinationRow = accessor.GetRowSpan(y);
                        for (var x = 0; x < snapshot.Width; x++)
                        {
                            var pixel = sourceRow[x];
                            destinationRow[x] = new SixLabors.ImageSharp.PixelFormats.Rgb24(pixel.R, pixel.G, pixel.B);
                        }
                    }
                });
                image.Save(path);
            },
            ct);
    }

    private void OnModeDetected(SstvModeDefinition mode)
    {
        lock (_gate)
        {
            _previousLine = null;
            _observedStep = null;
            _progress = 0.0;
        }

        Updated?.Invoke();
    }

    private void OnLineDecoded(DecodedImageUpdate update)
    {
        var snapshot = Snapshot(update.Image);
        lock (_gate)
        {
            _current = snapshot;
            _progress = ComputeProgress(update, snapshot.Height);
        }

        Updated?.Invoke();
    }

    // Unlike ReceiveHistoryRecorder's own version of this same step-learning technique (which only
    // needs a boolean "is this complete" and so can safely defer any check until the step is known),
    // a progress FRACTION is read continuously, including before the step is learned -- so the first
    // event of an image (no step yet) falls back to a plain Line/Height estimate rather than
    // returning nothing. That fallback has no false-100% risk the way ReceiveHistoryRecorder's
    // boolean check would (a fraction being slightly off for one frame is harmless), so it doesn't
    // need that class's own extra guard against checking too early.
    private double ComputeProgress(DecodedImageUpdate update, int imageHeight)
    {
        if (_previousLine is int previousLine && _observedStep is null)
        {
            _observedStep = update.Line - previousLine;
        }

        _previousLine = update.Line;

        if (_observedStep is not int step)
        {
            return Math.Clamp((double)update.Line / imageHeight, 0.0, 1.0);
        }

        // Snap to exactly 1.0 on the completing event -- Line + step alone asymptotes to
        // (Height - step) / Height and would never actually reach 1.0 for multi-row-per-event
        // families (PD/MP/RM8/RM12), which would leave a live progress readout stuck just under
        // 100% for the rest of the image's on-screen lifetime.
        if (update.Line + step >= imageHeight)
        {
            return 1.0;
        }

        return Math.Clamp((double)(update.Line + step) / imageHeight, 0.0, 1.0);
    }

    private void OnDecodeRestarted(SstvModeDefinition abandonedMode)
    {
        lock (_gate)
        {
            _current = EmptyImage;
            _progress = null;
        }

        Updated?.Invoke();
    }

    private static ArrayImageSource Snapshot(IImageSource source)
    {
        var pixels = new Rgb24[source.Width * source.Height];
        for (var y = 0; y < source.Height; y++)
        {
            source.GetScanline(y).CopyTo(pixels.AsSpan(y * source.Width, source.Width));
        }

        return new ArrayImageSource(source.Width, source.Height, pixels);
    }
}
