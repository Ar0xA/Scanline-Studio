namespace Yoniq.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md. A single point-in-time snapshot of a connected rig's state.
///
/// <see cref="ObservedAt"/> means <see cref="IRadioController.StateChanges"/> can never be deduplicated
/// by plain record equality (<c>==</c>/Rx's <c>DistinctUntilChanged()</c>) — every poll produces a
/// <see cref="RadioState"/> with a fresh timestamp even when nothing else changed, so naive equality
/// dedup silently degrades into "publish every poll" while looking like it filters. That's actually the
/// correct default here: unlike <see cref="RadioConnectionEvent"/>'s discrete transitions (where
/// dropping one is lossy), each <see cref="RadioState"/> is a complete, self-sufficient snapshot, so
/// publishing every poll is lossless and a subscriber that wants to filter should compare the specific
/// fields it cares about, not rely on record equality.</summary>
public readonly record struct RadioState(
    long FrequencyHz,
    RadioMode Mode,
    bool IsTransmitting,
    int? SignalStrengthDb,
    DateTimeOffset ObservedAt);
