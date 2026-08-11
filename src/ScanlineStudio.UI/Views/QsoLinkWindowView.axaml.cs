using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class QsoLinkWindowView : Window
{
    public QsoLinkWindowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is QsoLinkWindowViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
