using Avalonia.Controls;

namespace ScanlineStudio.UI.Views;

/// <summary>
/// The always-visible header strip: VFO / Favourites / Transceiver.
/// DataContext is a RadioStatusViewModel, supplied by the host.
/// </summary>
public partial class RadioHeaderView : UserControl
{
    public RadioHeaderView() => InitializeComponent();
}
