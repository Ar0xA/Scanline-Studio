using Avalonia.Controls;
using Avalonia.Interactivity;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class TxControlsPaneView : UserControl
{
    public TxControlsPaneView()
    {
        InitializeComponent();
    }

    /// <summary>TX history plan (2026-09-01): same code-behind-Click pattern MainWindow.axaml.cs's
    /// OnPreviousFrameThumbnailDoubleTapped/OnGalleryThumbnailDoubleTapped already use for a
    /// DataTemplate item reaching back to its containing pane's own command -- see the strip's own
    /// AXAML comment for why this is NOT a XAML ancestor-cast Command binding.</summary>
    private void OnSentFrameResendClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TransmittedFrameViewModel entry } && DataContext is TxControlsPaneViewModel vm)
        {
            vm.ResendSentFrameCommand.Execute(entry);
        }
    }

    private void OnSentFrameSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TransmittedFrameViewModel entry } && DataContext is TxControlsPaneViewModel vm)
        {
            vm.SaveSentFrameCommand.Execute(entry);
        }
    }
}
