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
    public async Task DecodeFromFileAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Piece C2 single-flight guard, checked before anything else -- same shape/reasoning as
        // PlayWithPttAsync's own _transmitInFlight guard.
        if (Interlocked.CompareExchange(ref _fileDecodeInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException("A file decode is already in progress.");
        }

        try
        {
            // Cross-check against a concurrent transmit -- see PlayWithPttAsync's own mirrored check
            // for why this needs to be bidirectional, not just this direction.
            if (Volatile.Read(ref _transmitInFlight) != 0)
            {
                throw new InvalidOperationException("Cannot decode a file while a transmit or tune is in progress.");
            }

            // _autoDetectPaused lives in this session's own handler, not the decoder -- legacy's
            // equivalent pause (pDem->m_SyncMode = -1) lives INSIDE CSSTVDEM itself, so legacy file
            // playback is genuinely suppressed by it too (Sound.cpp:334's WaveFile.ReadWrite feeds
            // the same demod loop the pause affects). Reject rather than silently bypassing it.
            if (_autoDetectPaused)
            {
                throw new InvalidOperationException("Cannot decode a file while auto-detect is paused.");
            }

            var (samples, sampleRate) = await Task.Run(() => WavFile.Read(path), ct).ConfigureAwait(false);

            if (sampleRate != _decoder.SampleRate)
            {
                throw new InvalidOperationException(
                    $"File sample rate ({sampleRate} Hz) does not match the configured decode rate ({_decoder.SampleRate} Hz).");
            }

            if (samples.Length == 0)
            {
                throw new InvalidOperationException("File contains no audio samples.");
            }

            // Round-2 plan-review blocker fix: acquire _rxTransitionGate directly and use the PRIVATE
            // *Locked variants, not the public StartReceivingAsync/StopReceivingAsync pair -- the
            // public StopReceivingAsync has a real early-return-without-detaching path (its own
            // rxTransitionGate wait can time out), so calling it and assuming detachment would race
            // the live drain thread's PushSamples calls against this method's own, tripping
            // RestartableSstvDecoder's _pushActive CAS. Holding the gate for the WHOLE decode
            // serializes this against every other RX transition by construction.
            if (!await _rxTransitionGate.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false))
            {
                throw new TimeoutException("Timed out waiting to start file decode -- a concurrent RX transition did not finish in time.");
            }

            // Restart-required-settings backlog item 4, round-4 finding R1: re-check the rate AGAIN
            // now that the gate is actually held -- the check above ran BEFORE acquiring it, so a
            // RequestSampleRateAsync call that committed a new rate while this one waited for the
            // gate would otherwise go undetected here, and the file's samples would be pushed at the
            // wrong rate. Previously fine when the rate was immutable; not anymore.
            if (sampleRate != _decoder.SampleRate)
            {
                _rxTransitionGate.Release();
                throw new InvalidOperationException(
                    $"File sample rate ({sampleRate} Hz) does not match the configured decode rate ({_decoder.SampleRate} Hz) -- the decode rate changed while waiting to start.");
            }

            var wasReceiving = _isReceiving;
            try
            {
                await StopReceivingLockedAsync().ConfigureAwait(false);

                // Round-2 plan-review blocker fix: reuse the SAME DI-singleton _decoder rather than a
                // "fresh instance" (not implementable -- ReceivedImageBuffer/ReceiveHistoryRecorder
                // subscribe directly to this singleton, and RestartableSstvDecoder has no public
                // force-restart hook). StopReceivingLockedAsync above already called _decoder.ResetAgc()
                // as part of its own RX-stop transition; RequestAbandonReception here clears any
                // in-progress reception state before the first file-sourced chunk arrives. Residual
                // AGC/filter-delay-line carry-over across this seam is legacy-faithful, not a
                // compromise -- Sound.cpp:334-364 swaps only the buffer source on the same CSSTVDEM
                // instance, so its own state runs straight through the identical seam.
                _decoder.RequestAbandonReception();

                // fsk_cwid.md §8.2: a NESTED try/finally around the decode loop, not a plain
                // sequential call after it -- a plain call would never run for a cancelled/faulted
                // decode (control would jump straight past it to the OUTER finally below). Nesting it
                // HERE, inside the existing inner try (not before it, and not moved into the outer
                // finally alongside StartReceivingLockedAsync) means that if FlushOrDropCwArmForEndOfFile
                // itself throws, the outer finally's StartReceivingLockedAsync/_rxTransitionGate.Release()
                // still run -- see that method's own doc comment.
                var completedNormally = false;
                try
                {
                    // Runs on a background thread -- PushSamples/decode events are synchronous, so
                    // without this the whole loop would run on whatever thread called this method (the
                    // UI thread, in practice) and block it for the file's full decode duration.
                    await Task.Run(
                        () =>
                        {
                            const int ChunkSize = 4096;
                            for (var chunkOffset = 0; chunkOffset < samples.Length; chunkOffset += ChunkSize)
                            {
                                ct.ThrowIfCancellationRequested();
                                ObjectDisposedException.ThrowIf(_disposed, this);

                                var length = Math.Min(ChunkSize, samples.Length - chunkOffset);
                                var chunk = new ReadOnlyMemory<float>(samples, chunkOffset, length);

                                // Same three fan-out targets live capture feeds (matching legacy's own
                                // fftIN.CollectFFT reading from the same buffer WaveFile.ReadWrite
                                // fills, Sound.cpp:368) -- reusing these exact delegate instances, not
                                // reimplementing their isolation logic. _decoder.PushSamples is
                                // deliberately NOT wrapped here (unlike the live _decoderHandler) -- a
                                // decode failure on this explicit, single-shot, user-initiated call
                                // should propagate, not be silently swallowed the way an ambient
                                // hot-path capture callback's failure is.
                                PushSamplesToDecoder(chunk);
                                _waterfallHandler(chunk);
                                _levelMeterHandler(chunk);

                                // fsk_cwid.md §8.2: "re-decode a recording and get its CW ID" is an
                                // explicit goal -- UNLIKE _audioAutoSaveHandler, which is deliberately
                                // excluded from this loop (the file itself is already the audio), the
                                // CW arm needs every chunk to compute its own capture window the same
                                // way live capture does.
                                _cwIdHandler(chunk);
                            }
                        },
                        ct).ConfigureAwait(false);
                    completedNormally = true;
                }
                finally
                {
                    FlushOrDropCwArmForEndOfFile(completedNormally);
                }
            }
            finally
            {
                try
                {
                    if (wasReceiving)
                    {
                        await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _rxTransitionGate.Release();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _fileDecodeInFlight, 0);
        }
    }
}
