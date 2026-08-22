using Microsoft.Extensions.Logging;
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
public sealed partial class ReceivedImageBuffer : IReceivedImageBuffer
{
    private static readonly IImageSource EmptyImage = new ArrayImageSource(1, 1, [new Rgb24(0, 0, 0)]);

    private readonly object _gate = new();
    private readonly ILogger<ReceivedImageBuffer> _logger;
    private IImageSource _current = EmptyImage;
    private double? _progress;

    // Auditor-caught, round 2: bumped on every OnModeDetected/OnDecodeRestarted -- both are the
    // events that change what Current's IDENTITY means (a fresh image starting, or the current one
    // being blanked on a restart), as opposed to OnLineDecoded, which only updates the SAME image's
    // pixels. SaveAsync captures this alongside its own snapshot (both under the SAME lock read,
    // right below) and hands it to Saved -- a subscriber compares that captured value against
    // Generation's THEN-current value at whatever later point it actually applies the save's result,
    // to detect a newer image having superseded the one that was actually saved. A first attempt at
    // this (round 1) instead had the UI-side subscriber capture its OWN separately-incremented
    // counter at Saved's callback-entry time -- too late: it missed the SAME-ORDER race during the
    // encode+write+recorder's own directory-resolve window BEFORE Saved ever fires, which is the
    // LIKELY interleaving for back-to-back bulk-WAV-decode restarts, not an unlikely edge case.
    // Owning the counter here, captured at the true start of the save, closes that window instead of
    // narrowing it.
    private int _generation;

    // Completion-detection state -- same technique, same field shapes as
    // ReceiveHistoryRecorder.cs's own (independently-solved) version of this exact problem: the
    // scanline-group step size (1 row per event for most families, 2 for PD/MP/RM8/RM12 per
    // IScanlineDecoder.RowsPerTransmissionLine, not exposed on the public SstvModeDefinition) isn't
    // known up front, so it's learned from the first two DecodedImageUpdate.Line deltas rather than
    // guessed.
    private int? _previousLine;
    private int? _observedStep;

    public ReceivedImageBuffer(ISstvDecoder decoder, ILogger<ReceivedImageBuffer> logger)
    {
        _logger = logger;
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

    public int Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public event Action? Updated;

    public event Action<string, int>? Saved;

    public Task SaveAsync(string path, CancellationToken ct = default)
    {
        IImageSource snapshot;
        int generation;
        lock (_gate)
        {
            snapshot = _current;
            generation = _generation;
        }

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

                // Isolated deliberately: this Task is the one this class's own callers await -- an
                // uncaught exception from a Saved subscriber (e.g. a live UI pane reacting to the
                // path) would fault THIS task, incorrectly reporting the save itself as failed even
                // though it fully succeeded. Same reasoning as ReceiveHistoryRecorder's own fan-out
                // handlers: a subscriber's own failure must never surface into/interrupt the
                // operation it's just reacting to.
                RaiseSaved(path, generation);
            },
            ct);
    }

    // Shared by SaveAsync above and NotifySaved below -- ReceiveHistoryRecorder's completed-image
    // path calls NotifySaved directly (it writes its own already-captured pixel snapshot rather
    // than going through SaveAsync, see IReceivedImageBuffer.NotifySaved's own doc comment for
    // why), but both call sites need the identical isolation-and-log contract for a throwing
    // subscriber.
    public void NotifySaved(string path, int generation) => RaiseSaved(path, generation);

    private void RaiseSaved(string path, int generation)
    {
        try
        {
            Saved?.Invoke(path, generation);
        }
        catch (Exception ex)
        {
            Log.SavedSubscriberFailed(_logger, path, ex);
        }
    }

    private void OnModeDetected(SstvModeDefinition mode)
    {
        lock (_gate)
        {
            _previousLine = null;
            _observedStep = null;
            _progress = 0.0;
            _generation++;
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

        // Reaches exactly 1.0 on the completing event for both step-1 and step-2 (PD/MP/RM8/RM12)
        // families without a separate snap: the completing event always has Line + step == Height
        // exactly (never overshoots it), so Clamp's own upper bound produces 1.0 naturally --
        // Tier B audit finding, correcting an earlier comment here that wrongly described this as
        // needing a special case.
        return Math.Clamp((double)(update.Line + step) / imageHeight, 0.0, 1.0);
    }

    private void OnDecodeRestarted(SstvModeDefinition abandonedMode)
    {
        lock (_gate)
        {
            _current = EmptyImage;
            _progress = null;
            _generation++;
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

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "A Saved event subscriber threw for {FilePath}")]
        public static partial void SavedSubscriberFailed(ILogger logger, string filePath, Exception ex);
    }
}
