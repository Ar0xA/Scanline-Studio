using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class RadioConnectionGaveUpWindowView : Window
{
    public RadioConnectionGaveUpWindowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RadioConnectionGaveUpWindowViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
