using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Cw;
using ScanlineStudio.Core.Sstv;

namespace ScanlineStudio.Application;

/// <summary>fsk_cwid.md §8.2 -- the post-image CW-ID capture window: arm on <see cref="ISstvDecoder.ModeDetected"/>
/// at the true (lag-corrected) image-start-plus-length origin, no pre-roll/backfill; drop (never
/// re-arm) on a later-epoch <see cref="ISstvDecoder.DecodeRestarted"/>; hand off the closed window to
/// a background <see cref="ICwIdDecoder"/> decode, raising <see cref="ISstvSessionService.CwIdDecoded"/>
/// only once that completes. Split into its own partial-class file for the same reason
/// <c>SstvSessionService.AudioAutoSave.cs</c> is -- a large, self-contained subsystem with its own
/// state.
///
/// UNLIKE the audio-auto-save arm this file's own shape otherwise mirrors, this one runs during a
/// FILE decode too (no <c>_fileDecodeInFlight</c> gate anywhere below) -- "re-decode a recording and
/// get its CW ID" is an explicit goal (fsk_cwid.md §8.2/§10), and <see cref="SstvSessionService.DecodeFromFileAsync"/>
/// fans out to <see cref="_cwIdHandler"/> explicitly as a result.</summary>
public sealed partial class SstvSessionService
{
    public event Action<CwIdDecodedInfo>? CwIdDecoded;

    // A SEPARATE lock from _audioCaptureLock -- this subsystem's own decode-dispatch and buffer
    // bookkeeping should never contend with the (also hot-path) audio-auto-save lock, matching this
    // class's own established "one lock per independent subsystem" convention (_scratchPrepareLock
    // vs _audioCaptureLock is the precedent).
    private readonly object _cwCaptureLock = new();

    // Cached from StationIdSettings at StartReceivingLockedAsync time, same "re-read the persisted
    // section, cache into a field, no live-apply setter" pattern StationIdSettings.FskIdRxEnabled
    // already uses at that exact call site (SstvSessionService.cs's own `_decoder.StationIdDecodeEnabled
    // = stationIdSettings.FskIdRxEnabled` line) -- deliberately NOT SetAutoSaveAudioEnabled's
    // different "public setter, live-appliable" shape, since there is no existing "CW-ID enabled" live
    // toggle surface anywhere in the UI to call it from, unlike auto-save-audio's own dedicated Apply
    // button. Inherits the same accepted limitation FskIdRxEnabled already has: a DecodeFromFileAsync
    // call made before RX has EVER started once in this process still sees the field default (false),
    // since nothing has read the persisted setting yet -- pre-existing on the sibling setting, not a
    // new gap introduced here.
    private volatile bool _cwIdRxEnabled;
    private volatile int _cwIdRxWindowSeconds = StationIdSettings.DefaultCwIdRxWindowSeconds;

    // §8.2's own "_pushedSampleCount is the single funnel for live and file audio" field -- lives here
    // (not in AudioAutoSave.cs, despite PushSamplesToDecoder itself living there) because this is the
    // subsystem that actually needs it; PushSamplesToDecoder just increments it alongside the existing
    // _pushEpoch bump, both BEFORE the push, for the same "any ModeDetected/DecodeRestarted this call
    // synchronously raises observes the new value" reason _pushEpoch's own doc comment already gives.
    private long _pushedSampleCount;

    // Tier 2-equivalent armed state -- _cwArmedReceptionId == 0 means "nothing armed," same sentinel
    // convention as _audioArmedReceptionId (ISstvDecoder.ReceptionSequence's own contract reserves 0
    // for "no reception"). _cwArmedWindowOpen/_cwArmedWindowClose are ABSOLUTE _pushedSampleCount
    // coordinates, not lengths -- computed once at arm time and never recomputed.
    private float[]? _cwArmedBuffer;
    private int _cwArmedWritten;
    private long _cwArmedWindowOpen;
    private long _cwArmedWindowClose;
    private long _cwArmedReceptionId;
    private long _cwArmedEpoch;
    private int _cwArmedSampleRate;

    /// <summary>Arm/close entry point for a fresh or restarted reception. UNCONDITIONAL -- no
    /// <c>_fileDecodeInFlight</c> gate (see this file's own header comment for why), and no
    /// <c>_autoDetectPaused</c> check either: <see cref="ISstvDecoder.RequestAbandonReception"/>
    /// (called by <see cref="SetAutoDetectPaused"/>) always drains any in-progress reception before a
    /// pause takes effect, so a genuine <see cref="ISstvDecoder.ModeDetected"/> cannot fire while
    /// paused in practice -- this mirrors <see cref="OnAudioAutoSaveModeDetected"/>'s own lack of such
    /// a guard for the identical reason.</summary>
    private void OnCwIdModeDetected(SstvModeDefinition mode)
    {
        lock (_cwCaptureLock)
        {
            // Drop, not close-and-decode, any still-open previous arm first -- a no-op in the normal
            // case (the prior reception's own window already closed itself, or was dropped by
            // OnCwIdDecodeRestarted). If one IS still open here, the previous reception was superseded
            // by a fresh lock with no intervening DecodeRestarted this class ever saw -- an edge case,
            // but §8.2's own design principle (drop rather than emit a wrong-reception splice) applies
            // just as much here as at end-of-file.
            DropCwArmLocked();

            if (!_cwIdRxEnabled)
            {
                return;
            }

            var receptionId = _decoder.ReceptionSequence;
            var epoch = Interlocked.Read(ref _pushEpoch);
            var anchorLagSamples = _decoder.AnchorLagSamples;
            var pushedSampleCount = Interlocked.Read(ref _pushedSampleCount);
            var sampleRate = _decoder.SampleRate;

            // §8.2's own arm-time formula: imageStart = pushedSampleCount - AnchorLagSamples. A
            // larger lag makes imageStart EARLIER (never later), which is bounded-safe by
            // construction -- see AnchorLagSamples' own doc comment. Clamped to >=0 per §8.2's own
            // explicit guard (a lag figure exceeding the session's own pushed-sample count so far
            // would otherwise produce a negative bound).
            var imageStart = Math.Max(0L, pushedSampleCount - anchorLagSamples);

            var rowsPerTransmissionLine = RowsPerTransmissionLine(mode);
            var transmissionLineCount = mode.ImageHeight / rowsPerTransmissionLine;
            var imageDurationSamples = (long)Math.Round(transmissionLineCount * mode.LineDurationMs / 1000.0 * sampleRate);

            var windowOpen = Math.Max(0L, imageStart + imageDurationSamples);
            var windowSeconds = Math.Clamp(
                _cwIdRxWindowSeconds, StationIdSettings.MinCwIdRxWindowSeconds, StationIdSettings.MaxCwIdRxWindowSeconds);
            var windowSamplesLong = (long)windowSeconds * sampleRate;
            var windowClose = windowOpen + windowSamplesLong;
            var windowSamples = windowSamplesLong > int.MaxValue ? int.MaxValue : (int)windowSamplesLong;

            _cwArmedBuffer = new float[windowSamples];
            _cwArmedWritten = 0;
            _cwArmedWindowOpen = windowOpen;
            _cwArmedWindowClose = windowClose;
            _cwArmedReceptionId = receptionId;
            _cwArmedEpoch = epoch;
            _cwArmedSampleRate = sampleRate;

            SafeLog(() => Log.CwIdWindowArmed(_logger, receptionId, windowSeconds));
        }
    }

    /// <summary>Drop-only, same-epoch-suppressed -- see fsk_cwid.md §8.2's own reasoning (mirrors
    /// <see cref="OnAudioAutoSaveDecodeRestarted"/> exactly): <see cref="ISstvDecoder.ReceptionSequence"/>
    /// is never bumped by <see cref="ISstvDecoder.DecodeRestarted"/>, so a same-epoch restart
    /// necessarily refers to the OLDER reception the arm above already accounted for. No re-arm path
    /// exists anywhere in this file -- the next real <see cref="ISstvDecoder.ModeDetected"/> arms
    /// fresh via <see cref="OnCwIdModeDetected"/>.</summary>
    private void OnCwIdDecodeRestarted(SstvModeDefinition abandonedMode)
    {
        lock (_cwCaptureLock)
        {
            if (_cwArmedReceptionId == 0)
            {
                return;
            }

            var epoch = Interlocked.Read(ref _pushEpoch);
            if (epoch == _cwArmedEpoch)
            {
                return;
            }

            DropCwArmLocked();
        }
    }

    /// <summary>Fan-out target for <see cref="IAudioEngine.SamplesCaptured"/> (live capture) AND
    /// called directly from <see cref="DecodeFromFileAsync"/>'s own chunk loop (file decode) -- same
    /// dual-caller shape as <see cref="_waterfallHandler"/>/<see cref="_levelMeterHandler"/>. Must run
    /// AFTER <see cref="PushSamplesToDecoder"/> for the SAME chunk (§8.2's chunk-index invariant) --
    /// guaranteed by subscription order for live capture (<see cref="_cwIdHandler"/> is subscribed
    /// last) and by call order in <see cref="DecodeFromFileAsync"/>'s own loop.</summary>
    private void OnCwIdSamplesCaptured(ReadOnlyMemory<float> samples)
    {
        // §8.2's own explicit requirement: mirror _decoderHandler's own _autoDetectPaused early
        // return EXACTLY -- during a pause, _decoderHandler never calls PushSamplesToDecoder for a
        // real chunk, so _pushedSampleCount was never incremented for it either. Computing this
        // chunk's own absolute range against a _pushedSampleCount that doesn't include it would
        // desync this handler's index math from the true arrival stream.
        if (_autoDetectPaused)
        {
            return;
        }

        lock (_cwCaptureLock)
        {
            if (_cwArmedReceptionId == 0 || _cwArmedBuffer is null)
            {
                return;
            }

            var chunkEnd = Interlocked.Read(ref _pushedSampleCount);
            var chunkStart = chunkEnd - samples.Length;

            if (chunkEnd <= _cwArmedWindowOpen)
            {
                // Entirely before the window -- nothing to capture yet.
                return;
            }

            var sliceStart = (int)Math.Max(0L, _cwArmedWindowOpen - chunkStart);
            var sliceEndAbsolute = Math.Min(chunkEnd, _cwArmedWindowClose);
            var sliceEnd = (int)(sliceEndAbsolute - chunkStart);
            if (sliceEnd > sliceStart)
            {
                AppendToCwArmedBufferLocked(samples.Span[sliceStart..sliceEnd]);
            }

            if (chunkEnd >= _cwArmedWindowClose)
            {
                CloseCwArmLocked();
            }
        }
    }

    /// <summary>§8.2's end-of-file rule: flush a window that has captured at least one real sample
    /// (i.e. <see cref="_cwArmedWindowOpen"/> was actually reached before the file ended), drop one
    /// that hasn't -- or one whose decode loop did not complete normally (cancelled/faulted), which
    /// must always drop regardless of how much was captured. Called from
    /// <see cref="DecodeFromFileAsync"/>'s own nested try/finally around its decode loop -- see that
    /// method's own comment for why THAT placement (not before/after the whole file-decode try) is
    /// load-bearing.</summary>
    private void FlushOrDropCwArmForEndOfFile(bool completedNormally)
    {
        lock (_cwCaptureLock)
        {
            if (_cwArmedReceptionId == 0 || _cwArmedBuffer is null)
            {
                return;
            }

            if (completedNormally && _cwArmedWritten > 0)
            {
                CloseCwArmLocked();
                return;
            }

            var receptionId = _cwArmedReceptionId;
            var reason = completedNormally ? "windowNeverReached" : "decodeDidNotCompleteNormally";
            DropCwArmLocked();
            SafeLog(() => Log.CwIdWindowDroppedAtEndOfFile(_logger, receptionId, reason));
        }
    }

    /// <summary>Mirrors <see cref="HandleAudioCaptureReset"/>'s own reasoning for the audio-auto-save
    /// arm: an open CW arm at a capture-reset seam (sample-rate/device change, TX pause/resume, file-
    /// decode entry) has no reliable close reason, so it is discarded, not flushed.</summary>
    private void DropCwArmForCaptureReset()
    {
        lock (_cwCaptureLock)
        {
            DropCwArmLocked();
        }
    }

    /// <summary>Must be called under <see cref="_cwCaptureLock"/>. A no-op if nothing is armed.</summary>
    private void DropCwArmLocked()
    {
        _cwArmedBuffer = null;
        _cwArmedWritten = 0;
        _cwArmedWindowOpen = 0;
        _cwArmedWindowClose = 0;
        _cwArmedReceptionId = 0;
        _cwArmedEpoch = 0;
    }

    /// <summary>Appends the slice to the armed buffer. Must be called under <see cref="_cwCaptureLock"/>.
    /// No clamping/defensive truncation needed (unlike <see cref="AppendToArmedBufferLocked"/>'s own
    /// safety-margin case) -- the buffer is sized to EXACTLY the window length at arm time, and every
    /// caller here already clips its slice to <see cref="_cwArmedWindowClose"/> before calling this,
    /// so total appended length can never exceed capacity.</summary>
    private void AppendToCwArmedBufferLocked(ReadOnlySpan<float> slice)
    {
        slice.CopyTo(_cwArmedBuffer.AsSpan(_cwArmedWritten));
        _cwArmedWritten += slice.Length;
    }

    /// <summary>Closes the currently armed window (a no-op if nothing is armed) and hands it to a
    /// background decode -- never on the audio drain thread, same reasoning as
    /// <see cref="CloseArmedSliceLocked"/>. Must be called under <see cref="_cwCaptureLock"/>.</summary>
    private void CloseCwArmLocked()
    {
        if (_cwArmedReceptionId == 0 || _cwArmedBuffer is null)
        {
            return;
        }

        var receptionId = _cwArmedReceptionId;
        var sampleRate = _cwArmedSampleRate;
        var samples = _cwArmedBuffer.AsMemory(0, _cwArmedWritten);

        DropCwArmLocked();

        _ = Task.Run(() => DecodeAndRaiseCwIdAsync(receptionId, samples, sampleRate));
    }

    /// <summary>Background-thread continuation of <see cref="CloseCwArmLocked"/>: decodes the closed
    /// window and raises <see cref="ISstvSessionService.CwIdDecoded"/> only for a non-empty result.
    /// Isolated -- a failure here must never affect the live decode path, which has already moved on
    /// by the time this runs. Genuinely <see langword="async"/> (not a blocking <c>.GetAwaiter().GetResult()</c>
    /// on a background thread), so a future <see cref="ICwIdDecoder"/> backend that does real async
    /// I/O (e.g. a sidecar process, fsk_cwid.md §8.5) does not tie up a thread-pool thread waiting on
    /// it.</summary>
    private async Task DecodeAndRaiseCwIdAsync(long receptionId, ReadOnlyMemory<float> samples, int sampleRate)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _cwIdDecoder.DecodeAsync(samples, sampleRate).ConfigureAwait(false);
            if (result.Text.Length == 0)
            {
                SafeLog(() => Log.CwIdDecodeSkippedNoTone(_logger, receptionId));
                return;
            }

            var callsign = CwIdCallsignExtractor.Extract(result.Text);
            var info = new CwIdDecodedInfo(receptionId, result.Text, callsign, result.Confidence, result.ToneHz, result.Wpm, CwDecoderBackend.Classical);
            SafeLog(() => Log.CwIdDecoded(_logger, receptionId, result.Text, result.Confidence, stopwatch.ElapsedMilliseconds));
            RaiseCwIdDecoded(info);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CwIdDecodeFailed(_logger, receptionId, ex));
        }
    }

    private void RaiseCwIdDecoded(CwIdDecodedInfo info)
    {
        try
        {
            CwIdDecoded?.Invoke(info);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CwIdDecodedSubscriberFailed(_logger, info.ReceptionSequence, ex));
        }
    }

    /// <summary>Derives rows-per-transmission-line from <paramref name="mode"/>'s own
    /// <see cref="ColorEncoding"/> -- shared with <see cref="ArmLocked"/> (audio auto-save)
    /// specifically so the already-corrected logic (an earlier draft wrongly grouped MC into this
    /// doubling family by NAME rather than reading <see cref="ColorEncoding"/> directly -- see
    /// <see cref="ArmLocked"/>'s own implementation-note comment for the full story) is never
    /// duplicated into a second copy that could silently drift or reintroduce the same mistake.</summary>
    private static int RowsPerTransmissionLine(SstvModeDefinition mode) =>
        mode.ColorEncoding is ColorEncoding.YCbCrLinePaired or ColorEncoding.MonoAveragedPaired ? 2 : 1;

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "The CW-ID SamplesCaptured handler threw {Count} time(s), most recently:")]
        public static partial void CwIdPushSamplesFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "CW-ID capture window armed for reception {ReceptionId} ({WindowSeconds}s)")]
        public static partial void CwIdWindowArmed(ILogger logger, long receptionId, int windowSeconds);

        [LoggerMessage(Level = LogLevel.Information, Message = "CW ID decoded for reception {ReceptionId}: \"{Text}\" (confidence {Confidence:0.00}, {ElapsedMs}ms)")]
        public static partial void CwIdDecoded(ILogger logger, long receptionId, string text, double confidence, long elapsedMs);

        [LoggerMessage(Level = LogLevel.Debug, Message = "CW-ID decode for reception {ReceptionId} found no tone")]
        public static partial void CwIdDecodeSkippedNoTone(ILogger logger, long receptionId);

        [LoggerMessage(Level = LogLevel.Error, Message = "CW-ID decode failed for reception {ReceptionId}")]
        public static partial void CwIdDecodeFailed(ILogger logger, long receptionId, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "CW-ID capture window for reception {ReceptionId} dropped at end-of-file ({Reason})")]
        public static partial void CwIdWindowDroppedAtEndOfFile(ILogger logger, long receptionId, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "A CwIdDecoded subscriber threw for reception {ReceptionId}")]
        public static partial void CwIdDecodedSubscriberFailed(ILogger logger, long receptionId, Exception ex);
    }
}
