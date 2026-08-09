namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>See spec/07-image-pipeline.md. Updated incrementally as
/// <c>ScanlineStudio.Abstractions.Sstv.ISstvDecoder.LineDecoded</c> fires; the UI binds to this for live
/// partial-image rendering (legacy `RxView`'s "image filling in top-to-bottom as it decodes"
/// behavior).</summary>
public interface IReceivedImageBuffer
{
    IImageSource Current { get; }

    /// <summary>How far the current decode has progressed, as a `[0.0, 1.0]` fraction of the image's
    /// total rows -- <see langword="null"/> when idle (no active decode). Reaches exactly
    /// <c>1.0</c> on the scanline group that completes the image, not just an asymptotic value close
    /// to it -- see <c>ReceivedImageBuffer</c>'s own doc comment for why a naive `Line / Height`
    /// alone can't reach exactly 1.0 for multi-row-per-event families.</summary>
    double? Progress { get; }

    /// <summary>Monotonic counter, bumped whenever the IDENTITY of <see cref="Current"/> changes (a
    /// fresh image starting, or the current one being blanked on a restart) -- as opposed to a pixel
    /// update to the SAME image, which does not bump it. Lets a subscriber of <see cref="Saved"/>
    /// detect whether a newer image has superseded the one a completed save actually wrote, by
    /// comparing the generation <see cref="Saved"/> was raised with against this property's
    /// then-current value.</summary>
    int Generation { get; }

    event Action? Updated;

    /// <summary>Fires with the destination path and the <see cref="Generation"/> value captured at
    /// the moment <see cref="SaveAsync"/> was invoked (not when the write completes) once that write
    /// to disk finishes -- the only real hook a live UI pane has for "what file did the just-completed
    /// image get saved to" (the sole production caller of <see cref="SaveAsync"/>, a history-recorder
    /// class in a different layer entirely, has no shared reference back to whatever pane is
    /// displaying <see cref="Current"/>). Raised on whatever thread the save's background work
    /// completes on -- same "subscriber marshals to the UI thread itself" contract as
    /// <see cref="Updated"/>. The generation is captured at invocation time specifically so a
    /// subscriber can detect a newer image having started (bumping <see cref="Generation"/>) at ANY
    /// point during the save -- encode, disk write, or the subscriber's own later reaction to this
    /// event -- not just after this event fires.</summary>
    event Action<string, int>? Saved;

    Task SaveAsync(string path, CancellationToken ct = default);
}
