using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>One dialog for every update-check outcome. The "never" checkbox is always shown, so a user who
/// ticked it can untick it from any Help → Check for updates result. The caller reads
/// <see cref="ToPreferences"/> after the window closes, however it closed.</summary>
public sealed partial class UpdateCheckDialogViewModel : ObservableObject
{
    private readonly IUrlLauncher _urlLauncher;
    private readonly UpdateCheckResult _result;
    private readonly UpdatePreferences _initial;

    public UpdateCheckDialogViewModel(
        ILocalizationService localization,
        IUrlLauncher urlLauncher,
        UpdateCheckResult result,
        string currentVersion,
        UpdatePreferences initial)
    {
        _urlLauncher = urlLauncher;
        _result = result;
        _initial = initial;

        var latest = result.LatestVersion?.ToString();
        (Title, Message) = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => (
                localization.GetString("UpdateCheck.Title.Available"),
                localization.GetString("UpdateCheck.Available", latest ?? string.Empty, currentVersion)),
            UpdateCheckStatus.UpToDate => (
                localization.GetString("UpdateCheck.Title.UpToDate"),
                localization.GetString("UpdateCheck.UpToDate", currentVersion)),
            _ => (
                localization.GetString("UpdateCheck.Title.Failed"),
                localization.GetString("UpdateCheck.Failed." + result.Failure)),
        };

        ShowOpenPage = result.ReleasePageUrl is not null && result.Status != UpdateCheckStatus.UpToDate;
        OpenPageLabel = localization.GetString(result.Status == UpdateCheckStatus.UpdateAvailable
            ? "UpdateCheck.OpenDownloadPage"
            : "UpdateCheck.OpenReleasesPage");
        ShowSkipVersion = result.Status == UpdateCheckStatus.UpdateAvailable;
        SkipVersionLabel = localization.GetString("UpdateCheck.SkipVersion", latest ?? string.Empty);
        NeverNotifyLabel = localization.GetString("UpdateCheck.NeverNotify");
        _skipThisVersion = latest is not null && latest == initial.SkippedVersion;
        _neverNotify = initial.NeverNotify;
    }

    public event Action? RequestClose;

    public string Title { get; }

    public string Message { get; }

    public bool ShowOpenPage { get; }

    public string OpenPageLabel { get; }

    public bool ShowSkipVersion { get; }

    public string SkipVersionLabel { get; }

    public string NeverNotifyLabel { get; }

    [ObservableProperty]
    private bool _skipThisVersion;

    [ObservableProperty]
    private bool _neverNotify;

    /// <summary>Unticking "skip" clears it only if it named this dialog's version; any other skipped version is kept.</summary>
    public UpdatePreferences ToPreferences()
    {
        var latest = _result.LatestVersion?.ToString();
        var skipped = !ShowSkipVersion ? _initial.SkippedVersion
            : SkipThisVersion ? latest
            : _initial.SkippedVersion == latest ? null
            : _initial.SkippedVersion;
        return new UpdatePreferences(NeverNotify, skipped);
    }

    [RelayCommand]
    private void OpenPage()
    {
        if (_result.ReleasePageUrl is { } url)
        {
            _urlLauncher.Open(url.AbsoluteUri);
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
