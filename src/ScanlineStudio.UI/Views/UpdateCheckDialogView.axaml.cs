using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class UpdateCheckDialogView : Window
{
    public UpdateCheckDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is UpdateCheckDialogViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
