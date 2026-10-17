using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Tx;
using Xunit.Abstractions;
using PublicSettings = ScanlineStudio.Abstractions.Drm.DrmTxSettings;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P7a part 2: the public <see cref="DrmReceiver"/> facade against the internal chain the goldens verify,
/// on our own TX output, across injected audio gaps, and its signal-state events.</summary>
public sealed class DrmReceiverTests(ITestOutputHelper output)
{
    private const int Block = 4096;
    private const int TailZeroBlocks = 8;
    private static readonly DateTimeOffset Stamp = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string id in GoldenFixtures.CaseIds())
            data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Facade_GivesTheSameFilesAsTheInternalChain(string caseId)
    {
        short[] pcm = RxAcquisitionGoldenTests.LoadInput(caseId, out _);
        RxGoldenRun.Result golden = RxGoldenRun.Get(caseId);
        var receiver = new DrmReceiver(48000);
        var files = new List<DrmReceivedFile>();
        var bsr = new List<byte[]>();
        receiver.FileReceived += files.Add;
        receiver.BsrReceived += b => bsr.Add(b.ToArray());

        float[] input = Padded(pcm);
        for (int i = 0; i < input.Length; i += Block)
            receiver.Push(input.AsSpan(i, Block));

        Assert.Equal(golden.Fixed.Files.Count, files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            Assert.Equal(golden.Fixed.Files[i].Data.ToArray(), files[i].Data.ToArray());
            Assert.Equal(golden.Fixed.Files[i] with { Data = files[i].Data }, files[i]);
        }

        Assert.Equal(golden.Fixed.Bsr, bsr);
    }

    [Theory]
    [InlineData(DrmRobustnessMode.B, DrmQamOrder.Qam16, DrmReedSolomonLevel.None)]
    [InlineData(DrmRobustnessMode.A, DrmQamOrder.Qam64, DrmReedSolomonLevel.Rs2)]
    [InlineData(DrmRobustnessMode.E, DrmQamOrder.Qam4, DrmReedSolomonLevel.Rs4)]
    public async Task Facade_ReceivesOurTransmitterOutput(DrmRobustnessMode mode, DrmQamOrder qam, DrmReedSolomonLevel rs)
    {
        byte[] payload = File.ReadAllBytes(Path.Combine(GoldenFixtures.GoldenDir, "inputs", "test.txt"));
        var settings = new PublicSettings(mode, DrmBandwidth.Hz2500, qam, DrmProtectionLevel.High, DrmInterleaving.LongInterleaver, rs, "PA0TST", true, 400);
        var tx = new DrmTransmitter(new DrmTxNaming(new FixedTimeProvider(Stamp)), new BsrTransportIdCounter());
        IDrmTransmission t = tx.Prepare(payload, "test.txt", isImage: false, settings, newTxFileName: true);
        var receiver = new DrmReceiver(48000);
        var files = new List<DrmReceivedFile>();
        receiver.FileReceived += files.Add;
        await foreach (ReadOnlyMemory<float> chunk in t.ReadSamplesAsync())
            receiver.Push(chunk.Span);
        receiver.ZeroFill(TailZeroBlocks * Block);

        DrmReceivedFile f = Assert.Single(files);
        Assert.Equal(payload, f.Data.ToArray());
        Assert.Equal("PA0TST", f.Callsign);
    }

    /// <summary>A gap of dropped audio mid-body, handled three ways: zeros of the dropped length (the worker's policy for
    /// gaps up to 1 s), a plain timeline jump, and a receiver reset (the policy above 1 s).</summary>
    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void GapInjection_ZeroFillIsNoWorseThanAJumpOrAReset(double gapSeconds)
    {
        const string caseId = "B1_q4_high_long_rs1_jp2";
        float[] input = Padded(RxAcquisitionGoldenTests.LoadInput(caseId, out _));
        RxGoldenRun.Result golden = RxGoldenRun.Get(caseId);
        byte[] want = Assert.Single(golden.Fixed.Files).Data.ToArray();
        // Midway between frame sync and the file's completion in a clean run, so the gap lands inside the body.
        GapResult clean = RunWithGap(input, -1, 0, "none");
        int gapStart = (int)((clean.FirstSyncSamples + clean.FileAfterGapSamples) / 2 / Block * Block);
        int gap = (int)(gapSeconds * 48000);

        var results = new Dictionary<string, GapResult>();
        foreach (string policy in new[] { "zero-fill", "jump", "reset" })
            results[policy] = RunWithGap(input, gapStart, gap, policy);

        output.WriteLine($"clean run: frame sync at {clean.FirstSyncSamples / 48000.0:F1} s, file at {clean.FileAfterGapSamples / 48000.0:F1} s; gap at {gapStart / 48000.0:F1} s");
        foreach ((string policy, GapResult r) in results)
        {
            string outage = r.OutageSamples == long.MaxValue ? "never regained" : $"{r.OutageSamples / 48000.0:F2} s";
            output.WriteLine($"gap {gapSeconds} s, {policy}: file {(r.File is null ? "lost" : r.File.SequenceEqual(want) ? "exact" : "WRONG")} " +
                $"{r.FileAfterGapSamples / 48000.0:F1} s after the gap, sync outage {outage}, finite {r.Finite}");
        }

        Assert.All(results.Values, r => Assert.True(r.Finite));
        Assert.All(results.Values.Where(r => r.File is not null), r => Assert.Equal(want, r.File));
        Assert.All(results.Values.Where(r => r.File is not null), r => Assert.True(r.FileAfterGapSamples > 0, "the gap fell before the file completed"));
        GapResult zero = results["zero-fill"];
        Assert.NotEqual(long.MaxValue, zero.OutageSamples);
        Assert.NotNull(zero.File);
        Assert.All(results.Values, r => Assert.True(zero.OutageSamples <= r.OutageSamples, "zero-fill has the shortest sync outage"));
        Assert.All(results.Values.Where(r => r.File is not null), r => Assert.True(zero.FileAfterGapSamples <= r.FileAfterGapSamples, "zero-fill completes the file first"));
    }

    [Fact]
    public void SignalState_FiresOnSyncGainAndDrop()
    {
        float[] input = Padded(RxAcquisitionGoldenTests.LoadInput("B1_q16_high_long", out _));
        var receiver = new DrmReceiver(48000);
        var states = new List<DrmSignalState>();
        receiver.SignalStateChanged += states.Add;
        for (int i = 0; i < input.Length; i += Block)
            receiver.Push(input.AsSpan(i, Block));
        int synced = states.FindIndex(s => s.FrameSync);
        Assert.True(synced >= 0, "frame sync reported");
        Assert.Equal(0, states[synced].Generation);
        Assert.True(states[synced].IsActive);
        Assert.Equal(1, states[synced].RobustnessMode);

        // Silence after the transmission: the receiver loses sync and says so immediately.
        int before = states.Count;
        receiver.ZeroFill(48000 * 10);
        Assert.Contains(states.Skip(before), s => !s.FrameSync);

        // Between sync changes the rate is bounded: at most one state per 400 ms of audio.
        receiver.ZeroFill(48000 * 4);
        int idle = states.Count;
        receiver.ZeroFill(48000 * 4);
        Assert.True(states.Count - idle <= 11, $"{states.Count - idle} states in 4 s of silence");
    }

    private static GapResult RunWithGap(float[] input, int gapStart, int gap, string policy)
    {
        var receiver = new DrmReceiver(48000);
        byte[]? file = null;
        long consumed = 0, gapEnd = gapStart < 0 ? 0 : -1, fileAt = -1, lostAt = -1, regainedAt = -1, firstSync = -1;
        receiver.FileReceived += f =>
        {
            file = f.Data.ToArray();
            fileAt = consumed;
        };
        receiver.SignalStateChanged += s =>
        {
            if (s.FrameSync && firstSync < 0)
                firstSync = consumed;
            if (gapEnd < 0 || gapStart < 0)
                return;
            if (!s.FrameSync && lostAt < 0)
                lostAt = consumed;
            else if (s.FrameSync && lostAt >= 0 && regainedAt < 0)
                regainedAt = consumed;
        };

        int pos = 0;
        while (pos < input.Length)
        {
            if (pos == gapStart)
            {
                switch (policy)
                {
                    case "zero-fill":
                        receiver.ZeroFill(gap);
                        break;
                    case "reset":
                        receiver.Reset();
                        break;
                }

                pos += gap;
                gapEnd = consumed;
                if (!receiver.State.FrameSyncFlag)
                    lostAt = consumed;
                continue;
            }

            int n = Math.Min(Block, input.Length - pos);
            if (pos < gapStart && pos + n > gapStart)
                n = gapStart - pos;
            receiver.Push(input.AsSpan(pos, n));
            pos += n;
            consumed += n;
        }

        receiver.ZeroFill(TailZeroBlocks * Block);
        DrmDemodState st = receiver.State;
        bool finite = float.IsFinite(st.TimeOffset) && float.IsFinite(st.SamplerateOffsetEstimation) && float.IsFinite(st.FreqOffsetInit)
            && st.Zi.All(float.IsFinite) && st.RsBuffer.All(float.IsFinite);
        long outage = lostAt < 0 ? 0 : (regainedAt < 0 ? long.MaxValue : regainedAt - gapEnd);
        return new GapResult(file, fileAt - gapEnd, finite, outage, firstSync);
    }

    private static float[] Padded(short[] pcm)
    {
        int blocks = ((pcm.Length + Block - 1) / Block) + TailZeroBlocks;
        var input = new float[blocks * Block];
        for (int i = 0; i < pcm.Length; i++)
            input[i] = pcm[i] / 32768f;
        return input;
    }

    /// <param name="FileAfterGapSamples">Samples after the gap at which the file completed; negative if before it.</param>
    /// <param name="OutageSamples">Samples after the gap without frame sync: 0 if sync held, long.MaxValue if never regained.</param>
    private sealed record GapResult(byte[]? File, long FileAfterGapSamples, bool Finite, long OutageSamples, long FirstSyncSamples);
}
