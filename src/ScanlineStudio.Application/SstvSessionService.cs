using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

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
                // comment). A future step wires real diagnostic logging once ScanlineStudio.Application has
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

    public bool IsReceiving => _isReceiving;

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

    public Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
        => PlayWithPttAsync(_encoder.EncodeAsync(mode, image, ct), _encoder.SampleRate, ct);

    public Task TuneAsync(double frequencyHz, TimeSpan duration, CancellationToken ct = default)
    {
        const int sampleRate = 48_000;
        return PlayWithPttAsync(GenerateTone(frequencyHz, duration, sampleRate, ct), sampleRate, ct);
    }

    public async Task<int> GetTxVolumePercentAsync(CancellationToken ct = default)
    {
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
        return settings.TxVolumePercent ?? 100;
    }

    public async Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var current = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
        var updated = appSettings.WithSection(
            AudioDeviceSettings.SectionKey,
            current with { TxVolumePercent = percent },
            AudioSettingsJsonContext.Default.AudioDeviceSettings);
        await _settingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
    }

    /// <summary>Shared PTT-guarantee shape for both <see cref="TransmitAsync"/> and <see cref="TuneAsync"/>:
    /// pauses capture (resumed afterward only if RX was already running), keys PTT, plays
    /// <paramref name="samples"/>, then un-keys PTT in a <c>finally</c> no matter how playback ends.</summary>
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private async Task PlayWithPttAsync(IAsyncEnumerable<float> samples, int sampleRate, CancellationToken ct)
    {
        var wasReceiving = _isReceiving;
        if (wasReceiving)
        {
            await StopReceivingAsync().ConfigureAwait(false);
        }

        var device = await ResolveDeviceAsync(forCapture: false, ct).ConfigureAwait(false);
        var gain = (await GetTxVolumePercentAsync(ct).ConfigureAwait(false)) / 100f;

        await _radioSession.SetPttAsync(true, ct).ConfigureAwait(false);
        try
        {
            await _audioEngine.StartPlaybackAsync(device, sampleRate, ct).ConfigureAwait(false);
            await PumpToPlaybackAsync(samples, gain, ct).ConfigureAwait(false);
        }
        finally
        {
            // Cleanup must never be defeated by the very cancellation (Stop TX / SWR auto-cutoff,
            // spec/14-roadmap.md's Piece 6) that triggered it -- reusing the possibly-cancelled `ct`
            // here (the original shape) left the rig keyed indefinitely and RX capture permanently
            // stopped, since SetPttAsync/StartReceivingAsync both wait on an already-cancelled token
            // before ever sending anything. A fresh, non-linked, bounded-timeout token instead:
            // uncancellable-in-practice for Hamlib's native calls, but still gives up eventually if a
            // wedged rigctld TCP read would otherwise hang this cleanup forever. StopPlaybackAsync
            // moved in here too (previously inside the try, so it was skipped entirely on
            // cancellation, leaving the output stream open with buffered audio still draining). Each
            // step is independently guarded so one failure can never mask the original exception or
            // prevent a sibling cleanup step from running.
            using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
            await TryCleanupAsync(() => _audioEngine.StopPlaybackAsync()).ConfigureAwait(false);
            await TryCleanupAsync(() => _radioSession.SetPttAsync(false, cleanupCts.Token)).ConfigureAwait(false);
            if (wasReceiving)
            {
                await TryCleanupAsync(() => StartReceivingAsync(cleanupCts.Token)).ConfigureAwait(false);
            }
        }
    }

    private static async Task TryCleanupAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup step -- see PlayWithPttAsync's own doc comment for why a failure
            // here must never mask the original exception or block a sibling cleanup step.
        }
    }

    private static async IAsyncEnumerable<float> GenerateTone(
        double frequencyHz, TimeSpan duration, int sampleRate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var totalSamples = (long)(duration.TotalSeconds * sampleRate);
        var angularStep = 2.0 * Math.PI * frequencyHz / sampleRate;
        for (var i = 0L; i < totalSamples; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return (float)Math.Sin(angularStep * i);
            if (i % 4096 == 0)
            {
                await Task.Yield();
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

    private async Task PumpToPlaybackAsync(IAsyncEnumerable<float> samples, float gain, CancellationToken ct)
    {
        const int chunkSize = 4096;
        var buffer = new float[chunkSize];
        var count = 0;

        await foreach (var sample in samples.WithCancellation(ct).ConfigureAwait(false))
        {
            buffer[count++] = sample * gain;
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
