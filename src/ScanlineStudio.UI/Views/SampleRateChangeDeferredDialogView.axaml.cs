using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class SampleRateChangeDeferredDialogView : Window
{
    public SampleRateChangeDeferredDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SampleRateChangeDeferredDialogViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
