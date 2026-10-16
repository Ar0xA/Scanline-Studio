using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>What the TX pane sends: one SSTV mode, or DRM (EasyPal-compatible digital SSTV).</summary>
public abstract record TxTarget;

/// <summary>An analogue SSTV mode.</summary>
public sealed record SstvTarget(SstvModeDefinition Mode) : TxTarget;

/// <summary>DRM (HamDRM/EasyPal). Its image size comes from the DRM size preset, not from the target.</summary>
public sealed record DrmTarget : TxTarget;

/// <summary>The frame the TX editor composes for. <see cref="FrameSeconds"/> is null when the airtime does not
/// follow from the geometry (DRM: it depends on the compressed file size and the profile).</summary>
public sealed record TargetGeometry(int Width, int Height, string Id, string DisplayName, double? FrameSeconds);

/// <summary>DRM picture size. QSSTV's TX offers the same three fixed sizes plus "use source size".</summary>
public enum DrmSizePreset
{
    Size320x240,
    Size640x480,
    Size800x600,
}

public static class TxTargetGeometry
{
    public static TargetGeometry ToGeometry(this SstvModeDefinition mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return new TargetGeometry(mode.ImageWidth, mode.ImageHeight, mode.Id, mode.DisplayName, TxControlsPaneViewModel.GetFrameSeconds(mode));
    }

    public static (int Width, int Height) Dimensions(this DrmSizePreset preset) => preset switch
    {
        DrmSizePreset.Size320x240 => (320, 240),
        DrmSizePreset.Size800x600 => (800, 600),
        _ => (640, 480),
    };

    public static TargetGeometry ForDrm(DrmSizePreset preset, string displayName)
    {
        var (width, height) = preset.Dimensions();
        return new TargetGeometry(width, height, $"drm-{width}x{height}", displayName, FrameSeconds: null);
    }
}
