using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Tx;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using Xunit.Abstractions;

namespace ScanlineStudio.Application.Tests;

/// <summary>The default-on gate for DRM RX (plan decision 5): DRM and SSTV run on the same audio, so neither decoder may
/// take the other's transmissions for its own. Real decoders and encoders on both sides.</summary>
/// <remarks>The DRM side uses our transmitter, which is QSSTV-identical on all 79 golden cases
/// (<c>Core.Drm.Tests</c>), so its output stands in for QSSTV's <c>out48k</c> streams.</remarks>
public sealed class DrmSstvCrossDetectionTests(ITestOutputHelper output)
{
    private const int Rate = 11025;

    public static TheoryData<DrmRobustnessMode, DrmQamOrder> DrmConfigs()
    {
        var data = new TheoryData<DrmRobustnessMode, DrmQamOrder>();
        foreach (DrmRobustnessMode mode in new[] { DrmRobustnessMode.A, DrmRobustnessMode.B, DrmRobustnessMode.E })
        {
            foreach (DrmQamOrder qam in new[] { DrmQamOrder.Qam4, DrmQamOrder.Qam16, DrmQamOrder.Qam64 })
                data.Add(mode, qam);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DrmConfigs))]
    public async Task TheSstvDecoder_DetectsNoModeInDrmAudio(DrmRobustnessMode mode, DrmQamOrder qam)
    {
        byte[] payload = new byte[3000];
        new Random(7).NextBytes(payload);
        var settings = new DrmTxSettings(mode, DrmBandwidth.Hz2500, qam, DrmProtectionLevel.High, DrmInterleaving.LongInterleaver,
            DrmReedSolomonLevel.Rs2, "PA0TST", true, 400);
        IDrmTransmission tx = new DrmTransmitter(new DrmTxNaming(TimeProvider.System), new BsrTransportIdCounter())
            .Prepare(payload, "x.bin", isImage: false, settings, newTxFileName: true);
        var audio48 = new List<float>();
        await foreach (ReadOnlyMemory<float> chunk in tx.ReadSamplesAsync())
            audio48.AddRange(chunk.ToArray());
        float[] audio = TestResampler.Resample([.. audio48], 48000, Rate);

        using var decoder = new AnalogFmSstvDecoder(Rate);
        var modes = new List<string>();
        int lines = 0;
        decoder.ModeDetected += m => modes.Add(m.Id);
        decoder.LineDecoded += _ => lines++;
        for (int i = 0; i < audio.Length; i += 4096)
            decoder.PushSamples(new ReadOnlyMemory<float>(audio, i, Math.Min(4096, audio.Length - i)));

        output.WriteLine($"DRM {mode} {qam}, {audio.Length / (double)Rate:F1} s: SSTV modes detected [{string.Join(", ", modes)}], {lines} lines");
        Assert.Empty(modes);
    }

    /// <summary>Positive control: the harness above does detect a real SSTV transmission.</summary>
    [Fact]
    public async Task TheSstvDecoder_DetectsMartinM1_InTheSameHarness()
    {
        SstvModeDefinition mode = SstvModeRegistry.MartinM1;
        var image = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, Gradient(mode.ImageWidth, mode.ImageHeight));
        var audio = new List<float>();
        await foreach (float sample in new AnalogFmSstvEncoder(Rate).EncodeAsync(mode, image))
            audio.Add(sample);
        float[] samples = [.. audio];

        using var decoder = new AnalogFmSstvDecoder(Rate);
        var modes = new List<string>();
        decoder.ModeDetected += m => modes.Add(m.Id);
        for (int i = 0; i < samples.Length; i += 4096)
            decoder.PushSamples(new ReadOnlyMemory<float>(samples, i, Math.Min(4096, samples.Length - i)));

        Assert.Contains(mode.Id, modes);
    }

    [Fact]
    public async Task TheDrmReceiver_ReceivesNothingFromAnySstvMode()
    {
        var results = new List<string>();
        foreach (SstvModeDefinition mode in SstvModeRegistry.All)
        {
            var encoder = new AnalogFmSstvEncoder(Rate);
            var image = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, Gradient(mode.ImageWidth, mode.ImageHeight));
            var receiver = new DrmReceiver(Rate);
            int files = 0, bsr = 0, frameSyncs = 0;
            receiver.FileReceived += _ => files++;
            receiver.BsrReceived += _ => bsr++;
            receiver.SignalStateChanged += s =>
            {
                if (s.FrameSync)
                    frameSyncs++;
            };

            var block = new float[4096];
            int n = 0;
            await foreach (float sample in encoder.EncodeAsync(mode, image))
            {
                block[n++] = sample;
                if (n == block.Length)
                {
                    receiver.Push(block);
                    n = 0;
                }
            }

            receiver.Push(block.AsSpan(0, n));
            receiver.ZeroFill(Rate);
            results.Add($"{mode.Id}: files {files}, bsr {bsr}, frame-sync states {frameSyncs}");
            Assert.True(files == 0 && bsr == 0, $"{mode.Id}: DRM received {files} file(s), {bsr} bsr");
        }

        foreach (string r in results)
            output.WriteLine(r);
    }

    private static Rgb24[] Gradient(int w, int h)
    {
        var pixels = new Rgb24[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                pixels[(y * w) + x] = new Rgb24((byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), (byte)((x ^ y) & 0xFF));
        }

        return pixels;
    }
}
