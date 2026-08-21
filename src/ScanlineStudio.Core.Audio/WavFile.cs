using System.Text;

namespace ScanlineStudio.Core.Audio;

/// <summary>
/// Minimal 16-bit PCM mono WAV read/write. Used for the file-based encode/decode round-trip proof
/// in Phase 1 (spec/14-roadmap.md) — no audio hardware involved, so it's fully deterministic and
/// testable without a native backend.
/// </summary>
public static class WavFile
{
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        const short bitsPerSample = 16;
        const short numChannels = 1;
        var byteRate = sampleRate * numChannels * bitsPerSample / 8;
        var blockAlign = (short)(numChannels * bitsPerSample / 8);
        var dataSize = samples.Length * blockAlign;

        using var writer = new BinaryWriter(File.Create(path));

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write(numChannels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            var clamped = Math.Clamp(sample, -1f, 1f);
            // Scale by 32768 (this port's internal DSP convention, not 32767) so it matches the
            // decoder/AGC/demodulator's own scale. At clamped==1.0f this multiplies to exactly
            // 32768f, one past short.MaxValue; (short)32768f is NOT saturation -- it wraps to
            // short.MinValue (confirmed empirically), which would silently flip a full-scale
            // positive sample to full-scale negative. Clamp the scaled value into short's range
            // before narrowing.
            var scaled = Math.Clamp(clamped * 32768f, short.MinValue, short.MaxValue);
            writer.Write((short)scaled);
        }
    }

    private const short AudioFormatPcm = 1;
    private const short AudioFormatExtensible = unchecked((short)0xFFFE);

    // KSDATAFORMAT_SUBTYPE_PCM -- the WAVE_FORMAT_EXTENSIBLE SubFormat GUID that means "still PCM."
    // .NET's Guid(byte[16]) constructor expects the same mixed-endian layout Windows GUIDs are stored
    // in on the wire, so the 16 raw extension bytes can be handed to it directly, no manual re-ordering.
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");

    public static (float[] Samples, int SampleRate) Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));

        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadInt32(); // chunk size, unused
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        int sampleRate = 0;
        short bitsPerSample = 16;
        short numChannels = 1;
        float[]? samples = null;
        var sawFmt = false;

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            // Round-1 code-review finding (Tier A Batch 8 chunk 8a): the loop condition only checks
            // that at least 1 byte remains, not the full 8-byte chunk header -- 1-7 stray trailing
            // bytes (a truncated download, a writer that appended junk) previously let ReadInt32()
            // run past EOF and leak a bare EndOfStreamException, unlike every other malformed-input
            // path here, which throws InvalidDataException/NotSupportedException. A caller with a
            // catch list built around this method's OWN documented exceptions would miss it.
            if (reader.BaseStream.Length - reader.BaseStream.Position < 8)
            {
                throw new InvalidDataException("Truncated chunk header at end of file.");
            }

            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();
            var remainingInStream = reader.BaseStream.Length - reader.BaseStream.Position;
            if (chunkSize < 0 || chunkSize > remainingInStream)
            {
                throw new InvalidDataException(
                    $"Chunk '{chunkId}' declares size {chunkSize}, but only {remainingInStream} bytes remain in the file.");
            }

            if (chunkId == "fmt ")
            {
                // Round-1 code-review finding: a `chunkSize` below 16 (the fixed format-field size
                // read unconditionally just below) previously wasn't rejected here -- the file-level
                // bound above only checks against bytes remaining in the FILE, not against this
                // chunk's own declared size, so parsing would read past this chunk's true end and
                // desynchronize from the real chunk boundaries (a silent mis-parse, not a crash: the
                // `remaining > 0` guard at the extension read already prevented a negative-length
                // ReadBytes, so this was never memory-unsafe, just wrong).
                if (chunkSize < 16)
                {
                    throw new InvalidDataException(
                        $"'fmt ' chunk declares size {chunkSize}, too small for the required 16-byte format fields.");
                }

                var audioFormat = reader.ReadInt16();
                numChannels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                bitsPerSample = reader.ReadInt16();
                var remaining = chunkSize - 16;
                var extension = remaining > 0 ? reader.ReadBytes(remaining) : [];

                if (audioFormat == AudioFormatExtensible)
                {
                    // Extension layout: cbSize (2) + validBitsPerSample (2) + channelMask (4) +
                    // SubFormat GUID (16) = 24 bytes; the GUID itself starts at offset 8. A too-short
                    // extension is malformed input, not an unsupported-but-valid format -- distinct
                    // exception type from the real non-PCM-SubFormat case below it (round-1
                    // code-review finding: both cases previously shared one NotSupportedException
                    // message that described only the SubFormat case).
                    if (extension.Length < 24)
                    {
                        throw new InvalidDataException(
                            $"WAVE_FORMAT_EXTENSIBLE 'fmt ' chunk's extension is {extension.Length} bytes, too short for the required 24-byte SubFormat block.");
                    }

                    if (new Guid(extension[8..24]) != PcmSubFormat)
                    {
                        throw new NotSupportedException(
                            "WAVE_FORMAT_EXTENSIBLE with a non-PCM SubFormat is not supported.");
                    }
                }
                else if (audioFormat != AudioFormatPcm)
                {
                    throw new NotSupportedException($"Unsupported WAV audio format tag {audioFormat}.");
                }

                sawFmt = true;
            }
            else if (chunkId == "data")
            {
                if (!sawFmt)
                {
                    throw new InvalidDataException("'data' chunk encountered before 'fmt ' chunk.");
                }

                if (bitsPerSample != 16 || numChannels != 1)
                {
                    throw new NotSupportedException("Only 16-bit mono PCM WAV is supported.");
                }

                var sampleCount = chunkSize / 2;
                samples = new float[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                {
                    samples[i] = reader.ReadInt16() / 32768f;
                }
            }
            else
            {
                // Seek rather than ReadBytes -- a legitimately large unknown chunk (e.g. a big LIST/
                // JUNK chunk) would otherwise force a throwaway allocation of the whole chunk just to
                // discard it (round-1 code-review finding).
                reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }

            // RIFF pads every sub-chunk to an even byte boundary.
            if (chunkSize % 2 != 0 && reader.BaseStream.Position < reader.BaseStream.Length)
            {
                reader.ReadByte();
            }
        }

        return (samples ?? [], sampleRate);
    }
}
