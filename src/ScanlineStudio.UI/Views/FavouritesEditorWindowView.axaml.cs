using Avalonia.Controls;

namespace ScanlineStudio.UI.Views;

public partial class FavouritesEditorWindowView : Window
{
    public FavouritesEditorWindowView()
    {
        InitializeComponent();
    }

    private void CloseButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
