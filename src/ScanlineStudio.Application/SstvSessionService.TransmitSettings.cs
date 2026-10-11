using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Cw;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

public sealed partial class SstvSessionService
{
    /// <summary>Resolves the CW-ID/FSK station-ID settings + operator identity into one fully-formed
    /// <see cref="StationIdTransmitOptions"/> -- see that type's own doc comment for why this
    /// resolution lives here (Application layer) rather than inside the pure-DSP encoder. Settings-
    /// boundary validation for WPM/tone-frequency lives here too (Phase 1 code-review finding): a
    /// corrupted/hand-edited settings.json with WPM &lt;= 0 would otherwise make
    /// <c>CwMorseGenerator.MillisecondsPerDotFromWpm</c> return Infinity/negative -- this falls back
    /// to the documented default instead of letting a bad value reach the generator or abort an
    /// in-flight transmission. Also the read-only preview <see cref="ISstvSessionService.GetStationIdTransmitOptionsAsync"/>
    /// exposes (see that member's own doc comment) -- <see cref="TransmitAsync"/> and that preview
    /// path share this exact same resolution, so they can never disagree with each other.</summary>
    public async Task<StationIdTransmitOptions> GetStationIdTransmitOptionsAsync(CancellationToken ct = default)
        // resolveSoundFile: false -- this is a read-only preview (TxControlsPaneViewModel's
        // Identification summary, refreshed on every Options-dialog close), never a real
        // transmission. A full file-read/resample/IIR-filter pass here would run on every dialog
        // close for no reason -- StationIdTransmitOptions.SoundFileIdEnabled (a cheap, I/O-free flag)
        // is what this preview should read instead of SoundFileSamples for a "configured" summary.
        => (await ResolveTransmitSettingsAsync(resolveSoundFile: false, ct).ConfigureAwait(false)).StationId;

    private async Task<(StationIdTransmitOptions StationId, double SampleRateOffsetHz, bool TxBpfEnabled, int TxBpfTapCount, bool TxLpfEnabled, double TxLpfFrequencyHz)> ResolveTransmitSettingsAsync(bool resolveSoundFile, CancellationToken ct)
    {
        // Round-15 finding (discovered while testing finding 3, not itself in the auditor's report):
        // same unbounded-external-read shape as ResolveDeviceAsync/GetTxVolumePercentAsync/
        // LoadAudioSettingsAsync inside PlayWithPttAsync -- but reached from TransmitAsync's own
        // preamble, BEFORE PlayWithPttAsync (and its _transmitInFlight guard) is ever entered. Lower
        // severity than finding 3 (a hang here does NOT strand _transmitInFlight, since it hasn't
        // been acquired yet -- a concurrent second TransmitAsync/TuneAsync call is unaffected), but
        // still a real unbounded wait on a settings read (e.g. a config file on a hung network mount)
        // with no caller-visible way to bound it on the TuneAsync-with-no-ct production path. Same
        // fix, same reasoning.
        //
        // Round-18 finding 4 (round-17's own deferral (a), now fixed): WaitAsync alone cannot bound
        // this -- JsonSettingsStore.LoadAsync does blocking File.Exists/File.OpenRead BEFORE its own
        // first `await` (this file's own EstimateSampleCount comment already states this exact fact,
        // just never connected it to the WaitAsync bounds above), so on a hung network-mounted
        // settings path the call doesn't even return a genuinely-pending Task for WaitAsync to race --
        // the calling thread blocks synchronously before WaitAsync is ever reached. Task.Run offloads
        // that synchronous prefix onto a pool thread, so the awaited Task really is pending
        // immediately and WaitAsync's bound becomes real. Cost: a wedged mount leaks one abandoned
        // pool thread per call using this pattern -- bounded and slow-growing in practice, since this
        // path (unlike PlayWithPttAsync's own) isn't behind _transmitInFlight.
        var appSettings = await Task.Run(() => _settingsStore.LoadAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
        var stationIdSettings = appSettings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings)
            ?? new StationIdSettings();
        var audioSettings = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
        var operatorSettings = appSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
            ?? new OperatorSettings();

        // Main.cpp:6969's !sys.m_CWIDText.IsEmpty() -- checked on the RAW (pre-macro) text, matching
        // legacy exactly (a macro token that resolves to empty would still fire in legacy, since the
        // gate never re-checks after MacroText expansion).
        var cwEnabled = stationIdSettings.CwIdMode == CwIdMode.Cw && !string.IsNullOrEmpty(stationIdSettings.CwText);

        // Tier B audit finding (Area 3, matches round 16's TxVolumePercent / round 18's TuneAsync
        // precedent): this doc comment's own "settings-boundary validation lives here" claim used to
        // check only the lower bound (`> 0`). A corrupted/hand-edited settings.json could still reach
        // the transmitter WITH PTT KEYED: CwWpm=1 -> MillisecondsPerDotFromWpm(1)=1110ms/dot, minutes
        // of keyed CW after every image with none of TuneAsync's own duration backstop on this path;
        // CwToneFrequencyHz>=24000 (this file's encoder Nyquist) aliases to an arbitrary on-air tone,
        // and +Infinity would reach Math.Sin as NaN samples (NaN itself already falls through `> 0`,
        // since a NaN comparison is always false). Bounded to the Options dialog's own product-decided
        // legitimate range (OptionsWindowView.axaml's CwWpm/CwToneFrequencyHz NumericUpDown Minimum/
        // Maximum) rather than an arbitrary wider one -- anything outside it is definitionally the
        // "corrupted settings" case this method's own doc comment already promises to fall back from.
        var wpm = stationIdSettings.CwWpm is >= 10 and <= 50
            ? stationIdSettings.CwWpm.Value
            : StationIdSettings.DefaultCwWpm;
        var toneFrequencyHz = stationIdSettings.CwToneFrequencyHz is >= 100 and <= 3000
            ? stationIdSettings.CwToneFrequencyHz.Value
            : StationIdSettings.DefaultCwToneFrequencyHz;
        var nrRstEnabled = stationIdSettings.NrRstEnabled ?? StationIdSettings.DefaultNrRstEnabled;

        // Auditor code-review finding on Phase 4 (real, low-severity, and refined in round 2): legacy
        // caps the MACRO-RESOLVED CW-ID text at 77 chars for plain (non-macro) literal text --
        // `MacroText`'s own break condition (`Main.cpp:10829`, `if (n >= (size-1)) break;` with
        // `size = sizeof(bf)-2 = 78`) stops once `n` reaches 77, not 78; `sizeof(bf)-2` is the buffer
        // SIZE passed in, not the actual max character count written. A macro token can overshoot
        // this (`n += l` for a multi-char expansion checked only AFTER appending) -- legacy itself can
        // write past `bf[80]` in that case, a real legacy buffer bug this port has no reason to
        // reproduce; 77 is deliberately chosen as the exact literal-text limit, not an attempt to
        // replicate the macro-overshoot case. An uncapped resolved text doesn't corrupt anything here
        // either way (CwMorseGenerator has no buffer to overrun), just runs a longer Morse tail than
        // legacy would have sent for the same configured literal text -- capped to match legacy's
        // actual on-air behavior instead of silently diverging.
        const int maxCwResolvedTextLength = 77;
        var cwResolvedText = cwEnabled ? _macroTextResolver.Resolve(stationIdSettings.CwText!, operatorSettings) : string.Empty;
        if (cwResolvedText.Length > maxCwResolvedTextLength)
        {
            cwResolvedText = cwResolvedText[..maxCwResolvedTextLength];
        }

        // Main.cpp:7021-7025: sys.m_CWID is a single-value tri-state -- `cwEnabled` above already
        // checks `== CwIdMode.Cw` specifically (not merely `!= Off`), so this check is naturally
        // mutually exclusive with it already, no explicit if/else-if restructure needed.
        var soundFileIdEnabled = stationIdSettings.CwIdMode == CwIdMode.SoundFile && !string.IsNullOrEmpty(stationIdSettings.SoundFileMmvPath);

        // Cheap, I/O-free even when resolveSoundFile is false -- see SoundFileIdEnabled's own doc
        // comment for why the read-only preview path needs this but must NOT trigger the real file
        // read below.
        ReadOnlyMemory<float>? soundFileSamples = null;
        if (resolveSoundFile && soundFileIdEnabled)
        {
            var mmvPath = stationIdSettings.SoundFileMmvPath!;
            // The transmission lease is already held. Capture the rate before scheduling so
            // even an abandoned worker cannot read a later encoder configuration.
            var targetSampleRate = _encoder.SampleRate;
            var resolver = SoundFileSamplesResolverForTests ?? TryResolveSoundFileSamples;
            var resolveTask = Task.Run(() => resolver(mmvPath, targetSampleRate), ct);
            float[]? resolvedSamples;
            try
            {
                resolvedSamples = await resolveTask.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _ = resolveTask.ContinueWith(
                    task => SafeLog(() => Log.SoundFileIdReadFailed(_logger, mmvPath, task.Exception!)),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }

            // Deliberately an `if`, NOT a ternary ("soundFileSamples = resolvedSamples is null ? null
            // : new ReadOnlyMemory<float>(resolvedSamples)") -- confirmed by a real failing test, not
            // a hypothetical: ReadOnlyMemory<float> has an implicit operator FROM float[] (including a
            // null array), so the C# conditional-expression common-type algorithm resolves the ternary
            // through THAT conversion path even for the bare `null` literal branch, producing
            // `ReadOnlyMemory<float>` (non-nullable, default/empty) as the ternary's type -- which then
            // silently boxes into HasValue=true, Length=0 on assignment to this ReadOnlyMemory<float>?
            // field, not the "unconfigured" null this method's whole contract depends on. Leaving
            // soundFileSamples at its already-null default and only assigning in the non-null case
            // sidesteps the whole conversion-path ambiguity.
            if (resolvedSamples is not null)
            {
                soundFileSamples = new ReadOnlyMemory<float>(resolvedSamples);
            }
        }

        var stationId = new StationIdTransmitOptions
        {
            CwEnabled = cwEnabled,
            CwResolvedText = cwResolvedText,
            CwToneFrequencyHz = toneFrequencyHz,
            CwWpm = wpm,
            FskIdEnabled = stationIdSettings.FskIdTxEnabled,
            Callsign = operatorSettings.Callsign ?? string.Empty,
            NrRstText = nrRstEnabled ? stationIdSettings.NrRstText : null,
            SoundFileIdEnabled = soundFileIdEnabled,
            SoundFileSamples = soundFileSamples,
        };

        // Settings-boundary validation, same shape/precedent as CwToneFrequencyHz above: a
        // corrupted/hand-edited settings.json outside legacy's own accepted +/-1500 Hz manual range
        // (Option.cpp:1142-1146) falls back to 0.0 (no correction) rather than reaching the encoder.
        // The `is >= x and <= y` pattern is NaN-safe by construction (a NaN comparison is always
        // false in a range pattern, same as the `>` bug CwToneFrequencyHz's own history already
        // found and fixed) -- catches the Clock calibration round-2 plan-review's NaN/Infinity
        // finding without a separate double.IsFinite check here.
        var sampleRateOffsetHz = audioSettings.TxSampleRateOffsetHz is >= -1500.0 and <= 1500.0
            ? audioSettings.TxSampleRateOffsetHz
            : 0.0;

        // Options stub backlog item 3 (docs/plans/options-stub-item3-tx-bpf-lpf-plan.md):
        // TxBpfEnabled/TxLpfEnabled default true/false when absent (legacy's own real CSSTVMOD ctor
        // defaults, sstv.cpp:2759-2760). TxBpfTapCount/TxLpfFrequencyHz clamped to legacy's own real
        // Save-handler ranges (Option.cpp:452-459) -- tap count [2,512] rounded to the nearest EVEN
        // value (see TxOutputBandpassFilter's own doc comment for why odd is legacy UB), LPF
        // frequency [100,3000] -- same settings-boundary-validation-lives-here precedent as
        // sampleRateOffsetHz immediately above, not a separate Resolve() method (this section has
        // none).
        var txBpfEnabled = audioSettings.TxBpfEnabled ?? true;
        // TxOutputBandpassFilter's own ClampTapCount is internal to ScanlineStudio.Core.Sstv, not
        // visible here -- this clamp is trivial enough to duplicate independently at this layer,
        // same precedent as SstvDecoderSettings.Resolve()'s own clamps duplicating decoder-side ones.
        var txBpfTapCount = audioSettings.TxBpfTapCount is { } tap ? Math.Clamp(tap, 2, 512) : 24;
        txBpfTapCount = txBpfTapCount % 2 == 0 ? txBpfTapCount : txBpfTapCount - 1;
        var txLpfEnabled = audioSettings.TxLpfEnabled ?? false;
        var txLpfFrequencyHz = audioSettings.TxLpfFrequencyHz is >= 100.0 and <= 3000.0
            ? audioSettings.TxLpfFrequencyHz.Value
            : 2000.0;

        return (stationId, sampleRateOffsetHz, txBpfEnabled, txBpfTapCount, txLpfEnabled, txLpfFrequencyHz);
    }

    /// <summary>Shared parse core for both <see cref="TryResolveSoundFileSamples"/> (the real
    /// TX-time resolution) and <see cref="ValidateStationIdSoundFileAsync"/> (the Options dialog's
    /// pre-Save validation, ui_transition_plan.md step 8/T2-2) -- exists/size-cap/header-parse are
    /// identical for both callers; only what happens to the parsed payload differs (resample for TX,
    /// just a duration calc for validation). Never throws -- see this class's own doc comment on the
    /// original single-purpose method this was extracted from for the exact exception-widening
    /// history.</summary>
    private readonly record struct SoundFileParseOutcome(byte[]? FileBytes, MmvSoundFile.Header Header, SoundFileIdValidationFailure Failure);

    private SoundFileParseOutcome TryParseSoundFile(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                Log.SoundFileIdMissing(_logger, path);
                return new SoundFileParseOutcome(null, default, SoundFileIdValidationFailure.FileNotFound);
            }

            if (fileInfo.Length > MaxSoundFileIdBytes)
            {
                Log.SoundFileIdTooLarge(_logger, path, MaxSoundFileIdBytes);
                return new SoundFileParseOutcome(null, default, SoundFileIdValidationFailure.FileTooLarge);
            }

            var fileBytes = File.ReadAllBytes(path);
            var header = MmvSoundFile.ParseHeader(fileBytes);
            if (header is null)
            {
                Log.SoundFileIdUnplayableHeader(_logger, path);
                return new SoundFileParseOutcome(null, default, SoundFileIdValidationFailure.UnplayableHeader);
            }

            return new SoundFileParseOutcome(fileBytes, header.Value, SoundFileIdValidationFailure.None);
        }
        // Code-review finding: the ORIGINAL catch clause covered only IOException/
        // UnauthorizedAccessException, and only around the FileInfo/ReadAllBytes calls -- leaving
        // MmvSoundFile.Resample's own call outside any try at all (a real crash bug, since fixed,
        // used to be reachable through it) and missing exception types a free-text path TextBox can
        // realistically produce (e.g. a trailing-space/invalid-character path throwing
        // ArgumentException on some platforms). Widened to cover the whole method body and every
        // exception type a bad user-supplied path or file could plausibly throw -- this method's own
        // contract ("never throws out of this method," matching legacy's own unconfigured-sound-file
        // silent-no-op behavior) must hold for EVERY failure mode, not just I/O ones.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Log.SoundFileIdReadFailed(_logger, path, ex);
            return new SoundFileParseOutcome(null, default, SoundFileIdValidationFailure.ReadError);
        }
    }

    /// <summary>Reads, parses, and resamples a sound-file station-ID file (`docs/plans/sound-file-id-plan.md`)
    /// to <paramref name="targetSampleRateHz"/>. Returns <see langword="null"/> for any failure --
    /// missing/unreadable/oversized file, or an unplayable header (<see cref="MmvSoundFile.ParseHeader"/>'s
    /// own doc comment) -- matching legacy's own "unconfigured sound file -&gt; silent no-op"
    /// behavior; never throws out of this method. The actual parsing/resampling math is pure and
    /// lives in <c>ScanlineStudio.Core.Sstv.MmvSoundFile</c> -- this method owns only the file I/O
    /// boundary, per this port's established "Application does I/O, Core.Sstv does pure DSP"
    /// split.</summary>
    private float[]? TryResolveSoundFileSamples(string path, int targetSampleRateHz)
    {
        var outcome = TryParseSoundFile(path);
        if (outcome.FileBytes is null)
        {
            return null;
        }

        try
        {
            var payload = outcome.FileBytes.AsSpan(outcome.Header.PayloadOffset);
            return MmvSoundFile.Resample(payload, outcome.Header.SampleRateIndex, targetSampleRateHz);
        }
        // Code-review finding (step 8 refactor): TryParseSoundFile's own catch only covers ITS OWN
        // body (exists/size-cap/File.ReadAllBytes/ParseHeader) -- Resample runs after that method
        // returns, so it needs this same widened exception set repeated here, or the real crash bug
        // this catch was originally added to fix (Resample's own call outside any try at all) comes
        // back for the TX path specifically.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Log.SoundFileIdReadFailed(_logger, path, ex);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<SoundFileIdValidationResult> ValidateStationIdSoundFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                var outcome = TryParseSoundFile(path);
                if (outcome.FileBytes is null)
                {
                    return SoundFileIdValidationResult.Fail(outcome.Failure);
                }

                // Duration from the file's own ORIGINAL sample rate -- see this method's own
                // interface doc comment for why this is deliberately NOT resampled to any TX target
                // rate.
                var payloadBytes = outcome.FileBytes.Length - outcome.Header.PayloadOffset;
                var sampleCount = payloadBytes / 2; // MmvSoundFile's own 16-bit-mono trailing-odd-byte truncation.
                var sourceRateHz = MmvSoundFile.GetSourceSampleRateHz(outcome.Header.SampleRateIndex);
                return SoundFileIdValidationResult.Ok(sampleCount / (double)sourceRateHz);
            }, ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
        }
        // Code-review finding: TryParseSoundFile's File.ReadAllBytes can hang indefinitely against a
        // wedged network mount -- the TX-time caller of the same parse core already bounds this
        // (TransmitAsync's own WaitAsync(_cleanupTimeout, ct) around TryResolveSoundFileSamples).
        // Without the same bound here, a hung Options-dialog validation would hold OptionsWindowViewModel's
        // own _saveGate open indefinitely, wedging every subsequent Save/Apply/Connect. This method's
        // own "never throws" contract still holds -- a timeout maps to a real, if generic, failure
        // reason rather than propagating.
        catch (TimeoutException)
        {
            return SoundFileIdValidationResult.Fail(SoundFileIdValidationFailure.ReadError);
        }
    }
}
