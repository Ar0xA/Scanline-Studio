using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class ConfirmActionDialogView : Window
{
    public ConfirmActionDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConfirmActionDialogViewModel vm)
            {
                // Action<bool> can't method-group-convert to Close(object?) -- bool->object is a
                // boxing conversion, not the reference conversion TextPromptWindowView's own
                // Action<string?> -> Close(object?) relies on. Wired explicitly instead.
                vm.RequestClose += confirmed => Close(confirmed);
            }
        };
    }
}
