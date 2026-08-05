using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class OptionsWindowView : Window
{
    public OptionsWindowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is OptionsWindowViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
