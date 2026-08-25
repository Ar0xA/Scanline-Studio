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
