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
                // A Tune tone left running (PTT keyed) must not outlive this dialog -- covers every
                // close path (Save, Cancel, the window's own X button), not just Cancel, since none
                // of those routes is more "correct" to leave a transmitter keyed after.
                Closed += (_, _) => vm.StopTuneIfActive();
                // Same reasoning as StopTuneIfActive above, for the Radio/CAT tab's own Test PTT.
                Closed += (_, _) => vm.StopTestPttIfActive();
            }
        };
    }
}
