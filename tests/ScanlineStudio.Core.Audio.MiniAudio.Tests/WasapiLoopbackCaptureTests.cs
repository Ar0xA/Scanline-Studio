using Microsoft.Extensions.Logging.Abstractions;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// `BACKLOG.md` W2. Windows had no real audio coverage at all: 39 tests skip because
/// <c>[RequiresPipeWireFact]</c>'s probe shells out to <c>pactl</c>, which does not exist there. The
/// gate conflates "needs a real audio server" with "needs PulseAudio", so playback, capture,
/// enumeration, hotplug and the whole encode-to-device-to-decode round trip are unverified on the
/// platform most users run.
///
/// <para><b>WASAPI loopback is the Windows answer to the Linux null-sink cable.</b> The Linux round
/// trip is not a physical loop either — it captures a null sink's <c>.monitor</c>. Loopback does the
/// same job natively: it captures what an OUTPUT device is playing, with no extra software and no
/// VB-CABLE install.</para>
///
/// <para><b>Device cost: this tier OPENS a device, which is why it is opt-in.</b> A loopback open
/// takes a capture stream against an output endpoint. It does not silence that output or take it
/// from another application — loopback is explicitly a non-exclusive observer — but it is a real
/// device open, not an enumeration, so it belongs in the exclusive tier by the rule this project
/// already set.</para>
///
/// <para><b>Unverified when written.</b> These were authored on Linux, where loopback cannot run:
/// every backend except WASAPI returns <c>MA_DEVICE_TYPE_NOT_SUPPORTED</c>. They compile and the
/// native shim builds, and nothing more than that has been established.</para>
/// </summary>
public sealed class WasapiLoopbackCaptureTests
{
    [WindowsAudioExclusiveFact]
    public void LoopbackCapture_OpensAgainstAnOutputDevice()
    {
        using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
        enumerator.RefreshAsync().GetAwaiter().GetResult();

        var outputs = enumerator.OutputDevices;
        Assert.True(outputs.Count > 0, "no output devices, so loopback had nothing to open against.");
        var output = outputs.FirstOrDefault(d => d.IsDefault) ?? outputs[0];

        // The id is a PLAYBACK device's, deliberately. Loopback captures what an output is playing,
        // and miniaudio takes that id in capture.pDeviceID exactly as it does for a real capture.
        using var session = new MiniAudioCaptureSession(
            output.Id,
            sampleRate: 48000,
            NullLogger.Instance,
            loopback: true);

        Assert.NotNull(session);
    }

    [WindowsAudioExclusiveFact]
    public void LoopbackCapture_AgainstACaptureDeviceId_FailsRatherThanSilentlyCapturingNothing()
    {
        // An input device's id is not a valid loopback target. The failure must be an exception at
        // open, not a session that yields silence forever -- the latter would read as "the round trip
        // produced no audio" and send someone looking in the decoder.
        using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
        enumerator.RefreshAsync().GetAwaiter().GetResult();

        var inputs = enumerator.InputDevices;
        if (inputs.Count == 0)
        {
            return;
        }

        var input = inputs[0];

        Assert.Throws<InvalidOperationException>(() => new MiniAudioCaptureSession(
            input.Id,
            sampleRate: 48000,
            NullLogger.Instance,
            loopback: true));
    }
}
