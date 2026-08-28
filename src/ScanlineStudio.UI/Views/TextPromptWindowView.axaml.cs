using Avalonia.Controls;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class TextPromptWindowView : Window
{
    public TextPromptWindowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TextPromptWindowViewModel vm)
            {
                // Configurations-preset backlog, Phase 4 (2026-08-28): the first RequestClose in this
                // codebase that carries a result -- Close(string?) (Window's own generic-ShowDialog
                // close overload), not the parameterless Close the acknowledgement-only dialogs use.
                vm.RequestClose += Close;
            }
        };
    }
}
