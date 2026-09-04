using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.FontTests;

/// <summary>Phase 2 font-size presets: the load-bearing test proving the whole mechanism, mirroring
/// the role <c>AppearanceThemeResolutionTests</c> played for Phase 1's dark mode. Phase 1's own test
/// proved <c>{DynamicResource}</c> re-render for a DIRECT BIND on a control instance (a
/// <c>Background</c>/<c>AffectsRender</c> case). This phase needs a materially different shape
/// proven, not assumed from that precedent: a <c>{DynamicResource}</c> inside a <c>&lt;Setter&gt;</c>
/// in a real <c>Style</c> (the shape 74+ of the ~91 <c>AtomsFontScale*.axaml</c> keys are actually
/// consumed through), resolved via <c>AffectsMeasure</c> (a real re-layout, not just a repaint),
/// through a <c>ResourceDictionary.MergedDictionaries</c> collection-change notification (not a
/// <c>RequestedThemeVariant</c> flip -- <see cref="App.ApplyFontScale"/> is a completely separate
/// mechanism from Phase 1's theme axis, see that method's own doc comment). All three differences
/// were empirically confirmed correct via a throwaway reflection/headless probe against the real
/// installed Avalonia 11.3.12 package before this phase's implementation began (not assumed) -- this
/// test is that same probe, permanently encoded as project-owned coverage.
///
/// This project (not <c>ScanlineStudio.UI.Tests</c>) is the only place this can run -- see
/// <c>TestAppBuilder</c>'s own doc comment for why: it configures the REAL <c>ScanlineStudio.UI.App</c>
/// with real StyleIncludes (<c>AtomsTokens.axaml</c>/<c>Atoms.axaml</c>, and therefore every
/// <c>IndustryBtn24</c>-shaped <c>Style</c> that consumes an <c>AtomsFontScale*</c> key), unlike
/// <c>ScanlineStudio.UI.Tests</c>'s bare <c>Application</c>.</summary>
public sealed class AppearanceFontScaleResolutionTests
{
    [AvaloniaFact]
    public void SetterBoundControl_ReLayoutsWhenFontScaleSwapsAtRuntime_AcrossMultipleCycles()
    {
        // App is a process-global singleton under Avalonia.Headless, shared across this test
        // assembly's other real-window tests (TxImageEditorRealUiSmokeTests,
        // TxImageEditorPerspectiveCornerDragRealRenderTests) -- must not leak a non-Normal font
        // scale into them, same reasoning AppearanceThemeResolutionTests's own doc comment already
        // establishes for RequestedThemeVariant.
        try
        {
            App.ApplyFontScale(AppFontScale.Normal);

            // Classes="Industry IndustryBtn IndustryBtn24" -- the real Atoms.axaml Style shape
            // (Setter Property="Height" Value="{DynamicResource IndustryBtn24Height}"), not a direct
            // Bind(). Height/MinHeight/FontSize all arrive through that Setter, exactly like every
            // real button in the shipping app.
            var button = new Button { Classes = { "Industry", "IndustryBtn", "IndustryBtn24" }, Content = "Test" };
            var window = new Window { Content = button, Width = 200, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(24d, button.Bounds.Height);
            Assert.Equal(24d, button.Height);
            Assert.Equal(11d, button.FontSize);

            // Cycle 1: Normal -> Large.
            App.ApplyFontScale(AppFontScale.Large);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(28d, button.Bounds.Height);
            Assert.Equal(28d, button.Height);
            Assert.Equal(13d, button.FontSize);

            // Cycle 1 back: Large -> Normal. Round-2 plan-review addition: a single switch could
            // pass even if MergedDictionaries.Clear() misbehaves on a SECOND cycle (e.g. throws, or
            // silently fails to release ownership so the next Add fails) -- confirmed safe via the
            // same throwaway probe, but pinned here as real coverage, not left as an assumption.
            App.ApplyFontScale(AppFontScale.Normal);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(24d, button.Bounds.Height);
            Assert.Equal(24d, button.Height);
            Assert.Equal(11d, button.FontSize);

            // Cycle 2: Normal -> Large -> Normal again, confirming stability past the first cycle.
            App.ApplyFontScale(AppFontScale.Large);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(28d, button.Bounds.Height);

            App.ApplyFontScale(AppFontScale.Normal);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(24d, button.Bounds.Height);

            window.Close();
        }
        finally
        {
            App.ApplyFontScale(AppFontScale.Normal);
        }
    }

    [AvaloniaFact]
    public void TextBlockFontSizeFromSetter_ReLayoutsWhenFontScaleSwaps_DesiredSizeGrows()
    {
        // Proves AffectsMeasure specifically on the TEXT-measurement path (a real Skia glyph
        // remeasure, per TestAppBuilder's UseHeadlessDrawing = false), not just the box -- a
        // DIFFERENT code path inside Avalonia from Button's own Height/MinHeight, and the actual
        // risk surface for every FontSize-only key in the catalog (e.g. IndustryRowLabelFontSize).
        try
        {
            App.ApplyFontScale(AppFontScale.Normal);

            var textBlock = new TextBlock { Classes = { "IndustryRowLabel" }, Text = "Sample Row Label Text" };
            var window = new Window { Content = textBlock, Width = 400, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var normalDesiredSize = textBlock.DesiredSize;
            Assert.Equal(11d, textBlock.FontSize);
            Assert.True(normalDesiredSize.Width > 0, "Normal-size TextBlock should have measured a nonzero width.");

            App.ApplyFontScale(AppFontScale.Large);
            Dispatcher.UIThread.RunJobs();

            var largeDesiredSize = textBlock.DesiredSize;
            Assert.Equal(13d, textBlock.FontSize);
            Assert.True(largeDesiredSize.Width > normalDesiredSize.Width,
                $"Expected Large's DesiredSize.Width ({largeDesiredSize.Width}) to exceed Normal's ({normalDesiredSize.Width}) -- "
                + "a larger FontSize that doesn't actually re-measure the text would indicate AffectsMeasure isn't firing "
                + "for the Setter-resolved DynamicResource path.");

            window.Close();
        }
        finally
        {
            App.ApplyFontScale(AppFontScale.Normal);
        }
    }
}
