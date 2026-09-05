using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ScanlineStudio.Core.Audio;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>One clip of the recorded HF noise corpus, with the identity fields parsed out of its
/// filename. Layout: <c>&lt;day&gt;_&lt;time&gt;__&lt;range&gt;__&lt;receiver&gt;__&lt;index&gt;_noise&lt;nn&gt;.wav</c>.
/// Day and receiver are what stream selection stratifies on -- the corpus spans only 3 capture days,
/// so two unstratified seeds are not necessarily independent samples of "HF noise".</summary>
public sealed record RealNoiseClip(string Path, string FileName, string Day, string Receiver, string Group, int Index);

/// <summary>The recorded HF noise corpus on an external drive. Never copied into the repo, never
/// committed: the dataset's own license is unverified, so nothing from it ships. The sweep locates
/// it by environment variable and reads it in place.</summary>
public sealed class RealNoiseCorpus
{
    private RealNoiseCorpus(
        string directory,
        IReadOnlyList<RealNoiseClip> clips,
        IReadOnlyList<string> skipped,
        int sampleRate,
        double totalSeconds,
        string identityHash)
    {
        Directory = directory;
        Clips = clips;
        SkippedFiles = skipped;
        SampleRate = sampleRate;
        TotalSeconds = totalSeconds;
        IdentityHash = identityHash;
    }

    public string Directory { get; }

    public IReadOnlyList<RealNoiseClip> Clips { get; }

    /// <summary>Files rejected by the header probe, with the reason. Discovering an unreadable clip
    /// three hours into a sweep is the failure this exists to prevent.</summary>
    public IReadOnlyList<string> SkippedFiles { get; }

    public int SampleRate { get; }

    public double TotalSeconds { get; }

    /// <summary>Hash of the accepted file list, recorded in every run report so a later run against a
    /// changed or remounted drive is detectable rather than silently different.</summary>
    public string IdentityHash { get; }

    public IReadOnlyList<string> Days => Clips.Select(c => c.Day).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();

    public static RealNoiseCorpus Load(string directory, int expectedSampleRate = 48000)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Real-noise corpus directory not found: {directory}");
        }

        var clips = new List<RealNoiseClip>();
        var skipped = new List<string>();
        double totalSeconds = 0;

        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.wav").OrderBy(p => p, StringComparer.Ordinal))
        {
            var probe = ProbeWavHeader(path, expectedSampleRate);
            if (probe.Reason is not null)
            {
                skipped.Add($"{Path.GetFileName(path)}: {probe.Reason}");
                continue;
            }

            var fileName = Path.GetFileName(path);
            if (!TryParseIdentity(fileName, out var day, out var receiver, out var group, out var index))
            {
                skipped.Add($"{fileName}: filename does not match the corpus naming layout.");
                continue;
            }

            clips.Add(new RealNoiseClip(path, fileName, day, receiver, group, index));
            totalSeconds += probe.Seconds;
        }

        if (clips.Count == 0)
        {
            throw new InvalidOperationException(
                $"Real-noise corpus at {directory} yielded no usable clips ({skipped.Count} skipped).");
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', clips.Select(c => c.FileName)))))[..16];

        return new RealNoiseCorpus(directory, clips, skipped, expectedSampleRate, totalSeconds, hash);
    }

    /// <summary>Clips grouped by (day, receiver) -- the stratum stream selection draws from. Within a
    /// stratum, clips are ordered by group then index, which is their natural recording order.</summary>
    public IReadOnlyDictionary<(string Day, string Receiver), IReadOnlyList<RealNoiseClip>> Strata()
    {
        return Clips
            .GroupBy(c => (c.Day, c.Receiver))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RealNoiseClip>)g
                    .OrderBy(c => c.Group, StringComparer.Ordinal)
                    .ThenBy(c => c.Index)
                    .ToList());
    }

    internal static bool TryParseIdentity(string fileName, out string day, out string receiver, out string group, out int index)
    {
        day = string.Empty;
        receiver = string.Empty;
        group = string.Empty;
        index = 0;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var noiseAt = stem.LastIndexOf("_noise", StringComparison.Ordinal);
        if (noiseAt <= 0)
        {
            return false;
        }

        if (!int.TryParse(stem[(noiseAt + "_noise".Length)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        group = stem[..noiseAt];
        var parts = group.Split("__", StringSplitOptions.None);
        if (parts.Length < 3 || parts[0].Length < 8)
        {
            return false;
        }

        day = parts[0][..8];
        receiver = parts[^2];
        return true;
    }

    // Header-only probe: the corpus is about 48GB, so validation must never read sample data.
    private static (string? Reason, double Seconds) ProbeWavHeader(string path, int expectedSampleRate)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
            {
                return ("not a RIFF file", 0);
            }

            reader.ReadInt32();
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            {
                return ("not a WAVE file", 0);
            }

            short channels = 0;
            short bits = 0;
            var rate = 0;
            var dataBytes = 0;
            var sawFmt = false;

            while (reader.BaseStream.Length - reader.BaseStream.Position >= 8)
            {
                var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var chunkSize = reader.ReadInt32();
                if (chunkSize < 0 || chunkSize > reader.BaseStream.Length - reader.BaseStream.Position)
                {
                    return ("truncated or malformed chunk", 0);
                }

                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    reader.ReadInt16();
                    channels = reader.ReadInt16();
                    rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    bits = reader.ReadInt16();
                    reader.BaseStream.Position += chunkSize - 16;
                    sawFmt = true;
                }
                else if (chunkId == "data")
                {
                    dataBytes = chunkSize;
                    break;
                }
                else
                {
                    reader.BaseStream.Position += chunkSize + (chunkSize % 2);
                }
            }

            if (!sawFmt)
            {
                return ("no fmt chunk", 0);
            }

            if (rate != expectedSampleRate)
            {
                return ($"sample rate {rate}Hz, expected {expectedSampleRate}Hz", 0);
            }

            if (channels != 1)
            {
                return ($"{channels} channels, expected mono (WavFile.Read rejects it)", 0);
            }

            if (bits != 16)
            {
                return ($"{bits}-bit, expected 16-bit (WavFile.Read rejects it)", 0);
            }

            if (dataBytes <= 0)
            {
                return ("empty or missing data chunk", 0);
            }

            return (null, dataBytes / 2.0 / rate);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
        {
            return (ex.Message, 0);
        }
    }

    internal static float[] ReadClip(RealNoiseClip clip)
    {
        var (samples, _) = WavFile.Read(clip.Path);
        return samples;
    }
}
