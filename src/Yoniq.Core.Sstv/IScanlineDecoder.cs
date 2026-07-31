using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Per-family decode strategy — counterpart to <see cref="IScanlineEncoder"/>. Shared
/// infrastructure (VIS detection, PLL demodulation, sample buffering) lives in
/// <see cref="AnalogFmSstvDecoder"/>, which selects the right strategy via
/// <see cref="ScanlineCodecFactory"/> once VIS reveals the mode. Implementations may hold mutable
/// state across calls (e.g. <c>RobotScanlineDecoder</c> caching the previous line's other chroma
/// channel) — a new decoder strategy instance is created per <see cref="AnalogFmSstvDecoder"/>
/// session, so this state never leaks across unrelated decodes.
/// </summary>
internal interface IScanlineDecoder
{
    /// <summary>See <see cref="IScanlineEncoder.RowsPerTransmissionLine"/>.</summary>
    int RowsPerTransmissionLine => 1;

    /// <param name="reader">Reads the demodulated frequency at a single sample point (each method's
    /// first argument; the second is accepted but ignored — kept so callers can pass either endpoint
    /// of a computed window without restructuring call sites). This is a direct port of how legacy
    /// actually reads a pixel: <c>Main.cpp:4144-4148</c> processes the recorded audio one raw sample
    /// at a time and, for each pixel, keeps whichever single sample is first at that pixel's computed
    /// index — no windowed average anywhere. An earlier version of this port block-averaged the whole
    /// per-pixel dwell window instead, which was an invented technique, not traced from source — and
    /// the likely cause of a measured resolution floor (modes under ~4 samples/pixel at 11025Hz
    /// failed round-trip tolerance, because the averaging window bled into the neighboring pixel's
    /// transition). Pass the pixel's window-start sample to match legacy's own "first sample wins"
    /// behavior for ordinary per-pixel scans.
    ///
    /// Piece 10 correction: an earlier version of this comment claimed <c>GetPictureLevel</c> "simply
    /// dereferences <c>*ip</c>, no windowed average" — false. <c>GetPictureLevel</c>
    /// (<c>Main.cpp:4057-4071</c>) peak-picks between two samples <c>m_KSB</c> apart; only
    /// <c>GetPixelLevel</c> (chroma/tone-selector reads, and every channel Scottie DX transmits) is
    /// the bare single dereference. <see cref="PixelSampleReader"/>'s two distinctly-named methods —
    /// <c>ReadBare</c> (the true bare dereference) and <c>ReadPeakPicked</c> (the real
    /// <c>GetPictureLevel</c> port) — replace what used to be a single delegate here, so each call
    /// site states explicitly which legacy behavior it's reproducing for that channel.</param>
    void DecodeLine(
        SstvModeDefinition mode,
        int sampleRate,
        int lineStartSample,
        int lineIndex,
        PixelSampleReader reader,
        Rgb24[] pixels);
}
