using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>OK-only startup notice after a settings load found a damaged or unreadable file. Built once per
/// notice, so the strings are resolved at construction.</summary>
public sealed partial class SettingsRecoveryDialogViewModel(ILocalizationService localization, SettingsRecoveryNotice notice)
{
    public event Action? RequestClose;

    public SettingsRecoveryKind Kind => notice.Kind;

    public string Message { get; } = localization.GetString($"SettingsRecovery.Message.{notice.Kind}");

    public string FilePath => notice.Path;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
