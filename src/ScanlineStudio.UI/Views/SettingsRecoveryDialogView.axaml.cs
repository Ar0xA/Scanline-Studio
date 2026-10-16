using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class SettingsRecoveryDialogView : Window
{
    public SettingsRecoveryDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsRecoveryDialogViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
