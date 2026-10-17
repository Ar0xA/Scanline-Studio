namespace ScanlineStudio.Application;

/// <summary>The DRM reception recorder, or null when it could not be built. Lets UI code depend on it without a
/// recorder construction failure taking down the main window (the host resolves the recorder in a try/catch).</summary>
public sealed record OptionalDrmReceptionRecorder(IDrmReceptionRecorder? Recorder);
