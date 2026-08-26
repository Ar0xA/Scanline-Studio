using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class ToneGeneratorWindowView : Window
{
    public ToneGeneratorWindowView()
    {
        InitializeComponent();

        // A Tune tone left running (PTT keyed) must not outlive this dialog -- same reasoning as
        // OptionsWindowView.axaml.cs's own StopTuneIfActive wiring, for every close path (the
        // window's own Close button and the title-bar X alike).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RadioStatusViewModel vm)
            {
                Closed += (_, _) => vm.StopTuneIfActive();
            }
        };
    }

    private void CloseButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
