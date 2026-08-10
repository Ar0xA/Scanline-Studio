using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ScanlineStudio.UI.Tests;

/// <summary>Phase 1 code-review exit gate (blocker 1): confirms IndustryStepperTheme's spin
/// buttons actually increment/decrement NumericUpDown.Value, not just render. An earlier version
/// of this template wired two bare RepeatButtons named PART_IncreaseButton/PART_DecreaseButton
/// directly onto the NumericUpDown itself -- those names belong to ButtonSpinner, not
/// NumericUpDown (confirmed against Avalonia 11.3.12's own reference metadata), so clicking them
/// did nothing. The fix hosts a real ButtonSpinner (PART_Spinner) wrapping the value TextBox; this
/// test clicks the rendered increase/decrease buttons through the real Click event, the same path
/// a real pointer click takes (ButtonSpinner.OnButtonClick -> Spin event -> NumericUpDown.OnSpin),
/// so it would have failed against the broken template and passes against the fix.</summary>
public sealed class IndustryStepperTests
{
    [AvaloniaFact]
    public void IndustryStepperTheme_IncreaseButtonClick_IncrementsValue()
    {
        var numericUpDown = CreateStepper();
        var window = ShowInWindow(numericUpDown);

        var increaseButton = FindPart<RepeatButton>(numericUpDown, "PART_IncreaseButton");
        increaseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SettleDispatcher();

        Assert.Equal(6m, numericUpDown.Value);
        // Code-review-flagged risk: confirms NumericUpDown actually pushes the new Value into
        // PART_TextBox's displayed Text (unverified whether this happens via NumericUpDown's own
        // code or a Fluent-template TwoWay binding this port's template doesn't replicate) -- Spin
        // alone (asserted above) wouldn't have caught a permanently blank/stale display.
        var textBox = FindPart<TextBox>(numericUpDown, "PART_TextBox");
        Assert.Equal("6", textBox.Text);

        window.Close();
    }

    [AvaloniaFact]
    public void IndustryStepperTheme_DecreaseButtonClick_DecrementsValue()
    {
        var numericUpDown = CreateStepper();
        var window = ShowInWindow(numericUpDown);

        var decreaseButton = FindPart<RepeatButton>(numericUpDown, "PART_DecreaseButton");
        decreaseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SettleDispatcher();

        Assert.Equal(4m, numericUpDown.Value);

        window.Close();
    }

    private static NumericUpDown CreateStepper()
    {
        EnsureIndustryStylesLoaded();

        return new NumericUpDown
        {
            Theme = (ControlTheme)Avalonia.Application.Current!.FindResource("IndustryStepperTheme")!,
            Classes = { "Industry" },
            Value = 5,
            Minimum = 0,
            Maximum = 10,
            Increment = 1,
        };
    }

    private static Window ShowInWindow(Control control)
    {
        var window = new Window { Content = control };
        window.Show();
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        return window;
    }

    private static void SettleDispatcher()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T FindPart<T>(Control root, string partName) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == partName);

    // AtomsTokens.axaml/Atoms.axaml aren't auto-loaded by this project's headless TestAppBuilder
    // (it configures a bare Avalonia.Application, not ScanlineStudio.UI.App, so App.axaml's own
    // <Application.Styles> never runs) -- load them once, directly, the same way Phase 1's
    // plan-review probes established for testing real Style/ControlTheme behavior headlessly.
    private static void EnsureIndustryStylesLoaded()
    {
        var app = Avalonia.Application.Current!;
        if (app.Styles.OfType<StyleInclude>().Any(s => s.Source!.OriginalString.Contains("Atoms.axaml")))
        {
            return;
        }

        var baseUri = new System.Uri("avares://ScanlineStudio.UI/");
        app.Styles.Add(new StyleInclude(baseUri) { Source = new System.Uri("avares://ScanlineStudio.UI/Styles/AtomsTokens.axaml") });
        app.Styles.Add(new StyleInclude(baseUri) { Source = new System.Uri("avares://ScanlineStudio.UI/Styles/Atoms.axaml") });
    }
}
