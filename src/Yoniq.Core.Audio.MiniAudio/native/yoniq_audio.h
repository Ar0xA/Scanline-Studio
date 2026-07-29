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
int yoniq_audio_context_init(char *backend_name_out);

void yoniq_audio_context_uninit(void);

/* Fills out_devices (a caller-allocated array of max_count entries) with up to max_count
 * capture (is_capture != 0) or playback (is_capture == 0) devices. Returns the actual count
 * written (0..max_count), or -1 on error. Cheap: this is basic-info-only enumeration (id + name),
 * it does not open/probe any device. */
int yoniq_audio_enumerate_devices(int is_capture, yoniq_audio_device_info *out_devices, int max_count);

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
int yoniq_audio_get_native_formats(const char *device_id, int is_capture, yoniq_audio_native_format *out_formats, int max_count);

/* Spike/gate test only (not the production capture path -- see piece Audio 5 for that): opens
 * the named capture device (by the id string yoniq_audio_enumerate_devices returned), captures
 * for duration_ms milliseconds, and writes the peak absolute sample value observed to *peak_out.
 * A silent device (or one that never actually delivers data) reports peak_out == 0. Returns 0 on
 * success, nonzero if the device could not be opened or started. */
int yoniq_audio_spike_capture_test(const char *device_id, int duration_ms, float *peak_out);

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
yoniq_audio_ring *yoniq_audio_ring_create(int capacity_frames, int channels);

void yoniq_audio_ring_destroy(yoniq_audio_ring *ring);

/* Writes up to frame_count frames from data (channels*frame_count floats, interleaved) into the
 * ring. Never blocks: if the ring doesn't have room for all of frame_count, writes as many as fit
 * and returns that (possibly smaller) count -- the same "may not accept everything" shape as
 * IAudioEngine.EnqueuePlaybackSamples on the C# side (see piece Audio 2). Returns -1 on error. */
int yoniq_audio_ring_write(yoniq_audio_ring *ring, const float *data, int frame_count);

/* Reads up to frame_count frames from the ring into out_data. Never blocks: if fewer than
 * frame_count frames are available, reads as many as are and returns that (possibly smaller)
 * count. Returns -1 on error. */
int yoniq_audio_ring_read(yoniq_audio_ring *ring, float *out_data, int frame_count);

/* Returns how many frames are currently buffered in the ring, available to be read without
 * blocking. Returns -1 on error. */
int yoniq_audio_ring_available_read(yoniq_audio_ring *ring);

/*
 * Piece Audio 5: the real capture path. A real-time native callback (never entering managed
 * code, see LevelAgc-style reasoning already established for the ring itself in piece Audio 3)
 * writes captured frames into an internal yoniq_audio_ring; a managed drain thread/loop reads
 * from it via yoniq_audio_capture_session_read. Requests mono f32 at the caller's chosen sample
 * rate -- miniaudio's own data converter handles resample/downmix from whatever the device's
 * real native format is, transparently.
 */

typedef struct yoniq_audio_capture_session yoniq_audio_capture_session;

/* Opens and starts capturing from the named device. ring_capacity_frames sizes the internal
 * buffer between the real-time callback and the managed drain side -- if the drain side falls
 * behind and this fills, the real-time callback drops the newest incoming frames (never blocks,
 * matching IAudioEngine's documented overrun policy, piece Audio 2). Returns NULL on failure. */
yoniq_audio_capture_session *yoniq_audio_capture_session_open(const char *device_id, int sample_rate, int ring_capacity_frames);

/* Stops and destroys the session. Safe to call on a session that failed to fully start (i.e. a
 * partially-initialized state yoniq_audio_capture_session_open itself cleans up on its own error
 * paths -- this is for a session that DID open successfully and is now done with). */
void yoniq_audio_capture_session_close(yoniq_audio_capture_session *session);

/* Reads up to frame_count frames of already-captured mono f32 audio into out_data. Never blocks;
 * returns the number of frames actually available (0..frame_count), or -1 on error. Call this
 * repeatedly from a managed drain thread/loop, never from the real-time callback itself. */
int yoniq_audio_capture_session_read(yoniq_audio_capture_session *session, float *out_data, int frame_count);

/* Returns nonzero (and clears the flag) if the device's own notification callback reported the
 * stream stopped since the session was opened or this was last checked. The notification
 * callback must be wired in at ma_device_config/ma_device_init time (this is why it lives here,
 * in piece Audio 5, rather than bolted on later). Piece Audio 8 verified this against a real
 * device disappearing mid-capture (a virtual sink unloaded while its monitor was open) and found
 * this does NOT become nonzero in that case on the PulseAudio backend -- only an actual
 * server-side suspend/resume raises it. See MiniAudioCaptureSession.HasStopped's own doc comment
 * on the C# side for the full finding and what callers should watch instead. */
int yoniq_audio_capture_session_check_and_clear_stopped(yoniq_audio_capture_session *session);

/*
 * Piece Audio 6: the real playback path -- the mirror image of piece Audio 5's capture session.
 * Managed code writes samples into an internal ring via yoniq_audio_playback_session_write; the
 * real-time native callback (never entering managed code) pulls from that ring to fill the
 * device's own output buffer, padding with silence on underrun rather than emitting
 * garbage/uninitialized audio.
 */

typedef struct yoniq_audio_playback_session yoniq_audio_playback_session;

/* Opens and starts playing to the named device. ring_capacity_frames sizes the buffer between
 * managed writes and the real-time pull callback. Returns NULL on failure. */
yoniq_audio_playback_session *yoniq_audio_playback_session_open(const char *device_id, int sample_rate, int ring_capacity_frames);

void yoniq_audio_playback_session_close(yoniq_audio_playback_session *session);

/* Enqueues up to frame_count frames from data for playback. Never blocks: if the ring doesn't
 * have room for all of it, writes as many as fit and returns that (possibly smaller) count --
 * this is the direct native backing for IAudioEngine.EnqueuePlaybackSamples's own "returns
 * accepted count" contract (piece Audio 2). Returns -1 on error. */
int yoniq_audio_playback_session_write(yoniq_audio_playback_session *session, const float *data, int frame_count);

/* Returns how many enqueued frames have not yet actually been played (still sitting in the ring).
 * Callers implementing IAudioEngine.StopPlaybackAsync's "block until everything has actually
 * played out" contract (piece Audio 2) should poll this down to 0 before closing the session --
 * closing early would truncate the tail of a real transmission. Returns -1 on error. */
int yoniq_audio_playback_session_pending_frames(yoniq_audio_playback_session *session);

/* Returns the cumulative count of frames the real-time callback has had to pad with silence
 * because the ring ran dry (an underrun) since the session was opened. Distinguishing an expected
 * underrun (nothing left to play, transmission legitimately finished) from an unwanted one
 * (managed code fell behind mid-transmission) requires knowing how many frames were actually
 * enqueued vs. expected to play -- context only the caller has, not this shim -- so this is a raw
 * counter for the caller to interpret, not a verdict. */
int yoniq_audio_playback_session_underrun_count(yoniq_audio_playback_session *session);

int yoniq_audio_playback_session_check_and_clear_stopped(yoniq_audio_playback_session *session);

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
 * filtering entirely, or an explicit order. Internally loops
 * ma_resampler_process_pcm_frames until all of input_frame_count has been consumed. Returns the
 * number of output frames actually written, or -1 on error (including output_capacity_frames
 * being too small to hold the fully resampled result). */
int yoniq_audio_resample_f32(const float *input, int input_frame_count, int sample_rate_in,
                              int sample_rate_out, int lpf_order, float *output, int output_capacity_frames);

#ifdef __cplusplus
}
#endif

#endif
