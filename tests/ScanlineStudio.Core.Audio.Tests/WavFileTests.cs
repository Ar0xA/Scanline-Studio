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

    [Fact]
    public void Read_FmtChunkTooSmallForFormatFields_Throws()
    {
        // Closes a coverage gap flagged by Tier A Batch 8 chunk 8a (docs/functional-audit-playbook.md):
        // a 'fmt ' chunk declaring fewer than the 16 bytes its own fixed format fields require was
        // previously read past its declared end without complaint (a silent mis-parse, not a crash --
        // the `remaining > 0` guard downstream already prevented a negative-length read).
        var path = Path.GetTempFileName();
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(8); // too small for the required 16 format-field bytes
                writer.Write(new byte[8]);
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
    public void Read_ExtensibleFormatWithTruncatedExtension_ThrowsInvalidData()
    {
        // Closes a coverage gap flagged by Tier A Batch 8 chunk 8a: a WAVE_FORMAT_EXTENSIBLE 'fmt '
        // chunk with fewer than the 24 bytes its SubFormat block requires previously shared an
        // exception with the (semantically different) "valid but non-PCM SubFormat" case -- both are
        // NotSupportedException with a message that only describes the SubFormat case. This is
        // malformed data, not an unsupported-but-well-formed feature, so it gets its own
        // InvalidDataException now.
        var path = Path.GetTempFileName();
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(20); // 16 + 4-byte extension, below the 24 bytes SubFormat parsing needs
                writer.Write(unchecked((short)0xFFFE));
                writer.Write((short)1);
                writer.Write(11025);
                writer.Write(11025 * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(new byte[4]);

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
    public void Read_TruncatedChunkHeaderAtEndOfFile_ThrowsInvalidData_NotEndOfStream()
    {
        // Closes a coverage gap flagged by Tier A Batch 8 chunk 8a: 1-7 stray trailing bytes (a
        // truncated download, a writer that appended junk) previously let the next ReadInt32() run
        // past EOF and leak a bare EndOfStreamException -- unlike every OTHER malformed-input path in
        // this method, which throws InvalidDataException/NotSupportedException. A caller with a catch
        // list built around this method's own documented exceptions would have missed it.
        var path = Path.GetTempFileName();
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                WriteFmtChunk(writer, audioFormat: 1, extension: []);

                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(2);
                writer.Write((short)1234);

                // A real chunk header is 8 bytes; only 3 stray bytes remain after this.
                writer.Write((byte)1);
                writer.Write((byte)2);
                writer.Write((byte)3);
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
    public void Read_OddSizedUnknownChunkIsLastInFile_NoPadByteRequired_DoesNotThrow()
    {
        // Closes a coverage gap flagged by Tier A Batch 8 chunk 8a -- the mirror case of
        // Read_OddSizedUnknownChunkBeforeData_SkipsPadByteAndStillParsesData above (odd size + pad
        // byte PRESENT + mid-file): here the odd-sized chunk is the very LAST thing in the file, so
        // there is no physical pad byte at all. The `Position < Length` guard on the RIFF pad-byte
        // read must recognize that and not try to consume a byte that doesn't exist.
        var path = Path.GetTempFileName();
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                WriteFmtChunk(writer, audioFormat: 1, extension: []);

                writer.Write(Encoding.ASCII.GetBytes("LIST"));
                writer.Write(3);
                writer.Write((byte)1);
                writer.Write((byte)2);
                writer.Write((byte)3);
                // No pad byte -- this is genuinely the last byte in the file, no "data" chunk follows.
            }

            File.WriteAllBytes(path, WrapInRiff(stream.ToArray()));

            var (samples, sampleRate) = WavFile.Read(path);
            Assert.Equal(11025, sampleRate);
            Assert.Empty(samples); // no "data" chunk here -- only proving the chunk walk completed cleanly
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData((short)8, (short)1)] // 8-bit mono
    [InlineData((short)16, (short)2)] // 16-bit stereo
    public void Read_UnsupportedBitDepthOrChannelCount_Throws(short bitsPerSample, short numChannels)
    {
        // Closes a coverage gap flagged by Tier A Batch 8 chunk 8a: the "Only 16-bit mono PCM WAV is
        // supported" guard (reachable and correctly gated by the sawFmt check ahead of it) had no test
        // for either half of its own condition.
        var path = Path.GetTempFileName();
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1); // PCM
                writer.Write(numChannels);
                writer.Write(11025);
                writer.Write(11025 * numChannels * bitsPerSample / 8);
                writer.Write((short)(numChannels * bitsPerSample / 8));
                writer.Write(bitsPerSample);

                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(2);
                writer.Write((short)0);
            }

            File.WriteAllBytes(path, WrapInRiff(stream.ToArray()));

            Assert.Throws<NotSupportedException>(() => WavFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_ExtensibleSubFormatGuid_MatchesRealWindowsWireLayout_NotJustSelfConsistentRoundTrip()
    {
        // Closes a coverage gap flagged by Tier A Batch 8 chunk 8a: the existing extensible tests
        // build their SubFormat bytes via `guid.ToByteArray()`, which is self-consistent with this
        // class's own `new Guid(byte[])` parse by construction and so can't detect a wrong wire
        // layout -- the "both halves wrong the same way" pattern CLAUDE.md's behavioral-parity rule
        // warns about. This test hardcodes the REAL KSDATAFORMAT_SUBTYPE_PCM wire bytes (Data1/Data2/
        // Data3 little-endian, Data4 in written order -- the standard Windows GUID on-wire layout)
        // independently of WavFile's own Guid construction.
        var path = Path.GetTempFileName();
        try
        {
            byte[] pcmSubFormatWireBytes =
            [
                0x01, 0x00, 0x00, 0x00, // Data1 = 00000001, little-endian
                0x00, 0x00, // Data2 = 0000, little-endian
                0x10, 0x00, // Data3 = 0010, little-endian
                0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71, // Data4, written order
            ];

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16 + 24);
                writer.Write(unchecked((short)0xFFFE));
                writer.Write((short)1);
                writer.Write(11025);
                writer.Write(11025 * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write((short)22); // cbSize
                writer.Write((short)16); // validBitsPerSample
                writer.Write(0); // channelMask
                writer.Write(pcmSubFormatWireBytes);

                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(2);
                writer.Write((short)0);
            }

            File.WriteAllBytes(path, WrapInRiff(stream.ToArray()));

            var (samples, _) = WavFile.Read(path);
            Assert.Single(samples); // succeeds -- proves the hardcoded wire bytes really are recognized as PCM
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
