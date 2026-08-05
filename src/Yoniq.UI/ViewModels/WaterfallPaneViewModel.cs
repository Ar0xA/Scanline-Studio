using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using Yoniq.Abstractions.Localization;
using Yoniq.Abstractions.Sstv;
using Yoniq.Application;

namespace Yoniq.UI.ViewModels;

/// <summary>Real pane #1 of 3 (Phase-3 plan decision #9) -- derives from <see cref="Tool"/> directly
/// (the pane VM IS the dockable; see <see cref="SpikePaneViewModel"/>'s doc comment history for why).
///
/// <b>Coalescing (Phase-3 plan finding #5)</b>: <see cref="IWaterfallSource.Frames"/> pushes
/// synchronously from the audio drain thread and can arrive faster than the UI renders. At most one
/// <c>Dispatcher.UIThread.Post</c> is ever in flight -- a frame arriving while one is already pending
/// just replaces <see cref="_pendingFrame"/> (latest-wins), it never queues a second post.</summary>
public sealed partial class WaterfallPaneViewModel : Tool
{
    private readonly object _gate = new();
    private WaterfallFrame? _pendingFrame;
    private bool _postScheduled;

    [ObservableProperty]
    private WaterfallFrame? _latestFrame;

    public WaterfallPaneViewModel(ISstvSessionService sstvSession, ILocalizationService localization)
    {
        Id = "Waterfall";
        Title = localization.GetString("Panes.Waterfall.Title");
        localization.CultureChanged += () => Title = localization.GetString("Panes.Waterfall.Title");

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
