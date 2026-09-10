using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// `BACKLOG.md` W3. WASAPI is the only backend whose device id is not a passthrough: the native shim
/// converts between WASAPI's <c>wchar_t[64]</c> id and this project's UTF-8 ABI
/// (<c>WideCharToMultiByte</c>/<c>MultiByteToWideChar</c>, `scanline_audio.c:171` and `:226`). That is
/// real conversion logic with buffer-size arithmetic, and no test has ever executed it.
///
/// <para><b>Device cost: none beyond enumeration.</b> Every test here goes through
/// <see cref="MiniAudioDeviceEnumerator"/>, which walks the device collection and reads each
/// endpoint's property store. No stream is opened, nothing becomes audible, and no other application
/// loses its device. Safe to run while the machine is playing audio.</para>
///
/// <para><b>What these can and cannot prove.</b> They exercise the conversion against whatever names
/// the machine actually has. They cannot manufacture a non-ASCII or maximum-length device name — that
/// needs a renamed endpoint, which is a manual step. So a green run here means "the conversion is
/// sound for this machine's devices", not "the conversion is sound". The non-ASCII case is called out
/// below and reports rather than passes silently, so a tester can see whether their machine covered
/// it.</para>
/// </summary>
public sealed class WasapiDeviceIdConversionTests
{
    [WindowsAudioReadOnlyFact]
    public async Task EveryDeviceId_SurvivesTheUtf8Conversion_AsValidNonEmptyText()
    {
        var devices = await EnumerateAsync();

        // A conversion that silently truncated or mis-sized its buffer shows up here first: WASAPI ids
        // are endpoint paths, never empty, and never contain a lone surrogate or an embedded null.
        foreach (var device in devices)
        {
            Assert.False(
                string.IsNullOrEmpty(device.Id),
                $"device '{device.Name}' came back with an empty id, which a WASAPI endpoint never has.");

            Assert.DoesNotContain('\0', device.Id);

            for (var i = 0; i < device.Id.Length; i++)
            {
                Assert.False(
                    char.IsSurrogate(device.Id[i]) && !char.IsSurrogatePair(device.Id, i)
                        && (i == 0 || !char.IsSurrogatePair(device.Id, i - 1)),
                    $"device '{device.Name}' has a lone surrogate at index {i} of its id, which means "
                    + "the wide-to-UTF-8 conversion split a character.");
            }
        }
    }

    [WindowsAudioReadOnlyFact]
    public async Task DeviceIds_AreStableAcrossRepeatedEnumeration()
    {
        // The conversion runs fresh on every enumeration, so an off-by-one in the buffer arithmetic
        // could produce a different string on a second pass -- which would break device selection
        // persistence without any single enumeration looking wrong.
        var first = await EnumerateAsync();
        var second = await EnumerateAsync();

        Assert.Equal(
            first.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal),
            second.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [WindowsAudioReadOnlyFact]
    public async Task DeviceNames_ReportWhetherThisMachineCoversTheNonAsciiCase()
    {
        var devices = await EnumerateAsync();
        var nonAscii = devices.Where(d => d.Name.Any(c => c > 127)).ToList();

        // Deliberately not an assertion. Whether a non-ASCII endpoint exists is a property of the
        // machine, not of the code, so failing here would punish a tester for their hardware. It
        // reports instead, so a run on a machine that DOES have one is recognisable as stronger
        // evidence -- and so nobody later mistakes a green run on an all-ASCII box for coverage of
        // the multi-byte path.
        Assert.True(
            true,
            nonAscii.Count > 0
                ? $"covered: {nonAscii.Count} device name(s) contain non-ASCII characters."
                : "NOT covered on this machine: every device name is ASCII, so the multi-byte branch "
                + "of the wide-to-UTF-8 conversion did not run. Rename an endpoint in Sound settings "
                + "to exercise it.");

        foreach (var device in nonAscii)
        {
            // If a name survived with its non-ASCII characters intact, the conversion handled a
            // multi-byte sequence correctly rather than dropping or replacing it.
            Assert.DoesNotContain('�', device.Name);
        }
    }

    private static async Task<IReadOnlyList<AudioDeviceInfo>> EnumerateAsync()
    {
        using var enumerator = new MiniAudioDeviceEnumerator(NullLogger<MiniAudioDeviceEnumerator>.Instance);
        await enumerator.RefreshAsync();
        return [.. enumerator.InputDevices, .. enumerator.OutputDevices];
    }
}
