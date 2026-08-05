using System.Reactive.Subjects;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.Tests;

internal sealed class FakeLocalizationService : ILocalizationService
{
    public IReadOnlyList<System.Globalization.CultureInfo> AvailableCultures { get; } = [System.Globalization.CultureInfo.GetCultureInfo("en")];

    public System.Globalization.CultureInfo CurrentCulture { get; } = System.Globalization.CultureInfo.GetCultureInfo("en");

    public event Action? CultureChanged;

    public Task SetCultureAsync(System.Globalization.CultureInfo culture, CancellationToken ct = default)
    {
        CultureChanged?.Invoke();
        return Task.CompletedTask;
    }

    public string GetString(string key, params object[] args) => key;
}

internal sealed class FakeWaterfallSource : IWaterfallSource, IDisposable
{
    private readonly Subject<WaterfallFrame> _frames = new();

    public IObservable<WaterfallFrame> Frames => _frames;

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
    }

    public void Emit(WaterfallFrame frame) => _frames.OnNext(frame);

    public void Dispose() => _frames.Dispose();
}

internal sealed class FakeReceivedImageBuffer : IReceivedImageBuffer
{
    public IImageSource Current { get; set; } = new ArrayImageSourceStub();

    public event Action? Updated;

    public Task SaveAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public void RaiseUpdated() => Updated?.Invoke();

    private sealed class ArrayImageSourceStub : IImageSource
    {
        public int Width => 1;

        public int Height => 1;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => new Rgb24[1];
    }
}

internal sealed class FakeSstvSessionService : ISstvSessionService
{
    public List<(SstvModeDefinition Mode, IImageSource Image)> TransmitCalls { get; } = [];

    public IWaterfallSource Waterfall { get; } = new FakeWaterfallSource();

    public IReceivedImageBuffer ReceivedImage { get; } = new FakeReceivedImageBuffer();

    public IReadOnlyList<SstvModeDefinition> AvailableModes { get; set; } = [];

    public event Action<SstvModeDefinition>? ModeDetected;

    public Task StartReceivingAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task StopReceivingAsync() => Task.CompletedTask;

    public Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        TransmitCalls.Add((mode, image));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);
}

internal sealed class FakeRadioSessionService : IRadioSessionService, IDisposable
{
    private readonly Subject<RadioState> _stateChanges = new();

    public RadioState? LastKnownState { get; set; }

    public IObservable<RadioState> StateChanges => _stateChanges;

    public IObservable<RadioConnectionEvent> ConnectionEvents { get; } = new Subject<RadioConnectionEvent>();

    public Task ConnectUsingSettingsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DisconnectAsync() => Task.CompletedTask;

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetModeAsync(RadioMode mode, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetPttAsync(bool tx, CancellationToken ct = default) => Task.CompletedTask;

    public void Push(RadioState state) => _stateChanges.OnNext(state);

    public void Dispose() => _stateChanges.Dispose();
}

internal sealed class FakeImageFileLoader : IImageFileLoader
{
    public IImageSource? ResultToReturn { get; set; }

    public Task<IImageSource> LoadAsync(string path, int targetWidth, int targetHeight, CancellationToken ct = default)
        => Task.FromResult(ResultToReturn ?? throw new InvalidOperationException("No result configured."));
}

internal sealed class FakeFilePickerService : IFilePickerService
{
    public string? PathToReturn { get; set; } = "/tmp/fake.png";

    public Task<string?> PickImageFileAsync() => Task.FromResult(PathToReturn);
}
