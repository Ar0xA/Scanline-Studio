namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Snapshot of how far an in-flight transmit has gotten, derived from samples successfully
/// enqueued to the playback device (not wall-clock time) — see
/// <c>ISstvSessionService.TransmitProgressChanged</c>'s own doc comment for the raise-site
/// threading contract.</summary>
/// <param name="Fraction">Clamped to <c>[0, 1]</c>.</param>
/// <param name="Elapsed">Audio-time elapsed so far (samples enqueued / sample rate), not wall-clock.</param>
/// <param name="EstimatedTotal">Total audio-time this transmission is expected to take.</param>
public readonly record struct TransmitProgressInfo(double Fraction, TimeSpan Elapsed, TimeSpan EstimatedTotal);
