using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;

namespace ScanlineStudio.Application;

/// <summary>ui_transition_plan.md step 12 (Auto-save RX audio) -- the capture ring, arm/close state
/// machine, push-epoch counter, and scratch-file retention behind <see
/// cref="ISstvSessionService.AudioSliceReady"/>/<see cref="ISstvSessionService.AudioCaptureReset"/>/
/// <see cref="ISstvSessionService.TrySaveReceptionAudioAsync"/>. See
/// <c>docs/plans/step12-auto-save-rx-audio-plan.md</c> for the full design this implements -- this
/// file does NOT do the <c>Recorded</c>+<see cref="ISstvSessionService.AudioSliceReady"/> correlation
/// itself (that is <c>RxAudioAutoSaver</c>, a separate class in this same project); this class only
/// ever produces one identity-keyed slice per reception and hands it off.
///
/// Split into its own partial-class file (not appended to the already-large main file) since this is
/// a large, self-contained subsystem with its own state, matching this project's "keep commits small
/// and reviewable" principle.</summary>
public sealed partial class SstvSessionService
{
    /// <summary>10 s, comfortably covering the ~8.8 s AVT worst case (measured against the actual
    /// header/training-gap constants -- see the plan doc's "Pre-roll and capacity" section for the
    /// full derivation). Ring cost at this size is small (~1.94 MB at the max supported 48500 Hz).</summary>
    private const int AudioPreRollMs = 10_000;

    /// <summary>Small margin (whole transmission lines) added to the close-trigger threshold for
    /// line-sync drift -- the mode's own nominal duration is a lower bound, not an exact stopwatch.</summary>
    private const int AudioCloseThresholdMarginLines = 2;

    /// <summary>Extra headroom (samples) added to the armed buffer's allocated CAPACITY beyond the
    /// close threshold, so the append that finally crosses the threshold can never itself overflow
    /// the array -- AppendToArmedBufferLocked still clamps defensively regardless.</summary>
    private const int AudioArmedBufferSafetyMarginSamples = 8192;

    /// <summary>Oldest-first eviction cap for BOTH the on-disk scratch files (this class) and
    /// <c>RxAudioAutoSaver</c>'s own join dictionary (a separate structure, same numeric cap, no
    /// shared trigger -- see the plan doc's "The join" section for why the two are independent).</summary>
    private const int AudioScratchRetentionCountCap = 8;

    /// <summary>256 MB -- byte-aware ONLY for the on-disk scratch files (they hold actual audio
    /// bytes); the join-dictionary cap above is count-only, since a parked join entry is metadata,
    /// not audio.</summary>
    private const long AudioScratchRetentionByteCap = 256L * 1024 * 1024;

    private readonly object _audioCaptureLock = new();
    private readonly Guid _audioScratchSessionId = Guid.NewGuid();
    private readonly List<AudioScratchEntry> _retainedAudioScratchFiles = [];

    private volatile bool _autoSaveAudioEnabled;
    private volatile string? _autoSaveAudioDirectory;

    // ISstvDecoder.ReceptionSequence's own chunking precondition (see that property's doc comment)
    // is what makes this safe: a single PushSamplesToDecoder call can never itself contain two
    // independent restart-triggering events, so a same-epoch DecodeRestarted is unambiguously "the
    // restart my own arm already accounted for," never a genuinely new one.
    private long _pushEpoch;

    // Tier 1: a small rolling ring holding exactly AudioPreRollMs of samples. Reallocated (not just
    // cleared) by HandleAudioCaptureReset, since the sample rate it was sized against may have
    // changed -- capacity 0 means "needs (re)allocation," checked by EnsureAudioPreRollRingAllocatedLocked.
    private float[] _audioPreRollRing = [];
    private int _audioPreRollRingCapacity;
    private int _audioPreRollWritePos;
    private int _audioPreRollFilledCount;

    // Tier 2: the current armed slice. _audioArmedReceptionId == 0 means "nothing armed" -- 0 is
    // reserved by ISstvDecoder.ReceptionSequence's own contract to mean exactly that, so this can
    // never collide with a real armed reception's id.
    private float[]? _audioArmedBuffer;
    private int _audioArmedWritten;
    private long _audioArmedCloseAtLength;
    private long _audioArmedReceptionId;
    private long _audioArmedEpoch;
    private int _audioArmedSampleRate;

    // A SEPARATE, small lock -- never _audioCaptureLock -- guarding only this HashSet membership
    // check. Keyed per scratch ROOT (not a single global flag): fixes a real leak an earlier draft
    // had, where changing AudioDirectory mid-session would create a new scratch root with no
    // session.marker, which PurgeDeadSiblingScratchSessions then never purges (a marker-less
    // directory is treated as "assume live, skip" forever).
    private readonly object _scratchPrepareLock = new();
    private readonly HashSet<string> _preparedScratchRoots = new(StringComparer.OrdinalIgnoreCase);

    public event Action<long, int>? AudioSliceReady;

    public event Action? AudioCaptureReset;

    /// <summary>See <see cref="ISstvSessionService.SetAutoSaveAudioEnabled"/>. Cached as a
    /// <see langword="volatile"/> field, never read from a settings store on the decode path.</summary>
    public void SetAutoSaveAudioEnabled(bool enabled) => _autoSaveAudioEnabled = enabled;

    /// <summary>See <see cref="ISstvSessionService.SetAudioDirectory"/>.</summary>
    public void SetAudioDirectory(string? directory) =>
        _autoSaveAudioDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory;

    /// <summary>See <see cref="ISstvSessionService.IsAudioAutoSaveActive"/>. Reads the same field
    /// <see cref="OnAudioAutoSaveModeDetected"/>/<see cref="CloseArmedSliceLocked"/> already guard
    /// with <see cref="_audioCaptureLock"/> -- no separate flag to keep in sync.</summary>
    public bool IsAudioAutoSaveActive
    {
        get
        {
            lock (_audioCaptureLock)
            {
                return _audioArmedReceptionId != 0;
            }
        }
    }

    /// <summary>See <see cref="ISstvSessionService.TrySaveReceptionAudioAsync"/>. A plain file
    /// rename, not re-encoded -- cheap enough to run synchronously (wrapped in
    /// <see cref="Task.FromResult{TResult}"/>) rather than a background thread; this is called once
    /// per completed reception, not on any hot path.</summary>
    public Task<bool> TrySaveReceptionAudioAsync(long receptionId, string path)
    {
        AudioScratchEntry entry;
        lock (_audioCaptureLock)
        {
            var index = _retainedAudioScratchFiles.FindIndex(e => e.ReceptionId == receptionId);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            entry = _retainedAudioScratchFiles[index];
            _retainedAudioScratchFiles.RemoveAt(index);
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(entry.Path, path, overwrite: true);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            // Re-retain rather than leave it untracked/unevictable -- a caller can legitimately retry
            // (e.g. a transient permission error on the destination path), and this keeps the file
            // reachable by both a retry and this session's own normal eviction accounting in the
            // meantime, rather than only ever being reclaimed by a LATER session's dead-session purge.
            lock (_audioCaptureLock)
            {
                // Insert at the FRONT, not the back -- this entry was already the oldest before the
                // failed move removed it, and re-adding it as the newest would let it wrongly claim
                // the "newest exempt from eviction" slot.
                _retainedAudioScratchFiles.Insert(0, entry);
            }

            SafeLog(() => Log.AudioSliceSaveFailed(_logger, receptionId, ex));
            return Task.FromResult(false);
        }
    }

    /// <summary>Routes every call into <c>_decoder.PushSamples</c> through this one helper so the
    /// push-epoch counter can never miss a call site (plan-review finding: an earlier draft named
    /// specific call sites individually and missed one). Bumped BEFORE the push, matching
    /// <see cref="ISstvDecoder.ReceptionSequence"/>'s own "bump before raise" shape -- any
    /// <see cref="ISstvDecoder.ModeDetected"/>/<see cref="ISstvDecoder.DecodeRestarted"/> this call
    /// synchronously raises will observe the NEW epoch value.
    ///
    /// fsk_cwid.md §8.2: also increments <c>_pushedSampleCount</c> by this chunk's own length, BEFORE
    /// the push, for the identical reason -- <c>SstvSessionService.CwId.cs</c>'s own arm/capture logic
    /// (<c>OnCwIdModeDetected</c>/<c>OnCwIdSamplesCaptured</c>) reads it synchronously from inside
    /// <see cref="ISstvDecoder.ModeDetected"/>/the SamplesCaptured fan-out and needs it to already
    /// include the chunk in flight. This is the "single funnel for live and file audio" §8.2 names --
    /// both <c>_decoderHandler</c> and <see cref="DecodeFromFileAsync"/>'s own chunk loop call ONLY
    /// this method, never <c>_decoder.PushSamples</c> directly.</summary>
    private void PushSamplesToDecoder(ReadOnlyMemory<float> samples)
    {
        Interlocked.Increment(ref _pushEpoch);
        Interlocked.Add(ref _pushedSampleCount, samples.Length);
        _decoder.PushSamples(samples);
    }

    /// <summary>Arm/close state machine entry point for a fresh or restarted reception. Gated on
    /// <see cref="_fileDecodeInFlight"/> -- capturing audio during a file decode serves no purpose
    /// (the file itself is the audio), and file-decoded receptions must not arm a slice at all.</summary>
    private void OnAudioAutoSaveModeDetected(SstvModeDefinition mode)
    {
        if (Volatile.Read(ref _fileDecodeInFlight) != 0)
        {
            return;
        }

        lock (_audioCaptureLock)
        {
            // Close any still-open previous arm first, under its OWN id -- a no-op if nothing is
            // armed (e.g. auto-save was disabled, or the previous slice already closed on its own
            // sample-count threshold).
            CloseArmedSliceLocked();

            if (!_autoSaveAudioEnabled)
            {
                return;
            }

            // Already bumped by the decoder in RestartableSstvDecoder.OnModeDetected/
            // AnalogFmSstvDecoder's own ModeDetected raise site, BEFORE this handler runs -- every
            // subscriber of the same raise (this handler, ReceiveHistoryRecorder's, any UI pane's)
            // reads the identical value, with no coordination needed between them.
            var receptionId = _decoder.ReceptionSequence;
            var epoch = Interlocked.Read(ref _pushEpoch);
            ArmLocked(mode, receptionId, epoch);
        }
    }

    /// <summary>The other half of the arm/close state machine. See
    /// <see cref="ISstvDecoder.ReceptionSequence"/>'s own doc comment for the full reasoning behind
    /// the same-epoch suppression below -- a same-epoch <see cref="ISstvDecoder.DecodeRestarted"/>
    /// necessarily refers to an OLDER reception (the minority ordering already handled at arm time
    /// above), never the one just armed.</summary>
    private void OnAudioAutoSaveDecodeRestarted(SstvModeDefinition abandonedMode)
    {
        if (Volatile.Read(ref _fileDecodeInFlight) != 0)
        {
            return;
        }

        lock (_audioCaptureLock)
        {
            if (_audioArmedReceptionId == 0)
            {
                return;
            }

            var epoch = Interlocked.Read(ref _pushEpoch);
            if (epoch == _audioArmedEpoch)
            {
                return;
            }

            CloseArmedSliceLocked();
        }
    }

    /// <summary>Fan-out target for <see cref="IAudioEngine.SamplesCaptured"/>, subscribed/
    /// unsubscribed alongside <see cref="_decoderHandler"/>/<see cref="_waterfallHandler"/>/
    /// <see cref="_levelMeterHandler"/> (see the constructor's own comment on
    /// <see cref="_audioAutoSaveHandler"/> for why this differs from <see cref="_recordingHandler"/>'s
    /// independent lifecycle).</summary>
    private void OnAudioAutoSaveSamplesCaptured(ReadOnlyMemory<float> samples)
    {
        lock (_audioCaptureLock)
        {
            // Auditor-caught (round 2 code-review): checking `!_autoSaveAudioEnabled` alone and
            // bailing out here used to ALSO stop feeding an already-open arm the instant the setting
            // was toggled off, contradicting ISstvSessionService.SetAutoSaveAudioEnabled's own
            // documented contract ("an in-progress slice is never truncated-and-emitted early by a
            // live toggle") -- the slice was never emitted EARLY, but it WAS silently missing samples
            // from the disabled window, producing a truncated-then-jump-cut-spliced WAV once it
            // eventually closed. An open arm must keep being fed regardless of the live setting; only
            // "disabled AND nothing armed" has nothing useful to do here.
            if (!_autoSaveAudioEnabled && _audioArmedReceptionId == 0)
            {
                return;
            }

            if (_autoSaveAudioEnabled)
            {
                EnsureAudioPreRollRingAllocatedLocked();
                WriteToPreRollRingLocked(samples.Span);
            }

            if (_audioArmedReceptionId == 0 || _audioArmedBuffer is null)
            {
                return;
            }

            AppendToArmedBufferLocked(samples.Span);

            if (_audioArmedWritten >= _audioArmedCloseAtLength)
            {
                CloseArmedSliceLocked();
            }
        }
    }

    /// <summary>ui_transition_plan.md step 12: raised on every RX stop/start seam relevant to this
    /// feature -- see <see cref="StopReceivingLockedAsync"/>'s own call site for why ONE hook there
    /// covers all 4 seams the plan doc calls for (sample-rate change, capture-device change, TX
    /// pause/resume, file-decode entry) without duplicating this call at each one. Clears the ring
    /// and discards any open arm -- NOT the retained scratch files or the (not-yet-built)
    /// correlator's join state; see this method's own reasoning inline.</summary>
    private void HandleAudioCaptureReset()
    {
        lock (_audioCaptureLock)
        {
            // Discard, don't close-and-emit: an open arm at this seam has no reliable close reason
            // (e.g. a mid-arm sample-rate change would otherwise splice two different rates into one
            // WAV) -- see the plan doc's "Accepted v1 limitations" for why this is a MISS, never a
            // wrong attach.
            _audioArmedBuffer = null;
            _audioArmedWritten = 0;
            _audioArmedCloseAtLength = 0;
            _audioArmedReceptionId = 0;
            _audioArmedEpoch = 0;

            // Reallocated, not just position-reset -- the sample rate this was sized against may
            // have changed at exactly this seam.
            _audioPreRollRing = [];
            _audioPreRollRingCapacity = 0;
            _audioPreRollWritePos = 0;
            _audioPreRollFilledCount = 0;
        }

        // fsk_cwid.md §8.2: same "discard, don't close-and-emit" reasoning as the audio-auto-save arm
        // immediately above, for the CW-ID capture window -- a SEPARATE lock (_cwCaptureLock), so a
        // dedicated call, not inline here.
        DropCwArmForCaptureReset();

        RaiseAudioCaptureReset();
    }

    /// <summary>Sizes and arms a fresh tier-2 buffer for <paramref name="mode"/>, seeded from the
    /// pre-roll ring's current contents. Must be called under <see cref="_audioCaptureLock"/>.</summary>
    private void ArmLocked(SstvModeDefinition mode, long receptionId, long epoch)
    {
        EnsureAudioPreRollRingAllocatedLocked();

        var sampleRate = _decoder.SampleRate;

        // Implementation note (2026-08-29, corrected during coding): derive rows-per-transmission-
        // line from mode.ColorEncoding directly, not a named-family list -- the plan doc's own
        // earlier prose wrongly grouped MC into this doubling family; SstvModeRegistry.
        // CreateMcFamilyMode actually declares ColorEncoding.RgbSequential with a literal
        // ImageHeight, not transmissionUnits * 2. See the plan doc's Capacity section for the full
        // correction. Shared with SstvSessionService.CwId.cs's own OnCwIdModeDetected via
        // RowsPerTransmissionLine (defined in that file), not duplicated -- this calculation must
        // stay correct in exactly one place.
        var rowsPerTransmissionLine = RowsPerTransmissionLine(mode);
        var transmissionLineCount = mode.ImageHeight / rowsPerTransmissionLine;
        var closeThresholdSamples = (long)Math.Round((transmissionLineCount + AudioCloseThresholdMarginLines) * mode.LineDurationMs / 1000.0 * sampleRate);

        var capacityLong = (long)_audioPreRollRingCapacity + closeThresholdSamples + AudioArmedBufferSafetyMarginSamples;
        var capacity = capacityLong > int.MaxValue ? int.MaxValue : (int)capacityLong;

        _audioArmedBuffer = new float[capacity];
        _audioArmedWritten = 0;
        CopyPreRollRingIntoArmedBufferLocked();

        _audioArmedCloseAtLength = _audioArmedWritten + closeThresholdSamples;
        _audioArmedReceptionId = receptionId;
        _audioArmedEpoch = epoch;
        _audioArmedSampleRate = sampleRate;
    }

    /// <summary>Closes the currently armed slice (a no-op if nothing is armed) and hands it to a
    /// background <see cref="Task.Run(Action)"/> for encode+write -- never on the audio drain thread
    /// (round 3 finding: up to a ~112 MB synchronous encode+write on the hot path). Must be called
    /// under <see cref="_audioCaptureLock"/>.</summary>
    private void CloseArmedSliceLocked()
    {
        if (_audioArmedReceptionId == 0 || _audioArmedBuffer is null)
        {
            return;
        }

        var receptionId = _audioArmedReceptionId;
        var sampleRate = _audioArmedSampleRate;
        var samples = _audioArmedBuffer.AsMemory(0, _audioArmedWritten);

        _audioArmedBuffer = null;
        _audioArmedWritten = 0;
        _audioArmedCloseAtLength = 0;
        _audioArmedReceptionId = 0;
        _audioArmedEpoch = 0;

        _ = Task.Run(() => EncodeAndRetainScratchFile(receptionId, samples, sampleRate));
    }

    /// <summary>Background-thread continuation of <see cref="CloseArmedSliceLocked"/>: encodes the
    /// closed slice to a scratch WAV, retains it (applying the eviction cap), and raises
    /// <see cref="AudioSliceReady"/> only once that's all done. Isolated -- a failure here must never
    /// affect the live decode path, which has already moved on by the time this runs.</summary>
    private void EncodeAndRetainScratchFile(long receptionId, ReadOnlyMemory<float> samples, int sampleRate)
    {
        try
        {
            var directory = _autoSaveAudioDirectory ?? DefaultAudioDirectory;
            var scratchRoot = Path.Combine(directory, "scratch");

            // Deliberately called with NO lock held -- see this method's own doc comment (auditor-
            // caught, round 2 code-review) for why disk/process I/O must never run under
            // _audioCaptureLock, the same lock the audio drain thread takes on every capture
            // callback.
            EnsureAudioScratchSessionPrepared(scratchRoot);

            var sessionDirectory = Path.Combine(scratchRoot, _audioScratchSessionId.ToString());
            Directory.CreateDirectory(sessionDirectory);
            var scratchPath = Path.Combine(sessionDirectory, $"{receptionId}.wav");

            WavFile.Write(scratchPath, samples.Span, sampleRate);
            var bytes = new FileInfo(scratchPath).Length;

            lock (_audioCaptureLock)
            {
                _retainedAudioScratchFiles.Add(new AudioScratchEntry(receptionId, scratchPath, bytes));
            }

            EvictExcessScratchFilesAndDelete();

            RaiseAudioSliceReady(receptionId, sampleRate);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.AudioSliceEncodeFailed(_logger, receptionId, ex));
        }
    }

    /// <summary>Oldest-first eviction while over EITHER cap, with the newest retained entry always
    /// exempt -- so a single worst-case slice can never evict itself the instant it's written.
    /// Auditor-caught (round 2 code-review): the actual <see cref="File.Delete(string)"/> calls must
    /// run OUTSIDE <see cref="_audioCaptureLock"/> -- only the in-memory list mutation is fast enough
    /// to hold that lock for; deciding what to evict happens under the lock, deleting happens
    /// after releasing it.</summary>
    private void EvictExcessScratchFilesAndDelete()
    {
        List<AudioScratchEntry>? toDelete = null;
        lock (_audioCaptureLock)
        {
            while (_retainedAudioScratchFiles.Count > 1 &&
                   (_retainedAudioScratchFiles.Count > AudioScratchRetentionCountCap || TotalRetainedScratchBytesLocked() > AudioScratchRetentionByteCap))
            {
                var oldest = _retainedAudioScratchFiles[0];
                _retainedAudioScratchFiles.RemoveAt(0);
                (toDelete ??= []).Add(oldest);
            }
        }

        if (toDelete is null)
        {
            return;
        }

        foreach (var entry in toDelete)
        {
            try
            {
                File.Delete(entry.Path);
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.AudioScratchEvictFailed(_logger, entry.ReceptionId, ex));
            }
        }
    }

    private long TotalRetainedScratchBytesLocked()
    {
        var total = 0L;
        foreach (var entry in _retainedAudioScratchFiles)
        {
            total += entry.Bytes;
        }

        return total;
    }

    /// <summary>Runs once per distinct scratch ROOT (guarded by <see cref="_preparedScratchRoots"/>,
    /// added FIRST so a failure below never retries on every subsequent write for the same root):
    /// creates this session's own scratch subdirectory, writes its PID+start-time liveness marker,
    /// and purges dead sibling sessions -- disk hygiene only, not a correctness requirement (the
    /// <c>scratch/{sessionGuid}/</c> naming already makes a cross-session id collision structurally
    /// impossible on its own). Deliberately lazy (on first scratch write for a given root), not at DI
    /// construction time -- avoids needing the resolved audio directory before it's actually known,
    /// and a delayed purge costs nothing since this is hygiene, not correctness.
    ///
    /// Auditor-caught (round 2 code-review): deliberately does NOT take <see cref="_audioCaptureLock"/>
    /// -- this does real, possibly-slow disk and process I/O (directory enumeration, per-sibling
    /// <see cref="Process.GetProcessById(int)"/> calls, and a recursive delete for each dead sibling),
    /// and that lock is also taken by the audio drain thread on every single capture callback
    /// (<see cref="OnAudioAutoSaveSamplesCaptured"/>). Running this under it would starve the drain
    /// thread for the whole purge and reintroduce, one level removed, exactly the hot-path hazard the
    /// design already moved the encode+write off the drain thread to avoid. <see cref="_scratchPrepareLock"/>
    /// is a separate, tiny lock guarding only the HashSet membership check -- never held across any
    /// actual I/O call.</summary>
    private void EnsureAudioScratchSessionPrepared(string scratchRoot)
    {
        lock (_scratchPrepareLock)
        {
            if (!_preparedScratchRoots.Add(scratchRoot))
            {
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(scratchRoot);
            PurgeDeadSiblingScratchSessions(scratchRoot, _audioScratchSessionId);

            var ownSessionDirectory = Path.Combine(scratchRoot, _audioScratchSessionId.ToString());
            Directory.CreateDirectory(ownSessionDirectory);
            var marker = $"{Environment.ProcessId}\n{Process.GetCurrentProcess().StartTime.Ticks}";
            File.WriteAllText(Path.Combine(ownSessionDirectory, "session.marker"), marker);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.AudioScratchSessionPrepareFailed(_logger, ex));
        }
    }

    /// <summary>Purges sibling session subdirectories confirmed DEAD via their own PID+start-time
    /// marker -- never uses an OS file lock for this (unreliable cross-platform, see the plan doc's
    /// "The join" section for why). Any access error, or a missing/malformed marker, is treated as
    /// "assume live, skip" -- the safe direction, since an un-purged leak only costs disk.</summary>
    private static void PurgeDeadSiblingScratchSessions(string scratchRoot, Guid ownSessionId)
    {
        var ownSessionDirectoryName = ownSessionId.ToString();
        foreach (var sessionDirectory in Directory.EnumerateDirectories(scratchRoot))
        {
            if (string.Equals(Path.GetFileName(sessionDirectory), ownSessionDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var markerPath = Path.Combine(sessionDirectory, "session.marker");
                if (!File.Exists(markerPath))
                {
                    continue;
                }

                var parts = File.ReadAllText(markerPath).Split('\n');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var startTimeTicks))
                {
                    continue;
                }

                bool isDead;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    isDead = process.StartTime.Ticks != startTimeTicks;
                }
                catch (ArgumentException)
                {
                    // Confirmed: no process with this PID exists.
                    isDead = true;
                }
                catch (Exception)
                {
                    // Any other error (permission denied, a race with the owning session, ...) --
                    // assume live, skip.
                    isDead = false;
                }

                if (isDead)
                {
                    Directory.Delete(sessionDirectory, recursive: true);
                }
            }
            catch (Exception)
            {
                // Never let one bad sibling directory abort the purge for the rest.
            }
        }
    }

    private void EnsureAudioPreRollRingAllocatedLocked()
    {
        if (_audioPreRollRingCapacity != 0)
        {
            return;
        }

        var sampleRate = _decoder.SampleRate;
        _audioPreRollRingCapacity = (int)((long)AudioPreRollMs * sampleRate / 1000);
        _audioPreRollRing = new float[_audioPreRollRingCapacity];
        _audioPreRollWritePos = 0;
        _audioPreRollFilledCount = 0;
    }

    private void WriteToPreRollRingLocked(ReadOnlySpan<float> samples)
    {
        if (_audioPreRollRingCapacity == 0)
        {
            return;
        }

        if (samples.Length >= _audioPreRollRingCapacity)
        {
            // This chunk alone fills (or overflows) the whole ring -- only its tail matters.
            samples[^_audioPreRollRingCapacity..].CopyTo(_audioPreRollRing);
            _audioPreRollWritePos = 0;
            _audioPreRollFilledCount = _audioPreRollRingCapacity;
            return;
        }

        var firstPartLength = Math.Min(samples.Length, _audioPreRollRingCapacity - _audioPreRollWritePos);
        samples[..firstPartLength].CopyTo(_audioPreRollRing.AsSpan(_audioPreRollWritePos));
        var remaining = samples.Length - firstPartLength;
        if (remaining > 0)
        {
            samples[firstPartLength..].CopyTo(_audioPreRollRing);
        }

        _audioPreRollWritePos = (_audioPreRollWritePos + samples.Length) % _audioPreRollRingCapacity;
        _audioPreRollFilledCount = Math.Min(_audioPreRollRingCapacity, _audioPreRollFilledCount + samples.Length);
    }

    /// <summary>Copies the ring's current contents into the just-allocated armed buffer, OLDEST
    /// sample first. Race-free: both the ring writes (in <see cref="OnAudioAutoSaveSamplesCaptured"/>)
    /// and this arm (from <see cref="OnAudioAutoSaveModeDetected"/>, raised synchronously from
    /// <see cref="PushSamplesToDecoder"/>) happen on the same audio-drain call, under the same lock.</summary>
    private void CopyPreRollRingIntoArmedBufferLocked()
    {
        if (_audioPreRollFilledCount == 0)
        {
            return;
        }

        var buffer = _audioArmedBuffer!;
        if (_audioPreRollFilledCount < _audioPreRollRingCapacity)
        {
            // The ring hasn't wrapped yet -- [0.._audioPreRollFilledCount) is already in order.
            _audioPreRollRing.AsSpan(0, _audioPreRollFilledCount).CopyTo(buffer);
            _audioArmedWritten = _audioPreRollFilledCount;
        }
        else
        {
            // Wrapped: the oldest sample is at _audioPreRollWritePos (the next slot about to be
            // overwritten) -- read the tail from there, then the head.
            var tailLength = _audioPreRollRingCapacity - _audioPreRollWritePos;
            _audioPreRollRing.AsSpan(_audioPreRollWritePos, tailLength).CopyTo(buffer);
            _audioPreRollRing.AsSpan(0, _audioPreRollWritePos).CopyTo(buffer.AsSpan(tailLength));
            _audioArmedWritten = _audioPreRollRingCapacity;
        }
    }

    /// <summary>Appends live samples to the armed buffer, clamping to available capacity rather than
    /// throwing if it were ever somehow undersized (defensive -- <see cref="ArmLocked"/>'s own
    /// safety margin should make this unreachable in practice). Must be called under
    /// <see cref="_audioCaptureLock"/>.</summary>
    private void AppendToArmedBufferLocked(ReadOnlySpan<float> samples)
    {
        var buffer = _audioArmedBuffer!;
        var available = buffer.Length - _audioArmedWritten;
        var toCopy = Math.Min(available, samples.Length);
        samples[..toCopy].CopyTo(buffer.AsSpan(_audioArmedWritten));
        _audioArmedWritten += toCopy;
    }

    private void RaiseAudioSliceReady(long receptionId, int sampleRate)
    {
        try
        {
            AudioSliceReady?.Invoke(receptionId, sampleRate);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.AudioSliceReadySubscriberFailed(_logger, receptionId, ex));
        }
    }

    private void RaiseAudioCaptureReset()
    {
        try
        {
            AudioCaptureReset?.Invoke();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.AudioCaptureResetSubscriberFailed(_logger, ex));
        }
    }

    /// <summary>Fallback default when <see cref="SetAudioDirectory"/> was never called -- matches
    /// <c>ReceiveHistorySettings.ResolveAudioDirectory</c>'s own default (a small, accepted
    /// duplication: this class deliberately has no <c>Core.Logbook</c>-settings dependency of its
    /// own, unlike the not-yet-built <c>RxAudioAutoSaver</c>).</summary>
    private static string DefaultAudioDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "ScanlineStudio", "History");

    private sealed record AudioScratchEntry(long ReceptionId, string Path, long Bytes);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "The auto-save-audio SamplesCaptured handler threw {Count} time(s), most recently:")]
        public static partial void AudioAutoSavePushSamplesFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to encode/retain the auto-saved audio slice for reception {ReceptionId}")]
        public static partial void AudioSliceEncodeFailed(ILogger logger, long receptionId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to move the auto-saved audio slice for reception {ReceptionId} to its final path")]
        public static partial void AudioSliceSaveFailed(ILogger logger, long receptionId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to evict a retained auto-saved audio scratch file for reception {ReceptionId}")]
        public static partial void AudioScratchEvictFailed(ILogger logger, long receptionId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to prepare this session's auto-save-audio scratch directory (startup purge/marker)")]
        public static partial void AudioScratchSessionPrepareFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "An AudioSliceReady subscriber threw for reception {ReceptionId}")]
        public static partial void AudioSliceReadySubscriberFailed(ILogger logger, long receptionId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "An AudioCaptureReset subscriber threw")]
        public static partial void AudioCaptureResetSubscriberFailed(ILogger logger, Exception ex);
    }
}
