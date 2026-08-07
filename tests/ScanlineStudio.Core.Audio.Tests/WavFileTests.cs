using System.Text;

namespace ScanlineStudio.Core.Audio.Tests;

/// <summary>
/// Closes ultracode audit findings #36/#37 (WAV read/write sample scale contradicted this port's own
/// internal DSP convention of 32768, not 32767 -- <c>AnalogFmSstvDecoder</c>/<c>LevelAgc</c>/
/// <c>PllFmDemodulator</c> all use 32768) and #38 (no <c>audioFormat</c> validation, no RIFF pad-byte
/// handling, no bound-checking on <c>chunkSize</c>, <c>data</c>-before-<c>fmt</c> silently used
/// defaults instead of failing). No legacy counterpart exists for any of this -- legacy has no WAV
/// file I/O at all -- so these are pure port-only bug fixes, not legacy-fidelity ports.
/// </summary>
public class WavFileTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    public void WriteThenRead_RoundTrips_WithinQuantizationTolerance(float sample)
    {
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, [sample], sampleRate: 11025);
            var (samples, sampleRate) = WavFile.Read(path);

            Assert.Equal(11025, sampleRate);
            Assert.Single(samples);
            // 1/32768 quantization step, not the old 1/32767 mismatch that #36/#37 introduced.
            Assert.Equal(sample, samples[0], tolerance: 1.0 / 32768.0 + 1e-6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_FullScalePositiveSample_DoesNotWrapToNegative()
    {
        // #37 fix: (short)(1.0f * 32768f) wraps to short.MinValue without an explicit post-scale
        // clamp -- confirmed empirically, not assumed. A full-scale +1.0f sample must come back
        // positive, not flip polarity.
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, [1.0f], sampleRate: 11025);
            var (samples, _) = WavFile.Read(path);

            Assert.True(samples[0] > 0.9f, $"Expected a near-full-scale positive sample, got {samples[0]}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_AudioFormatNotPcm_Throws()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, BuildWavBytes(audioFormat: 2, dataBytes: [0, 0])); // IEEE float tag
            Assert.Throws<NotSupportedException>(() => WavFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_ExtensibleFormatWithPcmSubFormat_Succeeds()
    {
        var path = Path.GetTempFileName();
        try
        {
            var pcmSubFormatGuid = new Guid("00000001-0000-0010-8000-00AA00389B71");
            File.WriteAllBytes(path, BuildWavBytes(
                audioFormat: unchecked((short)0xFFFE),
                dataBytes: [0, 0],
                extensibleSubFormat: pcmSubFormatGuid));

            var (samples, _) = WavFile.Read(path);
            Assert.Single(samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_ExtensibleFormatWithNonPcmSubFormat_Throws()
    {
        var path = Path.GetTempFileName();
        try
        {
            var nonPcmSubFormatGuid = Guid.NewGuid();
            File.WriteAllBytes(path, BuildWavBytes(
                audioFormat: unchecked((short)0xFFFE),
                dataBytes: [0, 0],
                extensibleSubFormat: nonPcmSubFormatGuid));

            Assert.Throws<NotSupportedException>(() => WavFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_OddSizedUnknownChunkBeforeData_SkipsPadByteAndStillParsesData()
    {
        var path = Path.GetTempFileName();
        try
        {
            // "LIST" chunk with an odd 3-byte payload -- RIFF requires a pad byte after it, at
            // offset 4+4+3 = 11, before the "data" chunk begins.
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                WriteFmtChunk(writer, audioFormat: 1, extension: []);

                writer.Write(Encoding.ASCII.GetBytes("LIST"));
                writer.Write(3);
                writer.Write((byte)1);
                writer.Write((byte)2);
                writer.Write((byte)3);
                writer.Write((byte)0); // RIFF pad byte

                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(2);
                writer.Write((short)1234);
            }

            var body = stream.ToArray();
            File.WriteAllBytes(path, WrapInRiff(body));

            var (samples, _) = WavFile.Read(path);
            Assert.Single(samples);
            Assert.Equal(1234 / 32768f, samples[0], precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_DataBeforeFmt_Throws()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(2);
                writer.Write((short)0);
            }

            File.WriteAllBytes(path, WrapInRiff(stream.ToArray()));

            Assert.Throws<InvalidDataException>(() => WavFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_NegativeChunkSize_ThrowsInsteadOfCrashing()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(-1);
            }

            File.WriteAllBytes(path, WrapInRiff(stream.ToArray()));

            Assert.Throws<InvalidDataException>(() => WavFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_ChunkSizeLargerThanRemainingFile_Throws()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(1_000_000); // wildly larger than the actual remaining file content
                writer.Write((short)1);
            }

            File.WriteAllBytes(path, WrapInRiff(stream.ToArray()));

            Assert.Throws<InvalidDataException>(() => WavFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildWavBytes(short audioFormat, byte[] dataBytes, Guid? extensibleSubFormat = null)
    {
        byte[] extension = [];
        if (audioFormat == unchecked((short)0xFFFE))
        {
            using var ext = new MemoryStream();
            using var writer = new BinaryWriter(ext);
            writer.Write((short)22); // cbSize
            writer.Write((short)16); // validBitsPerSample
            writer.Write(0); // channelMask
            writer.Write((extensibleSubFormat ?? Guid.Empty).ToByteArray());
            extension = ext.ToArray();
        }

        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true))
        {
            WriteFmtChunk(writer, audioFormat, extension);

            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataBytes.Length);
            writer.Write(dataBytes);
        }

        return WrapInRiff(body.ToArray());
    }

    private static void WriteFmtChunk(BinaryWriter writer, short audioFormat, byte[] extension)
    {
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16 + extension.Length);
        writer.Write(audioFormat);
        writer.Write((short)1); // numChannels
        writer.Write(11025); // sampleRate
        writer.Write(11025 * 2); // byteRate
        writer.Write((short)2); // blockAlign
        writer.Write((short)16); // bitsPerSample
        writer.Write(extension);
    }

    private static byte[] WrapInRiff(byte[] body)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(4 + body.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(body);
        return stream.ToArray();
    }
}
