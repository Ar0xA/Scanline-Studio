using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class LoopbackSelfTestResultWindowView : Window
{
    public LoopbackSelfTestResultWindowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LoopbackSelfTestResultWindowViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
