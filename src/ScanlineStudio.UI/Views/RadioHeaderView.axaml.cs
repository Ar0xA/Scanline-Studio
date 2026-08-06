using Avalonia.Controls;

namespace ScanlineStudio.UI.Views;

/// <summary>The always-visible header strip: VFO / Favourites / Transceiver. DataContext is a
/// <see cref="ViewModels.RadioStatusViewModel"/>, supplied by <see cref="MainWindow"/>.</summary>
public partial class RadioHeaderView : UserControl
{
    public RadioHeaderView() => InitializeComponent();
}
