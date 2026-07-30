namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Test-only reader for legacy MMSSTV's `.mmv` recording format (<c>Sound.cpp</c>'s
/// <c>CWaveFile::Rec</c>/<c>ReadWrite</c>/<c>Play</c>), used only for golden-vector fixtures (see
/// Fixtures/GoldenVectors/README.md). Confirmed directly against source, not assumed: this is NOT a
/// RIFF/WAV file (no RIFF/WAVE/fmt /data chunk structure anywhere in the write path) -- it's a
/// 4-byte header <c>[0x55, 0xAA, SampType, 0x00]</c> followed by a flat stream of little-endian
/// <c>int16</c> mono samples, written via bare <c>mmioWrite(handle, &amp;shortValue, 2)</c> calls.
/// <c>SampType</c> is an index into legacy's own <c>SampTable</c> (<c>ComLib.cpp:68</c>):
/// <c>{11025, 8000, 6000, 12000, 16000, 18000, 22050, 24000, 44100, 48000}</c> -- confirmed by
/// reading that array directly, not inferred from <c>InitSampType</c>'s bucket thresholds alone.
///
/// Kept in the test project rather than <c>Yoniq.Core.Sstv</c>: this format has no production
/// consumer (Yoniq v2 doesn't read/write `.mmv` files as a feature; it exists purely so a developer
/// with a real legacy install can capture golden-vector fixtures by hand), so promoting it would
/// add a public surface nothing else needs. Don't "helpfully" move this into `Yoniq.Core.Sstv`
/// later without an actual production use case driving it.
/// </summary>
internal static class MmvFile
{
    private static readonly int[] SampTable = [11025, 8000, 6000, 12000, 16000, 18000, 22050, 24000, 44100, 48000];

    public static (float[] Samples, int SampleRate) Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 4 || data[0] != 0x55 || data[1] != 0xAA)
        {
            throw new InvalidDataException($"'{path}' is missing the .mmv magic header (0x55 0xAA).");
        }

        var sampType = data[2];
        if (sampType >= SampTable.Length)
        {
            // Legacy itself is inconsistent about this bound (Sound.cpp:679 clamps at >9,
            // Main.cpp:6862 clamps at >8) -- neither of those is a real validation we can port
            // faithfully, so this throws rather than silently clamping to an unverified guess.
            throw new NotSupportedException(
                $"'{path}': SampType {sampType} is outside the known SampTable range (0-{SampTable.Length - 1}).");
        }

        var sampleRate = SampTable[sampType];

        var body = data.AsSpan(4);
        var sampleCount = body.Length / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var raw = (short)(body[i * 2] | (body[(i * 2) + 1] << 8));
            samples[i] = raw / 32768.0f;
        }

        return (samples, sampleRate);
    }
}
