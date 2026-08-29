using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

/// <summary>The always-visible header strip: VFO / Favourites / Transceiver. DataContext is a
/// <see cref="ViewModels.RadioStatusViewModel"/>, supplied by <see cref="MainWindow"/>.</summary>
public partial class RadioHeaderView : UserControl
{
    public RadioHeaderView() => InitializeComponent();

    /// <summary>ui_transition_plan.md step 11 (T2-1): click-to-edit entry point for the VFO card's
    /// frequency readout -- <see cref="RadioStatusViewModel.BeginEditFrequencyCommand"/> flips
    /// <see cref="RadioStatusViewModel.IsEditingFrequency"/>, then this grabs focus for the
    /// now-visible <see cref="FrequencyEntry"/> TextBox. Same "Loaded-priority Post, best-effort"
    /// shape as <c>TxImageEditorPaneView.axaml.cs</c>'s own <c>FocusInlineTextEditor</c> -- the
    /// TextBox's own <c>IsVisible</c> binding hasn't necessarily applied/measured yet in the same
    /// synchronous callback that just flipped the flag.</summary>
    private void OnFrequencyReadoutPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not RadioStatusViewModel vm)
        {
            return;
        }

        // Left-button only (code-review fix, matches TxImageEditorPaneView.axaml.cs:205's own
        // precedent) -- a right- or middle-click has no "enter edit mode" meaning here (a
        // right-click especially reads as "open a context menu," not this).
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        vm.BeginEditFrequencyCommand.Execute(null);
        Dispatcher.UIThread.Post(
            () =>
            {
                FrequencyEntry.Focus();
                FrequencyEntry.SelectAll();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Enter applies (<see cref="RadioStatusViewModel.SetFrequencyCommand"/>, which itself
    /// only exits edit mode on success -- an invalid/out-of-range entry stays open so the operator
    /// can see the rejection and correct it); Escape cancels
    /// (<see cref="RadioStatusViewModel.CancelEditFrequencyCommand"/>, unconditional revert). Same
    /// "code-behind KeyDown, Enter commits, Escape cancels" idiom as
    /// <c>TxImageEditorPaneView.axaml.cs</c>'s own inline text-edit TextBox.</summary>
    private void OnFrequencyEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RadioStatusViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            vm.SetFrequencyCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelEditFrequencyCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Clicking away without pressing Enter/Escape treats the edit the same as Escape --
    /// deliberately never auto-COMMITS on focus loss, since this sends a real command to a real
    /// radio; an operator who clicks elsewhere mid-edit almost certainly didn't mean to apply
    /// whatever was left typed.</summary>
    private void OnFrequencyEntryLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RadioStatusViewModel { IsEditingFrequency: true } vm)
        {
            vm.CancelEditFrequencyCommand.Execute(null);
        }
    }
}
