namespace ScanlineStudio.Application;

/// <summary>ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: minimal seam so
/// <c>RxHistoryPaneViewModel</c> (which deliberately does not depend on <see cref="ISstvSessionService"/>
/// at all -- see that class's own top doc comment) can still subscribe <see cref="RxAudioAutoSaver.AudioAttached"/>
/// without a direct concrete-class dependency, matching this project's DI-everywhere convention.
/// Auditor-caught (round 1 code-review): without a live subscriber, <see cref="RxAudioAutoSaver.AudioAttached"/>
/// had zero subscribers anywhere in the UI, so a just-received frame's in-memory
/// <c>ReceiveHistoryEntry.AudioFilePath</c> never updated until the next unrelated refresh -- the
/// Gallery's new "Open audio file location"/"Re-decode this frame" buttons stayed hidden, and
/// deleting that entry silently orphaned its WAV.</summary>
public interface IRxAudioAutoSaver
{
    /// <summary>See <see cref="RxAudioAutoSaver.AudioAttached"/>.</summary>
    event Action<string, string>? AudioAttached;
}
