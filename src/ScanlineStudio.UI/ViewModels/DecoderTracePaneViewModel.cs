using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Un-stub-RX-tab Piece B2: Decode Activity card's "DECODER TRACE" column -- port of
/// legacy's <c>TTScope</c> (`Scope.cpp`), a manual single-shot capture oscilloscope showing two
/// channels: channel 0 (the VIS/sync-envelope detector, d12 or d19) and channel 1 (demodulated
/// picture frequency). Legacy's own real UI was dead as shipped (<c>CScope::GetFlag</c> always
/// returns 0, so <c>PaintScope</c>/<c>TimerTimer</c>'s own repaint never actually ran) -- this is a
/// revival of the real, cited pan/zoom/gain math and pixel-mapping formulas
/// (<c>Scope.cpp:247-347</c>), not a fresh invention; see <see cref="DecoderTraceControl"/>'s own
/// doc comment for the rendering side.
///
/// <b>Deliberate simplification vs. legacy, not silently dropped</b>: legacy's zoom
/// (<c>SBUpWClick</c>/<c>SBDownWClick</c>) re-centers the visible window on wherever the user last
/// clicked the plot (<c>m_CursorX</c>/<c>AdjXoff</c>) -- this port has no click-to-position-cursor
/// gesture (B2's own scope keeps all interaction on buttons, not the plot itself), so zooming here
/// keeps the window's LEFT edge fixed instead, clamped back into bounds the same way. A real UX
/// divergence, not a DSP-fidelity one -- spec/06's port-first scoping only covers DSP/codec math.</summary>
public sealed partial class DecoderTracePaneViewModel : ViewModelBase
{
    /// <summary>Legacy's own <c>SCOPESIZE</c> (`sstv.h:187`) -- the fixed capture size this port's
    /// <see cref="ISstvSessionService.ArmScopeCapture"/> is always called with.</summary>
    public const int ScopeSize = 8192;

    /// <summary>Same poll cadence as <c>RxImagePaneViewModel</c>'s own telemetry timer -- no
    /// push/event mechanism exists for "a capture just completed" (<see cref="ISstvSessionService"/>'s
    /// own doc comments describe polling), 250ms is well under legacy's own real-time paint cadence
    /// while cheap enough to run only while a capture is actually in flight (see
    /// <see cref="_pollTimer"/>'s own start/stop around <see cref="Capture"/>).</summary>
    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ISstvSessionService _sstvSession;
    private readonly DispatcherTimer _pollTimer;

    /// <summary>Auditor code-review finding: <see cref="ISstvSessionService.ArmScopeCapture"/> only
    /// latches a deferred request -- the decoder-side buffers don't actually reset (see
    /// <c>ScopeCaptureBuffer.Arm</c>'s own doc comment) until the NEXT <c>PushSamples</c> call
    /// drains it. If no reception is active between pressing Capture and the first poll tick (RX
    /// stopped, audio device closed), <see cref="PollCapture"/> would otherwise read the PREVIOUS
    /// capture's still-published array and present it as fresh, silently. These two fields hold
    /// whatever <see cref="TryGetScopeCaptureChannel0"/>/<c>Channel1</c> already reported at
    /// <see cref="Capture"/> time, so <see cref="PollCapture"/> can tell "still the old array" (via
    /// <see cref="ReferenceEquals"/> -- <c>ScopeCaptureBuffer.Arm</c> always allocates a genuinely
    /// new <c>double[]</c>, so a real new capture can never be reference-equal to the old one) apart
    /// from "not filled yet" (a plain <see langword="null"/>), which a simple "wait for null first"
    /// check cannot: the buffer can fill and re-publish entirely between two 250ms poll ticks, so a
    /// caller watching for a transient null could miss it and hang forever.</summary>
    private double[]? _previousChannel0Snapshot;
    private double[]? _previousChannel1Snapshot;

    [ObservableProperty]
    private double[]? _channel0Snapshot;

    [ObservableProperty]
    private double[]? _channel1Snapshot;

    [ObservableProperty]
    private bool _isCapturing;

    /// <summary>Legacy's own default (`Scope.cpp:29`, <c>m_XOFF = (8192 - m_XW)/2</c> at 2048's own
    /// default <see cref="XWindow"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowRangeDisplay))]
    [NotifyPropertyChangedFor(nameof(CanPanLeft))]
    [NotifyPropertyChangedFor(nameof(CanPanRight))]
    private int _xOffset = 3072;

    /// <summary>Legacy's own default (`Scope.cpp:29`).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowRangeDisplay))]
    [NotifyPropertyChangedFor(nameof(CanNarrowWindow))]
    [NotifyPropertyChangedFor(nameof(CanWidenWindow))]
    private int _xWindow = 2048;

    /// <summary>Legacy's own default (`Scope.cpp:32`).</summary>
    [ObservableProperty]
    private double _gain = 2.0;

    private readonly ILocalizationService _localization;

    public DecoderTracePaneViewModel(ISstvSessionService sstvSession, ILocalizationService localization)
    {
        _sstvSession = sstvSession;
        _localization = localization;
        _pollTimer = new DispatcherTimer(CapturePollInterval, DispatcherPriority.Background, (_, _) => PollCapture());
    }

    public string WindowRangeDisplay => _localization.GetString("Panes.RxDecodeLog.TraceRangeFormat", XOffset, XOffset + XWindow, ScopeSize);

    /// <summary>Matches legacy's own <c>LeftBtn->Enabled</c> condition (<c>UpdateBtn</c>,
    /// `Scope.cpp:233-238`).</summary>
    public bool CanPanLeft => XOffset > 0;

    /// <summary>Matches legacy's own <c>RightBtn->Enabled</c> condition (`Scope.cpp:239-244`).</summary>
    public bool CanPanRight => XOffset + XWindow < ScopeSize;

    /// <summary>Matches legacy's own <c>SBDownW->Enabled</c> condition (`Scope.cpp:227-232`).</summary>
    public bool CanNarrowWindow => XWindow >= 64;

    /// <summary>Matches legacy's own <c>SBUpW->Enabled</c> condition (`Scope.cpp:221-226`).</summary>
    public bool CanWidenWindow => XWindow <= ScopeSize - 512;

    /// <summary>Port of legacy's <c>TrigNext</c> (`Scope.cpp:83-89`, fired by <c>SBTrigClick</c>) --
    /// arms a fresh capture and starts polling for it to complete. A re-capture while one is already
    /// in flight is a legitimate re-trigger (matches <c>ArmScopeCapture</c>'s own re-arm semantics),
    /// not blocked here.</summary>
    [RelayCommand]
    private void Capture()
    {
        // See _previousChannel0Snapshot's own doc comment -- captured BEFORE arming, so PollCapture
        // can tell a genuinely new fill apart from the still-published previous one.
        _previousChannel0Snapshot = _sstvSession.TryGetScopeCaptureChannel0();
        _previousChannel1Snapshot = _sstvSession.TryGetScopeCaptureChannel1();
        Channel0Snapshot = null;
        Channel1Snapshot = null;
        IsCapturing = true;
        _sstvSession.ArmScopeCapture(ScopeSize);
        _pollTimer.Start();
    }

    /// <summary>Channel 0 always eventually fills (it writes on every sample regardless of lock
    /// state) -- that's the stopping condition for the poll. Channel 1 may legitimately never fill
    /// (no active reception since the arm, or AVT) -- once channel 0 is done, whatever channel 1
    /// has (data or still <see langword="null"/>) is final for this capture, matching legacy's own
    /// two independently-gated <c>CScope</c> instances.</summary>
    private void PollCapture()
    {
        var channel1 = _sstvSession.TryGetScopeCaptureChannel1();
        if (!ReferenceEquals(channel1, _previousChannel1Snapshot))
        {
            Channel1Snapshot = channel1;
        }

        var channel0 = _sstvSession.TryGetScopeCaptureChannel0();
        if (channel0 is null || ReferenceEquals(channel0, _previousChannel0Snapshot))
        {
            return;
        }

        Channel0Snapshot = channel0;
        IsCapturing = false;
        _pollTimer.Stop();
    }

    /// <summary>Port of legacy's <c>LeftBtnClick</c> (`Scope.cpp:247-254`) -- pan back by a quarter
    /// window, clamped to 0.</summary>
    [RelayCommand]
    private void PanLeft()
    {
        if (XOffset <= 0)
        {
            return;
        }

        XOffset = Math.Max(0, XOffset - XWindow / 4);
    }

    /// <summary>Port of legacy's <c>RightBtnClick</c> (`Scope.cpp:256-263`) -- pan forward by a
    /// quarter window, clamped so the window never runs past <see cref="ScopeSize"/>.</summary>
    [RelayCommand]
    private void PanRight()
    {
        var next = XOffset + XWindow / 4;
        XOffset = Math.Min(next, ScopeSize - XWindow);
    }

    /// <summary>Port of legacy's <c>SBDownWClick</c> (`Scope.cpp:282-296`) -- narrows the visible
    /// window (more detail): -512 samples down to 1024, then -32 samples down to the 64-sample
    /// floor. The exact two-tier step size, not a single "halve" -- verified directly against
    /// source, an earlier draft of this port's own plan wrongly described this as "zoom-double/
    /// halve".</summary>
    [RelayCommand]
    private void NarrowWindow()
    {
        if (XWindow >= 1024)
        {
            XWindow -= 512;
        }
        else if (XWindow >= 64)
        {
            XWindow -= 32;
        }

        ClampXOffsetToWindow();
    }

    /// <summary>Port of legacy's <c>SBUpWClick</c> (`Scope.cpp:298-312`) -- widens the visible
    /// window (less detail, more history): +32 samples up to 512, then +512 samples up to
    /// <see cref="ScopeSize"/>.</summary>
    [RelayCommand]
    private void WidenWindow()
    {
        if (XWindow < 512)
        {
            XWindow += 32;
        }
        else if (XWindow <= ScopeSize - 512)
        {
            XWindow += 512;
        }

        ClampXOffsetToWindow();
    }

    private void ClampXOffsetToWindow()
    {
        if (XOffset < 0)
        {
            XOffset = 0;
        }
        else if (XOffset + XWindow > ScopeSize)
        {
            XOffset = Math.Max(0, ScopeSize - XWindow);
        }
    }

    /// <summary>Port of legacy's <c>UpBtnClick</c> (`Scope.cpp:314-318`) -- ×1.2 per press, not an
    /// additive step.</summary>
    [RelayCommand]
    private void GainUp() => Gain *= 1.2;

    /// <summary>Port of legacy's <c>DownBtnClick</c> (`Scope.cpp:320-324`).</summary>
    [RelayCommand]
    private void GainDown() => Gain /= 1.2;

    /// <summary>Port of legacy's <c>SBUpDownClick</c> (`Scope.cpp:334-348`) -- sets gain so channel
    /// 1's own peak magnitude within the CURRENT visible window fills 80% of its half of the plot.
    /// A no-op if channel 1 has no data in the visible window (peak stays 0) -- matches legacy's own
    /// <c>if(peak)</c> guard.</summary>
    [RelayCommand]
    private void AutoGain()
    {
        if (Channel1Snapshot is not { } channel1)
        {
            return;
        }

        var peak = 0.0;
        var end = Math.Min(XOffset + XWindow, channel1.Length);
        for (var i = Math.Max(0, XOffset); i < end; i++)
        {
            peak = Math.Max(peak, Math.Abs(channel1[i]));
        }

        if (peak > 0)
        {
            Gain = 16384.0 * 0.8 / peak;
        }
    }
}
