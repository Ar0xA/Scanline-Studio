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
        StationIdTransmitOptions? stationId = null,
        CancellationToken ct = default)
    {
        ValidateImageDimensions(mode, image);

        return EncodeAsyncCore(mode, image, stationId ?? StationIdTransmitOptions.None, ct);
    }

    /// <summary>Spec/18-path-to-1.0.md Medium item: "No TX send-progress feedback during transmit."
    /// Replicates <see cref="EncodeAsyncCore"/>'s own running-accumulator expression verbatim
    /// (same per-segment formula, same operand order, same summation order) rather than summing
    /// <c>DurationMs</c> first and multiplying once -- floating-point addition is not associative,
    /// so those two forms are only guaranteed to agree in exact arithmetic; over the ~hundreds of
    /// thousands of segments a full image produces, they can diverge by enough to flip the final
    /// truncation by one sample on some (mode, image, stationId) combination even though a
    /// single-mode spot check would show exact agreement. This is a real traversal of every segment
    /// (same iterators <see cref="EncodeAsyncCore"/> walks), not O(1) metadata math -- no tone
    /// synthesis or filtering happens, but the cost is still proportional to image size.
    ///
    /// Honesty note on how this was verified: mutation-testing this specific property (temporarily
    /// reverting to sum-then-multiply, confirming a test catches it) did NOT actually fail on any of
    /// the several structurally different modes tried in
    /// <c>AnalogFmSstvEncoderEstimateSampleCountTests</c> -- the divergence this comment describes is
    /// real (IEEE-754 addition is provably non-associative) but apparently doesn't manifest for these
    /// particular solid-color test images at these particular mode sizes; constructing an image/mode
    /// combination that DOES trigger it was not attempted (would require reasoning about exact
    /// per-segment floating-point rounding across hundreds of thousands of terms). Bit-identical
    /// output here is guaranteed by identical code path/operand order (structural correctness), not
    /// by an empirical test that has been shown to fail without it.</summary>
    public long EstimateSampleCount(
        SstvModeDefinition mode,
        IImageSource image,
        StationIdTransmitOptions? stationId = null)
    {
        ValidateImageDimensions(mode, image);

        var lineEncoder = ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding);
        var idealSamplesSoFar = 0.0;
        foreach (var (_, durationMs) in GenerateFrequencySegments(mode, image, lineEncoder, stationId ?? StationIdTransmitOptions.None))
        {
            idealSamplesSoFar += durationMs / 1000.0 * SampleRate;
        }

        return (long)idealSamplesSoFar;
    }

    /// <summary>Duration of the fixed leader-tone burst <see cref="GenerateFrequencySegments"/>
    /// unconditionally sends before every mode's VIS header (legacy's <c>TMmsstv::OutHEAD</c>,
    /// <c>m_VOX==0</c> case only -- legacy's real VOX feature, <c>m_VOX==1</c>, a user-configured
    /// sound file with an optional FSK ID, is a different thing this port doesn't implement; see
    /// <c>Main.cpp:7274-7304</c>). Named for what it actually is, not "VOX tone" -- this is the
    /// always-on leader burst, not the configurable VOX feature.</summary>
    public static double GetLeaderToneDurationMs(SstvModeDefinition mode) =>
        VisHeader.GenerateOutHeadSegments(mode.NarrowModeCode is not null).Sum(s => s.DurationMs);

    /// <summary>Which VIS-header shape <paramref name="mode"/> actually transmits, and the real
    /// on-air value -- see <see cref="VisHeaderKind"/>'s own doc comment. Mirrors
    /// <see cref="GenerateFrequencySegments"/>'s own branch order and precedence (AVT checked
    /// first) exactly, so this can never disagree with what TX actually sends.</summary>
    public static (VisHeaderKind Kind, int Value) GetVisHeaderInfo(SstvModeDefinition mode)
    {
        if (mode == SstvModeRegistry.Avt)
        {
            // AVT's own VIS block uses a normal (non-forced-parity) computed byte -- same call as
            // the Standard arm below, not the bare VisCode, so both arms answer "what byte is
            // actually on the air" the same way (code-review nit: they coincided today only
            // because AVT's own VisCode happens to need parity bit 0).
            return (VisHeaderKind.Avt, VisHeader.GetTransmittedByte(mode.VisCode));
        }

        if (mode.NarrowModeCode is { } narrowCode)
        {
            return (VisHeaderKind.Narrow, narrowCode);
        }

        if (mode.ExtendedVisCode is { } extendedCode)
        {
            return (VisHeaderKind.Extended, extendedCode);
        }

        var forcedParityBit = mode == SstvModeRegistry.Rm12 ? VisHeader.Rm12ForcedParityBit : (int?)null;
        return (VisHeaderKind.Standard, VisHeader.GetTransmittedByte(mode.VisCode, forcedParityBit));
    }

    private static void ValidateImageDimensions(SstvModeDefinition mode, IImageSource image)
    {
        if (image.Width != mode.ImageWidth || image.Height != mode.ImageHeight)
        {
            throw new ArgumentException(
                $"Image dimensions {image.Width}x{image.Height} do not match mode '{mode.Id}' expected {mode.ImageWidth}x{mode.ImageHeight}.",
                nameof(image));
        }
    }

    private async IAsyncEnumerable<float> EncodeAsyncCore(
        SstvModeDefinition mode,
        IImageSource image,
        StationIdTransmitOptions stationId,
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
        // total and taking the difference keeps rounding error bounded. Doc correction (Tier A
        // Batch 8 chunk 8c): this port TRUNCATES (see the ultracode finding #25 comment below), which
        // bounds the error to [0,1) samples -- a one-sided lag, not the "+/-0.5 sample" round-to-
        // nearest figure this line previously (incorrectly) claimed.
        var idealSamplesSoFar = 0.0;
        var emittedSamples = 0L;

        foreach (var (frequencyHz, durationMs) in GenerateFrequencySegments(mode, image, lineEncoder, stationId))
        {
            ct.ThrowIfCancellationRequested();

            idealSamplesSoFar += durationMs / 1000.0 * SampleRate;
            // ultracode audit finding #25: legacy's CSSTVMOD::Do uses `for (; m_iPos < int(m_dPos); ...)`
            // on an equivalent running accumulator -- floor/truncation, not round-to-nearest. Both are
            // bounded (non-cumulative, +/-1 sample per segment boundary) and Math.Round is arguably
            // more accurate (zero-mean vs legacy's 0.5-sample lag). Doc correction (Tier A Batch 8
            // chunk 8c): the previous "knowing parity trade for future sample-exact diffing against a
            // legacy TX capture" framing overstated what this buys -- legacy's own accumulator uses a
            // DIFFERENT operand order (`(tim*SampFreq)/1000.0`, multiply-then-divide, vs this port's
            // divide-then-multiply), and legacy's VCO is a quantized sine LOOKUP TABLE plus integer
            // gain/mask stages (`sstv.cpp:77-148,2866-2901`), not this port's exact `Math.Sin` --
            // sample-exact legacy diffing was never actually reachable through this choice alone.
            // Keep truncation anyway -- it is the real ported behavior, not an invented approximation.
            // Comment left here so a future reader doesn't "fix" this back to Round without reason.
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
                    // segment -- do not special-case it out of the filter call. Doc correction (Tier
                    // A Batch 8 chunk 8c): for this branch's ONLY reachable trigger (frequencyHz
                    // exactly 0 -- GenerateFrequencySegments never emits a negative frequency),
                    // "skip the phase advance" and "advance by 0" are behaviorally identical
                    // (`phaseIncrement` is itself 0), so the distinction from a zeroed-and-restarted
                    // phase doesn't actually bite here; it would matter only for a hypothetical
                    // future f&lt;0 segment, which is why the branch is written as `&lt;= 0`, not `== 0`.
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
        IScanlineEncoder lineEncoder,
        StationIdTransmitOptions stationId)
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

        foreach (var segment in GenerateFooterSegments(mode, stationId.FskIdEnabled))
        {
            yield return segment;
        }

        // Main.cpp:7016-7027 (TMmsstv::SendSSTV, m_wLine == SSTVSET.m_TL+1): FSK-ID packet first,
        // then CW-ID -- independent, not mutually exclusive; both fire (or don't) based on their own
        // gates. FSK-ID's trigger gate is `sys.m_TXFSKID && !sys.m_Call.IsEmpty()` -- the caller
        // (ScanlineStudio.Application.SstvSessionService) already resolved the FIRST half into
        // stationId.FskIdEnabled; the second half (empty callsign) is left to
        // FskStationIdEncoder.Generate's own existing empty-check (it yields nothing), not
        // re-checked here, so this stays a single source of truth for that gate.
        if (stationId.FskIdEnabled)
        {
            var callsign = StationIdCallsignNormalizer.Normalize(stationId.Callsign);
            var nrRstText = CapNrRstTextForStationId(stationId.NrRstText);
            foreach (var segment in FskStationIdEncoder.Generate(callsign, nrRstText))
            {
                yield return segment;
            }
        }

        // Main.cpp:7021-7025: `sys.m_CWID == 1` -> OutputCWID(); `== 2` -> OutputMMV() (sound-file,
        // out of v1 scope -- StationIdTransmitOptions has no field for it at all, so there is nothing
        // to branch on here; see CwIdMode's own doc comment for why selecting SoundFile silently
        // transmits nothing today, matching legacy's own unconfigured-sound-file behavior).
        if (stationId.CwEnabled)
        {
            var dotDurationMs = CwMorseGenerator.MillisecondsPerDotFromWpm(stationId.CwWpm);
            foreach (var segment in CwMorseGenerator.Generate(stationId.CwResolvedText, stationId.CwToneFrequencyHz, dotDurationMs))
            {
                yield return segment;
            }
        }
    }

    // Settings-boundary length cap for the NR/RST sub-packet's STRING form (the implementation
    // plan's "cap lengths (16 char callsign, 8 char NR string)" -- legacy's OutputFSKID has no TX-
    // side length check of its own; the 8-char bound is the RX decoder's abort threshold
    // (sstv.cpp:2518), so an uncapped TX side could silently emit a packet no compliant receiver can
    // decode).
    //
    // Code-review finding (real bug, fixed): an earlier version capped the RAW text's length before
    // filtering. Every separator a real exchange contains (space, '-', '.', '/') is below '0' and is
    // REMOVED by FskStationIdWireFormat.FilterNrRstChars, so raw length is NOT a safe proxy for
    // filtered length -- e.g. raw "5 9 9 0 0 1 2" (13 chars, under the old raw cap) filters to
    // "5990012" (7 digits), a DIFFERENT string than what capping the raw text first would have left
    // for the encoder to filter, and can flip compact-vs-string form on the wire. Filtering FIRST,
    // then capping the FILTERED result at 3 (RST digits) + MaxNrStringLength, is the correct fix for
    // that real bug. Doc correction (Tier A Batch 8 chunk 8c): "exact (not just safe-but-lossy)
    // bound" overstated what this guarantees -- capping the FILTERED string can still, in principle,
    // flip a legacy-compact NR to this port's string form for a sufficiently long all-digit
    // remainder (e.g. a 12-digit remainder ending "...001234" is compact-eligible uncapped but
    // becomes string-eligible once truncated to 11 chars) -- legacy has no TX-side cap at all, so
    // there's no "what legacy would send" to match once truncation happens either way. Unreachable
    // with any realistic RST/NR exchange (real fields are a handful of digits), but not literally
    // exact for arbitrary input. FskStationIdEncoder.GenerateNrRstSubPacket re-filters its input
    // internally -- harmless here since FilterNrRstChars is idempotent (filtering an already-
    // filtered string is a no-op, confirmed directly from its own stateless per-char predicate), so
    // passing pre-filtered text through changes nothing about what that method computes.
    private static string? CapNrRstTextForStationId(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var filtered = FskStationIdWireFormat.FilterNrRstChars(raw);
        const int maxFilteredLength = 3 + FskStationIdWireFormat.MaxNrStringLength;
        return filtered.Length > maxFilteredLength ? filtered[..maxFilteredLength] : filtered;
    }

    // Main.cpp:6994-7013 (TMmsstv::SendSSTV, "MMSSTV フッター" -- footer): legacy always appends
    // this immediately after the last image line.
    //
    // Code-review correction: an earlier version of this comment justified the always-off assumption
    // below by "no radio/PTT layer exists yet" -- stale, ScanlineStudio.Application.SstvSessionService's
    // PlayWithPttAsync now keys/un-keys PTT for every TX call. The real reason is narrower: `sys.m_VOX`
    // is a hardware Voice/Voltage-Operated-eXchange auto-keying MODE, not something this port's own
    // software PTT control has any equivalent concept of at all (regardless of whether a PTT layer
    // exists) -- defaults to legacy's own default, off (`Main.cpp:822`), which is also the more common
    // case for typical (non-VOX-triggered) transmit anyway. If VOX support is ever modeled, this
    // condition needs `|| isVoxEnabled` alongside the narrow-mode check below; flagged here rather
    // than silently baked in as "always off" forever.
    internal const double FooterAlternatingToneDurationMs = 100.0;

    // SSTVSET.m_TW (`sstv.cpp:1109`) is one line's duration *in samples*; the footer's trailing
    // carrier is capped at `min(m_TW, SampFreq/2)` samples (`Main.cpp:6998-7000`) -- expressed here
    // in milliseconds (sample-rate-independent) as `min(LineDurationMs, 500ms)`.
    //
    // Round-2 auditor finding, traced further than the citation above: `m_TW` is set only by
    // `CSSTVSET::SetMode` (`sstv.cpp:1109`), and every call site of that except ini-load is RX/VIS-
    // detection-driven -- legacy's real footer carrier duration tracks the LAST RECEIVED mode, not
    // the TX mode, a legacy quirk this port doesn't have an RX-mode-tracking equivalent for at the
    // point this method runs. Using `mode` here (the TX mode being encoded) is a deliberate,
    // evidently-intended divergence from that quirk -- not a restatement of what `m_TW` actually is.
    internal const double FooterMaxTrailingCarrierMs = 500.0;

    // Main.cpp:7011: `mp->Write(WORD(SSTVSET.m_fTxNarrow ? 1900 : 1500), 300)` -- the FSK-ID-
    // configured footer branch's single tone duration.
    internal const double FskIdFooterToneDurationMs = 300.0;

    internal static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateFooterSegments(
        SstvModeDefinition mode, bool fskIdEnabled = false)
    {
        // Main.cpp:6997/7010-7011: this branch is selected purely on sys.m_TXFSKID -- independent of
        // narrow-mode-ness (which only picks the tone frequency within this branch) and independent
        // of whether a callsign is actually configured (that only gates packet emission, handled by
        // the caller -- see GenerateFrequencySegments).
        if (fskIdEnabled)
        {
            yield return (mode.NarrowModeCode is not null ? 1900 : 1500, FskIdFooterToneDurationMs);
            yield break;
        }

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
