using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class AboutWindowView : Window
{
    public AboutWindowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AboutWindowViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
