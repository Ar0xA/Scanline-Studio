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
        double sampleRateOffsetHz = 0.0,
        bool txBpfEnabled = true,
        int txBpfTapCount = TxOutputBandpassFilter.DefaultTapCount,
        bool txLpfEnabled = false,
        double txLpfFrequencyHz = 2000.0,
        CancellationToken ct = default)
    {
        ValidateImageDimensions(mode, image);

        return EncodeAsyncCore(mode, image, stationId ?? StationIdTransmitOptions.None, sampleRateOffsetHz, txBpfEnabled, txBpfTapCount, txLpfEnabled, txLpfFrequencyHz, ct);
    }

    /// <summary>Resolves <see cref="SampleRate"/> + <paramref name="sampleRateOffsetHz"/> into the
    /// effective tone-generation rate, with a defensive floor -- a non-finite or non-positive
    /// result (a corrupted/hand-edited settings value that somehow reached this deep, or an
    /// absurdly large negative offset) would otherwise reach <c>Math.Sin</c> as NaN/Infinity
    /// samples WITH PTT KEYED (see <c>ISstvEncoder.EncodeAsync</c>'s own doc comment on this
    /// parameter). The real settings-boundary validation lives one layer up
    /// (<c>SstvSessionService</c>'s transmit-settings resolution, matching this codebase's own
    /// <c>CwToneFrequencyHz</c> precedent) -- this is a cheap last-resort guard, not the primary
    /// defense.</summary>
    private double ResolveEffectiveSampleRate(double sampleRateOffsetHz)
    {
        var effective = SampleRate + sampleRateOffsetHz;
        return double.IsFinite(effective) && effective > 0 ? effective : SampleRate;
    }

    /// <summary>Options stub backlog item 3: <c>CFQC::CalcLPF</c>::SetCount</c>'s TX-LPF sibling
    /// window-size formula, <c>CSSTVMOD::CalcFilter</c> (`sstv.cpp:2929`,
    /// <c>avgLPF.SetCount(int(SampFreq/m_lpffq + 0.5))</c>) -- rounds via <c>+0.5</c> BEFORE
    /// truncation, NOT <c>CFQC::CalcLPF</c>'s own bare-truncation formula (item 2,
    /// `ZeroCrossingFrequencyCounter.SetTuning`) -- a real, easy-to-miss difference between this
    /// port's own two <c>CSmooz</c> consumers, code-review round 1 finding: extracted into one shared
    /// method (was duplicated verbatim in <see cref="EncodeAsyncCore"/> and
    /// <see cref="RenderSegments"/>) specifically so this formula has exactly one place to test and
    /// one place to get right, after a review found NO test actually exercised the <c>+0.5</c> at a
    /// frequency where it changes the result (the chosen test frequency happened to make rounding and
    /// truncation agree).</summary>
    internal static int ResolveLpfWindowSize(double sampleRate, double lpfFrequencyHz) =>
        Math.Max(1, (int)(sampleRate / Math.Clamp(lpfFrequencyHz, 100.0, 3000.0) + 0.5));

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
        StationIdTransmitOptions? stationId = null,
        double sampleRateOffsetHz = 0.0)
    {
        ValidateImageDimensions(mode, image);

        var options = stationId ?? StationIdTransmitOptions.None;
        var effectiveSampleRate = ResolveEffectiveSampleRate(sampleRateOffsetHz);
        var lineEncoder = ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding);
        var idealSamplesSoFar = 0.0;
        foreach (var (_, durationMs) in GenerateFrequencySegments(mode, image, lineEncoder, options))
        {
            idealSamplesSoFar += durationMs / 1000.0 * effectiveSampleRate;
        }

        // Sound-file station ID (Main.cpp:7021-7025's `sys.m_CWID==2` -> OutputMMV): raw samples are
        // appended AFTER the whole tone/segment stream (docs/plans/sound-file-id-plan.md's variant
        // A', not interleaved into GenerateFrequencySegments at all), so their count is added here
        // directly rather than recomputed from a duration -- no floating-point rounding drift. Mutual
        // exclusivity with CwEnabled (legacy's own single-value sys.m_CWID tri-state) is enforced with
        // THIS EXACT expression at both this method and EncodeAsyncCore below -- must stay identical
        // at both sites so the estimate and the real encode can never disagree on whether a sound-file
        // block plays at all.
        var soundFile = options.CwEnabled ? null : options.SoundFileSamples;

        // Cast BEFORE adding the raw sample count, matching EncodeAsyncCore's own real emission
        // exactly (it emits (long)idealSamplesSoFar tone samples, then soundFile.Length raw ones) --
        // folding the raw count into idealSamplesSoFar before this cast is a DIFFERENT, not
        // guaranteed-equal computation (the two truncations can round across an integer boundary
        // differently).
        return (long)idealSamplesSoFar + (soundFile?.Length ?? 0);
    }

    /// <summary>Duration of the fixed leader-tone burst <see cref="GenerateFrequencySegments"/>
    /// unconditionally sends before every mode's VIS header (legacy's <c>TMmsstv::OutHEAD</c>,
    /// <c>m_VOX==0</c> case only -- legacy's real VOX feature, <c>m_VOX==1</c>, a user-editable
    /// comma-separated frequency/duration tone sequence that replaces this burst (used to prime an
    /// external VOX-activated transmitter/relay), is a different, self-contained TX-audio-generation
    /// feature this port deliberately doesn't implement (removed by direct user request, 2026-08-28,
    /// see `docs/removed-features.md`'s "VOX leader-tone priming" entry); see
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
        double sampleRateOffsetHz,
        bool txBpfEnabled,
        int txBpfTapCount,
        bool txLpfEnabled,
        double txLpfFrequencyHz,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var lineEncoder = ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding);
        var phase = 0.0;

        // Round-2 plan-review finding: the effective (offset-corrected) rate feeds ONLY the
        // accumulator/phaseIncrement below -- resolved once, here, matching legacy's own
        // once-per-transmission re-apply (Main.cpp:959/7948, never mid-transmission).
        var effectiveSampleRate = ResolveEffectiveSampleRate(sampleRateOffsetHz);

        // ultracode audit finding #26: legacy's TX output bandpass filter is applied to EVERY emitted
        // sample, as the last step of CSSTVMOD::Do() -- constructed locally, not as
        // a field, so every EncodeAsync call gets fresh (zeroed) filter state, matching legacy's own
        // per-transmission InitTXBuf -> m_BPF.Clear() reset (this encoder is a DI singleton; a
        // ctor-field filter would leak state across calls). See TxOutputBandpassFilter's own doc
        // comment for why this can't just reuse SearchBandpassFilter. Deliberately the NOMINAL
        // SampleRate, not effectiveSampleRate -- legacy builds this same filter from the nominal
        // rate too, never the TX-offset-corrected one (sstv.cpp:2768/2771/2923/2926), a Clock
        // calibration plan-review finding (round 1). Options stub backlog item 3: the on/off gate
        // (legacy's m_bpf) now lives at the ProcessSample call site below, not inside the filter
        // class -- matches legacy's own `if(m_bpf) d = m_BPF.Do(d);` shape (sstv.cpp:2914) exactly,
        // the delay line is never advanced at all when off, not merely bypassed post-construction.
        var bandpassFilter = new TxOutputBandpassFilter(SampleRate, txBpfTapCount);

        // Options stub backlog item 3 (docs/plans/options-stub-item3-tx-bpf-lpf-plan.md): legacy's
        // TX LPF (CSSTVMOD::avgLPF, a CSmooz moving average -- the exact same class already ported
        // as MovingAverage) smooths the discrete per-segment target FREQUENCY, applied PRE-VCO, once
        // per audio sample (sstv.cpp:2869, inside the m_Cnt-active/f>0 branch only). Fresh-per-call
        // here, NOT matching legacy's own InitTXBuf-clears-m_BPF-only precedent (avgLPF genuinely
        // PERSISTS across legacy transmissions, only SetCount clears it, sstv.cpp:2827,2929) -- a
        // deliberate, documented DIVERGENCE (simpler, deterministic, practically indistinguishable
        // after one window's worth of samples), not a faithfully-ported behavior. SetCount uses the
        // NOMINAL SampleRate (matching CalcFilter's own bare SampFreq, sstv.cpp:2929, and legacy's
        // own separate VCO-rate re-pointing at the offset-corrected rate, Main.cpp:959/7948) --
        // rounds via +0.5 before truncation, NOT CFQC::CalcLPF's bare-truncation formula (item 2).
        var lpfAverage = new MovingAverage(ResolveLpfWindowSize(SampleRate, txLpfFrequencyHz));

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

            idealSamplesSoFar += durationMs / 1000.0 * effectiveSampleRate;
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
                    // segment -- do not special-case it out of the filter call. Options stub backlog
                    // item 3: the TX LPF moving average is ALSO skipped here (sstv.cpp:2867's
                    // `if(f>0)` gates both the VCO advance AND avgLPF.Avg together) -- its history
                    // holds through the gap and resumes smoothing, not resets, once a real tone
                    // follows, matching legacy exactly.
                    sample = 0.0;
                }
                else
                {
                    // Options stub backlog item 3: legacy re-reads and re-smooths this SAME
                    // per-segment-constant frequencyHz on EVERY audio sample (m_TXBuf holds one
                    // discrete frequency code per requested duration, Do() reads+smooths it once per
                    // call, sstv.cpp:2866-2870) -- avgLPF converges toward each new segment's target
                    // like a glide filter rather than an instant jump. phaseIncrement is therefore
                    // now derived PER SAMPLE from the (optionally smoothed) frequency, not once per
                    // segment. Smoothing the raw Hz value directly (not legacy's normalized
                    // (f-1100)/1200) is algebraically identical, not a divergence -- CSmooz/
                    // MovingAverage is a linear operator (mean(a*x+b) = a*mean(x)+b) and legacy's VCO
                    // conversion is itself affine, so a boxcar mean commutes through it exactly.
                    var smoothedFrequencyHz = txLpfEnabled ? lpfAverage.Add(frequencyHz) : frequencyHz;
                    var phaseIncrement = 2 * Math.PI * smoothedFrequencyHz / effectiveSampleRate;

                    phase += phaseIncrement;
                    if (phase >= 2 * Math.PI)
                    {
                        phase -= 2 * Math.PI;
                    }

                    sample = Math.Sin(phase);
                }

                // Filtered in double, narrowed to float only here -- legacy's whole chain
                // (CSSTVMOD::Do's `d`) is double; narrowing before filtering would lose precision
                // the filter itself doesn't need to lose. Options stub backlog item 3: the bandpass
                // filter's own on/off gate (legacy's m_bpf) lives here, at the call site, not inside
                // the filter class -- when off, the delay line is never advanced at all, matching
                // legacy's own `if(m_bpf) d = m_BPF.Do(d);` shape (sstv.cpp:2914) exactly.
                yield return (float)(txBpfEnabled ? bandpassFilter.ProcessSample(sample) : sample);
            }
        }

        // Sound-file station ID (Main.cpp:7021-7025's `sys.m_CWID==2` -> OutputMMV): NOT interleaved
        // into GenerateFrequencySegments at all (docs/plans/sound-file-id-plan.md's variant A' --
        // simpler than a reserved sentinel frequency value, and avoids the RenderSegments test-seam
        // exposure a sentinel would have). Legacy's own row-playback branch
        // (sstv.cpp:2903-2907, `else if(m_RowCnt)`) is a separate `else if` from the tone/VCO branch
        // entirely, and is always the LAST thing SendSSTV emits (sys.m_CWID==1/==2 are mutually
        // exclusive, Main.cpp:7021-7025) -- so a plain second loop, placed after every tone segment
        // has already been emitted, reproduces the same effective ordering with no branch inside the
        // hot per-segment loop above. THIS EXACT expression must stay identical to
        // EstimateSampleCount's own copy -- CW wins if a caller somehow set both fields.
        var soundFile = stationId.CwEnabled ? null : stationId.SoundFileSamples;
        if (soundFile is { Length: > 0 })
        {
            var samples = soundFile.Value;
            for (var i = 0; i < samples.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                // No VCO advance (phase is untouched) and no TX LPF smoothing -- matches legacy's
                // row-playback branch being outside the `f>0` tone branch entirely
                // (sstv.cpp:2867-2870 gates avgLPF the same way it gates the VCO). The output
                // bandpass filter DOES still run, matching legacy's own shared `d=m_BPF.Do(d)` call
                // (sstv.cpp:2914) -- the only stage row playback and tone generation actually share.
                // `.Span[i]` is a transient rvalue, never a declared local -- a ReadOnlySpan<float>
                // local can't be declared inside this async iterator method (CS4012).
                var raw = samples.Span[i];
                yield return (float)(txBpfEnabled ? bandpassFilter.ProcessSample(raw) : raw);
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
    //
    // Clock calibration (stub survey Tier 3), revised decision: widened int -> double so this same
    // test-only seam can also verify the sampleRateOffsetHz behavior directly (the accumulator/
    // phaseIncrement math here is IDENTICAL to EncodeAsyncCore's, so a fractional effective rate
    // exercises the exact same code shape a real offset-corrected transmission would). Purely
    // test-only, no external API stability contract -- every existing caller passes a plain `int`
    // literal/constant, which widens to `double` implicitly with no source changes needed anywhere.
    internal static IEnumerable<float> RenderSegments(
        IEnumerable<(double FrequencyHz, double DurationMs)> segments,
        double sampleRate,
        bool applyFilter = true,
        int tapCount = TxOutputBandpassFilter.DefaultTapCount,
        bool applyLpf = false,
        double lpfFrequencyHz = 2000.0)
    {
        var phase = 0.0;
        var bandpassFilter = new TxOutputBandpassFilter(sampleRate, tapCount);
        var lpfAverage = new MovingAverage(ResolveLpfWindowSize(sampleRate, lpfFrequencyHz));
        var idealSamplesSoFar = 0.0;
        var emittedSamples = 0L;

        foreach (var (frequencyHz, durationMs) in segments)
        {
            idealSamplesSoFar += durationMs / 1000.0 * sampleRate;
            var targetEmitted = (long)idealSamplesSoFar;
            var samplesToEmit = targetEmitted - emittedSamples;
            emittedSamples = targetEmitted;

            for (var i = 0; i < samplesToEmit; i++)
            {
                double sample;
                if (frequencyHz <= 0)
                {
                    sample = 0.0;
                }
                else
                {
                    var smoothedFrequencyHz = applyLpf ? lpfAverage.Add(frequencyHz) : frequencyHz;
                    var phaseIncrement = 2 * Math.PI * smoothedFrequencyHz / sampleRate;

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
    // PlayWithPttAsync now keys/un-keys PTT for every TX call. A LATER version of this comment then
    // claimed `sys.m_VOX` was "a hardware Voice/Voltage-Operated-eXchange auto-keying MODE" -- also
    // wrong (self-caught, corrected 2026-08-28): it is a self-contained TX-audio-generation toggle
    // (a user-editable tone sequence replacing the fixed leader burst, to prime an external VOX-
    // activated transmitter/relay), not a hardware PTT-triggering concept at all. VOX itself was
    // removed by direct user request (2026-08-28, see `docs/removed-features.md`'s "VOX leader-tone
    // priming" entry) after being told the corrected mechanism -- this hardcoded always-off arm
    // matches legacy's own real default (`Main.cpp:822`) and needs no further consideration.
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
