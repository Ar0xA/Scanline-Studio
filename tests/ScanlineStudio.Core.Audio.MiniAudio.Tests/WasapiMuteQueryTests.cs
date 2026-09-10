using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// `BACKLOG.md` W4. <c>IsDeviceMutedAsync</c> reaches Windows' mute state through COM's
/// <c>IAudioEndpointVolume</c> (`scanline_audio.c:1144`, `:1186`). The Linux path has a real test with
/// <c>pactl set-sink-mute</c> as an independent oracle. Windows has had none.
///
/// <para><b>Device cost.</b> Split deliberately, because the two halves cost different things:</para>
///
/// <list type="bullet">
/// <item>The read-only tests take <b>nothing</b>. <c>IAudioEndpointVolume</c> comes from the
/// endpoint's <c>Activate</c>, not from an audio client, so reading mute state opens no stream and
/// interrupts no other application. These run whenever the suite runs on Windows.</item>
/// <item>The oracle test <b>changes the machine's mute state</b> and is opt-in. There is no way to
/// verify a mute query without something to compare against, and on Windows the only oracle is the
/// real setting.</item>
/// </list>
///
/// <para><b>A note this port needs and Linux's equivalent does not.</b> The Windows branch does NOT
/// take <c>g_context_mutex</c>, unlike the Linux one. The reasoning recorded for TT1-15 — that the
/// native shim serialises against context teardown — therefore does not transfer here, and these
/// tests must not be read as covering that.</para>
/// </summary>
public sealed class WasapiMuteQueryTests
{
    [WindowsAudioReadOnlyFact]
    public async Task MuteQuery_ReturnsADefiniteAnswerForEveryEnumeratedDevice()
    {
        var (enumerator, query) = Create();
        using (enumerator)
        using (query)
        {
            await enumerator.RefreshAsync();
            var devices = enumerator.OutputDevices;
            Assert.True(devices.Count > 0, "no output devices found, so nothing could be queried.");

            foreach (var device in devices)
            {
                var muted = await query.IsDeviceMutedAsync(device, isCapture: false);

                // null means the query gave up. On Windows, with a device that enumeration just
                // returned, that is a failure of the COM path rather than a legitimate answer -- the
                // endpoint demonstrably exists.
                Assert.True(
                    muted.HasValue,
                    $"device '{device.Name}' enumerated but its mute state came back null, so the "
                    + "IAudioEndpointVolume activation failed for an endpoint that exists.");
            }
        }
    }

    [WindowsAudioReadOnlyFact]
    public async Task MuteQuery_IsStableWhenNothingChanges()
    {
        // Two reads with no intervening change must agree. A COM path that failed to release its
        // interface, or that raced its own activation, tends to show up as a second call differing
        // from the first rather than as an outright error.
        var (enumerator, query) = Create();
        using (enumerator)
        using (query)
        {
            await enumerator.RefreshAsync();
            var device = enumerator.OutputDevices.FirstOrDefault(d => d.IsDefault)
                ?? enumerator.OutputDevices[0];

            var first = await query.IsDeviceMutedAsync(device, isCapture: false);
            var second = await query.IsDeviceMutedAsync(device, isCapture: false);

            Assert.Equal(first, second);
        }
    }

    [WindowsAudioReadOnlyFact]
    public async Task MuteQuery_ReturnsNullForADeviceThatDoesNotExist()
    {
        // The failure path, reachable without touching a real endpoint: an id that no endpoint owns
        // must produce null rather than a fabricated false, which would read as "not muted" and
        // silently disable the transmit-time mute warning.
        var (enumerator, query) = Create();
        using (enumerator)
        using (query)
        {
            var absent = new AudioDeviceInfo(
                Id: "{00000000-0000-0000-0000-000000000000}.{00000000-0000-0000-0000-000000000000}",
                Name: "device that does not exist",
                MaxInputChannels: 0,
                MaxOutputChannels: 2,
                SupportedSampleRates: [48000]);

            Assert.Null(await query.IsDeviceMutedAsync(absent, isCapture: false));
        }
    }

    /// <summary>
    /// The only test here with an independent oracle, and the only one that costs anything.
    ///
    /// <para><b>What it takes:</b> it mutes the default output device, reads the query, then unmutes
    /// it — or restores whatever the state was before, if it was already muted. Your speakers go
    /// silent for the duration of one query. It restores in a <c>finally</c>, so an assertion failure
    /// still puts the machine back.</para>
    ///
    /// <para><b>Why it cannot be avoided:</b> a mute query can only be verified against a known mute
    /// state, and Windows offers no per-application or virtual endpoint to use instead. The Linux
    /// equivalent has the same shape — it drives <c>pactl set-sink-mute</c> — it is simply cheaper
    /// there because a null sink can be created for the purpose.</para>
    ///
    /// <para>Opt in with <c>SCANLINE_WINDOWS_AUDIO_EXCLUSIVE=1</c>.</para>
    /// </summary>
    [WindowsAudioExclusiveFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task MuteQuery_TracksTheRealMuteState_WhenItIsChangedUnderneath()
    {
        var (enumerator, query) = Create();
        using (enumerator)
        using (query)
        {
            await enumerator.RefreshAsync();
            var device = enumerator.OutputDevices.FirstOrDefault(d => d.IsDefault)
                ?? enumerator.OutputDevices[0];

            var original = await query.IsDeviceMutedAsync(device, isCapture: false);
            Assert.True(original.HasValue, "could not read the starting mute state, so nothing can be restored safely.");

            try
            {
                WindowsEndpointVolume.SetMute(device.Id, mute: true);
                Assert.True(
                    await query.IsDeviceMutedAsync(device, isCapture: false),
                    $"'{device.Name}' was muted through IAudioEndpointVolume but the query still reported unmuted.");

                WindowsEndpointVolume.SetMute(device.Id, mute: false);
                Assert.False(
                    await query.IsDeviceMutedAsync(device, isCapture: false),
                    $"'{device.Name}' was unmuted but the query still reported muted.");
            }
            finally
            {
                // Restore the state we found, not a guessed default -- the machine may legitimately
                // have been muted before the test started.
                WindowsEndpointVolume.SetMute(device.Id, mute: original.Value);
            }
        }
    }

    private static (MiniAudioDeviceEnumerator Enumerator, MiniAudioDeviceMuteQuery Query) Create() => (
        new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance),
        new MiniAudioDeviceMuteQuery());
}
