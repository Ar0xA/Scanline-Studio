namespace ScanlineStudio.Abstractions.Radio;

/// <summary>Result of a one-shot, throwaway connection attempt (<see cref="IRadioSessionService.TestConnectionAsync"/>)
/// -- deliberately NOT the same path as <see cref="IRadioSessionService.ConnectUsingSettingsAsync"/>,
/// which connects the app's real, persistent <see cref="IRadioController"/> session. A "Test
/// Connection" button needs to try whatever the operator has currently typed into a settings dialog
/// (possibly not yet saved) without disturbing an already-working live session, so this resolves and
/// polls a fresh, disposable <see cref="IRadioProtocol"/> directly instead. <see cref="Success"/>
/// <see langword="false"/> covers both "no/ambiguous backend registered for this spec" and "a real
/// connection/poll attempt threw" -- <see cref="ErrorMessage"/> distinguishes them for display,
/// <see cref="RigId"/>/<see cref="Capabilities"/> are only meaningful when <see cref="Success"/> is
/// <see langword="true"/>.</summary>
public readonly record struct RadioConnectionTestResult(bool Success, string? RigId, RadioCapabilities Capabilities, string? ErrorMessage);
