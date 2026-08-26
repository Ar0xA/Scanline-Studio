using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Backs the Loopback self-test result dialog (Calibration menu, stub survey Tier 3) -- see
/// <see cref="ScanlineStudio.Application.ISstvSessionService.RunLoopbackSelfTestAsync"/>'s own doc
/// comment for the full feature design. The self-test's result is surfaced ONLY through this
/// dedicated dialog (round-5 plan-review decision) -- never through the live RX pane's own
/// <c>Current</c>/<c>Progress</c>, which the self-test's private decoder is by design unreachable
/// from. Constructed directly with <c>new</c> (not DI-resolved), same reasoning as
/// <see cref="AboutWindowViewModel"/>/<see cref="QsoLinkWindowViewModel"/>'s own doc comments -- one
/// caller (<see cref="TxControlsPaneViewModel.LoopbackSelfTestCompleted"/>), no container round trip
/// needed.</summary>
public sealed partial class LoopbackSelfTestResultWindowViewModel : ObservableObject
{
    public WriteableBitmap Bitmap { get; }

    public string OutcomeText { get; }

    /// <summary>Non-null when the self-test either locked onto a DIFFERENT mode than the one
    /// requested, or never locked onto any mode at all -- both are real, user-visible warnings, not
    /// decorative: a complete decode of the wrong mode still reports
    /// <see cref="LoopbackSelfTestOutcome.Completed"/> (code-review round-1 finding: without this,
    /// that case was indistinguishable from a genuine success), and a total no-lock result was
    /// previously visible only as generic "Decode stopped before the last line" text with no mention
    /// that NO mode was ever detected at all (a second round-1 finding).</summary>
    public string? ModeMismatchWarning { get; }

    public LoopbackSelfTestResultWindowViewModel(SstvModeDefinition requestedMode, LoopbackSelfTestResult result, IReadOnlyList<SstvModeDefinition> availableModes, ILocalizationService localization)
    {
        Bitmap = ImageSourceBitmapConverter.ToBitmap(result.Image);

        OutcomeText = result.Outcome switch
        {
            LoopbackSelfTestOutcome.Completed => localization.GetString("LoopbackSelfTest.Outcome.Completed"),
            LoopbackSelfTestOutcome.AbandonedByAutoStop => localization.GetString("LoopbackSelfTest.Outcome.AbandonedByAutoStop"),
            _ => localization.GetString("LoopbackSelfTest.Outcome.Incomplete"),
        };

        ModeMismatchWarning = result.DetectedModeId switch
        {
            null => localization.GetString("LoopbackSelfTest.NoModeDetectedWarning"),
            { } detectedModeId when detectedModeId != requestedMode.Id => localization.GetString(
                "LoopbackSelfTest.ModeMismatchWarning",
                // UI must never reference Core.Sstv's SstvModeRegistry directly (layering rule) --
                // resolving the detected mode's DisplayName from the caller-supplied AvailableModes
                // list instead, falling back to the raw id if it's somehow not in that list.
                availableModes.FirstOrDefault(m => m.Id == detectedModeId)?.DisplayName ?? detectedModeId,
                requestedMode.DisplayName),
            _ => null,
        };
    }

    /// <summary>Same convention as <see cref="AboutWindowViewModel.RequestClose"/> -- the View's
    /// code-behind subscribes <c>vm.RequestClose += Close;</c>.</summary>
    public event Action? RequestClose;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
