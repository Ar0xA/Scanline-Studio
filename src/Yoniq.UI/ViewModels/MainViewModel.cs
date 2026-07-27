using CommunityToolkit.Mvvm.ComponentModel;

namespace Yoniq.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "Welcome to Yoniq v2!";
}
