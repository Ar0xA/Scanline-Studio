using System.Text;

namespace Yoniq.Core.Audio;

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
            writer.Write((short)(clamped * short.MaxValue));
        }
    }

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

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                reader.ReadInt16(); // audio format
                numChannels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                bitsPerSample = reader.ReadInt16();
                var remaining = chunkSize - 16;
                if (remaining > 0)
                {
                    reader.ReadBytes(remaining);
                }
            }
            else if (chunkId == "data")
            {
                if (bitsPerSample != 16 || numChannels != 1)
                {
                    throw new NotSupportedException("Only 16-bit mono PCM WAV is supported.");
                }

                var sampleCount = chunkSize / 2;
                samples = new float[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                {
                    samples[i] = reader.ReadInt16() / (float)short.MaxValue;
                }
            }
            else
            {
                reader.ReadBytes(chunkSize);
            }
        }

        return (samples ?? [], sampleRate);
    }
}
