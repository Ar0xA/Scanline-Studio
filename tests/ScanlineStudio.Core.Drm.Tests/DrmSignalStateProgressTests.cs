using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Tests.Impairment;
using ScanlineStudio.Core.Drm.Tx;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary><see cref="DrmSignalState.ExpectedSegments"/> is a real progress denominator and
/// <see cref="DrmSignalState.CurrentFileComplete"/> tells a stored file's run-out or repeat from a reception.</summary>
public sealed class DrmSignalStateProgressTests
{
    public static TheoryData<string> Configs() => new() { "robust", "normal", "fast", "a64low-so1-long-rs0" };

    private static List<DrmSignalState> Run(DrmReceiver receiver, float[] audio)
    {
        var states = new List<DrmSignalState>();
        receiver.SignalStateChanged += states.Add;
        for (int i = 0; i < audio.Length; i += 4096)
            receiver.Push(audio.AsSpan(i, Math.Min(4096, audio.Length - i)));
        receiver.ZeroFill(2 * 48000);
        return states;
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void ExpectedSegments_IsTheBodySegmentCount_FromTheFirstFullSegment(string name)
    {
        DrmSweepConfig config = RxImpairmentHarness.Configs().Single(c => c.Name == name);
        byte[] payload = RxImpairmentHarness.Payload(config);
        float[] tx = RxImpairmentHarness.Transmit(config, payload);
        int bodySegments = RxImpairmentHarness.Truth(config).Segments;

        var receiver = new DrmReceiver(48000);
        int files = 0;
        receiver.FileReceived += _ => files++;
        List<DrmSignalState> states = Run(receiver, tx);

        Assert.Equal(1, files);
        DrmSignalState[] known = [.. states.Where(s => s.ExpectedSegments > 0)];
        Assert.NotEmpty(known);
        Assert.All(known, s => Assert.Equal(bodySegments, s.ExpectedSegments));
        Assert.Contains(states, s => s.ExpectedSegments == bodySegments && s.SegmentsReceived < bodySegments);

        // Complete flips once, false → true, and stays.
        bool[] complete = [.. states.Select(s => s.CurrentFileComplete)];
        int flip = Array.IndexOf(complete, true);
        Assert.True(flip > 0);
        Assert.All(complete[flip..], Assert.True);
        Assert.All(complete[..flip], Assert.False);
    }

    [Fact]
    public void ARepeatOfAStoredFile_ReadsCompleteFromItsHeader()
    {
        DrmSweepConfig config = RxImpairmentHarness.Configs().Single(c => c.Name == "fast");
        byte[] payload = RxImpairmentHarness.Payload(config);
        float[] tx = RxImpairmentHarness.Transmit(config, payload);
        var receiver = new DrmReceiver(48000);
        int files = 0;
        receiver.FileReceived += _ => files++;
        Run(receiver, tx);
        Assert.Equal(1, files);

        List<DrmSignalState> repeat = Run(receiver, tx);

        Assert.Equal(1, files);
        Assert.Contains(repeat, s => s.FrameSync);
        Assert.DoesNotContain(repeat, s => s.FrameSync && s.SegmentsReceived > 0 && !s.CurrentFileComplete);
    }

    [Fact]
    public void AfterAnotherFileStarted_ARepeatSwitchesBackToTheStoredFile()
    {
        DrmSweepConfig config = RxImpairmentHarness.Configs().Single(c => c.Name == "fast");
        byte[] a = RxImpairmentHarness.Payload(config);
        byte[] b = [.. a.Reverse()];
        float[] txA = Transmit(config, a, "a.bin");
        float[] txB = Transmit(config, b, "b.bin");
        var receiver = new DrmReceiver(48000);
        Run(receiver, txA);

        // Half of B: B is current and incomplete.
        List<DrmSignalState> partB = Run(receiver, txB[..(txB.Length / 2)]);
        Assert.Contains(partB, s => s.FrameSync && s.SegmentsReceived > 0 && !s.CurrentFileComplete);

        List<DrmSignalState> repeatA = Run(receiver, txA);
        DrmSignalState[] synced = [.. repeatA.Where(s => s.FrameSync && s.ReceivingName.Length > 0)];
        Assert.NotEmpty(synced);
        Assert.True(synced[^1].CurrentFileComplete);
    }

    private static float[] Transmit(DrmSweepConfig config, byte[] payload, string name)
    {
        var tx = new DrmTransmitter(new DrmTxNaming(new FixedTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero))), new BsrTransportIdCounter());
        IDrmTransmission t = tx.Prepare(payload, name, isImage: false, config.Settings, newTxFileName: true);
        var audio = new List<float>();
        foreach (ReadOnlyMemory<float> chunk in t.ReadSamplesAsync().ToBlockingEnumerable())
            audio.AddRange(chunk.Span);
        return [.. audio];
    }
}
