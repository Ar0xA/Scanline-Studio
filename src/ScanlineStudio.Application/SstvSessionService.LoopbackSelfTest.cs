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
    /// <inheritdoc/>
    public async Task<LoopbackSelfTestResult> RunLoopbackSelfTestAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.CompareExchange(ref _loopbackSelfTestInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException("A loopback self-test is already in progress.");
        }

        try
        {
            if (Volatile.Read(ref _transmitInFlight) != 0)
            {
                throw new InvalidOperationException("Cannot run a loopback self-test while a transmit or tune is in progress.");
            }

            // sampleRate comes from _encoder.SampleRate -- the encoder is what actually generates the
            // audio this decoder consumes. Restart-required-settings backlog item 4 (2026-08-27):
            // SampleRate is genuinely live now, not restart-only -- the real protection against a
            // mid-self-test rate change is the BeginTransmission/EndTransmission bracket below (round-4
            // plan-review finding B1: this is a SECOND, independent call site into the encoder that
            // needs the same bracket TransmitAsync's own TX path uses -- easy to miss since it doesn't
            // go through PlayWithPttAsync at all). Only the decoder BEHAVIOR settings need a fresh read
            // (DemodType/senseLevel/etc. have no such encoder-side counterpart to drift against).
            // Deliberately NOT ResolveTransmitSettingsAsync -- that resolves the REAL persisted
            // StationId/TxSampleRateOffsetHz, both of which this self-test must never use (see this
            // method's own interface doc comment for why).
            var encoderReconfig = _encoder as ISstvEncoderReconfiguration;
            encoderReconfig?.BeginTransmission();
            try
            {
                var sampleRate = _encoder.SampleRate;
                var appSettings = await Task.Run(() => _settingsStore.LoadAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);

                // Code-review round-1 finding: unguarded GetSection call here would let a corrupt/
                // version-skewed "SstvDecoder" section (e.g. a hand-edited settings.json) permanently
                // break the self-test with a generic "failed" error, while live RX silently falls back to
                // defaults via this SAME try/catch/fallback shape (Program.CreateSstvDecoder). Matching
                // that established pattern here instead of leaving this the one unguarded read.
                SstvDecoderSettings decoderSettings;
                try
                {
                    decoderSettings = appSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings)
                        ?? new SstvDecoderSettings();
                }
                catch (JsonException ex)
                {
                    SafeLog(() => Log.SettingsSectionReadFailed(_logger, SstvDecoderSettings.SectionKey, ex));
                    decoderSettings = new SstvDecoderSettings();
                }

                var resolved = decoderSettings.Resolve();

                // Fresh, throwaway instance -- see this method's own interface doc comment for why this
                // is the whole design (round-5 plan-review): never the shared _decoder, so nothing here
                // needs to serialize against _rxTransitionGate or touch ReceivedImageBuffer/
                // ReceiveHistoryRecorder/the live RX pane at all.
                // SNR stays off for the self-test, as it always has; it measures only, never changes pixels.
                using var decoder = new AnalogFmSstvDecoder(sampleRate, resolved with { SnrMeasurementEnabled = false });

                ArrayImageSource? lastImage = null;
                int? previousLine = null;
                int? observedStep = null;
                var lastLine = -1;
                string? detectedModeId = null;
                var decodeRestarted = false;

                // Learns the scanline step from the first two events rather than assuming 1 -- paired-line
                // families (PD/MP/RM8/RM12) advance 2 rows per event and RowsPerTransmissionLine isn't on
                // the public SstvModeDefinition. Same technique ReceivedImageBuffer/ReceiveHistoryRecorder
                // already use (round-5 plan-review finding).
                void OnLineDecoded(DecodedImageUpdate update)
                {
                    // Copy, not the live update.Image reference -- see ArrayImageSource.CopyFrom's own
                    // doc comment.
                    lastImage = ArrayImageSource.CopyFrom(update.Image);

                    if (previousLine is int previous && observedStep is null)
                    {
                        observedStep = update.Line - previous;
                    }

                    previousLine = update.Line;
                    lastLine = update.Line;
                }

                void OnModeDetected(SstvModeDefinition detected) => detectedModeId = detected.Id;

                // Code-review round-1 finding: DecodeRestarted has TWO reachable raise sites during a
                // self-test, not one -- Auto Stop AND a mid-reception re-lock (a stronger/cleaner sync
                // found while already locked, gated by syncRestartEnabled, which defaults true). With
                // AutoStopEnabled defaulting FALSE, under stock settings every DecodeRestarted here is
                // actually the re-lock case -- a real decode-fidelity failure, the exact thing this
                // feature exists to catch. Only attribute it to Auto Stop when that setting is actually
                // on; the event alone can't distinguish the two cases even then, so the outcome's own
                // display text is deliberately hedged ("may be"), not asserted as fact.
                void OnDecodeRestarted(SstvModeDefinition abandoned) => decodeRestarted = true;

                decoder.LineDecoded += OnLineDecoded;
                decoder.ModeDetected += OnModeDetected;
                decoder.DecodeRestarted += OnDecodeRestarted;

                try
                {
                    // Runs on a background thread -- encode+decode events are synchronous, so without this
                    // the whole loop would run on whatever thread called this method (the UI thread, in
                    // practice) and block it for the full self-test duration. Same reasoning as
                    // DecodeFromFileAsync's own push loop.
                    await Task.Run(
                        async () =>
                        {
                            // T1-3 (production_audit.md): EncodeBatchedAsync directly -- no local
                            // re-batching buffer needed at all, since decoder.PushSamples already
                            // accepts ReadOnlyMemory<float> and this loop has no per-sample transform
                            // to apply (unlike PumpToPlaybackAsync's own gain multiply). ChunkSize
                            // itself is kept only for the trailing-silence padding loop below, no
                            // longer for re-batching the encoder's own output.
                            const int ChunkSize = 4096;
                            //
                            // sampleRateOffsetHz: 0.0 and stationId: null (StationIdTransmitOptions.None) --
                            // both deliberate, see this method's own interface doc comment. TX BPF/LPF
                            // (Options stub backlog item 3) left at their legacy-matching defaults for
                            // the same reason -- this self-test is a minimal, deterministic decode
                            // check, not a faithful mirror of the user's real TX settings.
                            await foreach (var chunk in _encoder.EncodeBatchedAsync(mode, image, stationId: null, sampleRateOffsetHz: 0.0, ct: ct)
                                .WithCancellation(ct).ConfigureAwait(false))
                            {
                                decoder.PushSamples(chunk);
                            }

                            // Trailing silence: TryProcessBuffer can leave the final scanline undecoded
                            // without it -- the sync-anchor correction shifts consumed-vs-received sample
                            // counts, so exact-length encoded audio can fall short on the last line (round-5
                            // plan-review finding). One full transmission line covers any anchor offset; the
                            // sampleRate/2 floor keeps short-line modes sane.
                            var padSamples = Math.Max((int)Math.Ceiling(mode.LineDurationMs / 1000.0 * sampleRate), sampleRate / 2);
                            var silence = new float[Math.Min(padSamples, ChunkSize)];
                            for (var remaining = padSamples; remaining > 0;)
                            {
                                var n = Math.Min(remaining, silence.Length);
                                decoder.PushSamples(silence.AsMemory(0, n));
                                remaining -= n;
                            }
                        },
                        ct).ConfigureAwait(false);
                }
                finally
                {
                    decoder.LineDecoded -= OnLineDecoded;
                    decoder.ModeDetected -= OnModeDetected;
                    decoder.DecodeRestarted -= OnDecodeRestarted;
                }

                // Code-review round-1 finding: only attribute a restart to Auto Stop when that setting is
                // actually enabled -- see OnDecodeRestarted's own comment above for why a restart with
                // Auto Stop OFF (the default) is really a decode-fidelity failure, not expected behavior.
                var outcome = decodeRestarted && resolved.AutoStopEnabled
                    ? LoopbackSelfTestOutcome.AbandonedByAutoStop
                    : observedStep is int step && step > 0 && lastLine + step >= mode.ImageHeight
                        ? LoopbackSelfTestOutcome.Completed
                        : LoopbackSelfTestOutcome.Incomplete;

                return new LoopbackSelfTestResult(lastImage ?? EmptySelfTestImage, detectedModeId, outcome);
            }
            finally
            {
                encoderReconfig?.EndTransmission();
            }
        }
        finally
        {
            Volatile.Write(ref _loopbackSelfTestInFlight, 0);
        }
    }
}
