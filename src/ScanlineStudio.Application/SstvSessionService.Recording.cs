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
    public Task StartRecordingAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isReceiving)
        {
            throw new InvalidOperationException("Cannot start recording while not receiving.");
        }

        lock (_recordingLock)
        {
            if (_recordingChunks is not null)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            // Restart-required-settings backlog item 4, round-4 finding R2: the other half of
            // RequestSampleRateAsync's own TOCTOU close -- refuse to start while a rate change is
            // actively being applied, rather than starting a recording whose header could end up
            // describing a rate the audio wasn't actually captured at.
            if (_sampleRateChangeInProgress)
            {
                throw new InvalidOperationException("Cannot start recording while a sample rate change is being applied.");
            }

            // Configurations-preset backlog, Phase 1 (2026-08-28): same TOCTOU close as the check
            // immediately above, for RequestCaptureDeviceAsync's own commit sequence.
            if (_captureDeviceChangeInProgress)
            {
                throw new InvalidOperationException("Cannot start recording while a capture device change is being applied.");
            }

            _recordingChunks = [];
            _recordingPath = path;
            _recordingSampleRate = _decoder.SampleRate;
            // Code-review finding: subscribing here, INSIDE the same lock acquisition that
            // publishes _recordingChunks, not after releasing it -- a concurrent
            // FinalizeRecordingAsync landing in the gap between unlock and a subscribe-after-unlock
            // could otherwise grab-and-null the just-published state before this ever subscribes,
            // making its own unsubscribe a no-op and leaving this subscription permanently
            // dangling (double-subscribed, duplicated audio, on the next StartRecordingAsync).
            _audioEngine.SamplesCaptured += _recordingHandler;
        }

        SafeLog(() => Log.RecordingStarted(_logger, path));
        return Task.CompletedTask;
    }

    public Task StopRecordingAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return FinalizeRecordingAsync();
    }

    /// <summary>Grabs and clears the in-progress recording's buffered chunks under
    /// <see cref="_recordingLock"/>, then concatenates and writes them OUTSIDE the lock so the audio
    /// drain thread is never blocked for the duration of the file write. A no-op if no recording is
    /// in progress -- both <see cref="StopRecordingAsync"/> and <see cref="DisposeAsync"/> call this,
    /// and an in-progress recording must be finalized before <see cref="DisposeAsync"/> returns or
    /// the whole in-RAM buffer is silently discarded with no file written.</summary>
    private async Task FinalizeRecordingAsync()
    {
        List<ReadOnlyMemory<float>>? chunks;
        string? path;
        int sampleRate;
        lock (_recordingLock)
        {
            chunks = _recordingChunks;
            path = _recordingPath;
            // Restart-required-settings backlog item 4: the rate CAPTURED at StartRecordingAsync
            // time, read here (still inside the lock) rather than at WavFile.Write below, after this
            // lock is released and a Task.Run hop -- see _recordingSampleRate's own doc comment for
            // the race this closes. Falls back to the live decoder rate only for the (unreachable in
            // practice, since _recordingSampleRate is always set alongside _recordingChunks)
            // chunks-is-not-null-but-field-somehow-null case.
            sampleRate = _recordingSampleRate ?? _decoder.SampleRate;
            _recordingChunks = null;
            _recordingPath = null;
            _recordingSampleRate = null;

            // Code-review finding: unsubscribing here, INSIDE the same lock acquisition that clears
            // _recordingChunks -- see StartRecordingAsync's own subscribe-inside-the-lock comment
            // for the dangling-subscription race this closes. A no-op if chunks is about to come
            // back null below (nothing was ever subscribed).
            if (chunks is not null)
            {
                _audioEngine.SamplesCaptured -= _recordingHandler;
            }
        }

        if (chunks is null)
        {
            return;
        }

        var totalLength = 0;
        foreach (var chunk in chunks)
        {
            totalLength += chunk.Length;
        }

        var combined = new float[totalLength];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.Span.CopyTo(combined.AsSpan(offset));
            offset += chunk.Length;
        }

        await Task.Run(() => WavFile.Write(path!, combined, sampleRate)).ConfigureAwait(false);
        SafeLog(() => Log.RecordingSaved(_logger, path!, combined.Length));
    }
}
