using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Un-stub-RX-tab Piece B: RX Decoder Trace capture -- port of legacy's <c>TTScope</c>/<c>CScope</c>
/// (`sstv.h:188-206`, `sstv.cpp:154-210`). <see cref="ScopeCaptureBufferTests"/> covers the buffer's
/// own pure fill/publish mechanics; these exercise the DECODER's own wiring (which hook feeds which
/// channel, when, and in what units).
/// </summary>
public class ScopeCaptureTests
{
    [Fact]
    public void ArmScopeCapture_BeforeAnyLockEverHappens_Channel0FillsButChannel1NeverDoes()
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        decoder.ArmScopeCapture(16);
        decoder.PushSamples(new float[1]); // drains the arm request

        // Channel 0 writes on every sample regardless of lock state (legacy's own unconditional
        // write, sstv.cpp:1871-1882) -- via the forced catch-up (ForceScopeCaptureChannel0Progress),
        // driven by _levelAgcProcessedUpTo, which itself only advances once the pre-lock header-scan
        // machinery (TrySyncIntervalDetectionStep et al.) actually runs -- empirically confirmed that
        // takes on the order of ~20,000 samples' worth of pushed idle audio before its first pass, not
        // a handful of small chunks.
        for (var i = 0; i < 10 && decoder.TryGetScopeCaptureChannel0() is null; i++)
        {
            decoder.PushSamples(new float[25_000]);
        }

        var channel0 = decoder.TryGetScopeCaptureChannel0();
        Assert.NotNull(channel0);
        Assert.Equal(16, channel0!.Length);
        // Legacy's own channel-1 write sits inside if(m_Sync){...} too -- never locking means never
        // writing, not a bug.
        Assert.Null(decoder.TryGetScopeCaptureChannel1());
    }

    [Fact]
    public void ArmScopeCapture_WhileLocked_BothChannelsEventuallyFill_WithFiniteValues()
    {
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);
        var decoder = new AnalogFmSstvDecoder(11025);

        var offset = DecodeUntilLinesComplete(decoder, samples, 3, out var lineCount);
        Assert.True(lineCount >= 3, "Never decoded enough lines -- test setup problem.");

        decoder.ArmScopeCapture(64);

        const int chunkSize = 64;
        for (; offset < samples.Length && (decoder.TryGetScopeCaptureChannel0() is null || decoder.TryGetScopeCaptureChannel1() is null); offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        var channel0 = decoder.TryGetScopeCaptureChannel0();
        var channel1 = decoder.TryGetScopeCaptureChannel1();
        Assert.NotNull(channel0);
        Assert.NotNull(channel1);
        Assert.Equal(64, channel0!.Length);
        Assert.Equal(64, channel1!.Length);
        Assert.All(channel0, v => Assert.True(double.IsFinite(v)));
        Assert.All(channel1, v => Assert.True(double.IsFinite(v)));
    }

    [Fact]
    public void ArmScopeCapture_WhileLockedWithARealBacklog_DrainsItBeforeArming_NotAfter()
    {
        // Auditor code-review regression (real bug, fixed before this test was added): while
        // genuinely locked, the ONLY thing that advances the D12/D19 forward-fill cursors is
        // TrimBuffers' own ~44100-sample-cycle catch-up -- between trims they can trail
        // _levelAgcProcessedUpTo by up to ~46,100 samples (~4.2s @ 11025Hz). Arming without first
        // draining that backlog meant the FIRST push after arming dumped the whole stale gap into
        // the freshly-armed buffer in one shot -- an instantly "full" capture made of historical,
        // not trigger-instant, samples. This decodes well past one TrimBuffers cycle so a real
        // backlog genuinely exists, confirms it, then proves arming closes the gap immediately
        // (before the buffer even starts filling), not on the buffer's own first write.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);
        var decoder = new AnalogFmSstvDecoder(11025);

        const int chunkSize = 4096;
        var offset = 0;
        for (; offset < samples.Length && offset < 80_000; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        var agcCursor = decoder.LevelAgcProcessedUpToForTests;
        var d12Cursor = decoder.VisDataD12ProcessedUpTo;
        var backlogBeforeArm = agcCursor - d12Cursor;
        Assert.True(backlogBeforeArm > 1000, $"Test setup problem -- backlog was only {backlogBeforeArm} samples, too small to distinguish a drain from a dump.");

        decoder.ArmScopeCapture(64);
        decoder.PushSamples(new float[1]); // one trivial push -- drains the arm request (and, per the fix, the backlog too)

        // The backlog is gone -- D12 caught up to (approximately) where AGC was at arm time -- and
        // NONE of it landed in the new buffer.
        Assert.True(decoder.VisDataD12ProcessedUpTo >= agcCursor - 1);
        Assert.Null(decoder.TryGetScopeCaptureChannel0());
    }

    [Fact]
    public void Channel1Capture_UsesTheInvertedRawScaledDomain_NotPlainHz()
    {
        // Plan-review round 2 finding this pins: channel 1 must be Hz-inverted back to legacy's raw
        // +/-16384-domain scaled value (CHILL::Do's own return domain, sstv.cpp:3086), not the plain
        // Hz reading DemodulatedFrequencyAt returns. A picture-frequency signal only swings across
        // roughly the 1500-2300 Hz luminance range -- feeding that directly would confine every
        // captured value to a narrow few-hundred-wide band. The correct inverse
        // ((centerHz-hz)*32768/bandwidthHz, 800 Hz wide-mode bandwidth) amplifies that same swing by
        // ~41x, so a wide observed range is a direct, cheap proxy for "the conversion is happening."
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode);
        var decoder = new AnalogFmSstvDecoder(11025);
        var offset = DecodeUntilLinesComplete(decoder, samples, 3, out var lineCount);
        Assert.True(lineCount >= 3, "Never decoded enough lines -- test setup problem.");

        decoder.ArmScopeCapture(256);
        const int chunkSize = 64;
        for (; offset < samples.Length && decoder.TryGetScopeCaptureChannel1() is null; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        var channel1 = decoder.TryGetScopeCaptureChannel1();
        Assert.NotNull(channel1);
        var range = channel1!.Max() - channel1.Min();
        Assert.True(range > 2000, $"Channel 1 range was {range} -- looks like plain Hz, not the inverted +/-16384 domain.");
    }

    [Fact]
    public void ArmScopeCapture_Channel0Source_LatchesToTheCurrentlyLockedModesNarrowness()
    {
        // Un-stub-RX-tab Piece B: ForceMode locks a mode immediately, bypassing real VIS-header
        // detection -- a cheap way to get a genuinely narrow-mode _mode without encoding a real
        // narrow-family transmission (which needs a real narrow FSK header, out of scope for this
        // one latch-behavior test).
        var wideDecoder = new AnalogFmSstvDecoder(11025);
        wideDecoder.ForceMode(SstvModeRegistry.Robot36);
        wideDecoder.PushSamples(new float[1]); // drains ForceMode
        wideDecoder.ArmScopeCapture(4);
        wideDecoder.PushSamples(new float[1]); // drains the arm request
        Assert.False(wideDecoder.ScopeCaptureChannel0UsesD19ForTests);

        // Mr73 is NOT narrow in the NarrowModeCode sense (its LuminanceMinHz/MaxHz is the standard
        // 1500/2300 range, unlike the true narrow MN/MC families) -- Mn73 is the real narrow-family
        // mode this test needs (CreateMnFamilyMode, sync=1900Hz, confirmed via a throwaway diagnostic
        // that Mr73.NarrowModeCode is actually null).
        var narrowDecoder = new AnalogFmSstvDecoder(11025);
        narrowDecoder.ForceMode(SstvModeRegistry.Mn73);
        narrowDecoder.PushSamples(new float[1]);
        narrowDecoder.ArmScopeCapture(4);
        narrowDecoder.PushSamples(new float[1]);
        Assert.True(narrowDecoder.ScopeCaptureChannel0UsesD19ForTests);
    }

    [Fact]
    public void ArmScopeCapture_BeforeAnyModeIsLocked_DefaultsToTheWideSource()
    {
        // _mode is null before any lock -- must default to d12 (wide), matching legacy's own
        // m_fNarrow default of false, not throw or pick d19 arbitrarily.
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.ArmScopeCapture(4);
        decoder.PushSamples(new float[1]);

        Assert.False(decoder.ScopeCaptureChannel0UsesD19ForTests);
    }

    [Fact]
    public void ReArming_DiscardsAPreviousInProgressCapture_AndStartsFreshValues()
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        decoder.ArmScopeCapture(1_000_000); // large enough it won't complete from the small push below
        decoder.PushSamples(new float[1]);
        decoder.PushSamples(new float[25_000]); // one pass of the pre-lock header-scan machinery
        Assert.Null(decoder.TryGetScopeCaptureChannel0());

        decoder.ArmScopeCapture(4); // re-arm with a size small enough to complete quickly
        decoder.PushSamples(new float[1]);

        for (var i = 0; i < 10 && decoder.TryGetScopeCaptureChannel0() is null; i++)
        {
            decoder.PushSamples(new float[25_000]);
        }

        var channel0 = decoder.TryGetScopeCaptureChannel0();
        Assert.NotNull(channel0);
        Assert.Equal(4, channel0!.Length); // the RE-armed size, not the discarded 1,000,000
    }

    [Fact]
    public void ArmScopeCapture_WithZeroSize_IsImmediatelyFullOnBothChannels()
    {
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.ArmScopeCapture(0);
        decoder.PushSamples(new float[1]);

        Assert.Equal([], decoder.TryGetScopeCaptureChannel0());
        // Channel 1 too -- ScopeCaptureBuffer.Arm's own zero-size special case applies uniformly to
        // both channels, not just the one that happens to write unconditionally.
        Assert.Equal([], decoder.TryGetScopeCaptureChannel1());
    }

    private static int DecodeUntilLinesComplete(AnalogFmSstvDecoder decoder, float[] samples, int targetLineCount, out int lineCount)
    {
        var count = 0;
        decoder.LineDecoded += _ => count++;

        const int chunkSize = 32;
        var offset = 0;
        for (; offset < samples.Length && count < targetLineCount; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        lineCount = count;
        return offset;
    }

    private static float[] EncodeRealTransmission(SstvModeDefinition mode)
    {
        var image = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        var enumerator = encoder.EncodeAsync(mode, image).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                samples.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return samples.ToArray();
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
