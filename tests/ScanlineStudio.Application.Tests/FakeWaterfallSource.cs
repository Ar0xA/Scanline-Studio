using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeWaterfallSource : IWaterfallSource
{
    public List<ReadOnlyMemory<float>> PushedSamples { get; } = [];

    public Exception? ThrowOnPush { get; set; }

    public IObservable<WaterfallFrame> Frames { get; } = System.Reactive.Linq.Observable.Never<WaterfallFrame>();

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
        PushedSamples.Add(samples);
        if (ThrowOnPush is not null)
        {
            throw ThrowOnPush;
        }
    }
}
