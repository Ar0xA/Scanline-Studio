namespace ScanlineStudio.UI.Settings;

/// <summary>Pure UI-preference setting for the Gallery pane's manual "Export frame" action -- no
/// hardware/DSP access, so owning this section directly in <c>ScanlineStudio.UI</c> (rather than a
/// <c>ScanlineStudio.Core.*</c> project) doesn't violate the layering rule
/// (`UiLayeringArchitectureTests`), same precedent as <see cref="WindowGeometrySettings"/>/
/// <see cref="TxPaneUiSettings"/>. Stays UI-owned only while it stays manual-export-only: the
/// automatic RX-history auto-save path hardcodes PNG unconditionally
/// (`ReceiveHistoryRecorder.cs`) and does not read this value -- if a future auto-JPEG-save feature
/// is ever added, this would need to move somewhere `ScanlineStudio.Application`'s own decode
/// pipeline can reach, since that layer cannot reference a `ScanlineStudio.UI`-owned type.</summary>
public sealed record ImageExportSettings
{
    public const string SectionKey = "ImageExport";

    /// <summary>1..100, JPEG-only (ignored for PNG exports). Nullable -- System.Text.Json does not
    /// honor property-initializer defaults for <c>init</c>-only properties absent from the JSON
    /// payload (see <c>AppPerformanceSettings</c>'s own doc comment for the full STJ explanation);
    /// the real default (85, matching the Options dialog's own pre-existing placeholder value) is
    /// applied explicitly at both read sites (<c>OptionsWindowViewModel</c>,
    /// <c>RxHistoryPaneViewModel</c>), never here. <c>SixLabors.ImageSharp.JpegEncoder.Quality</c>'s
    /// own setter throws outside 1..100 -- since this value can also arrive via a hand-edited
    /// `settings.json`, not just the dialog's own 1..100-bounded `NumericUpDown`, every read site
    /// must clamp, not just default.</summary>
    public int? JpegQuality { get; init; }
}
