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

    // T1-3 (production_audit.md): batch size EncodeBatchedAsyncCore accumulates into before
    // yielding. Matches both real production consumers' own existing local-buffer size
    // (SstvSessionService.PumpToPlaybackAsync, the TX loopback self-test), which is purely
    // coincidental convenience, not a contract -- ISstvEncoder.EncodeBatchedAsync's own doc comment
    // explicitly forbids callers from assuming any specific batch size.
    private const int EncodeBatchSize = 4096;

    // Milestone-audit Phase 3 SHOULD finding (spec/14-roadmap.md): without this guard, a too-small
    // image throws IndexOutOfRangeException from deep inside a scanline encoder's own pixel-index
    // loop (e.g. RgbSequentialScanlineEncoder.cs's `image.GetScanline(lineIndex)[x]` for x up to
    // mode.ImageWidth-1) -- after the header and part of a line have already been yielded to the
    // sink, leaving no clean error state -- and a too-large one silently crops with no signal at all.
    // Legacy is structurally immune (its Line* functions read width straight from the bitmap itself,
    // Main.cpp:6692 etc.) -- this port's equivalent contract is "the image IS sized to the mode,"
    // enforced here instead of assumed by every caller.
    //
    // Deliberately a plain (non-iterator) method delegating to EncodeBatchedAsyncCore (T1-3:
    // formerly EncodeAsyncCore, before it became the batched producer -- see that method's own doc
    // comment), not `async IAsyncEnumerable<float> EncodeAsync` directly: a C# iterator method's
    // body doesn't run until the FIRST `MoveNextAsync()` call, so a guard written inside the
    // iterator itself would still defer the throw to whenever the caller starts enumerating --
    // better than throwing after partial output, but not as clean as throwing synchronously at the
    // `EncodeAsync()` call site itself, which this split achieves.
    //
    // T1-3: derived from EncodeBatchedAsyncCore via FlattenBatches, not a second implementation of
    // the DSP synthesis -- see EncodeBatchedAsyncCore's own doc comment for why (avoids exactly the
    // "two copies of the same math silently diverge" bug class this project has been burned by
    // before, e.g. the Scottie TX-channel-order incident, CLAUDE.md's own worked example).
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

        // CA2016 false positive, deliberately suppressed: FlattenBatches' own ct parameter must be
        // left UNBOUND here, not forwarded -- see FlattenBatches' own doc comment. Its
        // [EnumeratorCancellation] attribute means leaving the argument at its default lets a
        // caller's own `.WithCancellation(token)` on THIS method's returned enumerable flow directly
        // into the flatten step; explicitly forwarding `ct` here (what the analyzer suggests) would
        // BIND the parameter, silently defeating that -- the exact bug this shape exists to avoid.
#pragma warning disable CA2016
        return FlattenBatches(EncodeBatchedAsyncCore(mode, image, stationId ?? StationIdTransmitOptions.None, sampleRateOffsetHz, txBpfEnabled, txBpfTapCount, txLpfEnabled, txLpfFrequencyHz, 0.0, ct));
#pragma warning restore CA2016
    }

    /// <summary>Encodes with a simulated receiver tuning error, for the impairment bench only.
    ///
    /// Without this the bench encodes perfectly on-frequency signals, so AFC has nothing to correct
    /// and reports exactly zero whatever happens to it. That makes the project's "zero degradation
    /// across all 43 modes" gate vacuous for anything AFC-related: it cannot distinguish a change
    /// that helped, one that did nothing, and one that broke frequency correction outright.
    ///
    /// Not on <see cref="ISstvEncoder"/> deliberately -- a transmitter has no reason to mistune
    /// itself, and this must not become reachable from production.</summary>
    internal IAsyncEnumerable<float> EncodeWithCarrierOffsetAsync(
        SstvModeDefinition mode,
        IImageSource image,
        double carrierOffsetHz,
        CancellationToken ct = default)
    {
        ValidateImageDimensions(mode, image);
#pragma warning disable CA2016
        return FlattenBatches(EncodeBatchedAsyncCore(
            mode, image, StationIdTransmitOptions.None, 0.0, txBpfEnabled: true,
            TxOutputBandpassFilter.DefaultTapCount, txLpfEnabled: false, 2000.0, carrierOffsetHz, ct));
#pragma warning restore CA2016
    }

    /// <summary>See <see cref="ISstvEncoder.EncodeBatchedAsync"/>'s own doc comment for the full
    /// contract. Same synchronous-validation split as <see cref="EncodeAsync"/>, same reasoning.</summary>
    public IAsyncEnumerable<ReadOnlyMemory<float>> EncodeBatchedAsync(
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

        return EncodeBatchedAsyncCore(mode, image, stationId ?? StationIdTransmitOptions.None, sampleRateOffsetHz, txBpfEnabled, txBpfTapCount, txLpfEnabled, txLpfFrequencyHz, 0.0, ct);
    }

    // T1-3 (production_audit.md), plan-review finding: EncodeAsync's own per-float behavior is
    // DERIVED from EncodeBatchedAsyncCore below via this flatten, specifically so there is only ONE
    // copy of the DSP synthesis math in this class. Needs ITS OWN [EnumeratorCancellation], not just
    // relying on the ct already baked into `batches`' own production -- without it, a caller doing
    // `EncodeAsync(mode, image).WithCancellation(token)` (no ct positional/named argument at all)
    // silently drops that token on the floor, since GetAsyncEnumerator(token) would have nothing
    // marked to receive it.
    private static async IAsyncEnumerable<float> FlattenBatches(
        IAsyncEnumerable<ReadOnlyMemory<float>> batches,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var batch in batches.WithCancellation(ct).ConfigureAwait(false))
        {
            for (var i = 0; i < batch.Length; i++)
            {
                yield return batch.Span[i];
            }
        }
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
    /// port's own two <c>CSmooz</c> consumers. Its only caller is <see cref="TxToneSynthesizer"/>.
    /// It is tested directly at a frequency where the <c>+0.5</c> changes the result, because at
    /// many frequencies rounding and truncation agree.</summary>
    internal static int ResolveLpfWindowSize(double sampleRate, double lpfFrequencyHz) =>
        Math.Max(1, (int)(sampleRate / Math.Clamp(lpfFrequencyHz, 100.0, 3000.0) + 0.5));

    /// <summary>Spec/18-path-to-1.0.md Medium item: "No TX send-progress feedback during transmit."
    /// Replicates <see cref="TxToneSynthesizer.AdvanceSegment"/>'s own running-accumulator expression verbatim
    /// (same per-segment formula, same operand order, same summation order) rather than summing
    /// <c>DurationMs</c> first and multiplying once -- floating-point addition is not associative,
    /// so those two forms are only guaranteed to agree in exact arithmetic; over the ~hundreds of
    /// thousands of segments a full image produces, they can diverge by enough to flip the final
    /// truncation by one sample on some (mode, image, stationId) combination even though a
    /// single-mode spot check would show exact agreement. This is a real traversal of every segment
    /// (same iterators <see cref="EncodeBatchedAsyncCore"/> walks), not O(1) metadata math -- no tone
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
        // THIS EXACT expression at both this method and EncodeBatchedAsyncCore below -- must stay identical
        // at both sites so the estimate and the real encode can never disagree on whether a sound-file
        // block plays at all.
        var soundFile = options.CwEnabled ? null : options.SoundFileSamples;

        // Cast BEFORE adding the raw sample count, matching EncodeBatchedAsyncCore's own real emission
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

    /// <summary>The sole production TX synthesis loop, batching output into fixed-size chunks;
    /// EncodeAsync derives its per-float stream from this via FlattenBatches. The per-sample DSP math
    /// lives in <see cref="TxToneSynthesizer"/>; cancellation and batch flushing stay here.</summary>
    /// <param name="carrierOffsetHz">Receiver tuning error, in Hz, added to every emitted tone. Models
    /// an operator whose radio is mistuned: a BFO offset shifts every audio tone by the SAME amount
    /// with timing unchanged. Distinct from <c>sampleRateOffsetHz</c>, which is a CLOCK error that
    /// shifts timing and scales frequencies proportionally.
    ///
    /// Exists because the impairment bench otherwise encodes perfectly on-frequency signals, so AFC
    /// has nothing to correct and measures exactly zero however it behaves -- "helped", "did nothing"
    /// and "broke it" become indistinguishable. Not reachable through <see cref="ISstvEncoder"/>;
    /// test seam only, and zero by default so no production path changes.</param>
    private async IAsyncEnumerable<ReadOnlyMemory<float>> EncodeBatchedAsyncCore(
        SstvModeDefinition mode,
        IImageSource image,
        StationIdTransmitOptions stationId,
        double sampleRateOffsetHz,
        bool txBpfEnabled,
        int txBpfTapCount,
        bool txLpfEnabled,
        double txLpfFrequencyHz,
        double carrierOffsetHz,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var lineEncoder = ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding);

        // The effective (offset-corrected) rate feeds ONLY the accumulator and the VCO phase step --
        // resolved once, here, matching legacy's own once-per-transmission re-apply
        // (Main.cpp:959/7948, never mid-transmission). The BPF and LPF window use the nominal rate.
        var effectiveSampleRate = ResolveEffectiveSampleRate(sampleRateOffsetHz);

        // A local, never a field: this encoder is a DI singleton, so per-call construction is what
        // gives every transmission fresh (zeroed) filter state, matching legacy's per-transmission
        // InitTXBuf -> m_BPF.Clear() reset.
        var synthesizer = new TxToneSynthesizer(
            SampleRate, effectiveSampleRate, carrierOffsetHz, txBpfEnabled, txBpfTapCount, txLpfEnabled, txLpfFrequencyHz);

        // T1-3 (production_audit.md): shared across BOTH loops below (tone segments, then the
        // sound-file/CW-ID row-playback tail) so a batch boundary can straddle the two without ever
        // flushing early just because one loop ended -- every non-final batch is exactly
        // EncodeBatchSize, the final one is whatever's left (mirrors the "if (count > 0) flush" tail
        // shape both real production consumers already use on their own side). A FRESH array is
        // allocated on every full-batch flush, deliberately not reused across yields -- see
        // ISstvEncoder.EncodeBatchedAsync's own doc comment for the "don't retain past your
        // enumeration step" contract this choice makes strictly safer than the minimum the contract
        // requires. Do not switch this to a reused buffer without re-checking every consumer this
        // stricter guarantee currently lets skip its own defensive copy.
        var buffer = new float[EncodeBatchSize];
        var count = 0;

        foreach (var (frequencyHz, durationMs) in GenerateFrequencySegments(mode, image, lineEncoder, stationId))
        {
            ct.ThrowIfCancellationRequested();

            var samplesToEmit = synthesizer.AdvanceSegment(durationMs);
            for (var i = 0; i < samplesToEmit; i++)
            {
                buffer[count++] = synthesizer.NextToneSample(frequencyHz);
                if (count == EncodeBatchSize)
                {
                    yield return buffer;
                    buffer = new float[EncodeBatchSize];
                    count = 0;
                }
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
                buffer[count++] = synthesizer.FilterRaw(samples.Span[i]);
                if (count == EncodeBatchSize)
                {
                    yield return buffer;
                    buffer = new float[EncodeBatchSize];
                    count = 0;
                }
            }
        }

        // T1-3 (production_audit.md): final partial flush -- never an empty batch (ISstvEncoder.
        // EncodeBatchedAsync's own contract), so only flush if something is actually pending.
        if (count > 0)
        {
            yield return buffer.AsMemory(0, count);
        }

        await Task.CompletedTask;
    }

    /// <summary>Test-only seam: renders an arbitrary segment sequence through the same
    /// <see cref="TxToneSynthesizer"/> that <see cref="EncodeBatchedAsyncCore"/> uses, as a plain
    /// per-sample <c>IEnumerable&lt;float&gt;</c> with no mode, image, cancellation or batching. RX
    /// tests use it as a signal generator. It uses ONE rate for everything (nominal = effective =
    /// <paramref name="sampleRate"/>, no carrier offset), so it cannot model a sample-rate offset:
    /// production builds the BPF and LPF window at the nominal rate but steps the accumulator and VCO
    /// at the effective rate. Passing a fractional rate here therefore does NOT reproduce an
    /// offset-corrected transmission; it has no sound-file tail either.</summary>
    internal static IEnumerable<float> RenderSegments(
        IEnumerable<(double FrequencyHz, double DurationMs)> segments,
        double sampleRate,
        bool applyFilter = true,
        int tapCount = TxOutputBandpassFilter.DefaultTapCount,
        bool applyLpf = false,
        double lpfFrequencyHz = 2000.0)
    {
        var synthesizer = new TxToneSynthesizer(
            sampleRate, sampleRate, carrierOffsetHz: 0.0, applyFilter, tapCount, applyLpf, lpfFrequencyHz);

        foreach (var (frequencyHz, durationMs) in segments)
        {
            var samplesToEmit = synthesizer.AdvanceSegment(durationMs);
            for (var i = 0; i < samplesToEmit; i++)
            {
                yield return synthesizer.NextToneSample(frequencyHz);
            }
        }
    }

    internal static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateFrequencySegments(
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
