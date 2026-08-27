using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class RestartRequiredDialogView : Window
{
    public RestartRequiredDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RestartRequiredDialogViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
