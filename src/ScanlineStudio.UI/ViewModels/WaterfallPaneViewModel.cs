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
/// just replaces <see cref="_pendingFrame"/> (latest-wins), it never queues a second post.</summary>
public sealed partial class WaterfallPaneViewModel : ViewModelBase
{
    private readonly object _gate = new();
    private WaterfallFrame? _pendingFrame;
    private bool _postScheduled;

    [ObservableProperty]
    private WaterfallFrame? _latestFrame;

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
