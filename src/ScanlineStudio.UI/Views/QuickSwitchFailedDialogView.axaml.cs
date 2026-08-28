using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class QuickSwitchFailedDialogView : Window
{
    public QuickSwitchFailedDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is QuickSwitchFailedDialogViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
