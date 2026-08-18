namespace ScanlineStudio.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md. A single point-in-time snapshot of a connected rig's state.
///
/// <see cref="ObservedAt"/> means <see cref="IRadioController.StateChanges"/> can never be deduplicated
/// by plain record equality (<c>==</c>/Rx's <c>DistinctUntilChanged()</c>) — every poll produces a
/// <see cref="RadioState"/> with a fresh timestamp even when nothing else changed, so naive equality
/// dedup silently degrades into "publish every poll" while looking like it filters. That's actually the
/// correct default here: unlike <see cref="RadioConnectionEvent"/>'s discrete transitions (where
/// dropping one is lossy), each <see cref="RadioState"/> is a complete, self-sufficient snapshot, so
/// publishing every poll is lossless and a subscriber that wants to filter should compare the specific
/// fields it cares about, not rely on record equality.
///
/// <para><see cref="SwrRatio"/>/<see cref="AlcLevel"/>/<see cref="PowerPercent"/> (trailing,
/// optional -- every existing positional construction site keeps compiling unchanged) are only ever
/// populated while <see cref="IsTransmitting"/> is true (meters are TX-only on real rigs; reading
/// them during RX both wastes a poll cycle and risks a stale/meaningless value -- see
/// <c>RigctldClientProtocol.PollAsync</c>/<c>HamlibRadioProtocol.PollAsync</c>). <see langword="null"/>
/// means "not read this poll" (RX, capability absent, or the read itself failed) -- never a
/// meaningful zero.</para>
///
/// <para><see cref="SignalStrengthDb"/> -- opposite gating from <see cref="SwrRatio"/>/
/// <see cref="AlcLevel"/>/<see cref="PowerPercent"/> above: an S-meter reading is only meaningful
/// while RECEIVING, so this is only ever populated while <see cref="IsTransmitting"/> is
/// <see langword="false"/> (see <c>RigctldClientProtocol.PollAsync</c>/<c>HamlibRadioProtocol.PollAsync</c>).
/// Hamlib's own documented unit (rig.h's <c>RIG_LEVEL_STRENGTH</c>): "effective (calibrated) signal
/// strength relative to S9, arg int (dB)" -- <c>0</c> means exactly S9, negative means below S9,
/// positive means above S9 (an "S9+N dB" reading). <see langword="null"/> means "not read this poll"
/// (TX, capability absent, or the read itself failed) -- never a meaningful zero (zero IS a real,
/// meaningful S9 reading here, unlike the TX-only meters' own null-vs-zero convention).</para></summary>
public readonly record struct RadioState(
    long FrequencyHz,
    RadioMode Mode,
    bool IsTransmitting,
    int? SignalStrengthDb,
    DateTimeOffset ObservedAt,
    float? SwrRatio = null,
    float? AlcLevel = null,
    float? PowerPercent = null);
