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

    [AvaloniaFact]
    public void OpenConfigCommand_RaisesBothRequestCloseAndConfigRequested()
    {
        var vm = new RadioConnectionGaveUpWindowViewModel("Could not connect: simulated failure");
        var closed = false;
        var configRequested = false;
        vm.RequestClose += () => closed = true;
        vm.ConfigRequested += () => configRequested = true;

        vm.OpenConfigCommand.Execute(null);

        Assert.True(closed);
        Assert.True(configRequested);
    }

    [AvaloniaFact]
    public void OpenConfigCommand_RaisesRequestCloseBeforeConfigRequested()
    {
        // MainWindow.axaml.cs's own ConfigRequested handler opens a SECOND modal window owned by
        // the same MainWindow -- this popup must actually close first, not just "close" and
        // "request config" firing in an unspecified order that happens to look fine today.
        var vm = new RadioConnectionGaveUpWindowViewModel("Could not connect: simulated failure");
        var order = new List<string>();
        vm.RequestClose += () => order.Add("close");
        vm.ConfigRequested += () => order.Add("config");

        vm.OpenConfigCommand.Execute(null);

        Assert.Equal(["close", "config"], order);
    }
}
