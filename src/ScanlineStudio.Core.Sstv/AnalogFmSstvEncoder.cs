using System.Runtime.CompilerServices;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Generic continuous-phase FM encoder. Shared infrastructure (VIS header, phase accumulation) is
/// family-agnostic; the actual per-line frequency sequence is delegated to a
/// <see cref="IScanlineEncoder"/> selected via <see cref="ScanlineCodecFactory"/> — see
/// <see cref="ColorEncoding"/>'s doc comment for why different families need different codec
/// logic rather than one generic interpreter.
/// </summary>
public sealed class AnalogFmSstvEncoder : ISstvEncoder
{
    public AnalogFmSstvEncoder(int sampleRate = 11025)
    {
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    // Milestone-audit Phase 3 SHOULD finding (spec/14-roadmap.md): without this guard, a too-small
    // image throws IndexOutOfRangeException from deep inside a scanline encoder's own pixel-index
    // loop (e.g. RgbSequentialScanlineEncoder.cs's `image.GetScanline(lineIndex)[x]` for x up to
    // mode.ImageWidth-1) -- after the header and part of a line have already been yielded to the
    // sink, leaving no clean error state -- and a too-large one silently crops with no signal at all.
    // Legacy is structurally immune (its Line* functions read width straight from the bitmap itself,
    // Main.cpp:6692 etc.) -- this port's equivalent contract is "the image IS sized to the mode,"
    // enforced here instead of assumed by every caller.
    //
    // Deliberately a plain (non-iterator) method delegating to EncodeAsyncCore, not
    // `async IAsyncEnumerable<float> EncodeAsync` directly: a C# iterator method's body doesn't run
    // until the FIRST `MoveNextAsync()` call, so a guard written inside the iterator itself would
    // still defer the throw to whenever the caller starts enumerating -- better than throwing after
    // partial output, but not as clean as throwing synchronously at the `EncodeAsync()` call site
    // itself, which this split achieves.
    public IAsyncEnumerable<float> EncodeAsync(
        SstvModeDefinition mode,
        IImageSource image,
        CancellationToken ct = default)
    {
        if (image.Width != mode.ImageWidth || image.Height != mode.ImageHeight)
        {
            throw new ArgumentException(
                $"Image dimensions {image.Width}x{image.Height} do not match mode '{mode.Id}' expected {mode.ImageWidth}x{mode.ImageHeight}.",
                nameof(image));
        }

        return EncodeAsyncCore(mode, image, ct);
    }

    private async IAsyncEnumerable<float> EncodeAsyncCore(
        SstvModeDefinition mode,
        IImageSource image,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var lineEncoder = ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding);
        var phase = 0.0;

        // ultracode audit finding #26: legacy's TX output bandpass filter is applied to EVERY emitted
        // sample, unconditionally, as the last step of CSSTVMOD::Do() -- constructed locally, not as
        // a field, so every EncodeAsync call gets fresh (zeroed) filter state, matching legacy's own
        // per-transmission InitTXBuf -> m_BPF.Clear() reset (this encoder is a DI singleton; a
        // ctor-field filter would leak state across calls). See TxOutputBandpassFilter's own doc
        // comment for why this can't just reuse SearchBandpassFilter.
        var bandpassFilter = new TxOutputBandpassFilter(SampleRate);

        // Running accumulator, not "round(durationMs -> samples) per segment": with ~245,000
        // individual per-pixel segments in a full image, independently rounding each one's sample
        // count biases every pixel the same direction and the error accumulates linearly (over a
        // second of drift by the end of the image). Tracking ideal elapsed samples as a running
        // total and taking the difference keeps rounding error bounded to +/-0.5 sample forever.
        var idealSamplesSoFar = 0.0;
        var emittedSamples = 0L;

        foreach (var (frequencyHz, durationMs) in GenerateFrequencySegments(mode, image, lineEncoder))
        {
            ct.ThrowIfCancellationRequested();

            idealSamplesSoFar += durationMs / 1000.0 * SampleRate;
            // ultracode audit finding #25: legacy's CSSTVMOD::Do uses `for (; m_iPos < int(m_dPos); ...)`
            // on an equivalent running accumulator -- floor/truncation, not round-to-nearest. Both are
            // bounded (non-cumulative, +/-1 sample per segment boundary) and Math.Round is arguably
            // more accurate (zero-mean vs legacy's 0.5-sample lag) -- this is a knowing parity trade
            // for future sample-exact diffing against a legacy TX capture, not a "legacy is more
            // correct" claim. Comment left here so a future reader doesn't "fix" this back to Round.
            var targetEmitted = (long)idealSamplesSoFar;
            var samplesToEmit = targetEmitted - emittedSamples;
            emittedSamples = targetEmitted;

            var phaseIncrement = 2 * Math.PI * frequencyHz / SampleRate;

            for (var i = 0; i < samplesToEmit; i++)
            {
                double sample;
                if (frequencyHz <= 0)
                {
                    // Station-ID work (spec/14-roadmap.md's "CW-ID / FSK station-ID subsystem"):
                    // CW-ID's inter-element/inter-letter/inter-word gaps are real silence, not a
                    // 0Hz tone. Matches legacy CSSTVMOD::Do (sstv.cpp:2865-2875) exactly: emit 0
                    // amplitude and do NOT advance the VCO phase (`m_vco.Do()` is skipped entirely
                    // when the buffered value is <= 0), so a tone immediately after a gap resumes
                    // in-phase rather than restarting from phase 0. The output bandpass filter
                    // still runs over the zero samples below (sstv.cpp:2914), same as every other
                    // segment -- do not special-case it out of the filter call.
                    sample = 0.0;
                }
                else
                {
                    phase += phaseIncrement;
                    if (phase >= 2 * Math.PI)
                    {
                        phase -= 2 * Math.PI;
                    }

                    sample = Math.Sin(phase);
                }

                // Filtered in double, narrowed to float only here -- legacy's whole chain
                // (CSSTVMOD::Do's `d`) is double; narrowing before filtering would lose precision
                // the filter itself doesn't need to lose.
                yield return (float)bandpassFilter.ProcessSample(sample);
            }
        }

        await Task.CompletedTask;
    }

    // Test-only seam (mirrors GenerateFooterSegments' existing internal-for-testing pattern below):
    // renders an arbitrary segment sequence through the identical frequency-to-sample math used by
    // EncodeAsyncCore above (running-accumulator sample count, silence handling, output bandpass
    // filter) without needing a full SstvModeDefinition/IImageSource/CancellationToken round trip.
    // Kept as a small, deliberately independent implementation rather than extracted shared code:
    // EncodeAsyncCore's per-sample loop can't cleanly share a `ref double phase` across an iterator
    // method boundary, and this method has no cancellation/async concerns of its own to preserve.
    internal static IEnumerable<float> RenderSegments(
        IEnumerable<(double FrequencyHz, double DurationMs)> segments,
        int sampleRate,
        bool applyFilter = true)
    {
        var phase = 0.0;
        var bandpassFilter = new TxOutputBandpassFilter(sampleRate);
        var idealSamplesSoFar = 0.0;
        var emittedSamples = 0L;

        foreach (var (frequencyHz, durationMs) in segments)
        {
            idealSamplesSoFar += durationMs / 1000.0 * sampleRate;
            var targetEmitted = (long)idealSamplesSoFar;
            var samplesToEmit = targetEmitted - emittedSamples;
            emittedSamples = targetEmitted;

            var phaseIncrement = 2 * Math.PI * frequencyHz / sampleRate;

            for (var i = 0; i < samplesToEmit; i++)
            {
                double sample;
                if (frequencyHz <= 0)
                {
                    sample = 0.0;
                }
                else
                {
                    phase += phaseIncrement;
                    if (phase >= 2 * Math.PI)
                    {
                        phase -= 2 * Math.PI;
                    }

                    sample = Math.Sin(phase);
                }

                // applyFilter=false is test-only (verifying raw VCO phase behavior, e.g. that
                // silence doesn't disturb phase continuity, independent of filter transients) --
                // production always filters, matching EncodeAsyncCore.
                yield return (float)(applyFilter ? bandpassFilter.ProcessSample(sample) : sample);
            }
        }
    }

    private static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateFrequencySegments(
        SstvModeDefinition mode,
        IImageSource image,
        IScanlineEncoder lineEncoder)
    {
        // SHOULD item 5 (spec/14-roadmap.md): OutHEAD's pre-VIS leader-tone burst is emitted
        // UNCONDITIONALLY first, for every mode including AVT -- confirmed directly against source
        // (Main.cpp:7392-7393, OutHEAD() called before the narrow-vs-normal branch that follows;
        // AVT's own 3x-VIS-repeat lives INSIDE that later branch, at Main.cpp:7430 (comprehensive-
        // review correction: an earlier version of this comment cited 7429, which is `int d;`, the
        // declaration one line above the real `int e = (m_TxMode==smAVT)?3:1;`), so it gets the
        // same OutHEAD burst as every other non-narrow mode, not a special AVT-only header). See
        // VisHeader.cs's own OutHeadToneDurationMs/GenerateOutHeadSegments for the exact tone
        // sequences.
        foreach (var segment in VisHeader.GenerateOutHeadSegments(mode.NarrowModeCode is not null))
        {
            yield return segment;
        }

        // Mirrors legacy's own branching (Main.cpp:7430-7578) exactly: AVT gets a wholly different
        // header (3x VIS + training sequence, no post-VIS pulse); Scottie gets a normal single VIS
        // plus an extra 9ms/1200Hz pulse; RM12's VIS byte needs a forced (non-computed) parity bit
        // because legacy's own assigned byte for it doesn't follow the even-parity convention every
        // other normal VIS code does; everyone else gets a normal single VIS transmission.
        if (mode == SstvModeRegistry.Avt)
        {
            foreach (var segment in VisHeader.GenerateAvtSegments(mode.VisCode))
            {
                yield return segment;
            }
        }
        else if (mode.NarrowModeCode is { } narrowCode)
        {
            foreach (var segment in VisHeader.GenerateNarrowModeSegments(narrowCode))
            {
                yield return segment;
            }
        }
        else if (mode.ExtendedVisCode is { } extendedCode)
        {
            foreach (var segment in VisHeader.GenerateExtendedSegments(extendedCode))
            {
                yield return segment;
            }
        }
        else
        {
            var forcedParityBit = mode == SstvModeRegistry.Rm12 ? VisHeader.Rm12ForcedParityBit : (int?)null;
            foreach (var segment in VisHeader.GenerateSegments(mode.VisCode, forcedParityBit))
            {
                yield return segment;
            }

            if (SstvModeRegistry.IsScottieFamily(mode))
            {
                yield return (VisHeader.ScottiePostVisPulseFrequencyHz, VisHeader.ScottiePostVisPulseDurationMs);
            }
        }

        for (var y = 0; y < mode.ImageHeight; y += lineEncoder.RowsPerTransmissionLine)
        {
            foreach (var segment in lineEncoder.GenerateLine(mode, image, y))
            {
                yield return segment;
            }
        }

        foreach (var segment in GenerateFooterSegments(mode))
        {
            yield return segment;
        }
    }

    // Main.cpp:6994-7013 (TMmsstv::SendSSTV, "MMSSTV フッター" -- footer): legacy always appends
    // this immediately after the last image line. This is specifically the `!sys.m_TXFSKID` branch
    // (no FSK station ID configured) -- the only branch implementable right now, since FSK/CW
    // station ID is a deliberately deferred, separately-scoped feature (see spec/06-sstv-dsp.md's
    // station-ID task list). The alternate branch, `mp->Write(fTxNarrow ? 1900 : 1500, 300)`, only
    // runs when FSK ID *is* configured, so it isn't reachable yet either way and is left for that
    // future work to add alongside the ID packet itself, not invented here as a guess.
    //
    // `sys.m_VOX` isn't modeled anywhere in this port (no radio/PTT layer exists yet, per
    // spec/14-roadmap's phase ordering) -- defaults to legacy's own default, off (`Main.cpp:822`),
    // which is the more common case for typical (non-VOX-triggered) transmit anyway. If VOX support
    // is ever added, this condition needs `|| isVoxEnabled` alongside the narrow-mode check below;
    // flagged here rather than silently baked in as "always off" forever.
    internal const double FooterAlternatingToneDurationMs = 100.0;

    // SSTVSET.m_TW (`sstv.cpp:1109`) is one line's duration *in samples*; the footer's trailing
    // carrier is capped at `min(m_TW, SampFreq/2)` samples (`Main.cpp:6998-7000`) -- expressed here
    // in milliseconds (sample-rate-independent) as `min(LineDurationMs, 500ms)`.
    internal const double FooterMaxTrailingCarrierMs = 500.0;

    internal static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateFooterSegments(SstvModeDefinition mode)
    {
        var trailingCarrierMs = Math.Min(mode.LineDurationMs, FooterMaxTrailingCarrierMs);

        if (mode.NarrowModeCode is null)
        {
            yield return (1500, trailingCarrierMs);
            yield return (1900, FooterAlternatingToneDurationMs);
            yield return (1500, FooterAlternatingToneDurationMs);
            yield return (1900, FooterAlternatingToneDurationMs);
            yield return (1500, FooterAlternatingToneDurationMs);
        }
        else
        {
            yield return (1900, trailingCarrierMs);
        }
    }
}
