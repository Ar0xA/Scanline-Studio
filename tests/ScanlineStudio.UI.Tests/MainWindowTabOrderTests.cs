using System.Text.RegularExpressions;

namespace ScanlineStudio.UI.Tests;

/// <summary>Round-1 plan-review finding (rx-log-qso.md, 2026-08-15): <see cref="ScanlineStudio.UI.ViewModels.MainViewModel.LogbookTabIndex"/>
/// is a hardcoded index into `MainWindow.axaml`'s main `TabControl` -- nothing would catch a future
/// tab reorder silently making "Log QSO" switch to the wrong tab. Text-based (regex over the real
/// source, not a runtime-parsed AXAML tree) matching this repo's own established convention for
/// this kind of structural check, see <see cref="NoHardcodedAxamlStringsTests"/>.</summary>
public sealed partial class MainWindowTabOrderTests
{
    [GeneratedRegex("<TabItem\\s+Classes=\"Industry\"\\s+Header=\"\\{loc:Translate\\s+([^}]+)\\}\"")]
    private static partial Regex TabItemHeaderRegex();

    [Fact]
    public void MainTabControl_LogbookTabIndex_PointsAtTheActualLogbookTab()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var mainWindowPath = Path.Combine(uiSourceDirectory, "Views", "MainWindow.axaml");
        var text = File.ReadAllText(mainWindowPath);

        var headers = TabItemHeaderRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

        Assert.True(
            ViewModels.MainViewModel.LogbookTabIndex < headers.Count,
            $"MainViewModel.LogbookTabIndex ({ViewModels.MainViewModel.LogbookTabIndex}) is out of range for the {headers.Count} top-level TabItems found in MainWindow.axaml.");
        Assert.Equal("MainWindow.Tabs.Logbook", headers[ViewModels.MainViewModel.LogbookTabIndex]);
    }

    /// <summary>Same reasoning as <see cref="MainTabControl_LogbookTabIndex_PointsAtTheActualLogbookTab"/>
    /// above, for <see cref="ViewModels.MainViewModel.GalleryTabIndex"/>'s disk/DB reconcile
    /// trigger (<see cref="ViewModels.MainViewModel.OnSelectedTabIndexChanged"/>) -- a silent tab
    /// reorder would otherwise fire the reconcile scan on the wrong tab (or never, if it silently
    /// pointed at an out-of-range index).</summary>
    [Fact]
    public void MainTabControl_GalleryTabIndex_PointsAtTheActualGalleryTab()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var mainWindowPath = Path.Combine(uiSourceDirectory, "Views", "MainWindow.axaml");
        var text = File.ReadAllText(mainWindowPath);

        var headers = TabItemHeaderRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

        Assert.True(
            ViewModels.MainViewModel.GalleryTabIndex < headers.Count,
            $"MainViewModel.GalleryTabIndex ({ViewModels.MainViewModel.GalleryTabIndex}) is out of range for the {headers.Count} top-level TabItems found in MainWindow.axaml.");
        Assert.Equal("MainWindow.Tabs.Gallery", headers[ViewModels.MainViewModel.GalleryTabIndex]);
    }

    /// <summary>Same reasoning as <see cref="MainTabControl_LogbookTabIndex_PointsAtTheActualLogbookTab"/>
    /// above, for <see cref="ViewModels.MainViewModel.SaveOrApplyCommand"/>'s contextual Ctrl+S
    /// routing (ui_transition_plan.md step 2) -- a silent tab reorder would otherwise route Ctrl+S
    /// to Apply on the wrong tab, or to Apply while Receive is selected.</summary>
    [Fact]
    public void MainTabControl_TransmitTabIndex_PointsAtTheActualTransmitTab()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var mainWindowPath = Path.Combine(uiSourceDirectory, "Views", "MainWindow.axaml");
        var text = File.ReadAllText(mainWindowPath);

        var headers = TabItemHeaderRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

        Assert.True(
            ViewModels.MainViewModel.TransmitTabIndex < headers.Count,
            $"MainViewModel.TransmitTabIndex ({ViewModels.MainViewModel.TransmitTabIndex}) is out of range for the {headers.Count} top-level TabItems found in MainWindow.axaml.");
        Assert.Equal("MainWindow.Tabs.Transmit", headers[ViewModels.MainViewModel.TransmitTabIndex]);
    }

    /// <summary>Same reasoning as <see cref="MainTabControl_LogbookTabIndex_PointsAtTheActualLogbookTab"/>
    /// above, for the header-row callsign chip's jump target: <see cref="ViewModels.OptionsWindowViewModel.TxTabIndex"/>
    /// is a hardcoded index into `OptionsWindowView.axaml`'s own `TabControl` -- nothing would catch a
    /// future tab reorder silently sending the chip to the wrong tab.</summary>
    [Fact]
    public void OptionsTabControl_TxTabIndex_PointsAtTheActualTxTab()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var optionsWindowPath = Path.Combine(uiSourceDirectory, "Views", "OptionsWindowView.axaml");
        var text = File.ReadAllText(optionsWindowPath);

        var headers = TabItemHeaderRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

        Assert.True(
            ViewModels.OptionsWindowViewModel.TxTabIndex < headers.Count,
            $"OptionsWindowViewModel.TxTabIndex ({ViewModels.OptionsWindowViewModel.TxTabIndex}) is out of range for the {headers.Count} top-level TabItems found in OptionsWindowView.axaml.");
        Assert.Equal("Options.Tx.Tab", headers[ViewModels.OptionsWindowViewModel.TxTabIndex]);
    }

    /// <summary>Same reasoning as <see cref="OptionsTabControl_TxTabIndex_PointsAtTheActualTxTab"/>
    /// above, for the radio-connection give-up popup's own "Config" button jump target: see
    /// <see cref="ViewModels.OptionsWindowViewModel.RadioTabIndex"/>.</summary>
    [Fact]
    public void OptionsTabControl_RadioTabIndex_PointsAtTheActualRadioTab()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var optionsWindowPath = Path.Combine(uiSourceDirectory, "Views", "OptionsWindowView.axaml");
        var text = File.ReadAllText(optionsWindowPath);

        var headers = TabItemHeaderRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

        Assert.True(
            ViewModels.OptionsWindowViewModel.RadioTabIndex < headers.Count,
            $"OptionsWindowViewModel.RadioTabIndex ({ViewModels.OptionsWindowViewModel.RadioTabIndex}) is out of range for the {headers.Count} top-level TabItems found in OptionsWindowView.axaml.");
        Assert.Equal("Options.Radio.Tab", headers[ViewModels.OptionsWindowViewModel.RadioTabIndex]);
    }

    [Fact]
    public void HelpMenu_UserGuide_IsLocalizedAndBoundToItsCommand()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var mainWindowPath = Path.Combine(uiSourceDirectory, "Views", "MainWindow.axaml");
        var text = File.ReadAllText(mainWindowPath);

        Assert.Contains(
            "Header=\"{loc:Translate MainWindow.Menu.Help.UserGuide}\" Command=\"{Binding OpenUserGuideCommand}\"",
            text,
            StringComparison.Ordinal);
    }

    private static string FindUiSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (ScanlineStudio.sln) from test output directory.");
        }

        return Path.Combine(dir.FullName, "src", "ScanlineStudio.UI");
    }
}
