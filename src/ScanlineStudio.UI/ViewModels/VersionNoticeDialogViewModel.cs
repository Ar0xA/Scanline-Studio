using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Localization;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Startup "new version" notice: optional per-version release summary, a beta paragraph
/// for prerelease versions only, and the always-shown request for bug reports and feedback.
/// <see cref="DontShowAgain"/> is read by the caller after the window closes, however it closed.</summary>
public sealed partial class VersionNoticeDialogViewModel : ObservableObject
{
    /// <summary>Prefix of the per-version summary key, e.g. <c>VersionNotice.ReleaseSummary.0.9.2-beta</c>.
    /// Versioned so a forgotten edit can never show an old summary under a new version.</summary>
    public const string ReleaseSummaryKeyPrefix = "VersionNotice.ReleaseSummary.";

    public VersionNoticeDialogViewModel(ILocalizationService localization, string version, bool isPrerelease)
    {
        Title = localization.GetString("VersionNotice.Title", version);
        WhatsNewHeading = localization.GetString("VersionNotice.WhatsNewHeading", version);
        ReleaseItems = localization.TryGetString(ReleaseSummaryKeyPrefix + version, out var summary)
            ? SplitItems(summary)
            : [];
        ShowBeta = isPrerelease;
        BetaText = localization.GetString("VersionNotice.Beta");
        FeedbackText = localization.GetString(
            "VersionNotice.Feedback",
            MenuPath(localization, "MainWindow.Menu.Help.ReportBug"),
            MenuPath(localization, "MainWindow.Menu.Help.SendFeedback"));
        DontShowAgainLabel = localization.GetString("VersionNotice.DontShowAgain", version);
    }

    public event Action? RequestClose;

    public string Title { get; }

    public string WhatsNewHeading { get; }

    /// <summary>One entry per line of the release summary, any leading bullet stripped (the view draws its own).</summary>
    public IReadOnlyList<string> ReleaseItems { get; }

    public bool ShowReleaseSummary => ReleaseItems.Count > 0;

    public bool ShowBeta { get; }

    public string BetaText { get; }

    public string FeedbackText { get; }

    public string DontShowAgainLabel { get; }

    [ObservableProperty]
    private bool _dontShowAgain;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private static string[] SplitItems(string summary) =>
        summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimStart('•', '-', '*').Trim())
            .Where(line => line.Length > 0)
            .ToArray();

    // "Help → Report a bug…" from the live menu labels; '_' is the menu access-key marker.
    private static string MenuPath(ILocalizationService localization, string itemKey) =>
        $"{localization.GetString("MainWindow.Menu.Help").Replace("_", string.Empty, StringComparison.Ordinal)} → {localization.GetString(itemKey)}";
}
