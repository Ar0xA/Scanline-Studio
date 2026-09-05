using System.Text;
using ScanlineStudio.Core.Audio;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Corpus loading and stream assembly, exercised against a synthetic corpus written to a
/// temp directory. Deliberately does not touch the real external drive: these tests must run on any
/// machine, and the drive-backed sweep is opt-in only.</summary>
public sealed class RealNoiseCorpusTests : IDisposable
{
    private const int CorpusSampleRate = 48000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scanline-real-noise-" + Guid.NewGuid().ToString("N"));

    public RealNoiseCorpusTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("19_12_04_13_18_10__101_105__GeXX179x__101_noise00.wav", "19_12_04", "GeXX179x", 0)]
    [InlineData("19_12_10_14_39_04__176_180__UnXX251x__176_noise05.wav", "19_12_10", "UnXX251x", 5)]
    public void TryParseIdentity_ExtractsDayReceiverAndIndex(string fileName, string expectedDay, string expectedReceiver, int expectedIndex)
    {
        Assert.True(RealNoiseCorpus.TryParseIdentity(fileName, out var day, out var receiver, out var group, out var index));

        Assert.Equal(expectedDay, day);
        Assert.Equal(expectedReceiver, receiver);
        Assert.Equal(expectedIndex, index);
        Assert.EndsWith("__" + expectedReceiver + "__" + fileName.Split("__")[^1].Split("_noise")[0], group, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-corpus-file.wav")]
    [InlineData("19_12_04__GeXX179x_noise00.wav")]
    [InlineData("19_12_04_13_18_10__101_105__GeXX179x__101_noiseXX.wav")]
    public void TryParseIdentity_RejectsAnythingOffLayout(string fileName)
    {
        Assert.False(RealNoiseCorpus.TryParseIdentity(fileName, out _, out _, out _, out _));
    }

    [Fact]
    public void Load_SkipsUnusableClips_AndReportsWhy()
    {
        WriteClip("19_12_04_00_00_00__1_2__GeXX1x__1_noise00.wav", seed: 1, seconds: 1.0);
        WriteClip("19_12_04_00_00_00__1_2__GeXX1x__1_noise01.wav", seed: 2, seconds: 1.0, sampleRate: 22050);
        WriteClip("19_12_04_00_00_00__1_2__GeXX1x__1_noise02.wav", seed: 3, seconds: 0.0);
        WriteStereoClip("19_12_04_00_00_00__1_2__GeXX1x__1_noise03.wav");
        WriteClip("random-other-file.wav", seed: 4, seconds: 1.0);

        var corpus = RealNoiseCorpus.Load(_dir);

        Assert.Single(corpus.Clips);
        Assert.Equal(4, corpus.SkippedFiles.Count);
        Assert.Contains(corpus.SkippedFiles, s => s.Contains("22050Hz", StringComparison.Ordinal));
        Assert.Contains(corpus.SkippedFiles, s => s.Contains("data chunk", StringComparison.Ordinal));
        Assert.Contains(corpus.SkippedFiles, s => s.Contains("2 channels", StringComparison.Ordinal));
        Assert.Contains(corpus.SkippedFiles, s => s.Contains("naming layout", StringComparison.Ordinal));
    }

    [Fact]
    public void IdentityHash_ChangesWhenTheAcceptedFileListChanges()
    {
        WriteStratum("19_12_04", "GeXX1x", clips: 3);
        var before = RealNoiseCorpus.Load(_dir).IdentityHash;

        WriteClip("19_12_04_00_00_00__1_2__GeXX1x__1_noise09.wav", seed: 77, seconds: 1.0);
        var after = RealNoiseCorpus.Load(_dir).IdentityHash;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Build_ReturnsTheRequestedLength_AndIsDeterministicForASeed()
    {
        WriteStratum("19_12_04", "GeXX1x", clips: 8);
        var corpus = RealNoiseCorpus.Load(_dir);

        var first = RealNoiseStreamBuilder.Build(corpus, seed: 12345, seedOrdinal: 0, requiredSeconds: 3.0);
        var second = RealNoiseStreamBuilder.Build(corpus, seed: 12345, seedOrdinal: 0, requiredSeconds: 3.0);

        Assert.Equal((int)(3.0 * RealNoiseStreamBuilder.OutputSampleRate), first.Samples.Length);
        Assert.Equal(first.FileNames, second.FileNames);
        Assert.Equal(first.Samples, second.Samples);
    }

    [Fact]
    public void Build_RecordsJoinsClipLevelsAndSpread()
    {
        // Two deliberately different clip levels, so the spread is a known quantity rather than luck.
        WriteClip("19_12_04_00_00_00__1_2__GeXX1x__1_noise00.wav", seed: 11, seconds: 1.0, amplitude: 0.5);
        WriteClip("19_12_04_00_00_00__1_2__GeXX1x__1_noise01.wav", seed: 12, seconds: 1.0, amplitude: 0.05);
        var corpus = RealNoiseCorpus.Load(_dir);

        var stream = RealNoiseStreamBuilder.Build(corpus, seed: 5, seedOrdinal: 0, requiredSeconds: 3.0);

        Assert.True(stream.FileNames.Count >= 4, $"expected several clips for a 3s stream, got {stream.FileNames.Count}");
        Assert.Equal(stream.FileNames.Count, stream.ClipRmsDb.Count);
        Assert.NotEmpty(stream.JoinOffsetsSeconds);
        Assert.All(stream.JoinOffsetsSeconds, t => Assert.InRange(t, 0.0, 3.0));
        // 0.5 against 0.05 is 20dB by construction.
        Assert.InRange(stream.ClipRmsSpreadDb, 19.0, 21.0);
        Assert.True(stream.ClipRmsSpreadWarning);
    }

    [Fact]
    public void Build_StratifiesAcrossCaptureDays()
    {
        WriteStratum("19_12_04", "GeXX1x", clips: 4);
        WriteStratum("19_12_05", "NeXX2x", clips: 4);
        WriteStratum("19_12_10", "SwXX3x", clips: 4);
        var corpus = RealNoiseCorpus.Load(_dir);

        var days = Enumerable.Range(0, 3)
            .Select(ordinal => RealNoiseStreamBuilder.Build(corpus, seed: 100 + ordinal, seedOrdinal: ordinal, requiredSeconds: 1.0).Day)
            .ToList();

        // The corpus spans only 3 capture days, so unstratified seeds could all land on one.
        Assert.Equal(3, days.Distinct().Count());
    }

    [Fact]
    public void Crossfade_HoldsConstantPower_ForUncorrelatedSegments()
    {
        // Averaged over many independent realizations: a single draw is too noisy to distinguish a
        // constant-power fade from an amplitude fade's ~3dB mid-fade dip.
        //
        // ONE generator for the whole test, not one per realization. Separate `new Random(seed)`
        // instances on sequential seeds are strongly correlated position-by-position in .NET's legacy
        // seeded generator, which collapses the effective sample size and shows up here as a power
        // ripple that would be misread as a crossfade defect. At 4000 independent realizations the
        // sampling floor of this statistic is about 0.6dB, well inside the 1.0dB bar below.
        const int crossfade = 512;
        const int realizations = 4000;
        var power = new double[crossfade];
        var random = new Random(20260905);

        for (var r = 0; r < realizations; r++)
        {
            var a = GaussianNoise(random, count: crossfade * 2, amplitude: 1.0);
            var b = GaussianNoise(random, count: crossfade * 2, amplitude: 1.0);
            var assembled = new List<float>(a);

            RealNoiseStreamBuilder.CrossfadeInto(assembled, b, crossfade);

            var overlapStart = a.Length - crossfade;
            for (var i = 0; i < crossfade; i++)
            {
                power[i] += (double)assembled[overlapStart + i] * assembled[overlapStart + i];
            }
        }

        for (var i = 0; i < crossfade; i++)
        {
            power[i] /= realizations;
        }

        var flatnessDb = 10.0 * Math.Log10(power.Max() / power.Min());
        Assert.True(flatnessDb < 1.0, $"crossfade power varies by {flatnessDb:F2}dB across the join");
    }

    [Fact]
    public void Crossfade_PreservesTheTotalLength()
    {
        var assembled = new List<float>(new float[1000]);

        RealNoiseStreamBuilder.CrossfadeInto(assembled, new float[600], crossfade: 100);

        Assert.Equal(1000 + 600 - 100, assembled.Count);
    }

    private void WriteStratum(string day, string receiver, int clips)
    {
        for (var i = 0; i < clips; i++)
        {
            WriteClip($"{day}_00_00_00__1_2__{receiver}__1_noise{i:D2}.wav", seed: day.GetHashCode(StringComparison.Ordinal) + i, seconds: 1.0);
        }
    }

    private void WriteClip(string name, int seed, double seconds, int sampleRate = CorpusSampleRate, double amplitude = 0.2)
    {
        var samples = GaussianNoise(seed, (int)(sampleRate * seconds), amplitude);
        WavFile.Write(Path.Combine(_dir, name), samples, sampleRate);
    }

    // Hand-rolled, because WavFile.Write only emits mono -- and a stereo clip is exactly the case
    // WavFile.Read throws on, which is what the header probe exists to catch before a long run.
    private void WriteStereoClip(string name)
    {
        using var stream = File.Create(Path.Combine(_dir, name));
        using var writer = new BinaryWriter(stream);
        const int frames = 4800;
        var dataBytes = frames * 2 * 2;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(CorpusSampleRate);
        writer.Write(CorpusSampleRate * 2 * 2);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var i = 0; i < frames * 2; i++)
        {
            writer.Write((short)0);
        }
    }

    private static float[] GaussianNoise(int seed, int count, double amplitude) =>
        GaussianNoise(new Random(seed), count, amplitude);

    private static float[] GaussianNoise(Random random, int count, double amplitude)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            samples[i] = (float)(amplitude * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return samples;
    }
}
