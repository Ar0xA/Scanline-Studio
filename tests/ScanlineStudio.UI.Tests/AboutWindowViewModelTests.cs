using System.Reflection;
using Avalonia.Headless.XUnit;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class AboutWindowViewModelTests
{
    [AvaloniaFact]
    public void Constructor_ParameterlessOverload_PopulatesApplicationNameAndVersion_NeverBlank()
    {
        // spec/18-path-to-1.0.md High item 5. The parameterless constructor reads
        // Assembly.GetEntryAssembly() -- under the test host that's the test RUNNER's own
        // assembly, not ScanlineStudio.Host, so this can't assert the exact real-app values (the
        // Assembly-injecting overload below does that instead). What it CAN assert -- and what's
        // worth catching a regression on -- is that the fallback chain never produces a blank/null
        // display value, which is the actual failure mode a broken assembly attribute lookup would
        // produce.
        var vm = new AboutWindowViewModel();

        Assert.False(string.IsNullOrWhiteSpace(vm.ApplicationName));
        Assert.False(string.IsNullOrWhiteSpace(vm.VersionDisplay));
    }

    [AvaloniaFact]
    public void Constructor_WithThisAssembly_PopulatesTheRealDirectoryBuildPropsStampedValues()
    {
        // Code-review finding: injecting this project's OWN assembly (which carries the real
        // Directory.Build.props Product/Version/Copyright, unlike the test runner's own assembly
        // the parameterless overload above sees) lets this test assert the ACTUAL real-app values
        // instead of only "is this non-blank" -- catches a regression like Product/Copyright
        // silently disappearing from Directory.Build.props, or the Version stamp reverting to the
        // .NET default "1.0.0.0".
        var vm = new AboutWindowViewModel(typeof(AboutWindowViewModel).Assembly);

        Assert.Equal("Scanline Studio", vm.ApplicationName);
        Assert.StartsWith("0.9.0", vm.VersionDisplay);
        Assert.False(string.IsNullOrWhiteSpace(vm.Copyright));
    }

    [AvaloniaFact]
    public void CloseCommand_RaisesRequestClose()
    {
        var vm = new AboutWindowViewModel();
        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.CloseCommand.Execute(null);

        Assert.True(closed);
    }
}
