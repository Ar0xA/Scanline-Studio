using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Milestone-audit SHOULD item 7 (spec/14-roadmap.md): legacy's <c>DecodeFSK</c> (sstv.cpp:1858) runs
/// UNCONDITIONALLY every sample, including throughout AVT training -- its own narrow-packet-completion
/// handler (sstv.cpp:2589-2593) commits to the narrow mode whenever `(m_SyncRestart || !m_Sync) &amp;&amp;
/// m_NextMode &amp;&amp; (m_SyncMode >= 0)`, a condition AVT training's own state satisfies throughout
/// (m_SyncMode values 4/5/6/7/8/256 during training are all >= 0, m_Sync stays false until an image
/// actually locks). This port's <c>TryDecodeHeader</c> used to short-circuit straight into
/// <c>TryResolveAvtTraining</c> while <c>_avtTrainingPending</c>, silently missing any narrow packet
/// whose audio falls within the training window. This test constructs exactly that: a real AVT VIS
/// header + partial training audio, immediately followed by a full real narrow-mode transmission, and
/// confirms the decoder locks onto the NARROW mode, not AVT.
/// </summary>
public class NarrowFskDuringAvtTrainingTests
{
    private const int SampleRate = 11025;

    [Fact]
    public async Task NarrowHeader_WithinAvtTrainingWindow_AbortsAvtTrainingAndLocksNarrowMode()
    {
        var avtMode = SstvModeRegistry.Avt;
        var avtPixels = new Rgb24[avtMode.ImageWidth * avtMode.ImageHeight];
        Array.Fill(avtPixels, new Rgb24(128, 64, 32));
        var avtImage = new ArrayImageSource(avtMode.ImageWidth, avtMode.ImageHeight, avtPixels);

        var avtEncoder = new AnalogFmSstvEncoder(SampleRate);
        var avtSamples = new List<float>();
        await foreach (var sample in avtEncoder.EncodeAsync(avtMode, avtImage))
        {
            avtSamples.Add(sample);
        }

        // AVT's own training window is [origin, deadline] = roughly [2.73s, 8.04s] after TX start
        // (3x910ms VIS repeats, then up to ~7.13s of training sequence/fallback budget --
        // VisHeader.AvtVisRepeatCount/AvtVisBlockDurationMs/AvtExtraHeaderDurationMs). 4.5s lands
        // comfortably inside that window, with real margin on both sides -- not a byte-exact
        // boundary, deliberately: this test only needs "somewhere mid-training," not a precise edge.
        var avtPrefixSampleCount = (int)(4.5 * SampleRate);
        var avtPrefix = avtSamples.Take(avtPrefixSampleCount).ToArray();

        // Full narrow-mode transmission, not just its header: Commit() defers ModeDetected for
        // non-AVT modes until TryResolveSyncAnchorCorrection succeeds, which needs several lines'
        // worth of buffered samples PAST the anchor (TryResolveSyncAnchorCorrection's own
        // lineCount*pageWidthSamples gate) -- a header-only splice proved the fix's own Commit()/
        // LockAnchorCommitted fires (confirmed via temporary diagnostics during development) but
        // starved the anchor-correction step of the trailing data it needs to finish and fire
        // ModeDetected. The full image comfortably provides that.
        var narrowMode = SstvModeRegistry.Mn110;
        var narrowPixels = new Rgb24[narrowMode.ImageWidth * narrowMode.ImageHeight];
        Array.Fill(narrowPixels, new Rgb24(200, 100, 50));
        var narrowImage = new ArrayImageSource(narrowMode.ImageWidth, narrowMode.ImageHeight, narrowPixels);

        var narrowEncoder = new AnalogFmSstvEncoder(SampleRate);
        var narrowSamples = new List<float>();
        await foreach (var sample in narrowEncoder.EncodeAsync(narrowMode, narrowImage))
        {
            narrowSamples.Add(sample);
        }

        var combined = new float[avtPrefix.Length + narrowSamples.Count];
        Array.Copy(avtPrefix, combined, avtPrefix.Length);
        narrowSamples.CopyTo(combined, avtPrefix.Length);

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        var detectedModes = new List<SstvModeDefinition>();
        var restarts = new List<SstvModeDefinition>();
        decoder.ModeDetected += m => detectedModes.Add(m);
        decoder.DecodeRestarted += m => restarts.Add(m);

        decoder.PushSamples(combined);

        Assert.NotEmpty(detectedModes);
        Assert.Equal(narrowMode.Id, detectedModes[0].Id);
        // Not a "restart" -- AVT training never committed to _mode (it was still pending), so this is
        // this decoder's first-ever lock, exactly like any other pre-lock detection.
        Assert.Empty(restarts);
    }
}
