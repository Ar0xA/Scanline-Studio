using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ScanlineStudio.UI.Tests;

/// <summary>PROJECT_BRIEF.md's own tracked open question, Still Open item 1: whether Avalonia
/// 11.3.12 actually populates <see cref="ContextMenu.PlacementTarget"/> on a REAL right-click, which
/// <c>TxImageEditorPaneView.OnOpenElementQuickStyleFlyout</c> (the Quick Style/Fill &amp; Border
/// flyouts' anchor-resolution handler) originally depended on. Neither the plan-review nor the
/// code-review round for those flyouts could verify this from source (no Avalonia source on disk,
/// only the compiled assembly), and <c>TxImageEditorPaneViewModelTests.BuildElementContextMenu</c>'s
/// own doc comment explicitly says it mirrors the DataContext-copy manually rather than actually
/// opening the popup with a real pointer event.
///
/// <para><b>Answer, confirmed two ways:</b> (1) reading Avalonia's actual pinned-version source
/// (<c>ContextMenu.cs</c>, tag <c>11.3.12</c>) directly -- <c>ControlContextRequested</c> (the real
/// right-click handler Avalonia wires via <c>control.ContextRequested += ControlContextRequested</c>)
/// calls the PRIVATE 3-arg <c>Open(Control, Control, PlacementMode)</c> overload directly, which only
/// ever does <c>_popup.PlacementTarget = placementTarget</c> -- the underlying <c>Popup</c>'s own
/// property, never <c>this.PlacementTarget</c> (the public property on <c>ContextMenu</c> itself,
/// the one application code reads). So <c>ContextMenu.PlacementTarget</c> is NEVER automatically
/// populated by opening the menu, on any platform -- this is plain C# logic in a shared,
/// platform-agnostic file, not a headless quirk. (2)
/// <see cref="RealRightClick_DoesNotAutomaticallyPopulateContextMenuPlacementTarget"/> below
/// reproduces this empirically, via a REAL simulated right-click through Avalonia's actual raw-input
/// pipeline, confirming the source-reading conclusion rather than resting on it alone.</para>
///
/// <para><b>The fix's own history, worth pinning here:</b> the FIRST fix attempt captured the
/// right-clicked anchor via a SEPARATE <see cref="Control.ContextRequested"/> handler subscribed on
/// the same Border as <see cref="ContextMenu"/> itself. Auditor code-review flagged that this
/// shape's correctness in PRODUCTION rested on an unverified assumption -- whether XAML's compiler
/// subscribes an attribute-declared event handler before or after a child-property-element like
/// <c>&lt;Border.ContextMenu&gt;</c> assigns <c>Border.ContextMenu</c> (which is what wires
/// Avalonia's OWN internal <c>ControlContextRequested</c> handler on that same event). A real
/// headless test proved the risk was genuine, not hypothetical: reversing that subscription order
/// broke capture entirely. The SHIPPED fix sidesteps the question altogether by capturing on
/// <see cref="InputElement.PointerPressedEvent"/> instead (in
/// <c>TxImageEditorPaneView.OnOverlayElementPointerPressed</c>) -- a plain, un-competed handler on a
/// DIFFERENT event that always fires before the release that opens a context menu, for any button,
/// so there is no subscription-order question to reason about at all.
/// <see cref="RealRightClickThenMenuItemClick_WithPointerPressedCapturedAnchor_ActuallyOpensTheAttachedFlyout"/>
/// proves this exact (shipped) shape works end-to-end.</para>
///
/// <para>Deliberately a MINIMAL reproduction (a hand-built Border+ContextMenu+Flyout, not the full
/// production <see cref="ScanlineStudio.UI.Views.TxImageEditorPaneView"/>): that much larger View
/// cannot itself be shown in a real headless <see cref="Window"/> in THIS test assembly -- its
/// styling pulls in custom bundled fonts that <c>ScanlineStudio.UI.Tests</c>'s fast
/// <c>HeadlessFontManagerStub</c> cannot resolve, confirmed empirically; <c>ScanlineStudio.UI.FontTests</c>
/// exists specifically because that stub/real-font split can't coexist in one process. What specific
/// bindings resolve to which command is already covered separately by
/// <c>TxImageEditorPaneViewModelTests</c>'s <c>*ContextMenu_EveryPushedCommand_ResolvesToTheElementsOwnInstance</c>
/// tests; this file's only job is the anchor-resolution MECHANISM.</para>
///
/// <para>Uses <see cref="Avalonia.Headless.HeadlessWindowExtensions.MouseDown"/>/<c>MouseUp</c>
/// against a real <see cref="Window"/> -- these route through Avalonia's actual raw-input pipeline
/// (not a manually raised <see cref="RoutedEventArgs"/> on a single control). Deliberately does NOT
/// use screen/OS-level coordinate clicking (this project's own standing caution: coordinate-based
/// automation on a real desktop has previously misdirected a click onto an unrelated window) --
/// everything here runs inside the .NET test process against the headless platform, with real
/// Avalonia-computed layout bounds read back via <see cref="Visual.TranslatePoint(Point, Visual)"/>,
/// not eyeballed screen pixels. <c>FluentTheme</c> must be loaded (a bare headless
/// <see cref="Window"/> has no default template, so a real <see cref="Popup"/> -- <c>ContextMenu</c>'s
/// own backing control -- has no overlay layer to host itself in and throws; discovered
/// empirically, see <see cref="EnsureFluentThemeLoaded"/>'s own doc comment).</para></summary>
public sealed class TxImageEditorQuickStyleFlyoutRealClickTests
{
    [AvaloniaFact]
    public void RealRightClick_DoesNotAutomaticallyPopulateContextMenuPlacementTarget()
    {
        var (window, border) = BuildMinimalRepro();
        try
        {
            RightClick(window, border);

            var contextMenu = border.ContextMenu!;
            Assert.True(contextMenu.IsOpen);
            // This is the confirmed, real Avalonia 11.3.12 behavior -- NOT a bug in this test, the
            // opposite of what TxImageEditorPaneView.axaml.cs originally assumed. See this class's
            // own doc comment for the exact source-level reason.
            Assert.Null(contextMenu.PlacementTarget);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Proves the SHIPPED fix shape: capture the pressed control via
    /// <see cref="InputElement.PointerPressedEvent"/> (fires on any button, always before the
    /// subsequent release that opens a context menu), instead of the never-populated
    /// <see cref="ContextMenu.PlacementTarget"/> or a competing <see cref="Control.ContextRequested"/>
    /// subscription whose ordering against <c>Border.ContextMenu</c>'s own assignment turned out to
    /// be unverified (see this class's own doc comment).</summary>
    [AvaloniaFact]
    public void RealRightClickThenMenuItemClick_WithPointerPressedCapturedAnchor_ActuallyOpensTheAttachedFlyout()
    {
        var (window, border, getCapturedAnchor) = BuildMinimalReproWithPointerPressedCapture();
        try
        {
            RightClick(window, border);
            var contextMenu = border.ContextMenu!;
            Assert.True(contextMenu.IsOpen);
            Assert.Same(border, getCapturedAnchor()); // the shipped fix's own capture mechanism, working

            var menuItem = Assert.IsType<MenuItem>(Assert.Single(contextMenu.Items));
            var flyout = FlyoutBase.GetAttachedFlyout(border);
            Assert.NotNull(flyout);
            var openedCount = 0;
            flyout!.Opened += (_, _) => openedCount++;

            // OnOpenElementQuickStyleFlyout's own doc comment: the Click handler runs BEFORE the
            // owning Popup finishes closing, so ShowAttachedFlyout is deferred via
            // Dispatcher.UIThread.Post at Background priority -- pump jobs after raising Click for
            // that deferred call to actually run.
            menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            PumpDispatcher();

            Assert.Equal(1, openedCount);
            Assert.True(flyout.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window Window, Border ElementBorder) BuildMinimalRepro()
    {
        EnsureFluentThemeLoaded();

        var border = new Border { Width = 100, Height = 60, Background = Brushes.Gray };
        var menuItem = new MenuItem { Header = "Quick Style" };
        border.ContextMenu = new ContextMenu { Items = { menuItem } };
        FlyoutBase.SetAttachedFlyout(border, new Flyout { Content = new TextBlock { Text = "Flyout content" } });

        var window = new Window { Content = border, Width = 400, Height = 300 };
        window.Show();
        PumpDispatcher();

        return (window, border);
    }

    /// <summary>Same structure as <see cref="BuildMinimalRepro"/>, plus the shipped fix shape: a
    /// <see cref="InputElement.PointerPressedEvent"/> handler on the Border records itself into a
    /// captured local (standing in for the real fix's small per-View field,
    /// <c>TxImageEditorPaneView._lastContextMenuAnchor</c>), and the MenuItem's Click handler reads
    /// THAT instead of <see cref="ContextMenu.PlacementTarget"/>.</summary>
    private static (Window Window, Border ElementBorder, Func<Control?> GetCapturedAnchor) BuildMinimalReproWithPointerPressedCapture()
    {
        EnsureFluentThemeLoaded();

        Control? capturedAnchor = null;
        var border = new Border { Width = 100, Height = 60, Background = Brushes.Gray };
        border.AddHandler(InputElement.PointerPressedEvent, (sender, _) => capturedAnchor = sender as Control);

        var menuItem = new MenuItem { Header = "Quick Style" };
        menuItem.Click += (_, _) =>
        {
            if (capturedAnchor is not { } target)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => FlyoutBase.ShowAttachedFlyout(target), DispatcherPriority.Background);
        };

        border.ContextMenu = new ContextMenu { Items = { menuItem } };
        FlyoutBase.SetAttachedFlyout(border, new Flyout { Content = new TextBlock { Text = "Flyout content" } });

        var window = new Window { Content = border, Width = 400, Height = 300 };
        window.Show();
        PumpDispatcher();

        return (window, border, () => capturedAnchor);
    }

    /// <summary>Simulates a real right mouse-button press+release at <paramref name="target"/>'s own
    /// center, routed through <see cref="HeadlessWindowExtensions"/> -- Avalonia's actual raw-input
    /// pipeline, the same path a real OS-level right-click takes, not a manually raised event on one
    /// control. <see cref="Visual.TranslatePoint(Point, Visual)"/> converts the target's own
    /// local-space center into <paramref name="window"/>-relative coordinates, handling every
    /// intermediate transform/offset itself -- no manual ancestor-bounds arithmetic, and no
    /// screen-pixel coordinates at all.</summary>
    private static void RightClick(Window window, Visual target)
    {
        var localCenter = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        var windowPoint = target.TranslatePoint(localCenter, window) ?? throw new InvalidOperationException("Target is not in the window's visual tree.");

        window.MouseDown(windowPoint, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(windowPoint, MouseButton.Right, RawInputModifiers.None);
        PumpDispatcher();
    }

    private static void PumpDispatcher()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A bare headless <see cref="Window"/> has no default <see cref="Avalonia.Styling.ControlTheme"/>
    /// applied (this project's TestAppBuilder configures a bare <see cref="Avalonia.Application"/>,
    /// same reasoning as <c>IndustryStepperTests.EnsureIndustryStylesLoaded</c>'s own comment) --
    /// discovered empirically here, not assumed: without a real Window template, there is no
    /// overlay layer for a <see cref="Popup"/> (ContextMenu's own backing control) to host itself
    /// in, throwing "Unable to create IPopupImpl and no overlay layer is found for the target
    /// control" the instant a real right-click tries to open one. <c>FluentTheme</c> is what the
    /// real app (<c>App.axaml</c>) actually loads, already referenced transitively via this test
    /// project's <c>ScanlineStudio.UI</c> project reference -- no new package needed.</summary>
    private static void EnsureFluentThemeLoaded()
    {
        var app = Avalonia.Application.Current!;
        if (app.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
        {
            return;
        }

        app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }
}
