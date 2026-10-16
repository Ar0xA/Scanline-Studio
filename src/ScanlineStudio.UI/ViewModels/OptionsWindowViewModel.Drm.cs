using CommunityToolkit.Mvvm.ComponentModel;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Options › Station: the three DRM profiles and the VOX tone. These are the
/// Options-owned fields of <see cref="DrmSettings"/>; the TX pane owns the rest.</summary>
public sealed partial class OptionsWindowViewModel
{
    private IReadOnlyList<DrmProfileEditorViewModel>? _drmProfiles;

    public IReadOnlyList<DrmProfileEditorViewModel> DrmProfiles =>
        _drmProfiles ??= [.. Enumerable.Range(0, DrmSettingsDefaults.ProfileCount).Select(i => new DrmProfileEditorViewModel(i, _localization))];

    [ObservableProperty]
    private bool _drmVoxTone = DrmSettingsDefaults.DefaultVoxTone;


    private void LoadDrmOptions(DrmSettings? drm)
    {
        foreach (var profile in DrmProfiles)
        {
            profile.Apply(DrmSettingsDefaults.ResolveProfile(drm, profile.Index), DrmSettingsDefaults.StoredProfileName(drm, profile.Index));
        }

        DrmVoxTone = DrmSettingsDefaults.ResolveVoxTone(drm);
    }

    private void ResetDrmOptions() => LoadDrmOptions(null);

    /// <summary>Captured on the UI thread; the returned function only merges into the stored section.</summary>
    private Func<DrmSettings, DrmSettings> CaptureDrmOptionsUpdate()
    {
        var profiles = DrmProfiles.Select(p => (DrmProfileSettings?)p.ToSettings()).ToArray();
        var voxTone = DrmVoxTone;
        return current => current with { Profiles = profiles, VoxTone = voxTone };
    }
}
