using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>The in-progress/completed decoded receive image, hosted in the fixed Receive tab's
/// "Incoming frame" card (spec/09-ui.md).
///
/// <b>Coalescing (Phase-3 plan finding #5)</b>: <see cref="IReceivedImageBuffer.Updated"/> fires
/// synchronously from the audio drain thread, once per decoded scanline group -- at most one
/// <c>Dispatcher.UIThread.Post</c> is ever in flight; a burst of updates while one is pending just
/// means the eventual post reads whatever <see cref="IReceivedImageBuffer.Current"/> is *then*
/// (latest-wins), not a queued backlog of every intermediate scanline.</summary>
public sealed partial class RxImagePaneViewModel : ViewModelBase
{
    private readonly IReceivedImageBuffer _receivedImage;
    private readonly object _gate = new();
    private bool _postScheduled;

    [ObservableProperty]
    private Bitmap? _image;

    /// <summary>The currently (or most recently) auto-detected RX mode -- real data from
    /// <see cref="ISstvSessionService.ModeDetected"/>. There is no manual "lock to a specific
    /// mode" decode feature in this port (<see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder"/>
    /// always auto-detects via the VIS header) -- the Receive tab's Mode card is real-data-only
    /// because of this; mock2's Auto/Locked segmented control and quick-mode-button grid have no
    /// backing feature and are intentionally omitted (spec/14-roadmap.md backlog).</summary>
    [ObservableProperty]
    private SstvModeDefinition? _detectedMode;

    public RxImagePaneViewModel(ISstvSessionService sstvSession)
    {
        _receivedImage = sstvSession.ReceivedImage;

        _receivedImage.Updated += OnUpdated;
        sstvSession.ModeDetected += OnModeDetected;
    }

    public string DetectedModeText => DetectedMode?.DisplayName ?? "—";

    public string LineTimeText => DetectedMode is { } mode ? $"{mode.LineDurationMs:0.0} ms" : "—";

    public string LinesText => DetectedMode is { } mode ? mode.ImageHeight.ToString(CultureInfo.InvariantCulture) : "—";

    partial void OnDetectedModeChanged(SstvModeDefinition? value)
    {
        OnPropertyChanged(nameof(DetectedModeText));
        OnPropertyChanged(nameof(LineTimeText));
        OnPropertyChanged(nameof(LinesText));
    }

    private void OnModeDetected(SstvModeDefinition mode)
    {
        Dispatcher.UIThread.Post(() => DetectedMode = mode);
    }

    private void OnUpdated()
    {
        lock (_gate)
        {
            if (_postScheduled)
            {
                return;
            }

            _postScheduled = true;
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_gate)
            {
                _postScheduled = false;
            }

            Image = ImageSourceBitmapConverter.ToBitmap(_receivedImage.Current);
        });
    }
}
