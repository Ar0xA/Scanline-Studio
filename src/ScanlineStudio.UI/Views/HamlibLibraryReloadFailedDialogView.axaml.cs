using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class HamlibLibraryReloadFailedDialogView : Window
{
    public HamlibLibraryReloadFailedDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is HamlibLibraryReloadFailedDialogViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
