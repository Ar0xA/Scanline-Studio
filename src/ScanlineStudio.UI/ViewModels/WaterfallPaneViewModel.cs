using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Live spectrum/waterfall pane, hosted in the fixed Receive tab's "Spectrum & waterfall"
/// card (spec/09-ui.md).
///
/// <b>Coalescing (Phase-3 plan finding #5)</b>: <see cref="IWaterfallSource.Frames"/> pushes
/// synchronously from the audio drain thread and can arrive faster than the UI renders. At most one
/// <c>Dispatcher.UIThread.Post</c> is ever in flight -- a frame arriving while one is already pending
/// just replaces <see cref="_pendingFrame"/> (latest-wins), it never queues a second post.
///
/// <b>ZeroDb/GainDb defaults (batch 8)</b>: wired to mock2's "Zero"/"Gain" sliders. Chosen from real
/// measurement, not guessed -- fed all 8 real legacy-captured `.mmv` golden-vector fixtures
/// (tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors) through the actual
/// <see cref="IWaterfallSource"/>: real signal peaks consistently land 38-46dB, background/noise
/// floor consistently -50 to -65dB (this port's FFT magnitude is unnormalized, so its dB scale does
/// NOT match a conventional -100..0 dBFS convention -- see <c>ScanlineStudio.Core.Sstv.WaterfallSource</c>'s
/// own magnitude computation). Default <see cref="ZeroDb"/>=0 sits above the real noise floor (so ambient
/// noise stays near-black rather than painting the whole display); default <see cref="GainDb"/>=50
/// gives a 0-50dB window comfortably covering the measured 38-46dB real-signal peak range.</summary>
public sealed partial class WaterfallPaneViewModel : ViewModelBase
{
    private readonly object _gate = new();
    private WaterfallFrame? _pendingFrame;
    private bool _postScheduled;

    [ObservableProperty]
    private WaterfallFrame? _latestFrame;

    [ObservableProperty]
    private double _zeroDb = 0.0;

    [ObservableProperty]
    private double _gainDb = 50.0;

    public WaterfallPaneViewModel(ISstvSessionService sstvSession)
    {
        sstvSession.Waterfall.Frames.Subscribe(OnFrame);
    }

    private void OnFrame(WaterfallFrame frame)
    {
        lock (_gate)
        {
            _pendingFrame = frame;
            if (_postScheduled)
            {
                return;
            }

            _postScheduled = true;
        }

        Dispatcher.UIThread.Post(() =>
        {
            WaterfallFrame frameToShow;
            lock (_gate)
            {
                frameToShow = _pendingFrame!;
                _postScheduled = false;
            }

            LatestFrame = frameToShow;
        });
    }
}
