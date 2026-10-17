using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Tx;

namespace ScanlineStudio.Application.Tests;

/// <summary>P7a-2 step 9: the DRM pass of <see cref="SstvSessionService.DecodeFromFileAsync"/>, on our real DRM
/// transmitter's output, at the decoder's rate and (decision 6) at another rate as a DRM-only pass.</summary>
public sealed class SstvSessionServiceDrmFileDecodeTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private readonly string _dir = Directory.CreateTempSubdirectory("drm-file-decode-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<(byte[] Payload, float[] Audio48k)> TransmitAsync()
    {
        byte[] payload = "DRM file decode test payload, PA0TST de Scanline Studio."u8.ToArray();
        var settings = new DrmTxSettings(DrmRobustnessMode.B, DrmBandwidth.Hz2500, DrmQamOrder.Qam16, DrmProtectionLevel.High,
            DrmInterleaving.LongInterleaver, DrmReedSolomonLevel.None, "PA0TST", true, 400);
        IDrmTransmission tx = new DrmTransmitter(new DrmTxNaming(TimeProvider.System), new BsrTransportIdCounter())
            .Prepare(payload, "test.txt", isImage: false, settings, newTxFileName: true);
        var audio = new List<float>();
        await foreach (ReadOnlyMemory<float> chunk in tx.ReadSamplesAsync())
            audio.AddRange(chunk.ToArray());
        return (payload, audio.ToArray());
    }

    private string Wav(float[] samples, int rate)
    {
        string path = Path.Combine(_dir, $"drm-{rate}.wav");
        WavFile.Write(path, samples, rate);
        return path;
    }

    [Fact]
    public async Task AtTheDecoderRate_11025_TheFileIsReceived_WhileSstvAlsoRuns()
    {
        (byte[] payload, float[] audio) = await TransmitAsync();
        string path = Wav(TestResampler.Resample(audio, 48000, 11025), 11025);
        var h = SstvSessionServiceReceiveDrmTests.Create(rate: 11025, realFactory: new DrmReceiverFactory());

        await h.Service.DecodeFromFileAsync(path).WaitAsync(Wait);

        DrmReceivedFile file = Assert.Single(h.Files);
        Assert.Equal(payload, file.Data.ToArray());
        Assert.Equal("PA0TST", file.Callsign);
        Assert.NotEmpty(h.Decoder.PushedSamples);
        Assert.False(h.States[^1].IsActive);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task AtAnotherRate_ADrmOnlyPassRuns_AndNoSstvHandlerSeesTheAudio()
    {
        (byte[] payload, float[] audio) = await TransmitAsync();
        string path = Wav(TestResampler.Resample(audio, 48000, 11025), 11025);
        var h = SstvSessionServiceReceiveDrmTests.Create(rate: 8000, realFactory: new DrmReceiverFactory());

        await h.Service.DecodeFromFileAsync(path).WaitAsync(Wait);

        Assert.Equal(payload, Assert.Single(h.Files).Data.ToArray());
        Assert.Empty(h.Decoder.PushedSamples);
        Assert.Empty(h.Waterfall.PushedSamples);
        Assert.Equal(0f, h.Service.RawInputPeakLevel);
        Assert.Contains(h.Logger.Entries, e => e.Message.Contains("DRM-only pass", StringComparison.Ordinal));
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task AtAnotherRate_WithDrmOff_StillThrows()
    {
        (_, float[] audio) = await TransmitAsync();
        string path = Wav(TestResampler.Resample(audio, 48000, 11025), 11025);
        var h = SstvSessionServiceReceiveDrmTests.Create(drmEnabled: false, rate: 8000, realFactory: new DrmReceiverFactory());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.DecodeFromFileAsync(path));
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task WhileLiveRxRuns_TheLiveWorkerStops_AFileReceiverIsUsed_ThenAFreshLiveWorkerStarts()
    {
        var h = SstvSessionServiceReceiveDrmTests.Create();
        await h.Service.StartReceivingAsync();
        string path = Wav(new float[8000], 8000);

        await h.Service.DecodeFromFileAsync(path).WaitAsync(Wait);

        FakeDrmReceiver[] created = [.. h.Factory.Created];
        Assert.Equal(3, created.Length);
        Assert.Equal(0, created[0].PushedSamples);
        Assert.Equal(8000, created[1].PushedSamples);
        Assert.Equal("reset", created[1].Calls.First());
        Assert.Equal("reset", created[1].Calls.Last());
        Assert.Contains(created[1].Calls, c => c.StartsWith("zero:", StringComparison.Ordinal));
        Assert.True(h.Service.IsDrmReceiveActive);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task AFileReceiverThrow_IsLogged_AndTheSstvDecodeCompletes()
    {
        var h = SstvSessionServiceReceiveDrmTests.Create();
        h.Factory.Configure = r => r.ThrowOnNextPush = new InvalidOperationException("rs_buffer overflow");
        string path = Wav(new float[20000], 8000);

        await h.Service.DecodeFromFileAsync(path).WaitAsync(Wait);

        Assert.Equal(20000, h.Decoder.PushedSamples.Sum(m => m.Length));
        Assert.Contains(h.Logger.Entries, e => e.Message.Contains("DRM RX file receiver threw", StringComparison.Ordinal));
        await h.Service.DisposeAsync();
    }
}
