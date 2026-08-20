#ifndef YONIQ_AUDIO_H
#define YONIQ_AUDIO_H

/*
 * Our own designed C ABI around miniaudio, deliberately NOT exposing any miniaudio struct
 * directly to managed code: ma_device's layout varies by platform AND by which MA_NO_* flags
 * this file is compiled with, so no struct marshaled on the C# side could ever be safely pinned
 * to a specific layout. Every type here is a flat, fixed-size, unconditional POD struct whose
 * offsets the C compiler computes for us.
 */

#ifdef __cplusplus
extern "C" {
#endif

/* Opus-review fix: on ELF (Linux)/Mach-O (macOS), every non-static symbol is exported by default,
 * which is why this shim worked there without any explicit export markers. MSVC does the opposite
 * -- nothing in a DLL is visible to a P/Invoke caller unless explicitly marked dllexport, and
 * miniaudio's own "Windows build requires no special linking" documentation (which the .csproj's
 * BuildNativeShimWindows target quotes) is about include paths/link libraries, not DLL symbol
 * visibility, since miniaudio is normally compiled directly into its consumer rather than exposed
 * across a DLL boundary the way this shim is. Without this, every single P/Invoke in NativeAudio.cs
 * would throw EntryPointNotFoundException on Windows. */
#if defined(_WIN32)
#define YONIQ_AUDIO_API __declspec(dllexport)
#else
#define YONIQ_AUDIO_API
#endif

#define YONIQ_AUDIO_ID_SIZE 256
#define YONIQ_AUDIO_NAME_SIZE 256
#define YONIQ_AUDIO_BACKEND_NAME_SIZE 32

typedef struct
{
    char id[YONIQ_AUDIO_ID_SIZE];     /* backend-native device id, as a UTF-8/ASCII string */
    char name[YONIQ_AUDIO_NAME_SIZE]; /* UTF-8 device name */
    int is_default;                   /* nonzero if this is the backend's default device */
} yoniq_audio_device_info;

/* Initializes the process-wide audio context with an explicit, per-platform backend list (never
 * the native library's own "default" list -- see the .c file for why that's unsafe). Writes the
 * resolved backend's name into backend_name_out (must be >= YONIQ_AUDIO_BACKEND_NAME_SIZE bytes).
 * Returns 0 on success, nonzero on failure. Must be called exactly once before any other function
 * here; not safe to call again without yoniq_audio_context_uninit first. */
YONIQ_AUDIO_API int yoniq_audio_context_init(char *backend_name_out);

YONIQ_AUDIO_API void yoniq_audio_context_uninit(void);

/* Fills out_devices (a caller-allocated array of max_count entries) with up to max_count
 * capture (is_capture != 0) or playback (is_capture == 0) devices. Returns the actual count
 * written (0..max_count), or -1 on error. Cheap: this is basic-info-only enumeration (id + name),
 * it does not open/probe any device. */
YONIQ_AUDIO_API int yoniq_audio_enumerate_devices(int is_capture, yoniq_audio_device_info *out_devices, int max_count);

typedef struct
{
    int channels;    /* 0 means "any channel count supported", matching miniaudio's own convention */
    int sample_rate; /* 0 means "any sample rate supported", matching miniaudio's own convention */
} yoniq_audio_native_format;

/* Probes the named device's native format capabilities -- unlike yoniq_audio_enumerate_devices,
 * this opens/queries the device briefly, which is slower and can fail on a busy or
 * exclusive-mode device. On backends that resample/mix server-side (PulseAudio/PipeWire in
 * particular), the reported format(s) may just be the server's own current format, not a real
 * capability list -- callers must not treat this as authoritative for filtering devices. Returns
 * the number of entries written to out_formats (0..max_count), or -1 on error. */
YONIQ_AUDIO_API int yoniq_audio_get_native_formats(const char *device_id, int is_capture, yoniq_audio_native_format *out_formats, int max_count);

/* Spike/gate test only (not the production capture path -- see piece Audio 5 for that): opens
 * the named capture device (by the id string yoniq_audio_enumerate_devices returned), captures
 * for duration_ms milliseconds, and writes the peak absolute sample value observed to *peak_out.
 * A silent device (or one that never actually delivers data) reports peak_out == 0. Returns 0 on
 * success, nonzero if the device could not be opened or started. */
YONIQ_AUDIO_API int yoniq_audio_spike_capture_test(const char *device_id, int duration_ms, float *peak_out);

/*
 * Piece Audio 3: a standalone lock-free single-producer/single-consumer ring buffer, built on
 * miniaudio's own ma_pcm_rb (already implemented and battle-tested in the library, not
 * reinvented). Deliberately independent of any real audio device here -- proving the ring's own
 * produce/consume/wraparound correctness in isolation, before piece Audio 5 wires a real capture
 * device's native callback to write into one of these and a managed drain thread to read from it.
 */

typedef struct yoniq_audio_ring yoniq_audio_ring;

/* Creates a ring buffer holding up to capacity_frames frames of `channels` channels each (f32).
 * Returns NULL on allocation/init failure. */
YONIQ_AUDIO_API yoniq_audio_ring *yoniq_audio_ring_create(int capacity_frames, int channels);

YONIQ_AUDIO_API void yoniq_audio_ring_destroy(yoniq_audio_ring *ring);

/* Writes up to frame_count frames from data (channels*frame_count floats, interleaved) into the
 * ring. Never blocks: if the ring doesn't have room for all of frame_count, writes as many as fit
 * and returns that (possibly smaller) count -- the same "may not accept everything" shape as
 * IAudioEngine.EnqueuePlaybackSamples on the C# side (see piece Audio 2). Returns -1 on error. */
YONIQ_AUDIO_API int yoniq_audio_ring_write(yoniq_audio_ring *ring, const float *data, int frame_count);

/* Reads up to frame_count frames from the ring into out_data. Never blocks: if fewer than
 * frame_count frames are available, reads as many as are and returns that (possibly smaller)
 * count. Returns -1 on error. */
YONIQ_AUDIO_API int yoniq_audio_ring_read(yoniq_audio_ring *ring, float *out_data, int frame_count);

/* Returns how many frames are currently buffered in the ring, available to be read without
 * blocking. Returns -1 on error. */
YONIQ_AUDIO_API int yoniq_audio_ring_available_read(yoniq_audio_ring *ring);

/*
 * Piece Audio 5: the real capture path. A real-time native callback (never entering managed
 * code, see LevelAgc-style reasoning already established for the ring itself in piece Audio 3)
 * writes captured frames into an internal yoniq_audio_ring; a managed drain thread/loop reads
 * from it via yoniq_audio_capture_session_read. Requests mono f32 at the caller's chosen sample
 * rate -- miniaudio's own data converter handles resample/downmix from whatever the device's
 * real native format is, transparently.
 */

typedef struct yoniq_audio_capture_session yoniq_audio_capture_session;

/* Shared by both yoniq_audio_capture_session_open and yoniq_audio_playback_session_open --
 * identical fields, one type rather than two structurally-identical ones. period_size_in_frames
 * and periods are new (sound-FIFO-buffer-size backlog item): 0 in either means "leave miniaudio's
 * own default period/backend heuristic alone" -- the exact behavior every caller got before these
 * two fields existed, so a zero-initialized options struct is always safe-by-construction. Only
 * set on ma_device_config when non-zero (see both _open functions' bodies).
 *
 * channels/channel_select are new (stereo-capture-source/stereo-TX backlog item -- NOT a
 * confirmed legacy port, see MiniAudioCaptureSession/PlaybackSession's own doc comments on their
 * matching constructor params for the explicit assumption flag). channels is 1 (mono, today's
 * only pre-existing behavior, zero-initialized default) or 2 (stereo device open -- required for
 * either a real Left/Right capture split, or duplicating a mono TX signal to both output
 * channels). channel_select only matters for CAPTURE when channels==2: 0 = unused/ignored,
 * 1 = Left (even sample indices of the interleaved input), 2 = Right (odd indices). Ignored
 * entirely for playback opens -- stereo TX always duplicates the same mono ring content to both
 * channels, there is no "which channel" choice on the output side. This shim's own ring buffers
 * (yoniq_audio_ring) and everything on the managed side of the boundary
 * (IAudioEngine.SamplesCaptured/EnqueuePlaybackSamples) stay mono always -- channels only ever
 * exist between the native device and this shim's own callbacks; see capture_session_data_callback
 * and playback_session_data_callback for exactly where the stereo<->mono conversion happens. */
typedef struct yoniq_audio_open_options
{
    int sample_rate;
    int ring_capacity_frames;
    int period_size_in_frames;
    int periods;
    int channels;       /* 1 or 2 */
    int channel_select; /* 0=unused, 1=Left, 2=Right -- capture-only, see doc comment above */
} yoniq_audio_open_options;

/* Opens and starts capturing from the named device. options->ring_capacity_frames sizes the
 * internal buffer between the real-time callback and the managed drain side -- if the drain side
 * falls behind and this fills, the real-time callback drops the newest incoming frames (never
 * blocks, matching IAudioEngine's documented overrun policy, piece Audio 2).
 * options->period_size_in_frames/periods (0 = miniaudio's own default) additionally tune the
 * underlying hardware/backend buffer size -- a separate, lower-level knob from
 * ring_capacity_frames, which only sizes this shim's own managed-drain-side ring. Returns NULL on
 * failure. */
YONIQ_AUDIO_API yoniq_audio_capture_session *yoniq_audio_capture_session_open(const char *device_id, const yoniq_audio_open_options *options);

/* Stops and destroys the session. Safe to call on a session that failed to fully start (i.e. a
 * partially-initialized state yoniq_audio_capture_session_open itself cleans up on its own error
 * paths -- this is for a session that DID open successfully and is now done with). */
YONIQ_AUDIO_API void yoniq_audio_capture_session_close(yoniq_audio_capture_session *session);

/* Reads up to frame_count frames of already-captured mono f32 audio into out_data. Never blocks;
 * returns the number of frames actually available (0..frame_count), or -1 on error. Call this
 * repeatedly from a managed drain thread/loop, never from the real-time callback itself. */
YONIQ_AUDIO_API int yoniq_audio_capture_session_read(yoniq_audio_capture_session *session, float *out_data, int frame_count);

/* Returns nonzero (and clears the flag) if the device's own notification callback reported the
 * stream stopped since the session was opened or this was last checked. The notification
 * callback must be wired in at ma_device_config/ma_device_init time (this is why it lives here,
 * in piece Audio 5, rather than bolted on later). Piece Audio 8 verified this against a real
 * device disappearing mid-capture (a virtual sink unloaded while its monitor was open) and found
 * this does NOT become nonzero in that case on the PulseAudio backend -- only an actual
 * server-side suspend/resume raises it. See MiniAudioCaptureSession.HasStopped's own doc comment
 * on the C# side for the full finding and what callers should watch instead. */
YONIQ_AUDIO_API int yoniq_audio_capture_session_check_and_clear_stopped(yoniq_audio_capture_session *session);

/* Piece Engine 0: returns the cumulative count of real-time callbacks in which the capture side
 * could not write all of the frames it received into the ring (i.e. an overrun -- the managed
 * drain side fell behind and the newest frames were dropped, matching IAudioEngine's documented
 * overrun policy above). Mirrors yoniq_audio_playback_session_underrun_count's own shape and
 * caveat exactly: a raw counter for the caller to interpret (a nonzero count after a session ends
 * cleanly is not itself an error), not a verdict. */
YONIQ_AUDIO_API int yoniq_audio_capture_session_overrun_count(yoniq_audio_capture_session *session);

/*
 * Piece Audio 6: the real playback path -- the mirror image of piece Audio 5's capture session.
 * Managed code writes samples into an internal ring via yoniq_audio_playback_session_write; the
 * real-time native callback (never entering managed code) pulls from that ring to fill the
 * device's own output buffer, padding with silence on underrun rather than emitting
 * garbage/uninitialized audio.
 */

typedef struct yoniq_audio_playback_session yoniq_audio_playback_session;

/* Opens and starts playing to the named device. options->ring_capacity_frames sizes the buffer
 * between managed writes and the real-time pull callback; options->period_size_in_frames/periods
 * tune the underlying hardware/backend buffer size (0 = miniaudio's own default) -- see
 * yoniq_audio_open_options' own doc comment and yoniq_audio_capture_session_open's identical
 * shape. Returns NULL on failure. */
YONIQ_AUDIO_API yoniq_audio_playback_session *yoniq_audio_playback_session_open(const char *device_id, const yoniq_audio_open_options *options);

YONIQ_AUDIO_API void yoniq_audio_playback_session_close(yoniq_audio_playback_session *session);

/* Enqueues up to frame_count frames from data for playback. Never blocks: if the ring doesn't
 * have room for all of it, writes as many as fit and returns that (possibly smaller) count --
 * this is the direct native backing for IAudioEngine.EnqueuePlaybackSamples's own "returns
 * accepted count" contract (piece Audio 2). Returns -1 on error. */
YONIQ_AUDIO_API int yoniq_audio_playback_session_write(yoniq_audio_playback_session *session, const float *data, int frame_count);

/* Returns how many enqueued frames have not yet actually been played (still sitting in the ring).
 * Callers implementing IAudioEngine.StopPlaybackAsync's "block until everything has actually
 * played out" contract (piece Audio 2) should poll this down to 0 before closing the session --
 * closing early would truncate the tail of a real transmission. Returns -1 on error. */
YONIQ_AUDIO_API int yoniq_audio_playback_session_pending_frames(yoniq_audio_playback_session *session);

/* Functional-audit fix (Tier A Batch 1 re-audit round 8): this comment previously said "cumulative
 * count of FRAMES the real-time callback has had to pad with silence" -- wrong, and a real ABI-
 * contract mismatch, not just imprecise wording. The actual, always-correct implementation
 * (yoniq_audio.c's own underrun_count field comment) increments this exactly ONCE PER CALLBACK
 * that padded with silence, never once per padded frame -- an event counter, not a frame counter.
 * A caller computing e.g. "seconds of audio dropped" as underrun_count/sample_rate would be wrong
 * by roughly the period size, growing with buffer size. Returns the cumulative count of REAL-TIME
 * CALLBACKS in which the callback has had to pad with silence because the ring ran dry (an
 * underrun) since the session was opened. Distinguishing an expected underrun (nothing left to
 * play, transmission legitimately finished) from an unwanted one (managed code fell behind
 * mid-transmission) requires knowing how many frames were actually enqueued vs. expected to play --
 * context only the caller has, not this shim -- so this is a raw counter for the caller to
 * interpret, not a verdict. */
YONIQ_AUDIO_API int yoniq_audio_playback_session_underrun_count(yoniq_audio_playback_session *session);

YONIQ_AUDIO_API int yoniq_audio_playback_session_check_and_clear_stopped(yoniq_audio_playback_session *session);

/*
 * Piece Audio 6b: device-free, one-shot batch resampling using miniaudio's own ma_resampler --
 * exists purely to let a CI-safe test measure whether the default linear resampler (the same one
 * miniaudio's own data converter uses internally for capture/playback sample-rate conversion,
 * pieces Audio 5/6) meaningfully degrades the existing SSTV encode/decode round trip when a
 * signal is forced through an upsample-then-downsample pair, simulating "encoded at 44100Hz,
 * played to/captured from a 48000Hz native device." No device is opened here at all.
 */

/* Resamples input (input_frame_count mono f32 frames at sample_rate_in) to sample_rate_out,
 * writing up to output_capacity_frames frames into output. lpf_order selects the linear
 * resampler's low-pass filter order: pass -1 for miniaudio's own default (4), 0 to disable
 * filtering entirely, or an explicit order. Internally loops ma_resampler_process_pcm_frames
 * until all of input_frame_count has been consumed. Returns the number of output frames actually
 * written, or -1 on error (including output_capacity_frames being too small to hold the fully
 * resampled result). Opus-review correction: this does NOT flush the resampler's own internal
 * latency/tail after the last real input frame, so a handful of trailing output frames (on the
 * order of the resampler's reported output latency) are dropped rather than produced -- fine for
 * this function's actual purpose (measuring the linear resampler's steady-state quality impact,
 * see ResamplerQualityRoundTripTests), but callers wanting a bit-complete conversion would need a
 * final flush call (passing NULL as the input) that this function does not perform. */
YONIQ_AUDIO_API int yoniq_audio_resample_f32(const float *input, int input_frame_count, int sample_rate_in,
                              int sample_rate_out, int lpf_order, float *output, int output_capacity_frames);

#ifdef __cplusplus
}
#endif

#endif
