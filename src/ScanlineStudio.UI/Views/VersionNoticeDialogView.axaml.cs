using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class VersionNoticeDialogView : Window
{
    public VersionNoticeDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is VersionNoticeDialogViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
