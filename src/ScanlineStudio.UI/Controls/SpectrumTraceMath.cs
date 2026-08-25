using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.UI.Controls;

/// <summary>Pure data-transform math for <see cref="SpectrumTraceControl"/> -- kept out of the
/// control itself so it's testable without a real Avalonia render pass (same reasoning as
/// <see cref="WaterfallPalette"/>). None of this is a legacy DSP port (spec/06-sstv-dsp.md exempts
/// this visualization from strict port-first fidelity) except <see cref="ComputeMarkerFrequencies"/>,
/// which preserves legacy's real SSTV control-tone frequencies (sync/black/leader/white) -- see that
/// method's own doc comment for the citations.</summary>
public static class SpectrumTraceMath
{
    /// <summary>Maps an absolute audio frequency to an X pixel position within a trace of
    /// <paramref name="width"/> pixels showing the window [<paramref name="startHz"/>,
    /// <paramref name="startHz"/> + <paramref name="spanHz"/>). Not clamped -- a frequency outside
    /// the window maps outside [0, width); callers decide whether to skip drawing it.</summary>
    public static double MapFrequencyToX(double freqHz, double startHz, double spanHz, double width) =>
        spanHz > 0 ? (freqHz - startHz) / spanHz * width : 0.0;

    /// <summary>Inverse of <see cref="MapFrequencyToX"/> -- Notch UI's click-to-tune gesture (Un-stub
    /// RX-tab Piece A3). Not clamped -- same "callers decide" convention as
    /// <see cref="MapFrequencyToX"/>; a pointer position outside [0, width) maps to a frequency
    /// outside the visible [startHz, startHz+spanHz) window.</summary>
    public static double MapXToFrequency(double x, double startHz, double spanHz, double width) =>
        width > 0 ? x / width * spanHz + startHz : startHz;

    /// <summary>Maps a dB magnitude to a Y pixel position within a trace of <paramref name="height"/>
    /// pixels, using the same <paramref name="zeroDb"/>/<paramref name="gainDb"/> normalization
    /// window <see cref="WaterfallControl"/> uses for color -- deliberately the same two sliders
    /// control both the waterfall's color mapping and the spectrum trace's vertical scale, so they
    /// stay visually consistent with each other. 0 at <paramref name="zeroDb"/> (bottom, Y=height),
    /// 1 at <paramref name="zeroDb"/>+<paramref name="gainDb"/> (top, Y=0) -- normal spectrum-analyzer
    /// convention (weak signal low, strong signal high), not clamped -- callers decide how to handle
    /// an out-of-range Y.</summary>
    public static double MapDbToY(double db, double zeroDb, double gainDb, double height)
    {
        var normalized = gainDb > 0 ? (db - zeroDb) / gainDb : 0.0;
        return height - (normalized * height);
    }

    /// <summary>Time-based (not frame-based) peak-hold decay -- frame-based decay is frame-rate
    /// dependent (a live session at 44100Hz hops frames roughly 4x faster than at 11025Hz, so a fixed
    /// dB-per-FRAME decay would visibly decay 4x faster at the higher rate). <paramref name="decayDbPerSecond"/>
    /// default of 36 approximates legacy's own real per-frame pixel decay (`Main.cpp:3404-3417`,
    /// 8/4/2 y-pixels/frame depending on `m_FFTStg`'s 1/2/3 setting) converted to a time rate at
    /// legacy's typical frame cadence -- not a literal port (this port's own frame cadence differs
    /// with sample rate/hop size), a comparable-feeling default.</summary>
    public static double DecayPeak(double currentPeakDb, double newDb, double elapsedSeconds, double decayDbPerSecond = 36.0)
    {
        if (newDb >= currentPeakDb)
        {
            return newDb;
        }

        var decayed = currentPeakDb - (decayDbPerSecond * Math.Max(elapsedSeconds, 0.0));
        return Math.Max(decayed, newDb);
    }

    /// <summary>Real SSTV control-tone marker frequencies for <paramref name="mode"/> (or a sensible
    /// wide-mode default when no mode is currently locked) -- <c>SstvModeRegistry.cs:893</c>/<c>:923</c>
    /// already use this exact <c>NarrowModeCode is not null ? 1900.0 : 1200.0</c> ternary for the sync
    /// tone (reused here, not re-derived); <see cref="SstvModeDefinition.LuminanceMinHz"/>/
    /// <see cref="SstvModeDefinition.LuminanceMaxHz"/> give the black/white tones directly (default
    /// 1500/2300 wide, narrow modes carry 2044/2300 -- both narrow-mode construction sites in
    /// <c>SstvModeRegistry.cs</c> set these together, confirmed no mode mixes a 1900 sync with a 1500
    /// black tone). The result is the UNION of {sync, 1200, 1500, 1900, 2300, LuminanceMinHz,
    /// LuminanceMaxHz}, deduplicated -- not just {sync, LuminanceMinHz, LuminanceMaxHz} -- so the
    /// marker set is IDENTICAL whether idle (mode is null) or locked to a wide mode (auditor-caught:
    /// the narrower 3-element set would make 1900 vanish the instant a wide mode locks and reappear
    /// on idle, disagreeing with legacy's own wide-mode marker set which always shows
    /// 1200/1500/1900/2300, `Main.cpp:3269-3291`). 1200 is ALSO in the fixed base set (not just added
    /// via syncHz), matching legacy's narrow-mode set too -- legacy draws a dotted 1200 marker even in
    /// narrow mode (`Main.cpp:3269-3270`), which an earlier version of this method (only adding 1200
    /// via syncHz, which is 1900 for narrow modes) dropped; auditor-caught round-3, their own
    /// round-2 "matches legacy exactly" claim for narrow mode had an arithmetic slip. No repeater
    /// marker (`m_FX[4]`) -- this port has no repeater/squelch concept anywhere (confirmed, not just
    /// undecided).</summary>
    public static IReadOnlyList<SpectrumMarker> ComputeMarkerFrequencies(SstvModeDefinition? mode)
    {
        var syncHz = mode is null ? 1200.0 : (mode.NarrowModeCode is not null ? 1900.0 : 1200.0);
        var frequencies = new SortedSet<double> { 1200.0, 1500.0, 1900.0, 2300.0 };
        if (mode is not null)
        {
            frequencies.Add(mode.LuminanceMinHz);
            frequencies.Add(mode.LuminanceMaxHz);
        }

        frequencies.Add(syncHz);

        return frequencies.Select(f => new SpectrumMarker(f, IsSync: f == syncHz)).ToList();
    }
}

/// <summary>One frequency-reference vertical line on the spectrum trace. <see cref="IsSync"/> ==
/// solid line style, otherwise dotted -- simplified from legacy's exact per-mode solid/dotted table
/// (spec/06's UI exemption doesn't require pixel-exact replication) to "sync is always solid," which
/// matches legacy's own actual behavior in both wide and narrow modes.</summary>
public sealed record SpectrumMarker(double FrequencyHz, bool IsSync);
