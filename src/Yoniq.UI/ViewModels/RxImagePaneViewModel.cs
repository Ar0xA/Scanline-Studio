using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Localization;
using Yoniq.Abstractions.Sstv;
using Yoniq.Application;
using Yoniq.UI.Imaging;

namespace Yoniq.UI.ViewModels;

/// <summary>Real pane #2 of 3 (Phase-3 plan decision #9).
///
/// <b>Coalescing (Phase-3 plan finding #5)</b>: <see cref="IReceivedImageBuffer.Updated"/> fires
/// synchronously from the audio drain thread, once per decoded scanline group -- at most one
/// <c>Dispatcher.UIThread.Post</c> is ever in flight; a burst of updates while one is pending just
/// means the eventual post reads whatever <see cref="IReceivedImageBuffer.Current"/> is *then*
/// (latest-wins), not a queued backlog of every intermediate scanline.</summary>
public sealed partial class RxImagePaneViewModel : Tool
{
    private readonly IReceivedImageBuffer _receivedImage;
    private readonly object _gate = new();
    private bool _postScheduled;

    [ObservableProperty]
    private Bitmap? _image;

    public RxImagePaneViewModel(ISstvSessionService sstvSession, ILocalizationService localization)
    {
        _receivedImage = sstvSession.ReceivedImage;

        Id = "RxImage";
        Title = localization.GetString("Panes.RxImage.Title");
        localization.CultureChanged += () => Title = localization.GetString("Panes.RxImage.Title");

        _receivedImage.Updated += OnUpdated;
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
