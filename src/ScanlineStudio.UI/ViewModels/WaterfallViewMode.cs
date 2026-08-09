namespace ScanlineStudio.UI.ViewModels;

/// <summary>Backs mock2's "Both"/"Spec"/"WF" segment (`MainWindow.axaml`'s Spectrum &amp; waterfall
/// card) -- see <see cref="WaterfallPaneViewModel.ViewMode"/>.</summary>
public enum WaterfallViewMode
{
    Both,
    SpectrumOnly,
    WaterfallOnly,
}
