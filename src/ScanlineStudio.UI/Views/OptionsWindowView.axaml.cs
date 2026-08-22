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
                // Tier C audit finding (risk): unguarded before this fix -- RequestClose can fire
                // AFTER the window already closed (e.g. the user closes it manually while the
                // settings-save/SetCultureAsync await is still in flight; the continuation then
                // reaches RequestClose?.Invoke() and calls Close() on an already-closed window).
                // Unsubscribing on Closed makes that a guaranteed no-op instead of relying on
                // Window.Close() being benign when called twice.
                Closed += (_, _) => vm.RequestClose -= Close;
            }
        };
    }
}
