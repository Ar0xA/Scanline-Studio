using Avalonia.Headless.XUnit;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class RadioConnectionGaveUpWindowViewModelTests
{
    [AvaloniaFact]
    public void Constructor_PopulatesMessage()
    {
        var vm = new RadioConnectionGaveUpWindowViewModel("Could not connect: simulated failure");

        Assert.Equal("Could not connect: simulated failure", vm.Message);
    }

    [AvaloniaFact]
    public void CloseCommand_RaisesRequestClose()
    {
        var vm = new RadioConnectionGaveUpWindowViewModel("Could not connect: simulated failure");
        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.CloseCommand.Execute(null);

        Assert.True(closed);
    }
}
