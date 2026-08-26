using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class StorageSettingsWindowView : Window
{
    public StorageSettingsWindowView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is StorageSettingsWindowViewModel vm)
            {
                vm.RequestClose += Close;
                // Same reasoning as QsoLinkWindowView.axaml.cs's own fix: RequestClose can fire
                // AFTER the window already closed (e.g. the user X-es out while SaveAsync's own
                // await is still in flight); unsubscribing on Closed makes a late invoke a
                // guaranteed no-op instead of relying on Window.Close() being benign when called twice.
                Closed += (_, _) => vm.RequestClose -= Close;
            }
        };
    }
}
