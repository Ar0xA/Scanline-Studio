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

    /// <summary>Inverse of <see cref="Read"/> -- writes the same 4-byte header + flat little-endian
    /// <c>int16</c> mono sample stream. Added for TX-side golden-vector fixture prep (spec/14-roadmap.md,
    /// "Milestone audit, Phase 1+2" -> "Explicit prerequisite before Phase 3"): this port's own
    /// <see cref="AnalogFmSstvEncoder"/> output, written in this exact format, can be fed into a real
    /// legacy install via its own `File -&gt; Play` menu action (the same mechanism that produced the
    /// existing RX-direction `.mmv` fixtures, just run in the opposite direction) -- no live audio
    /// hardware/routing needed, since legacy's own Play feature reads the file directly and replays
    /// it through its real-time RX pipeline.
    ///
    /// IMPORTANT for whoever runs `File -&gt; Play` with the result: legacy prompts and RESAMPLES
    /// through a lowpass filter (`Sound.cpp:683-737`) if this file's own SampType doesn't match the
    /// running instance's currently-configured sound-card sample rate (Option dialog) -- silently
    /// defeating the whole point of a faithful TX capture. Set the legacy install's sample rate to
    /// match <paramref name="sampleRate"/> (11025Hz for every fixture this port currently generates)
    /// BEFORE playing the file, the same setup step the existing RX-direction fixtures'
    /// README already documents ("Sample rate set to 11025 Hz in legacy's Setup dialog before
    /// capture").</summary>
    public static void Write(string path, float[] samples, int sampleRate)
    {
        var sampType = Array.IndexOf(SampTable, sampleRate);
        if (sampType < 0)
        {
            throw new NotSupportedException(
                $"Sample rate {sampleRate} isn't one of legacy's own SampTable values ({string.Join(", ", SampTable)}).");
        }

        var data = new byte[4 + (samples.Length * 2)];
        data[0] = 0x55;
        data[1] = 0xAA;
        data[2] = (byte)sampType;
        data[3] = 0x00;

        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            // Code-level review finding (Opus verification, requested before any fixture generation):
            // `(short)Math.Round(clamped * 32768.0f)` can wrap +1.0 to -32768 instead of saturating at
            // +32767 -- Math.Round has no float overload, so the multiply widens to double, and while
            // .NET's saturating float->int32 conversion catches 32768.0 fine (stays exactly 32768),
            // the SUBSEQUENT int32->short narrowing is plain truncation, not saturation: 32768 =
            // 0x8000, which truncates to a short as -32768. Any sample >= ~0.99998474 (clamped*32768
            // >= 32767.5, rounding up to the even 32768) hits this -- genuinely reachable, not
            // hypothetical: AnalogFmSstvEncoder yields raw full-scale Math.Sin(phase), so ~1.3% of
            // every cycle's samples land in this window, injecting a ~2x full-scale discontinuity
            // (impulse noise) roughly every 75 samples throughout an entire transmission. Fixed by
            // clamping in the INTEGER domain after rounding, not relying on the cast to saturate.
            var raw = (short)Math.Clamp(Math.Round(clamped * 32768.0), short.MinValue, short.MaxValue);
            var offset = 4 + (i * 2);
            data[offset] = (byte)(raw & 0xFF);
            data[offset + 1] = (byte)((raw >> 8) & 0xFF);
        }

        File.WriteAllBytes(path, data);
    }
}
