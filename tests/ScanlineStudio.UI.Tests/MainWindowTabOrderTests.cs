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
