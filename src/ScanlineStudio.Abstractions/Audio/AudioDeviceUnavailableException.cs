namespace ScanlineStudio.Abstractions.Audio;

/// <summary>
/// Thrown by an <see cref="IAudioEngine"/> implementation when a real audio backend cannot open or
/// continue using a specific capture/playback device -- the device is missing, the backend/native
/// context failed to initialize, or the device id is not valid for the resolved backend. Per
/// spec/01-architecture.md's Error Handling rule: infrastructure layers (radio, audio, serial)
/// surface failures as typed exceptions specific to the operation, never silent failure or raw
/// error codes.
/// </summary>
public sealed class AudioDeviceUnavailableException : Exception
{
    public AudioDeviceUnavailableException(string message)
        : base(message)
    {
    }

    public AudioDeviceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
