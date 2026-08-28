namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Result of <c>ScanlineStudio.Application.ISstvSessionService.RequestCaptureDeviceAsync</c>
/// -- Configurations-preset backlog item, Phase 1 (2026-08-28), same shape as
/// <see cref="SampleRateApplyResult"/> (that member's own sibling), see that member's own doc comment
/// for the shared design rationale. Covers every EXPECTED outcome; an RX-transition-gate timeout
/// throws <see cref="TimeoutException"/> instead (matching <c>RequestSampleRateAsync</c>'s own
/// established choice).</summary>
public enum CaptureDeviceApplyResult
{
    /// <summary>The requested capture device is now live -- if RX capture was running, it has been
    /// reopened using this device (or, if the requested device could no longer be found at the
    /// moment of reopening, whatever device the existing resolver fell back to -- see
    /// <see cref="Rejected"/> for the case where NOTHING could be resolved at all).</summary>
    Applied,

    /// <summary>The requested device already matched what this method last committed to -- nothing
    /// was touched, no capture was disturbed. Compares against an in-memory latch (last device ID
    /// this method itself set, whether or not capture ever actually opened with it -- e.g. it's set
    /// on the idle/not-receiving path too), NOT persisted settings -- a device change made only
    /// through the Options dialog (never applied via this method) will not be reflected here.</summary>
    NoChange,

    /// <summary>Deferred: a recording is currently in progress. Nothing was changed -- retry once the
    /// recording stops.</summary>
    DeferredRecordingInProgress,

    /// <summary>The requested device failed to actually open -- either it (and every fallback the
    /// existing resolver tries) could not be RESOLVED to any real device at all (an extremely rare
    /// case, e.g. a headless machine with no capture hardware at the moment of reopening), or it
    /// resolved but the native backend refused to OPEN it (the realistic case: exclusive use, an
    /// unsupported sample rate, unplugged in the gap between resolve and open). The persisted device
    /// id/name has been rolled back to whatever was active before this call, and if RX capture was
    /// running, one restart attempt was made using that rolled-back value; if even that failed, RX
    /// capture is left stopped. One attempt only, not automatically retried.</summary>
    Rejected,
}
