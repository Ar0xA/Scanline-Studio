using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// `BACKLOG.md` W3. WASAPI is the only backend whose device id is not a passthrough: the native shim
/// converts between WASAPI's <c>wchar_t[64]</c> id and this project's UTF-8 ABI
/// (<c>WideCharToMultiByte</c> at `scanline_audio.c:171`, <c>MultiByteToWideChar</c> at `:226`). That
/// is real conversion logic with buffer-size arithmetic, and no test has ever executed it.
///
/// <para><b>Device cost: enumeration only.</b> No stream is opened and nothing becomes audible, so
/// these are safe to run while the machine is playing audio. One correction to an earlier version of
/// this comment: enumeration is not merely a property-store read. <c>RefreshAsync</c> probes native
/// formats per device, which activates an <c>IAudioClient</c> on each endpoint and calls
/// <c>IsFormatSupported</c> — it never calls <c>Initialize</c> or <c>Start</c>, so nothing is taken
/// from another application, but it is not free in TIME on a machine with many endpoints.</para>
///
/// <para><b>Both directions are covered, and the second one is easy to miss.</b> The wide-to-UTF-8
/// direction shows up directly in every device id. The UTF-8-to-wide direction at `:226` runs during
/// format probing, and if it were broken the probe would fail, degrade to an empty format list, and
/// every id assertion would still pass. So one test asserts on the probe's OUTPUT, which is only
/// non-empty if the id round-tripped back to a real endpoint.</para>
/// </summary>
public sealed class WasapiDeviceIdConversionTests(ITestOutputHelper output)
{
    [WindowsAudioReadOnlyFact]
    public async Task EveryDeviceId_SurvivesTheUtf8Conversion_Intact()
    {
        var devices = await RequireDevicesAsync();

        foreach (var device in devices)
        {
            Assert.False(
                string.IsNullOrEmpty(device.Id),
                $"device '{device.Name}' came back with an empty id. That is what the shim produces "
                + "when WideCharToMultiByte returns zero or negative and the buffer is blanked.");

            // The replacement character is the live assertion here. A split multi-byte sequence
            // decodes to U+FFFD -- whereas an embedded null cannot survive to be asserted on (the
            // managed decoder cuts at the first one) and a lone surrogate cannot occur at all
            // (UTF8.GetString emits U+FFFD instead). Asserting on those two would be unfalsifiable.
            Assert.DoesNotContain('�', device.Id);
        }
    }

    [WindowsAudioReadOnlyFact]
    public async Task DeviceIds_AreStableAcrossRepeatedEnumeration()
    {
        // The conversion runs fresh on every enumeration, so an off-by-one in the buffer arithmetic
        // could produce a different string on a second pass -- breaking device-selection persistence
        // without any single enumeration looking wrong.
        var first = await RequireDevicesAsync();
        var second = await RequireDevicesAsync();

        Assert.Equal(
            first.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal),
            second.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [WindowsAudioReadOnlyFact]
    public async Task AtLeastOneDevice_ReportsSupportedFormats_WhichOnlyHappensIfTheIdConvertsBack()
    {
        // This is the only assertion that can fail if MultiByteToWideChar at `:226` is broken. That
        // conversion turns our UTF-8 id back into a WASAPI id during format probing; if it produced
        // garbage the probe returns -1, the enumerator logs and degrades to an empty format list, and
        // every OTHER test in this file still passes. An empty list everywhere is the signature.
        var devices = await RequireDevicesAsync();

        Assert.Contains(
            devices,
            d => d.SupportedSampleRates.Count > 0);
    }

    [WindowsAudioReadOnlyFact]
    public async Task NonAsciiDeviceNames_AreReportedAsCoverage_NotAsserted()
    {
        var devices = await RequireDevicesAsync();
        var nonAscii = devices.Where(d => d.Name.Any(c => c > 127)).ToList();

        // Whether such a device exists is a property of the machine, not of the code, so asserting it
        // would punish a tester for their hardware. It is written to test output instead -- xUnit only
        // surfaces an assertion message on FAILURE, so an Assert.True(true, message) would print
        // nothing and this note would silently do nothing at all.
        output.WriteLine(nonAscii.Count > 0
            ? $"COVERED: {nonAscii.Count} of {devices.Count} device names contain non-ASCII characters."
            : $"NOT COVERED on this machine: all {devices.Count} device names are ASCII, so the "
            + "multi-byte branch of the wide-to-UTF-8 conversion did not run. Rename an endpoint in "
            + "Sound settings to exercise it.");

        foreach (var device in nonAscii)
        {
            Assert.DoesNotContain('�', device.Name);
        }
    }

    /// <summary>
    /// Enumerates, and fails rather than passing vacuously when the machine has no audio endpoints.
    /// That case is not hypothetical: it is the default state of a GitHub <c>windows-latest</c>
    /// runner, which is the first machine these are likely to run on.
    /// </summary>
    private static async Task<IReadOnlyList<AudioDeviceInfo>> RequireDevicesAsync()
    {
        using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
        await enumerator.RefreshAsync();

        IReadOnlyList<AudioDeviceInfo> devices = [.. enumerator.InputDevices, .. enumerator.OutputDevices];

        Assert.True(
            devices.Count > 0,
            "no audio devices were enumerated, so the WASAPI id conversion never ran and this test "
            + "would otherwise pass without asserting anything. A headless CI runner with no audio "
            + "endpoint is the usual cause.");

        return devices;
    }
}
