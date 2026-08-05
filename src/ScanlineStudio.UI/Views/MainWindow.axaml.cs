using Avalonia;
using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.OptionsRequested += optionsViewModel => new OptionsWindowView { DataContext = optionsViewModel }.ShowDialog(this);
            }
        };
    }
}
