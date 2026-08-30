namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>See spec/07-image-pipeline.md. Updated incrementally as
/// <c>ScanlineStudio.Abstractions.Sstv.ISstvDecoder.LineDecoded</c> fires; the UI binds to this for live
/// partial-image rendering (legacy `RxView`'s "image filling in top-to-bottom as it decodes"
/// behavior).</summary>
public interface IReceivedImageBuffer
{
    /// <summary>Every read returns an independent, safely-retainable snapshot -- never a view into
    /// a live/mutable decoder buffer, and never an array a later implementation detail writes into
    /// again after handing it out. Safe to hold indefinitely (a real consumer does: the TX image
    /// editor inserts this into template state kept until the user removes/undoes it).</summary>
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

    /// <summary>Fires with a destination path and a <see cref="Generation"/> value captured at the
    /// moment the corresponding write was invoked (not when the write completes), once that write to
    /// disk finishes -- via either <see cref="SaveAsync"/> itself, or <see cref="NotifySaved"/> for a
    /// caller that wrote its own already-captured snapshot. The only real hook a live UI pane has for
    /// "what file did the just-completed image get saved to" (the production callers -- a manual
    /// pane save, and a history-recorder class in a different layer entirely -- have no shared
    /// reference back to whatever pane is displaying <see cref="Current"/>). Raised on whatever
    /// thread the save's background work completes on -- same "subscriber marshals to the UI thread
    /// itself" contract as <see cref="Updated"/>. The generation is captured at invocation time
    /// specifically so a subscriber can detect a newer image having started (bumping
    /// <see cref="Generation"/>) at ANY point during the save -- encode, disk write, or the
    /// subscriber's own later reaction to this event -- not just after this event fires.</summary>
    event Action<string, int>? Saved;

    Task SaveAsync(string path, CancellationToken ct = default);

    /// <summary>Raises <see cref="Saved"/> directly, for a caller that already wrote its own pixel
    /// snapshot to disk without going through <see cref="SaveAsync"/> -- <c>ReceiveHistoryRecorder</c>'s
    /// completed-image path uses this, because reading <see cref="Current"/> asynchronously (as
    /// <see cref="SaveAsync"/> itself does) races a <c>DecodeRestarted</c> that can blank
    /// <see cref="Current"/> before the read happens. <paramref name="generation"/> must be
    /// <see cref="Generation"/>'s value captured at the same synchronous point the caller captured
    /// the pixel snapshot it saved, not this property's later value -- see <see cref="Saved"/>'s own
    /// doc comment for what a mismatch means to a subscriber.</summary>
    void NotifySaved(string path, int generation);
}
