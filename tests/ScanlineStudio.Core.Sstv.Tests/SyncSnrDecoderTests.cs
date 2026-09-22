using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

public class SyncSnrDecoderTests
{
    private const int Rate = 11025;

    /// <summary>Adds white Gaussian noise whose 400–2500 Hz power sits <paramref name="snrDb"/> below the
    /// clean signal's mean power (an FM signal's power lies essentially all in that band).</summary>
    internal static float[] AddInBandNoise(float[] clean, int sampleRate, double snrDb, int seed)
    {
        var power = clean.Average(x => (double)x * x);
        var sigma = Math.Sqrt(power / Math.Pow(10, snrDb / 10) * (sampleRate / 2.0) / SyncSnrEstimator.BandWidthHz);
        var random = new Random(seed);
        return clean.Select(x => (float)(x + (sigma * SyncSnrEstimatorTests.Gaussian(random)))).ToArray();
    }

    private static (AnalogFmSstvDecoder Decoder, IImageSource? Image, SstvModeDefinition? Mode) Decode(
        float[] samples, bool snrOn, RxBufferMode bufferMode = RxBufferMode.On, int chunk = 4096, Action<AnalogFmSstvDecoder>? before = null)
    {
        var decoder = new AnalogFmSstvDecoder(Rate, rxBufferMode: bufferMode) { SnrMeasurementEnabled = snrOn };
        IImageSource? image = null;
        SstvModeDefinition? mode = null;
        decoder.ModeDetected += m => mode = m;
        decoder.LineDecoded += u => image = u.Image;
        before?.Invoke(decoder);
        SyncSnrTestSignals.PushChunked(decoder, samples, chunk);
        return (decoder, image, mode);
    }

    private static Rgb24[] Pixels(IImageSource image)
    {
        var pixels = new Rgb24[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            image.GetScanline(y).CopyTo(pixels.AsSpan(y * image.Width, image.Width));
        }

        return pixels;
    }

    public static TheoryData<string> CoveredModes => new() { "martin-m1", "scottie-s1", "robot-36", "pd120", "mn73", "mc110", "p3" };

    [Theory]
    [MemberData(nameof(CoveredModes))]
    public async Task Noiseless_ReadsAtLeast35Db(string modeId)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
        var (samples, _) = await SyncSnrTestSignals.EncodeAsync(mode, Rate, 40);

        var (decoder, _, detected) = Decode(samples, snrOn: true);

        Assert.Equal(mode.Id, detected?.Id);
        Assert.True(decoder.ReceptionSnrDb >= 35, $"{modeId} noiseless read {decoder.ReceptionSnrDb:F1} dB");
        Assert.True(decoder.SyncSnrTrackerForTests.LinesContributed > 10);
    }

    [Theory]
    [InlineData("martin-m1", 5.0)]
    [InlineData("scottie-s1", 5.0)]
    [InlineData("pd120", 5.0)]
    [InlineData("mn73", 5.0)]
    [InlineData("robot-36", 15.0)]
    public async Task Noisy_PlacementMedianIsWithinAQuarterMillisecond_AndSnrIsClose(string modeId, double snrDb)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
        var (clean, truth) = await SyncSnrTestSignals.EncodeAsync(mode, Rate, 60);
        var noisy = AddInBandNoise(clean, Rate, snrDb, seed: 11);
        var errorsMs = new List<double>();

        var (decoder, _, _) = Decode(noisy, snrOn: true, before: d => d.SyncSnrLineObservedForTests = (line, placed, _) =>
        {
            if (!double.IsNaN(placed) && line < truth.Length)
            {
                errorsMs.Add((placed - truth[line]) / Rate * 1000.0);
            }
        });

        Assert.NotEmpty(errorsMs);
        errorsMs.Sort();
        Assert.InRange(errorsMs[errorsMs.Count / 2], -0.25, 0.25);
        Assert.InRange(decoder.ReceptionSnrDb - snrDb, -1.5, 1.5);
    }

    [Theory]
    [InlineData(RxBufferMode.Off, 4096)]
    [InlineData(RxBufferMode.On, 997)]
    [InlineData(RxBufferMode.Extended, 4096)]
    public async Task DecodedPixels_AreBitIdentical_OnAndOff(RxBufferMode bufferMode, int chunk)
    {
        var mode = SstvModeRegistry.ScottieS1;
        var (clean, _) = await SyncSnrTestSignals.EncodeAsync(mode, Rate, null);
        var noisy = AddInBandNoise(clean, Rate, 8, seed: 3);

        var off = Decode(noisy, snrOn: false, bufferMode, chunk);
        var on = Decode(noisy, snrOn: true, bufferMode, chunk);

        Assert.NotNull(off.Image);
        Assert.Equal(Pixels(off.Image!), Pixels(on.Image!));
        Assert.True(double.IsNaN(off.Decoder.ReceptionSnrDb));
        Assert.False(double.IsNaN(on.Decoder.ReceptionSnrDb));
    }

    [Fact]
    public async Task ForceModeAtStreamStart_SkipsLinesOutsideTheBuffer_NeverThrows()
    {
        var mode = SstvModeRegistry.MartinM1;
        var (clean, _) = await SyncSnrTestSignals.EncodeAsync(mode, Rate, 20);
        var decoder = new AnalogFmSstvDecoder(Rate) { SnrMeasurementEnabled = true };
        decoder.ForceMode(mode);

        var exception = Record.Exception(() => SyncSnrTestSignals.PushChunked(decoder, clean));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Avt_HasNoSnr()
    {
        var mode = SstvModeRegistry.Avt;
        var (samples, _) = await AvtSamples();
        var (decoder, _, detected) = Decode(samples, snrOn: true);

        Assert.Equal(mode.Id, detected?.Id);
        Assert.True(double.IsNaN(decoder.ReceptionSnrDb));
        Assert.True(double.IsNaN(decoder.LiveSnrDb));

        static async Task<(float[], int)> AvtSamples()
        {
            var avt = SstvModeRegistry.Avt;
            var encoder = new AnalogFmSstvEncoder(Rate);
            var list = new List<float>();
            await foreach (var s in encoder.EncodeAsync(avt, SyncSnrTestSignals.SourceFor(avt)))
            {
                list.Add(s);
            }

            return (list.ToArray(), 0);
        }
    }

    [Fact]
    public async Task Narrow_MeasuresAround1900Hz()
    {
        var mode = SstvModeRegistry.Mn73;
        var (samples, _) = await SyncSnrTestSignals.EncodeAsync(mode, Rate, 40);
        var (decoder, _, _) = Decode(samples, snrOn: true);

        Assert.InRange(decoder.SyncSnrTrackerForTests.CentreHzForTests ?? 0, 1880, 1920);
    }

    [Fact]
    public async Task Live_GoesNaNAtEndOfImage_ReceptionFigureSurvives()
    {
        var mode = SstvModeRegistry.Robot36;
        var (clean, _) = await SyncSnrTestSignals.EncodeAsync(mode, Rate, null);
        var samples = clean.Concat(new float[Rate * 2]).ToArray();
        double liveDuring = double.NaN;
        var (decoder, _, _) = Decode(samples, snrOn: true, before: d => d.LineDecoded += _ =>
        {
            if (!double.IsNaN(d.LiveSnrDb))
            {
                liveDuring = d.LiveSnrDb;
            }
        });

        Assert.False(double.IsNaN(liveDuring));
        Assert.True(double.IsNaN(decoder.LiveSnrDb));
        Assert.False(double.IsNaN(decoder.ReceptionSnrDb));
    }

    [Fact]
    public async Task NewReception_ResetsTheStore()
    {
        var mode = SstvModeRegistry.Robot36;
        var (clean, _) = await SyncSnrTestSignals.EncodeAsync(mode, Rate, null);
        var samples = clean.Concat(new float[Rate]).Concat(clean).ToArray();
        var snrAtSecondLock = new List<double>();
        var storedAtLock = new List<int>();
        var (decoder, _, _) = Decode(samples, snrOn: true, before: d => d.ModeDetected += _ =>
        {
            snrAtSecondLock.Add(d.ReceptionSnrDb);
            storedAtLock.Add(d.SyncSnrTrackerForTests.StoredLineCount);
        });

        Assert.Equal(2, storedAtLock.Count);
        Assert.All(storedAtLock, c => Assert.Equal(0, c));
        Assert.All(snrAtSecondLock, v => Assert.True(double.IsNaN(v)));
        Assert.False(double.IsNaN(decoder.ReceptionSnrDb));
    }

    [Fact]
    public async Task ToggleOff_ClearsBothFigures_OnNextPush()
    {
        var mode = SstvModeRegistry.Robot36;
        var (clean, _) = await SyncSnrTestSignals.EncodeAsync(mode, Rate, 60);
        var (decoder, _, _) = Decode(clean, snrOn: true);
        Assert.False(double.IsNaN(decoder.LiveSnrDb));

        decoder.SnrMeasurementEnabled = false;
        Assert.False(decoder.SnrMeasurementEnabled);
        decoder.PushSamples(new float[16]);

        Assert.True(double.IsNaN(decoder.LiveSnrDb));
        Assert.True(double.IsNaN(decoder.ReceptionSnrDb));
    }

    [Fact]
    public void Restartable_SnrToggle_SurvivesAPeriodicSwap()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000);
        Assert.False(decoder.InnerSnrMeasurementEnabledForTests);

        decoder.SnrMeasurementEnabled = true;
        Assert.True(decoder.InnerSnrMeasurementEnabledForTests);
        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]);
        }

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.True(decoder.SnrMeasurementEnabled);
        Assert.True(decoder.InnerSnrMeasurementEnabledForTests);
        Assert.True(double.IsNaN(decoder.LiveSnrDb));
        Assert.True(double.IsNaN(decoder.ReceptionSnrDb));
    }

    [Fact]
    public async Task ReSync_ResetsPlacement_SoTheNextLinesOnlyAcquire()
    {
        var mode = SstvModeRegistry.MartinM1;
        var (clean, _) = await SyncSnrTestSignals.EncodeAsync(mode, Rate, 60);
        var decoder = new AnalogFmSstvDecoder(Rate) { SnrMeasurementEnabled = true };
        var placedByLine = new SortedDictionary<int, bool>();
        decoder.SyncSnrLineObservedForTests = (line, placed, _) => placedByLine[line] = !double.IsNaN(placed);
        var half = clean.Length / 2;
        SyncSnrTestSignals.PushChunked(decoder, clean[..half]);
        var lastLineBefore = placedByLine.Keys.Last();
        Assert.True(placedByLine[lastLineBefore]);

        decoder.RequestReSync();
        SyncSnrTestSignals.PushChunked(decoder, clean[half..]);

        var after = placedByLine.Where(kv => kv.Key > lastLineBefore).Take(SyncSnrTracker.PlacementLines).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, kv => Assert.False(kv.Value));
    }
}
