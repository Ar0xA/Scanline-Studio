using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio;

/// <summary>See spec/02-radio-layer.md's "no radio" case. Null-object <see cref="IRadioProtocol"/> for
/// <see cref="NoneConnectionSpec"/> -- lets <see cref="RadioController"/> treat "no radio" as just
/// another resolved protocol with zero special-cased branches, rather than an if-check on the spec
/// type.
///
/// <see cref="PollAsync"/> deliberately never returns a value under normal operation -- it awaits an
/// infinite delay that only ever completes via cancellation (thrown as
/// <see cref="OperationCanceledException"/>, which <see cref="RadioController"/>'s poll loop already
/// treats as a clean shutdown signal). The alternative -- returning a fixed placeholder
/// <see cref="RadioState"/> every poll interval -- would publish a stream of meaningless "0 Hz"
/// snapshots to <see cref="IRadioController.StateChanges"/> for the entire time "no radio" is
/// selected, which is worse than simply producing nothing until a real backend connects.</summary>
public sealed class NoneRadioProtocol : IRadioProtocol
{
    public string RigId => "none";

    public RadioCapabilities Capabilities => RadioCapabilities.None;

    public async Task<RadioState> PollAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        throw new InvalidOperationException(
            "Unreachable -- Task.Delay(Infinite) only ever completes via cancellation.");
    }

    public Task SetFrequencyAsync(long hz, CancellationToken ct) =>
        throw new InvalidOperationException("No radio is connected -- nothing to set a frequency on.");

    public Task SetModeAsync(RadioMode mode, CancellationToken ct) =>
        throw new InvalidOperationException("No radio is connected -- nothing to set a mode on.");

    public Task SetPttAsync(bool tx, CancellationToken ct) =>
        throw new InvalidOperationException("No radio is connected -- nothing to key PTT on.");

    public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct) =>
        throw new InvalidOperationException("No radio is connected -- nothing to set a bandwidth on.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
