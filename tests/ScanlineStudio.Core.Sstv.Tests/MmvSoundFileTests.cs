namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Tests for <see cref="MmvSoundFile"/> against the exact byte-level contract in
/// `docs/plans/sound-file-id-plan.md`'s "MMV format contract"/"Resampling and amplitude" sections,
/// hand-derived from legacy <c>OutputMMV</c> (`Main.cpp:6847-6902`) directly.
/// </summary>
public class MmvSoundFileTests
{
    [Fact]
    public void ParseHeader_FewerThanFourBytes_ReturnsNull()
    {
        // Main.cpp:6857: len>=4 required before the header is even read.
        Assert.Null(MmvSoundFile.ParseHeader(new byte[] { 0x55, 0xAA, 0x01 }));
        Assert.Null(MmvSoundFile.ParseHeader([]));
    }

    [Fact]
    public void ParseHeader_MagicPresent_ReadsSampleRateByteAndOffsetsPayloadBy4()
    {
        var header = MmvSoundFile.ParseHeader([0x55, 0xAA, 0x05, 0x00, 0x11, 0x22]);

        Assert.Equal(new MmvSoundFile.Header(SampleRateIndex: 5, PayloadOffset: 4), header);
    }

    /// <summary>ui_transition_plan.md step 8 (T2-2): exercises every real index a
    /// <see cref="MmvSoundFile.ParseHeader"/>-produced <see cref="MmvSoundFile.Header"/> can ever
    /// carry (0-8 via the magic path, per <see cref="ParseHeader_MagicPresent_ClampsIndexAboveEightToZero"/>
    /// just below; 0-9 via the no-magic path) against the exact table
    /// <see cref="MmvSoundFile.Resample"/> itself already uses -- proves the two stay in sync, not
    /// just that this accessor returns SOME value.</summary>
    [Theory]
    [InlineData(0, 11025)]
    [InlineData(1, 8000)]
    [InlineData(2, 6000)]
    [InlineData(3, 12000)]
    [InlineData(4, 16000)]
    [InlineData(5, 18000)]
    [InlineData(6, 22050)]
    [InlineData(7, 24000)]
    [InlineData(8, 44100)]
    [InlineData(9, 48000)]
    public void GetSourceSampleRateHz_MatchesResampleOwnTable(int index, int expectedHz)
    {
        Assert.Equal(expectedHz, MmvSoundFile.GetSourceSampleRateHz(index));
    }

    [Fact]
    public void ParseHeader_MagicPresent_ClampsIndexAboveEightToZero()
    {
        // Main.cpp:6862: legacy's own literal `>8`, not `>9` -- index 9 (48000) is unreachable via
        // a magic header, preserved as a real legacy quirk, not "fixed."
        var header = MmvSoundFile.ParseHeader([0x55, 0xAA, 0x09, 0x00]);

        Assert.Equal(new MmvSoundFile.Header(SampleRateIndex: 0, PayloadOffset: 4), header);
    }

    [Fact]
    public void ParseHeader_NoMagic_ReadsFirstByteAndPayloadStartsAtZero()
    {
        // Main.cpp:6864-6866: no magic -> Samp=head[0], re-seek to offset 0 -- the 4 sniffed bytes
        // ARE audio data, not skipped.
        var header = MmvSoundFile.ParseHeader([0x03, 0x00, 0x11, 0x22]);

        Assert.Equal(new MmvSoundFile.Header(SampleRateIndex: 3, PayloadOffset: 0), header);
    }

    [Fact]
    public void ParseHeader_NoMagic_IndexAboveNine_IsUnplayable()
    {
        // Legacy's own no-magic path has no clamp at all (SampTable[10] indexed directly is
        // undefined behavior in the original C++) -- this port's own safety divergence: unplayable.
        Assert.Null(MmvSoundFile.ParseHeader([10, 0x00, 0x11, 0x22]));
    }

    [Fact]
    public void Resample_MatchingRate_SkipsFilterAndNormalizesDirectly()
    {
        // Main.cpp:6877/6895-6897: rates already match -> raw copy, no resample AND no IIR filter.
        // Source rate index 1 = 8000Hz (SampTable order, ComLib.cpp:68).
        var payload = ToLittleEndianBytes([10000, -10000, 32767, -32768, 0]);

        var result = MmvSoundFile.Resample(payload, sourceRateIndex: 1, targetSampleRateHz: 8000);

        var expected = new[] { 10000 / 32768f, -10000 / 32768f, 32767 / 32768f, -32768 / 32768f, 0f };
        Assert.Equal(expected.Length, result.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], result[i], precision: 6);
        }
    }

    [Fact]
    public void Resample_OddTrailingByte_IsDropped()
    {
        // Manual byte assembly (not MemoryMarshal.Cast), platform-endianness-independent, and the
        // trailing lone byte is simply never assembled into a sample.
        byte[] payload = [0x10, 0x00, 0x20, 0x00, 0xFF]; // 2 full shorts + 1 trailing byte

        var result = MmvSoundFile.Resample(payload, sourceRateIndex: 1, targetSampleRateHz: 8000);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void Resample_OddLengthPayloadAtLargeUpsampleRatio_DoesNotReadPastTheEndOfThePcmArray()
    {
        // Code-review finding (real bug, fixed before this test was added): the output-length
        // formula is derived from the RAW byte length, but the truncating index pick can reach
        // exactly pcm16.Length for a large enough upsample ratio on an odd-length payload -- an
        // 11-byte payload (5 whole shorts + 1 dropped trailing byte) upsampled 8000Hz -> 48000Hz
        // computes outputSampleCount=33, and the LAST index picks r=5 -- one past pcm16's own
        // 5-element length. Main.cpp:6869 avoids this in legacy via 2 bytes of buffer slack
        // (`new BYTE[len+2]`); this port's own analogue is a zero-padded extra short. Must not throw.
        byte[] payload = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]; // 11 bytes, source index 1 = 8000Hz

        var result = MmvSoundFile.Resample(payload, sourceRateIndex: 1, targetSampleRateHz: 48000);

        Assert.Equal(33, result.Length);
        Assert.All(result, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Resample_DifferingRate_MatchesIndependentlyComputedReference_WithinTightTolerance()
    {
        // Source rate index 1 = 8000Hz upsampled to 16000Hz -- exercises the truncating index pick,
        // the byte-domain output-length formula, and the IIR filter run in the target-rate domain.
        short[] sourcePcm = [10000, -10000, 5000, -5000, 8000, -8000, 3000, -3000];
        var payload = ToLittleEndianBytes(sourcePcm);
        const int sourceRateHz = 8000;
        const int targetRateHz = 16000;

        var result = MmvSoundFile.Resample(payload, sourceRateIndex: 1, targetSampleRateHz: targetRateHz);

        // Independently re-derives the exact pipeline order pinned in the plan (index-pick -> IIR in
        // double -> no short re-quantization -> /32768.0 -> narrow to float), NOT a call into
        // MmvSoundFile itself -- this is what makes it a real check on MmvSoundFile's own assembly of
        // already-trusted primitives (IirFilter is separately tested), not a tautology.
        var outputLengthBytes = (int)(payload.Length * (double)targetRateHz / sourceRateHz);
        outputLengthBytes &= ~1;
        var expectedCount = outputLengthBytes / 2;
        Assert.Equal(expectedCount, result.Length);

        var iir = new IirFilter();
        iir.Design(2700.0, targetRateHz, order: 4);
        for (var i = 0; i < expectedCount; i++)
        {
            var r = (int)(i * (double)sourceRateHz / targetRateHz);
            var expected = (float)(iir.Process(sourcePcm[r]) / 32768.0);
            Assert.Equal(expected, result[i], precision: 6);
        }
    }

    private static byte[] ToLittleEndianBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }

        return bytes;
    }
}
