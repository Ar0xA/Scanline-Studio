using Yoniq.Abstractions.Audio;
using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Audio;
using Yoniq.Core.Sstv;
using Yoniq.Settings;

namespace Yoniq.Application;

public sealed class SstvSessionService : ISstvSessionService
{
    private readonly IAudioEngine _audioEngine;
    private readonly IAudioDeviceEnumerator _deviceEnumerator;
    private readonly ISettingsStore _settingsStore;
    private readonly ISstvDecoder _decoder;
    private readonly ISstvEncoder _encoder;
    private readonly IRadioSessionService _radioSession;
    private readonly Action<ReadOnlyMemory<float>> _decoderHandler;
    private readonly Action<ReadOnlyMemory<float>> _waterfallHandler;
    private bool _isReceiving;

    public SstvSessionService(
        IAudioEngine audioEngine,
        IAudioDeviceEnumerator deviceEnumerator,
        ISettingsStore settingsStore,
        ISstvDecoder decoder,
        ISstvEncoder encoder,
        IWaterfallSource waterfall,
        IReceivedImageBuffer receivedImage,
        IRadioSessionService radioSession)
    {
        _audioEngine = audioEngine;
        _deviceEnumerator = deviceEnumerator;
        _settingsStore = settingsStore;
        _decoder = decoder;
        _encoder = encoder;
        Waterfall = waterfall;
        ReceivedImage = receivedImage;
        _radioSession = radioSession;

        // Isolated fan-out (Phase-3 plan decision #3): a throwing/slow handler on one target must
        // never prevent the other from running -- this is what actually fixes the bug the pre-build
        // spike's original IWaterfallSource design would otherwise have reintroduced.
        _decoderHandler = samples =>
        {
            try
            {
                _decoder.PushSamples(samples);
            }
            catch
            {
                // Deliberately swallowed here, not rethrown into the audio engine's own forwarder
                // (which has no exception isolation of its own -- see IWaterfallSource's doc
                // comment). A future step wires real diagnostic logging once Yoniq.Application has
                // an ILogger dependency; today, silently continuing beats corrupting the other
                // fan-out target or crashing the drain thread.
            }
        };
        _waterfallHandler = samples =>
        {
            try
            {
                Waterfall.PushSamples(samples);
            }
            catch
            {
            }
        };
    }

    public IWaterfallSource Waterfall { get; }

    public IReceivedImageBuffer ReceivedImage { get; }

    public IReadOnlyList<SstvModeDefinition> AvailableModes => SstvModeRegistry.All;

    public event Action<SstvModeDefinition>? ModeDetected
    {
        add => _decoder.ModeDetected += value;
        remove => _decoder.ModeDetected -= value;
    }

    public async Task StartReceivingAsync(CancellationToken ct = default)
    {
        if (_isReceiving)
        {
            return;
        }

        var device = await ResolveDeviceAsync(forCapture: true, ct).ConfigureAwait(false);
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
        await _audioEngine.StartCaptureAsync(device, settings.SampleRate, ct).ConfigureAwait(false);

        _audioEngine.SamplesCaptured += _decoderHandler;
        _audioEngine.SamplesCaptured += _waterfallHandler;
        _isReceiving = true;
    }

    public async Task StopReceivingAsync()
    {
        if (!_isReceiving)
        {
            return;
        }

        _audioEngine.SamplesCaptured -= _decoderHandler;
        _audioEngine.SamplesCaptured -= _waterfallHandler;
        await _audioEngine.StopCaptureAsync().ConfigureAwait(false);
        _isReceiving = false;
    }

    public async Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        var wasReceiving = _isReceiving;
        if (wasReceiving)
        {
            await StopReceivingAsync().ConfigureAwait(false);
        }

        var device = await ResolveDeviceAsync(forCapture: false, ct).ConfigureAwait(false);

        await _radioSession.SetPttAsync(true, ct).ConfigureAwait(false);
        try
        {
            await _audioEngine.StartPlaybackAsync(device, _encoder.SampleRate, ct).ConfigureAwait(false);
            await PumpToPlaybackAsync(_encoder.EncodeAsync(mode, image, ct), ct).ConfigureAwait(false);
            await _audioEngine.StopPlaybackAsync().ConfigureAwait(false);
        }
        finally
        {
            await _radioSession.SetPttAsync(false, ct).ConfigureAwait(false);
            if (wasReceiving)
            {
                await StartReceivingAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopReceivingAsync().ConfigureAwait(false);
        if (Waterfall is IDisposable disposableWaterfall)
        {
            disposableWaterfall.Dispose();
        }
    }

    private async Task PumpToPlaybackAsync(IAsyncEnumerable<float> samples, CancellationToken ct)
    {
        const int chunkSize = 4096;
        var buffer = new float[chunkSize];
        var count = 0;

        await foreach (var sample in samples.WithCancellation(ct).ConfigureAwait(false))
        {
            buffer[count++] = sample;
            if (count == chunkSize)
            {
                await EnqueueAllAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                count = 0;
            }
        }

        if (count > 0)
        {
            await EnqueueAllAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
    }

    private async Task EnqueueAllAsync(ReadOnlyMemory<float> chunk, CancellationToken ct)
    {
        var offset = 0;
        while (offset < chunk.Length)
        {
            var accepted = _audioEngine.EnqueuePlaybackSamples(chunk[offset..]);
            if (accepted == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
                continue;
            }

            offset += accepted;
        }
    }

    private async Task<AudioDeviceInfo> ResolveDeviceAsync(bool forCapture, CancellationToken ct)
    {
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
        var deviceId = forCapture ? settings.CaptureDeviceId : settings.PlaybackDeviceId;
        if (deviceId is null)
        {
            var kind = forCapture ? "capture" : "playback";
            throw new InvalidOperationException($"No {kind} audio device configured -- set one in settings before starting a session.");
        }

        await _deviceEnumerator.RefreshAsync(ct).ConfigureAwait(false);
        var devices = forCapture ? _deviceEnumerator.InputDevices : _deviceEnumerator.OutputDevices;
        var device = devices.FirstOrDefault(d => d.Id == deviceId);
        if (device is null)
        {
            var kind = forCapture ? "capture" : "playback";
            throw new InvalidOperationException($"Configured {kind} device '{deviceId}' was not found among currently available devices.");
        }

        return device;
    }

    private async Task<AudioDeviceSettings> LoadAudioSettingsAsync(CancellationToken ct)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
    }
}
