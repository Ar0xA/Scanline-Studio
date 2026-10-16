using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Localization;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Localized labels for the DRM transmission choices, in enum order (a ComboBox index is the enum's
/// position). Interleave is labelled by what is sent, not by QSSTV's swapped UI label.</summary>
public static class DrmChoiceLabels
{
    public static IReadOnlyList<string> Modes(ILocalizationService l) =>
        [l.GetString("Drm.Mode.A"), l.GetString("Drm.Mode.B"), l.GetString("Drm.Mode.E")];

    public static IReadOnlyList<string> Bandwidths(ILocalizationService l) =>
        [l.GetString("Drm.Bandwidth.Hz2200"), l.GetString("Drm.Bandwidth.Hz2500")];

    public static IReadOnlyList<string> QamOrders(ILocalizationService l) =>
        [l.GetString("Drm.Qam.Qam4"), l.GetString("Drm.Qam.Qam16"), l.GetString("Drm.Qam.Qam64")];

    public static IReadOnlyList<string> Protections(ILocalizationService l) =>
        [l.GetString("Drm.Protection.High"), l.GetString("Drm.Protection.Normal")];

    public static IReadOnlyList<string> Interleaves(ILocalizationService l) =>
        [l.GetString("Drm.Interleave.Long"), l.GetString("Drm.Interleave.Short")];

    public static IReadOnlyList<string> ReedSolomonLevels(ILocalizationService l) =>
    [
        l.GetString("Drm.ReedSolomon.None"), l.GetString("Drm.ReedSolomon.Rs1"), l.GetString("Drm.ReedSolomon.Rs2"),
        l.GetString("Drm.ReedSolomon.Rs3"), l.GetString("Drm.ReedSolomon.Rs4"),
    ];

    /// <summary>The short constellation name for captions ("16-QAM").</summary>
    public static string QamShort(ILocalizationService l, DrmQamOrder qam) => l.GetString(qam switch
    {
        DrmQamOrder.Qam4 => "Drm.Qam.Short.Qam4",
        DrmQamOrder.Qam64 => "Drm.Qam.Short.Qam64",
        _ => "Drm.Qam.Short.Qam16",
    });
}
