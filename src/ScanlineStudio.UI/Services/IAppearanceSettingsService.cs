namespace ScanlineStudio.UI.Services;

/// <summary>Thin broadcast layer over the <see cref="ScanlineStudio.UI.Settings.AppearanceSettings"/>
/// section, needed because <c>OptionsWindowViewModel</c> (writer) and <c>RadioStatusViewModel</c>
/// (reader) are two independently-alive ViewModel instances that can both be open at once -- a
/// setting saved in Options must reach an already-constructed <c>RadioStatusViewModel</c>
/// immediately, not just on next app start. Mirrors the shape
/// <see cref="ScanlineStudio.Application.IRadioSessionService.SafetySettingsChanged"/>/
/// <c>TxControlsPaneViewModel.OnSafetySettingsChanged</c> already establishes for this exact
/// cross-VM-live-update problem, rather than <c>ISettingsStore.Changes</c> (technically available,
/// but confirmed to have zero real subscribers anywhere in this codebase today -- not the proven
/// convention).
///
/// Deliberately does NOT own persistence itself -- <c>OptionsWindowViewModel</c> still writes
/// <see cref="ScanlineStudio.UI.Settings.AppearanceSettings"/> directly via its own
/// <c>ISettingsStore</c>, in the SAME <c>UpdateAsync</c> call already saving Theme/FontScale, and
/// then calls <see cref="NotifyDecodingIndicatorBlinksChanged"/> once that write succeeds -- same
/// "read helper + pure notify, no competing write path" split <c>IRadioSessionService</c> itself
/// does NOT need (it owns Safety settings alone), added here specifically because Appearance's
/// existing owner (OptionsWindowViewModel) must stay the only writer.</summary>
public interface IAppearanceSettingsService
{
    /// <summary>Read helper for a ViewModel's own constructor-time initial load (mirrors
    /// <c>IRadioSessionService.GetSsbAsPktPreferenceAsync</c>'s own shape) -- resolves
    /// <see langword="null"/> to <see cref="ScanlineStudio.UI.Settings.AppearanceSettings.DefaultDecodingIndicatorBlinks"/>.</summary>
    Task<bool> GetDecodingIndicatorBlinksAsync(CancellationToken ct = default);

    /// <summary>Called by <c>OptionsWindowViewModel</c> right after its own settings-store write
    /// succeeds -- broadcasts the already-persisted value to any live subscriber. Not async and does
    /// no I/O of its own; the write already happened.</summary>
    void NotifyDecodingIndicatorBlinksChanged(bool value);

    event Action<bool>? DecodingIndicatorBlinksChanged;
}
