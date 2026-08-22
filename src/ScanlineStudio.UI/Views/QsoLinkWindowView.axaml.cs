using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class QsoLinkWindowView : Window
{
    public QsoLinkWindowView()
    {
        InitializeComponent();

        // Tier C audit finding (risk): the title-bar X / Alt+F4 close had no guard against a
        // still-in-flight logbook/QRZ write (LogQsoAsync's own IsBusy gate already covers the Cancel
        // COMMAND, per that method's own doc comment, but not this window-level close path). Closing
        // mid-write hides the only surface that can display QsoLink.Error.LoggedButLinkFailed, and
        // since IsBusy/_createdQsoId are per-VM-instance state that a fresh dialog open doesn't carry
        // over, a user who X-es out and retries can log a second QsoRecord and push it to
        // ADIF-UDP/QRZ a second time -- the exact duplicate this VM's own design already guards
        // against everywhere else.
        Closing += (_, e) =>
        {
            if (DataContext is QsoLinkWindowViewModel { IsBusy: true })
            {
                e.Cancel = true;
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is QsoLinkWindowViewModel vm)
            {
                vm.RequestClose += Close;
                // Tier C audit finding (risk): unguarded before this fix -- RequestClose can fire
                // AFTER the window already closed (e.g. the user closes it manually while
                // LogQsoAsync's own await -- a real QRZ HTTP POST -- is still in flight; the
                // continuation then reaches RequestClose?.Invoke() and calls Close() on an
                // already-closed window). Unsubscribing on Closed makes that a guaranteed no-op
                // instead of relying on Window.Close() being benign when called twice.
                Closed += (_, _) => vm.RequestClose -= Close;
            }
        };
    }
}
