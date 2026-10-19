using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class UnexpectedErrorDialogView : Window
{
    public UnexpectedErrorDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is UnexpectedErrorDialogViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
