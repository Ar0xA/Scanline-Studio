using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class MacrosReferenceWindowView : Window
{
    public MacrosReferenceWindowView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MacrosReferenceWindowViewModel vm)
            {
                vm.RequestClose += Close;
                Closed += (_, _) => vm.RequestClose -= Close;
            }
        };
    }
}
