using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeWaterfallSource : IWaterfallSource, IWaterfallSourceReconfiguration
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

    // Restart-required-settings backlog item 4 (2026-08-27). Publicly settable (not just via
    // RequestSampleRate) so a test can seed a starting value distinct from SstvSampleRate.Default --
    // code-review round-1 finding: a test left at the constructor default coincided with the exact
    // rate it was requesting, making its own "not yet updated at commit time" assertion vacuous.
    public int SampleRate { get; set; } = SstvSampleRate.Default;

    public int RequestSampleRateCallCount { get; private set; }

    public void RequestSampleRate(int sampleRate)
    {
        RequestSampleRateCallCount++;
        SampleRate = sampleRate;
    }
}
