/*
 * Piece Audio 1: miniaudio shim spike. Everything below is scoped narrowly -- enumerate devices
 * with an explicit backend list, and prove real (non-silent) audio can be captured from a named
 * virtual sink's monitor. Production capture/playback (ring buffer inside this file, hot-plug
 * notification, resample policy) are separately-scoped later pieces (Audio 3/5/6/8) -- this file
 * intentionally does not attempt any of that yet.
 */

#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_ENGINE
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#include "scanline_audio.h"
#include <string.h>
#include <stdlib.h>

/* Second-opus-review fix: the mutex itself is now statically initialized and NEVER destroyed --
 * SRWLOCK_INIT/PTHREAD_MUTEX_INITIALIZER are valid, lockable states with no init/destroy call
 * needed at all. The previous version called scanline_mutex_init/_destroy from
 * scanline_audio_context_init/_uninit, which meant: (1) every other entry point below checked
 * g_context_initialized *before* locking, a TOCTOU window where a concurrent
 * scanline_audio_context_uninit could destroy a mutex this thread was about to lock (locking a
 * destroyed pthread_mutex_t/CRITICAL_SECTION is undefined behavior), and (2)
 * pthread_mutex_destroy/DeleteCriticalSection on a currently-locked mutex is itself undefined
 * behavior. Making the mutex permanently valid removes both problems at once: every entry point
 * below now locks FIRST and checks g_context_initialized *while holding the lock*, so the flag
 * check and the context-uninit that clears it can never interleave. */
#if defined(_WIN32)
#include <windows.h>
typedef SRWLOCK scanline_mutex;
#define SCANLINE_MUTEX_INIT SRWLOCK_INIT
static void scanline_mutex_lock(scanline_mutex *m) { AcquireSRWLockExclusive(m); }
static void scanline_mutex_unlock(scanline_mutex *m) { ReleaseSRWLockExclusive(m); }
#else
#include <pthread.h>
typedef pthread_mutex_t scanline_mutex;
#define SCANLINE_MUTEX_INIT PTHREAD_MUTEX_INITIALIZER
static void scanline_mutex_lock(scanline_mutex *m) { pthread_mutex_lock(m); }
static void scanline_mutex_unlock(scanline_mutex *m) { pthread_mutex_unlock(m); }
#endif

static ma_context g_context;
static int g_context_initialized = 0;

/* Opus-review fix: ma_context_enumerate_devices, ma_context_get_device_info, and the
 * source/sink-info lookups ma_device_init performs during open all iterate the *same*
 * pContext->pulse.pMainLoop (confirmed by reading the pulse backend directly: enumeration takes
 * miniaudio's own deviceEnumLock, get_device_info takes a *different* deviceInfoLock, and
 * ma_device_init's own source/sink lookups take neither) -- and pa_mainloop itself is not
 * thread-safe. Two of these running concurrently on different managed threads (e.g. two
 * MiniAudioDeviceEnumerator instances refreshing at once, or a refresh racing a session open) is
 * an unsynchronized concurrent pa_mainloop_iterate. Once a device is actually running it has its
 * own separate per-device mainloop/context (confirmed in ma_device_uninit__pulse, which only ever
 * touches pDevice->pulse.pPulseContext/pMainLoop, never the shared g_context) -- so this mutex
 * only needs to guard the context-level entry points below, never a session's read/write/close.
 *
 * Second-opus-review note: ma_device_init's own source/sink-info lookup goes through the exact
 * same unbounded ma_wait_for_operation__pulse loop Piece Audio 8 root-caused for session close --
 * so a device that vanishes in the narrow window between being enumerated and being opened could
 * still wedge this mutex forever, blocking every other enumerate/probe/session-open in the
 * process until the server responds (which, per Audio 8, it may never do). This is a real,
 * accepted, narrow residual risk, not a full fix: ma_device_start is deliberately excluded from
 * every critical section below (it only touches the *device's own* mainloop once init succeeds,
 * confirmed directly against miniaudio.h), which shrinks the exposure to init alone, but doesn't
 * eliminate it. A full fix would need the same background-thread-plus-timeout treatment
 * MiniAudioCaptureSession/PlaybackSession's CloseTimeout gives close -- except abandoning a
 * thread that still holds this *shared* mutex is worse than abandoning one that holds only its
 * own session's handle, so that isn't a drop-in port of the same pattern. Revisit if this ever
 * becomes a real-world problem, not a hypothetical one.
 *
 * Third-opus-review addendum: generic ma_device_init also falls through to ma_device_get_info,
 * which for PulseAudio (no onDeviceGetInfo callback registered for this backend) resolves to
 * ma_context_get_device_info -- the *same* shared-mainloop call scanline_audio_get_native_formats
 * makes -- so ma_device_init touches the shared mainloop twice per open, not once. Both are
 * still inside the single critical section below, so this doesn't change correctness, only
 * widens (slightly) how long the above residual risk's window actually is in practice. */
static scanline_mutex g_context_mutex = SCANLINE_MUTEX_INIT;

int scanline_audio_context_init(char *backend_name_out)
{
    scanline_mutex_lock(&g_context_mutex);

    if (g_context_initialized)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return -1; /* misuse: already initialized */
    }

    /* Explicit backend list, never miniaudio's own "default" list -- confirmed directly against
     * the pinned miniaudio.h (see class doc comment on the C# side for full reasoning): the
     * default priority order tries sndio/audio4/oss *before* pulseaudio on some platforms, and
     * always includes ma_backend_null as the lowest-priority terminator, meaning ma_context_init
     * with a NULL backend list can silently succeed against a fake, always-silent device. */
#if defined(_WIN32)
    ma_backend backends[] = {ma_backend_wasapi};
#elif defined(__APPLE__)
    ma_backend backends[] = {ma_backend_coreaudio};
#else
    ma_backend backends[] = {ma_backend_pulseaudio, ma_backend_alsa, ma_backend_jack};
#endif
    ma_uint32 backend_count = sizeof(backends) / sizeof(backends[0]);

    ma_context_config config = ma_context_config_init();
    ma_result result = ma_context_init(backends, backend_count, &config, &g_context);
    if (result != MA_SUCCESS)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return (int)result;
    }

    g_context_initialized = 1;

    if (backend_name_out != NULL)
    {
        const char *name = ma_get_backend_name(g_context.backend);
        size_t len = strlen(name);
        if (len >= SCANLINE_AUDIO_BACKEND_NAME_SIZE)
        {
            len = SCANLINE_AUDIO_BACKEND_NAME_SIZE - 1;
        }
        memcpy(backend_name_out, name, len);
        backend_name_out[len] = '\0';
    }

    scanline_mutex_unlock(&g_context_mutex);
    return 0;
}

void scanline_audio_context_uninit(void)
{
    scanline_mutex_lock(&g_context_mutex);
    if (g_context_initialized)
    {
        ma_context_uninit(&g_context);
        g_context_initialized = 0;
    }
    scanline_mutex_unlock(&g_context_mutex);
}

/* Converts a backend-native ma_device_id into our own ABI's plain string convention. Only the
 * backends this shim's explicit backend list (above) can ever select are handled -- anything
 * else is a programming error (a backend we didn't ask for), not a runtime condition to degrade
 * gracefully from. */
static void device_id_to_string(ma_backend backend, const ma_device_id *id, char *buf, size_t buf_size)
{
    buf[0] = '\0';
    switch (backend)
    {
    case ma_backend_pulseaudio:
        strncpy(buf, id->pulse, buf_size - 1);
        break;
    case ma_backend_alsa:
        strncpy(buf, id->alsa, buf_size - 1);
        break;
    case ma_backend_jack:
        snprintf(buf, buf_size, "%d", id->jack);
        break;
    case ma_backend_wasapi:
    {
        /* Opus-review fix: WASAPI's id is a wchar_t[64], not a string in our own ABI's UTF-8
         * convention -- this used to be an unconditional no-op (buf left empty), so every
         * Windows-enumerated device got the exact same empty Id, which would have broken
         * MiniAudioDeviceEnumerator (device lookup/matching by Id) and any settings-persistence
         * keyed on it the moment this shim actually ran on Windows. Converted here instead. */
#if defined(_WIN32)
        int len = WideCharToMultiByte(CP_UTF8, 0, id->wasapi, -1, buf, (int)buf_size, NULL, NULL);
        if (len <= 0)
        {
            buf[0] = '\0';
        }
#endif
        break;
    }
    case ma_backend_coreaudio:
        strncpy(buf, id->coreaudio, buf_size - 1);
        break;
    default:
        break;
    }
    buf[buf_size - 1] = '\0';
}

/* The reverse of device_id_to_string -- converts a string (in our own ABI's convention) back into
 * a backend-native ma_device_id for the CURRENTLY resolved backend. Returns 0 on success, nonzero
 * if the current backend isn't one this shim's explicit backend list can ever select (a
 * programming error, not a runtime condition). */
static int string_to_device_id(ma_backend backend, const char *device_id, ma_device_id *out_id)
{
    memset(out_id, 0, sizeof(*out_id));
    switch (backend)
    {
    case ma_backend_pulseaudio:
        strncpy(out_id->pulse, device_id, sizeof(out_id->pulse) - 1);
        return 0;
    case ma_backend_alsa:
        strncpy(out_id->alsa, device_id, sizeof(out_id->alsa) - 1);
        return 0;
    case ma_backend_coreaudio:
        /* Opus-review fix: this case was missing entirely, so on macOS every
         * scanline_audio_get_native_formats/capture_session_open/playback_session_open call would
         * have failed unconditionally (device_id_to_string already handled coreaudio going the
         * other way -- enumeration alone would have looked fine, masking this). */
        strncpy(out_id->coreaudio, device_id, sizeof(out_id->coreaudio) - 1);
        return 0;
    case ma_backend_jack:
        /* Functional-audit fix (Tier A Batch 1 re-audit round 6): this case was missing entirely,
         * the same bug shape as the coreaudio fix immediately above -- ma_backend_jack IS in this
         * shim's own requested backend list (see the backends[] array a few lines up), so a host
         * where PulseAudio and ALSA context-init both fail but JACK succeeds would enumerate real
         * devices fine (device_id_to_string's own jack case, above, already handles the reverse
         * direction) but then fail every single scanline_audio_get_native_formats/
         * capture_session_open/playback_session_open call unconditionally -- exactly the
         * "enumeration alone would have looked fine, masking this" trap the coreaudio comment
         * already names. device_id_to_string's own jack case renders id->jack via "%d" (a plain
         * int), so parse it back the same way. */
        out_id->jack = atoi(device_id);
        return 0;
#if defined(_WIN32)
    case ma_backend_wasapi:
    {
        int wlen = MultiByteToWideChar(CP_UTF8, 0, device_id, -1, out_id->wasapi, (int)(sizeof(out_id->wasapi) / sizeof(out_id->wasapi[0])));
        return (wlen > 0) ? 0 : -1;
    }
#endif
    default:
        return -1; /* unsupported backend for this shim's current scope */
    }
}

typedef struct
{
    ma_device_type wanted_type;
    scanline_audio_device_info *out_devices;
    int max_count;
    int count;
} enum_state;

static ma_bool32 enum_callback(ma_context *pContext, ma_device_type deviceType, const ma_device_info *pInfo, void *pUserData)
{
    enum_state *state = (enum_state *)pUserData;
    if (deviceType != state->wanted_type)
    {
        return MA_TRUE; /* keep enumerating */
    }
    if (state->count >= state->max_count)
    {
        return MA_FALSE; /* stop, caller's buffer is full */
    }

    scanline_audio_device_info *out = &state->out_devices[state->count];
    device_id_to_string(pContext->backend, &pInfo->id, out->id, SCANLINE_AUDIO_ID_SIZE);

    size_t name_len = strlen(pInfo->name);
    if (name_len >= SCANLINE_AUDIO_NAME_SIZE)
    {
        name_len = SCANLINE_AUDIO_NAME_SIZE - 1;
    }
    memcpy(out->name, pInfo->name, name_len);
    out->name[name_len] = '\0';

    out->is_default = pInfo->isDefault ? 1 : 0;

    state->count++;
    return MA_TRUE;
}

int scanline_audio_enumerate_devices(int is_capture, scanline_audio_device_info *out_devices, int max_count)
{
    /* ma_context_enumerate_devices (callback-based) chosen over ma_context_get_devices
     * deliberately: the latter returns context-owned memory invalidated by the next call to it,
     * guarded by a lock that (per independent review) supports but doesn't by itself prove safety
     * during a live stream -- the callback form copies each device out immediately, under this
     * shim's own control, with no such lifetime hazard. */
    enum_state state;
    state.wanted_type = is_capture ? ma_device_type_capture : ma_device_type_playback;
    state.out_devices = out_devices;
    state.max_count = max_count;
    state.count = 0;

    /* Second-opus-review fix: lock FIRST, check g_context_initialized while holding the lock --
     * see g_context_mutex's own doc comment for why the previous check-then-lock ordering was a
     * real TOCTOU race against a concurrent scanline_audio_context_uninit. Also shares the mutex with
     * get_native_formats/session-open, none of which are individually thread-safe against each
     * other either. */
    scanline_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return -1;
    }

    ma_result result = ma_context_enumerate_devices(&g_context, enum_callback, &state);
    scanline_mutex_unlock(&g_context_mutex);
    if (result != MA_SUCCESS)
    {
        return -1;
    }

    return state.count;
}

int scanline_audio_get_native_formats(const char *device_id, int is_capture, scanline_audio_native_format *out_formats, int max_count)
{
    ma_device_info info;

    scanline_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return -1;
    }

    /* g_context.backend must also be read only while holding the lock: it's part of the same
     * context state a concurrent uninit could otherwise be tearing down mid-read. */
    ma_device_id id;
    if (string_to_device_id(g_context.backend, device_id, &id) != 0)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return -1;
    }

    ma_result result = ma_context_get_device_info(&g_context, is_capture ? ma_device_type_capture : ma_device_type_playback, &id, &info);
    scanline_mutex_unlock(&g_context_mutex);
    if (result != MA_SUCCESS)
    {
        return -1;
    }

    int count = (int)info.nativeDataFormatCount;
    if (count > max_count)
    {
        count = max_count;
    }

    for (int i = 0; i < count; i++)
    {
        out_formats[i].channels = (int)info.nativeDataFormats[i].channels;
        out_formats[i].sample_rate = (int)info.nativeDataFormats[i].sampleRate;
    }

    return count;
}

typedef struct
{
    float peak;
} spike_capture_state;

static void spike_capture_data_callback(ma_device *pDevice, void *pOutput, const void *pInput, ma_uint32 frameCount)
{
    (void)pOutput;
    spike_capture_state *state = (spike_capture_state *)pDevice->pUserData;
    const float *samples = (const float *)pInput;
    for (ma_uint32 i = 0; i < frameCount; i++)
    {
        float v = samples[i];
        if (v < 0.0f)
        {
            v = -v;
        }
        if (v > state->peak)
        {
            state->peak = v;
        }
    }
}

int scanline_audio_spike_capture_test(const char *device_id, int duration_ms, float *peak_out)
{
    spike_capture_state state;
    state.peak = 0.0f;

    /* ma_device_init's own source/sink-info lookup touches the shared context's mainloop (see
     * g_context_mutex's doc comment), so both the flag check and string_to_device_id's read of
     * g_context.backend must happen under the same lock as init -- ma_device_start is
     * deliberately excluded (see g_context_mutex's second-opus-review note): once init succeeds,
     * the device has its own separate mainloop/context and start doesn't touch the shared one. */
    ma_device_id id;
    ma_device device;
    scanline_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return -1;
    }

    if (string_to_device_id(g_context.backend, device_id, &id) != 0)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return -2; /* unsupported backend for this spike */
    }

    ma_device_config config = ma_device_config_init(ma_device_type_capture);
    config.capture.pDeviceID = &id;
    config.capture.format = ma_format_f32;
    config.capture.channels = 1;
    config.sampleRate = 44100; /* arbitrary for this spike -- Audio 2 decides the real policy */
    config.dataCallback = spike_capture_data_callback;
    config.pUserData = &state;

    ma_result init_result = ma_device_init(&g_context, &config, &device);
    scanline_mutex_unlock(&g_context_mutex);
    if (init_result != MA_SUCCESS)
    {
        return -3;
    }

    ma_result start_result = ma_device_start(&device);
    if (start_result != MA_SUCCESS)
    {
        ma_device_uninit(&device);
        return -4;
    }

    ma_sleep((ma_uint32)duration_ms);

    ma_device_uninit(&device); /* stops and cleans up */

    if (peak_out != NULL)
    {
        *peak_out = state.peak;
    }

    return 0;
}

/*
 * Piece Audio 3: standalone SPSC ring buffer, built on miniaudio's own ma_pcm_rb -- see this
 * function's own doc comment in scanline_audio.h for why this exists independent of any real device.
 */

struct scanline_audio_ring
{
    ma_pcm_rb rb;
    int channels;
};

scanline_audio_ring *scanline_audio_ring_create(int capacity_frames, int channels)
{
    if (capacity_frames <= 0 || channels <= 0)
    {
        return NULL;
    }

    /* Functional-audit fix: ma_pcm_rb_init (below) multiplies capacity_frames * bytes-per-frame as
     * ma_uint32 math before ma_rb_init_ex's own `> 0x7FFFFFFF` guard ever runs -- for a large enough
     * capacity_frames, that product wraps mod 2^32 BEFORE the guard sees it, silently creating a
     * ring far smaller than requested instead of failing loudly. Not reachable today (this port's
     * only caller uses a small, fixed capacity), but a future settings-driven buffer-size knob could
     * make it live. Reject anything that would overflow here, in 64-bit math, before that
     * multiplication ever runs -- sizeof(float) matches ma_get_bytes_per_frame's own result for
     * ma_format_f32 (this ring's only format, see the ma_pcm_rb_init call below), checked directly
     * rather than duplicating that internal computation. */
    if ((ma_uint64)capacity_frames * (ma_uint64)channels * sizeof(float) > 0x7FFFFFFFULL)
    {
        return NULL;
    }

    scanline_audio_ring *ring = (scanline_audio_ring *)malloc(sizeof(scanline_audio_ring));
    if (ring == NULL)
    {
        return NULL;
    }

    ring->channels = channels;
    ma_result result = ma_pcm_rb_init(ma_format_f32, (ma_uint32)channels, (ma_uint32)capacity_frames, NULL, NULL, &ring->rb);
    if (result != MA_SUCCESS)
    {
        free(ring);
        return NULL;
    }

    return ring;
}

void scanline_audio_ring_destroy(scanline_audio_ring *ring)
{
    if (ring != NULL)
    {
        ma_pcm_rb_uninit(&ring->rb);
        free(ring);
    }
}

int scanline_audio_ring_write(scanline_audio_ring *ring, const float *data, int frame_count)
{
    if (ring == NULL || data == NULL || frame_count < 0)
    {
        return -1;
    }

    int total_written = 0;
    while (total_written < frame_count)
    {
        /* acquire_write is an in/out parameter: we ask for the remaining amount, it tells us how
         * much room actually is contiguously available (which can be less, e.g. right up against
         * the end of the underlying buffer before it wraps) -- looping here, not just acquiring
         * once, is what makes a write spanning a wrap boundary actually complete instead of
         * silently dropping the tail. */
        ma_uint32 frames_to_write = (ma_uint32)(frame_count - total_written);
        void *write_buffer;
        ma_result result = ma_pcm_rb_acquire_write(&ring->rb, &frames_to_write, &write_buffer);
        if (result != MA_SUCCESS || frames_to_write == 0)
        {
            break; /* ring is full (or an error) -- stop, report what was actually written */
        }

        memcpy(write_buffer, data + (size_t)total_written * ring->channels, (size_t)frames_to_write * ring->channels * sizeof(float));
        ma_pcm_rb_commit_write(&ring->rb, frames_to_write);

        total_written += (int)frames_to_write;
    }

    return total_written;
}

int scanline_audio_ring_read(scanline_audio_ring *ring, float *out_data, int frame_count)
{
    if (ring == NULL || out_data == NULL || frame_count < 0)
    {
        return -1;
    }

    int total_read = 0;
    while (total_read < frame_count)
    {
        ma_uint32 frames_to_read = (ma_uint32)(frame_count - total_read);
        void *read_buffer;
        ma_result result = ma_pcm_rb_acquire_read(&ring->rb, &frames_to_read, &read_buffer);
        if (result != MA_SUCCESS || frames_to_read == 0)
        {
            break; /* ring is empty (or an error) -- stop, report what was actually read */
        }

        memcpy(out_data + (size_t)total_read * ring->channels, read_buffer, (size_t)frames_to_read * ring->channels * sizeof(float));
        ma_pcm_rb_commit_read(&ring->rb, frames_to_read);

        total_read += (int)frames_to_read;
    }

    return total_read;
}

int scanline_audio_ring_available_read(scanline_audio_ring *ring)
{
    if (ring == NULL)
    {
        return -1;
    }

    return (int)ma_pcm_rb_available_read(&ring->rb);
}

/*
 * Piece Audio 5: the real capture path. See scanline_audio.h's own doc comment on
 * scanline_audio_capture_session_open for the design.
 */

struct scanline_audio_capture_session
{
    ma_device device;
    scanline_audio_ring *ring;
    volatile int stopped; /* set by capture_session_notification_callback, real-time thread; read
                            * and cleared by scanline_audio_capture_session_check_and_clear_stopped,
                            * managed thread -- a single int written from one side and read/cleared
                            * from the other needs no separate lock (no ordering dependency on
                            * anything else, a torn write of an int-sized value isn't a real
                            * concern on any platform this project targets). */
    volatile int overrun_count; /* Piece Engine 0: same single-writer (real-time callback)/
                                  * single-reader (managed) reasoning as `stopped` above -- no lock
                                  * needed. Mirrors scanline_audio_playback_session's underrun_count:
                                  * an event counter (incremented once per callback that dropped
                                  * frames), not a dropped-frame counter, for the same reason --
                                  * this is a raw signal for the caller to interpret, not a verdict. */
    int channels;       /* 1 or 2, set once at open, read-only from the real-time callback --
                          * stereo-capture-source backlog item, see scanline_audio_open_options'
                          * own doc comment. */
    int channel_select; /* 0=unused, 1=Left, 2=Right -- only meaningful when channels==2. */
};

/* Bounded stack scratch for the stereo->mono channel-extraction path below -- no dynamic
 * allocation in a real-time callback. frameCount is backend/caller-determined and not itself
 * bounded, so extraction is chunked in slices of at most this many frames. */
#define SCANLINE_AUDIO_CHANNEL_EXTRACT_CHUNK_FRAMES 256

static void capture_session_data_callback(ma_device *pDevice, void *pOutput, const void *pInput, ma_uint32 frameCount)
{
    (void)pOutput;
    scanline_audio_capture_session *session = (scanline_audio_capture_session *)pDevice->pUserData;

    if (session->channels == 1)
    {
        /* Today's exact pre-existing path, byte-for-byte unchanged -- mono in, mono to the ring
         * (this covers "Mono" channel-select too: miniaudio's own data converter already does
         * whatever downmix the device's real native format needs, exactly as before this feature
         * existed). Drop-newest-when-full is already scanline_audio_ring_write's own behavior (never
         * blocks, writes only as many frames as currently fit) -- exactly IAudioEngine's
         * documented overrun policy (piece Audio 2): if the managed drain side has fallen behind,
         * the newest incoming frames are dropped here, never corrupting or reordering what's
         * already buffered. */
        int frames_written = scanline_audio_ring_write(session->ring, (const float *)pInput, (int)frameCount);
        if (frames_written < 0 || (ma_uint32)frames_written < frameCount)
        {
            session->overrun_count++;
        }
        return;
    }

    /* Stereo capture (Left/Right channel select, stereo-capture-source backlog item): extract the
     * selected channel from the interleaved LRLR... input into the bounded scratch buffer above,
     * then feed the ring exactly the same way the mono path does -- the ring itself stays mono
     * always (see scanline_audio_open_options' own doc comment on where the stereo<->mono conversion
     * happens). */
    const float *input = (const float *)pInput;
    int channel_index = (session->channel_select == 2) ? 1 : 0; /* Right=index 1, else (Left/unset)=index 0 */
    float scratch[SCANLINE_AUDIO_CHANNEL_EXTRACT_CHUNK_FRAMES];
    ma_uint32 offset = 0;
    int any_overrun = 0; /* incremented at most once per callback below, matching the mono path's
                           * own "event counter, not a dropped-frame counter" semantics -- not once
                           * per internal chunk, which would silently change what the counter means
                           * between the mono and stereo paths. */
    while (offset < frameCount)
    {
        ma_uint32 chunk = frameCount - offset;
        if (chunk > SCANLINE_AUDIO_CHANNEL_EXTRACT_CHUNK_FRAMES)
        {
            chunk = SCANLINE_AUDIO_CHANNEL_EXTRACT_CHUNK_FRAMES;
        }

        for (ma_uint32 i = 0; i < chunk; i++)
        {
            scratch[i] = input[(offset + i) * 2 + (ma_uint32)channel_index];
        }

        int frames_written = scanline_audio_ring_write(session->ring, scratch, (int)chunk);
        if (frames_written < 0 || (ma_uint32)frames_written < chunk)
        {
            any_overrun = 1;
        }

        offset += chunk;
    }

    if (any_overrun)
    {
        session->overrun_count++;
    }
}

static void capture_session_notification_callback(const ma_device_notification *pNotification)
{
    if (pNotification->type == ma_device_notification_type_stopped)
    {
        scanline_audio_capture_session *session = (scanline_audio_capture_session *)pNotification->pDevice->pUserData;
        session->stopped = 1;
    }
}

scanline_audio_capture_session *scanline_audio_capture_session_open(const char *device_id, const scanline_audio_open_options *options)
{
    int sample_rate = options->sample_rate;
    int ring_capacity_frames = options->ring_capacity_frames;

    /* Third-opus-review fix: reverted to a single critical section spanning flag check, id
     * resolution, and ma_device_init, matching scanline_audio_spike_capture_test's own (deliberately
     * single-section) shape exactly. A prior revision split this into two short sections (id
     * resolution, then init) on the theory that string_to_device_id is fast enough not to be worth
     * holding the lock across -- true, but splitting manufactured a real gap for no actual benefit
     * (string_to_device_id is a microseconds-order strncpy either way), and left the three
     * functions that all touch g_context_mutex inconsistent with each other for no reason. Single
     * section, same as the spike test: simpler to verify correct, not just faster. */
    ma_device_id id;
    scanline_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return NULL;
    }

    if (string_to_device_id(g_context.backend, device_id, &id) != 0)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return NULL;
    }

    scanline_audio_capture_session *session = (scanline_audio_capture_session *)malloc(sizeof(scanline_audio_capture_session));
    if (session == NULL)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return NULL;
    }

    session->stopped = 0;
    session->overrun_count = 0;
    session->channels = (options->channels == 2) ? 2 : 1;
    session->channel_select = options->channel_select;
    session->ring = scanline_audio_ring_create(ring_capacity_frames, 1); /* ring is always mono -- see struct doc comment */
    if (session->ring == NULL)
    {
        scanline_mutex_unlock(&g_context_mutex);
        free(session);
        return NULL;
    }

    ma_device_config config = ma_device_config_init(ma_device_type_capture);
    config.capture.pDeviceID = &id;
    config.capture.format = ma_format_f32;
    config.capture.channels = (ma_uint32)session->channels;
    config.sampleRate = (ma_uint32)sample_rate;
    config.dataCallback = capture_session_data_callback;
    /* Wired in here at device-init time, not bolted on later (piece Audio 8): miniaudio's
     * notification callback must be set inside ma_device_config before ma_device_init, there is
     * no way to attach it to an already-initialized device. */
    config.notificationCallback = capture_session_notification_callback;
    config.pUserData = session;
    /* Sound-FIFO-buffer-size backlog item: 0 (the struct's zero-value, and thus every existing
     * caller's default) leaves miniaudio's own default period/backend heuristic alone -- only
     * touch these ma_device_config fields when the caller actually asked for something specific,
     * so a zero-initialized options struct reproduces today's exact pre-existing behavior. */
    if (options->period_size_in_frames > 0)
    {
        config.periodSizeInFrames = (ma_uint32)options->period_size_in_frames;
    }
    if (options->periods > 0)
    {
        config.periods = (ma_uint32)options->periods;
    }

    /* See g_context_mutex's doc comment: init's own source-info lookup touches the shared
     * context's mainloop, concurrently with any enumeration/probing/other session opens.
     * ma_device_start is deliberately NOT covered -- it only touches this device's own separate
     * mainloop once init has succeeded. */
    ma_result init_result = ma_device_init(&g_context, &config, &session->device);
    scanline_mutex_unlock(&g_context_mutex);
    if (init_result != MA_SUCCESS)
    {
        scanline_audio_ring_destroy(session->ring);
        free(session);
        return NULL;
    }

    ma_result start_result = ma_device_start(&session->device);
    if (start_result != MA_SUCCESS)
    {
        ma_device_uninit(&session->device);
        scanline_audio_ring_destroy(session->ring);
        free(session);
        return NULL;
    }

    return session;
}

void scanline_audio_capture_session_close(scanline_audio_capture_session *session)
{
    if (session == NULL)
    {
        return;
    }

    ma_device_uninit(&session->device); /* also stops it first */
    scanline_audio_ring_destroy(session->ring);
    free(session);
}

int scanline_audio_capture_session_read(scanline_audio_capture_session *session, float *out_data, int frame_count)
{
    if (session == NULL)
    {
        return -1;
    }

    return scanline_audio_ring_read(session->ring, out_data, frame_count);
}

int scanline_audio_capture_session_check_and_clear_stopped(scanline_audio_capture_session *session)
{
    if (session == NULL)
    {
        return 0;
    }

    int was_stopped = session->stopped;
    session->stopped = 0;
    return was_stopped;
}

int scanline_audio_capture_session_overrun_count(scanline_audio_capture_session *session)
{
    if (session == NULL)
    {
        return -1;
    }

    return session->overrun_count;
}

/*
 * Piece Audio 6: the real playback path -- see scanline_audio.h's own doc comment on
 * scanline_audio_playback_session_open for the design.
 */

struct scanline_audio_playback_session
{
    ma_device device;
    scanline_audio_ring *ring;
    volatile int stopped;         /* see scanline_audio_capture_session's own comment on this field --
                                    * same single-writer/single-reader reasoning applies here. */
    volatile int underrun_count;
    int channels; /* 1 or 2, set once at open -- stereo-TX backlog item. No channel_select
                   * equivalent here: stereo TX always duplicates the same mono ring content to
                   * both output channels, see scanline_audio_open_options' own doc comment. */
};

static void playback_session_data_callback(ma_device *pDevice, void *pOutput, const void *pInput, ma_uint32 frameCount)
{
    (void)pInput;
    scanline_audio_playback_session *session = (scanline_audio_playback_session *)pDevice->pUserData;

    if (session->channels == 1)
    {
        /* Today's exact pre-existing path, byte-for-byte unchanged. */
        int frames_read = scanline_audio_ring_read(session->ring, (float *)pOutput, (int)frameCount);
        if (frames_read < 0)
        {
            frames_read = 0;
        }

        if ((ma_uint32)frames_read < frameCount)
        {
            /* Underrun: not enough buffered data to fill this callback. Pad the remainder with
             * silence -- never leave garbage/uninitialized samples in the device's own output
             * buffer. */
            float *output = (float *)pOutput;
            memset(output + frames_read, 0, ((size_t)frameCount - (size_t)frames_read) * sizeof(float));
            session->underrun_count++;
        }
        return;
    }

    /* Stereo TX (stereo-TX-toggle backlog item): read mono from the ring into the same bounded
     * scratch buffer the capture side uses, then duplicate each sample into interleaved L/R
     * output -- chunked, no dynamic allocation. Underrun padding zeroes BOTH channels' worth of
     * bytes for the un-filled tail -- a real bug fixed as part of adding this path: a naive
     * single-channel-width memset (the mono path's own shape above) would only ever zero the L
     * channel's bytes on underrun here, leaving R with whatever the backend/miniaudio last left
     * in that buffer -- garbage, not silence. */
    float *output = (float *)pOutput;
    float scratch[SCANLINE_AUDIO_CHANNEL_EXTRACT_CHUNK_FRAMES];
    ma_uint32 offset = 0;
    int any_underrun = 0;
    while (offset < frameCount)
    {
        ma_uint32 chunk = frameCount - offset;
        if (chunk > SCANLINE_AUDIO_CHANNEL_EXTRACT_CHUNK_FRAMES)
        {
            chunk = SCANLINE_AUDIO_CHANNEL_EXTRACT_CHUNK_FRAMES;
        }

        int frames_read = scanline_audio_ring_read(session->ring, scratch, (int)chunk);
        if (frames_read < 0)
        {
            frames_read = 0;
        }

        for (int i = 0; i < frames_read; i++)
        {
            output[(offset + (ma_uint32)i) * 2 + 0] = scratch[i];
            output[(offset + (ma_uint32)i) * 2 + 1] = scratch[i];
        }

        if ((ma_uint32)frames_read < chunk)
        {
            memset(
                output + (offset + (ma_uint32)frames_read) * 2, 0,
                ((size_t)chunk - (size_t)frames_read) * 2 * sizeof(float));
            any_underrun = 1;
        }

        offset += chunk;
    }

    if (any_underrun)
    {
        session->underrun_count++;
    }
}

static void playback_session_notification_callback(const ma_device_notification *pNotification)
{
    if (pNotification->type == ma_device_notification_type_stopped)
    {
        scanline_audio_playback_session *session = (scanline_audio_playback_session *)pNotification->pDevice->pUserData;
        session->stopped = 1;
    }
}

scanline_audio_playback_session *scanline_audio_playback_session_open(const char *device_id, const scanline_audio_open_options *options)
{
    int sample_rate = options->sample_rate;
    int ring_capacity_frames = options->ring_capacity_frames;

    /* See scanline_audio_capture_session_open's identical pattern and comment (third-opus-review
     * fix: single critical section, matching the spike test). */
    ma_device_id id;
    scanline_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return NULL;
    }

    if (string_to_device_id(g_context.backend, device_id, &id) != 0)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return NULL;
    }

    scanline_audio_playback_session *session = (scanline_audio_playback_session *)malloc(sizeof(scanline_audio_playback_session));
    if (session == NULL)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return NULL;
    }

    session->stopped = 0;
    session->underrun_count = 0;
    session->channels = (options->channels == 2) ? 2 : 1;
    session->ring = scanline_audio_ring_create(ring_capacity_frames, 1); /* ring is always mono -- see struct doc comment */
    if (session->ring == NULL)
    {
        scanline_mutex_unlock(&g_context_mutex);
        free(session);
        return NULL;
    }

    ma_device_config config = ma_device_config_init(ma_device_type_playback);
    config.playback.pDeviceID = &id;
    config.playback.format = ma_format_f32;
    config.playback.channels = (ma_uint32)session->channels;
    config.sampleRate = (ma_uint32)sample_rate;
    config.dataCallback = playback_session_data_callback;
    /* Same reasoning as the capture session: must be set here, at config/init time -- miniaudio
     * has no way to attach a notification callback to an already-initialized device. */
    config.notificationCallback = playback_session_notification_callback;
    config.pUserData = session;
    /* See scanline_audio_capture_session_open's identical comment. */
    if (options->period_size_in_frames > 0)
    {
        config.periodSizeInFrames = (ma_uint32)options->period_size_in_frames;
    }
    if (options->periods > 0)
    {
        config.periods = (ma_uint32)options->periods;
    }

    /* See g_context_mutex's doc comment. ma_device_start deliberately not covered -- see the
     * capture session's identical comment. */
    ma_result init_result = ma_device_init(&g_context, &config, &session->device);
    scanline_mutex_unlock(&g_context_mutex);
    if (init_result != MA_SUCCESS)
    {
        scanline_audio_ring_destroy(session->ring);
        free(session);
        return NULL;
    }

    ma_result start_result = ma_device_start(&session->device);
    if (start_result != MA_SUCCESS)
    {
        ma_device_uninit(&session->device);
        scanline_audio_ring_destroy(session->ring);
        free(session);
        return NULL;
    }

    return session;
}

void scanline_audio_playback_session_close(scanline_audio_playback_session *session)
{
    if (session == NULL)
    {
        return;
    }

    ma_device_uninit(&session->device); /* also stops it first */
    scanline_audio_ring_destroy(session->ring);
    free(session);
}

int scanline_audio_playback_session_write(scanline_audio_playback_session *session, const float *data, int frame_count)
{
    if (session == NULL)
    {
        return -1;
    }

    return scanline_audio_ring_write(session->ring, data, frame_count);
}

int scanline_audio_playback_session_pending_frames(scanline_audio_playback_session *session)
{
    if (session == NULL)
    {
        return -1;
    }

    return scanline_audio_ring_available_read(session->ring);
}

int scanline_audio_playback_session_underrun_count(scanline_audio_playback_session *session)
{
    if (session == NULL)
    {
        return -1;
    }

    return session->underrun_count;
}

int scanline_audio_playback_session_check_and_clear_stopped(scanline_audio_playback_session *session)
{
    if (session == NULL)
    {
        return 0;
    }

    int was_stopped = session->stopped;
    session->stopped = 0;
    return was_stopped;
}

int scanline_audio_resample_f32(const float *input, int input_frame_count, int sample_rate_in,
                              int sample_rate_out, int lpf_order, float *output, int output_capacity_frames)
{
    if (input == NULL || output == NULL || input_frame_count < 0 || output_capacity_frames < 0)
    {
        return -1;
    }

    ma_resampler_config config = ma_resampler_config_init(
        ma_format_f32, 1, (ma_uint32)sample_rate_in, (ma_uint32)sample_rate_out, ma_resample_algorithm_linear);
    if (lpf_order >= 0)
    {
        config.linear.lpfOrder = (ma_uint32)lpf_order;
    }

    ma_resampler resampler;
    if (ma_resampler_init(&config, NULL, &resampler) != MA_SUCCESS)
    {
        return -1;
    }

    ma_uint64 total_frames_in = (ma_uint64)input_frame_count;
    ma_uint64 in_consumed = 0;
    ma_uint64 total_frames_out = 0;

    while (in_consumed < total_frames_in && total_frames_out < (ma_uint64)output_capacity_frames)
    {
        ma_uint64 frames_in = total_frames_in - in_consumed;
        ma_uint64 frames_out = (ma_uint64)output_capacity_frames - total_frames_out;

        ma_result result = ma_resampler_process_pcm_frames(
            &resampler, input + in_consumed, &frames_in, output + total_frames_out, &frames_out);
        if (result != MA_SUCCESS)
        {
            ma_resampler_uninit(&resampler, NULL);
            return -1;
        }

        in_consumed += frames_in;
        total_frames_out += frames_out;

        if (frames_in == 0 && frames_out == 0)
        {
            break; /* no progress possible -- avoid spinning forever */
        }
    }

    ma_resampler_uninit(&resampler, NULL);

    if (in_consumed < total_frames_in)
    {
        return -1; /* output_capacity_frames was too small to hold the full result */
    }

    return (int)total_frames_out;
}

/* ============================================================================================
 * Piece Audio 9: real OS device mute state. Device-scoped, not session-scoped (no
 * scanline_audio_capture_session/scanline_audio_playback_session parameter) -- see scanline_audio.h's own
 * doc comment on scanline_audio_get_device_mute for the read-only-by-design reasoning.
 *
 * User-directed reversal (same session): this piece originally also carried real OS-mixer VOLUME
 * get/set (WASAPI IAudioEndpointVolume/PulseAudio pa_context_set_sink/source_volume_by_name/
 * CoreAudio AudioObjectSetPropertyData/ALSA snd_mixer_selem_*_volume_*), removed once TX Pwr went
 * back to app-internal gain (matching WSJT-X's/fldigi's own Pwr controls, see
 * ScanlineStudio.Application.SstvSessionService.GetTxVolumePercentAsync) and RX dropped any
 * volume/mute concept entirely in favor of a plain incoming-level meter. Mute is the one piece
 * that stayed: it's a real OS device fact independent of whichever gain approach TX uses, so
 * TX Pwr can still show "muted" instead of a number that would otherwise silently lie about
 * whether anything is actually reaching the speaker.
 *
 * Windows/macOS paths below are written against each platform's documented API but UNVERIFIED in
 * this Linux-only dev sandbox -- same status as this file's own WASAPI/CoreAudio device-open
 * paths (spec/14-roadmap.md: "Windows/macOS legs unverified" for those). Real verification is the
 * windows-latest/macos-latest CI legs, then a manual pass on real hardware before this ships.
 * ============================================================================================ */

#if defined(_WIN32)

/* User-caught (real Windows publish, LNK2019): the previous fix here (an "auditor-caught" comment
 * claiming CLSID_MMDeviceEnumerator/IID_IMMDeviceEnumerator/IID_IAudioEndpointVolume "live in
 * uuid.lib, full stop") was ITSELF wrong -- verified directly against a real Windows SDK install
 * (um\arm64, um\x64, and the MSVC toolset libs), with IID_IUnknown as a control (that one really
 * is in uuid.lib). mmdeviceapi.h/endpointvolume.h do declare these three as plain
 * `EXTERN_C const IID ...;`, but nothing in the shipped import libraries actually provides their
 * storage -- linking uuid.lib does not resolve them, on x64 or ARM64, so a real `dotnet publish`
 * fails LNK2019 regardless of that library being present.
 *
 * Fix: define the three GUIDs OURSELVES via DEFINE_GUID, the option neither the original code nor
 * the previous "fix" considered (that one dismissed INITGUID as ineffective and stopped there,
 * without reaching for DEFINE_GUID directly). `<initguid.h>` is included AFTER
 * mmdeviceapi.h/endpointvolume.h specifically -- doing it before would also mass-define every
 * `PKEY_AudioEndpoint_*` property key those headers declare, which is not needed here and is
 * needless surface. The vendored `miniaudio.h` independently corroborates the first two GUID
 * VALUES below (not the DEFINE_GUID method -- it uses private `static const GUID MA_CLSID_.../
 * MA_IID_...` names instead, ~line 21786-21787). Values below are from the
 * Windows SDK's own IDL, not typed from memory. ole32.lib alone (CoCreateInstance/CoInitializeEx/
 * CoUninitialize) is still needed on BuildNativeShimWindows's `cl.exe ... /link` line -- uuid.lib
 * is no longer needed at all and has been dropped from the csproj. */
#define COBJMACROS /* C-callable (vtable-macro) COM interface access, not C++ method syntax. */
#include <mmdeviceapi.h>
#include <endpointvolume.h>
#include <initguid.h>
DEFINE_GUID(CLSID_MMDeviceEnumerator, 0xbcde0395, 0xe52f, 0x467c, 0x8e, 0x3d, 0xc4, 0x57, 0x92, 0x91, 0x69, 0x2e);
DEFINE_GUID(IID_IMMDeviceEnumerator, 0xa95664d2, 0x9614, 0x4f35, 0xa7, 0x46, 0xde, 0x8d, 0xb6, 0x36, 0x17, 0xe6);
DEFINE_GUID(IID_IAudioEndpointVolume, 0x5cdf2c82, 0x841e, 0x4546, 0x97, 0x22, 0x0c, 0xf7, 0x40, 0x78, 0x22, 0x9a);

/* device_id_w is the WASAPI endpoint id (already the same string device_id_to_string produces
 * for this backend) -- it alone selects a specific render OR capture endpoint, so is_capture is
 * not needed to resolve it, only to match the other backends' function shape. Each call pairs its
 * own CoInitializeEx/CoUninitialize since this can run on an arbitrary thread-pool thread with no
 * guarantee COM is already initialized there; MULTITHREADED (not APARTMENTTHREADED) since this is
 * a background worker with no message pump. */
static int scanline_wasapi_with_endpoint_volume(const wchar_t *device_id_w, int (*fn)(IAudioEndpointVolume *, void *), void *userdata)
{
    HRESULT co_hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    /* RPC_E_CHANGED_MODE: this thread already has COM initialized under a different concurrency
     * model -- proceed without our own CoUninitialize (we didn't add a reference), the existing
     * apartment still works fine for CoCreateInstance/Activate. Any other FAILED(co_hr) is real. */
    if (FAILED(co_hr) && co_hr != RPC_E_CHANGED_MODE)
    {
        return -1;
    }

    int result = -1;
    IMMDeviceEnumerator *enumerator = NULL;
    IMMDevice *device = NULL;
    IAudioEndpointVolume *endpoint_volume = NULL;

    HRESULT hr = CoCreateInstance(&CLSID_MMDeviceEnumerator, NULL, CLSCTX_ALL, &IID_IMMDeviceEnumerator, (void **)&enumerator);
    if (SUCCEEDED(hr))
    {
        hr = IMMDeviceEnumerator_GetDevice(enumerator, device_id_w, &device);
    }
    if (SUCCEEDED(hr))
    {
        hr = IMMDevice_Activate(device, &IID_IAudioEndpointVolume, CLSCTX_ALL, NULL, (void **)&endpoint_volume);
    }
    if (SUCCEEDED(hr))
    {
        result = fn(endpoint_volume, userdata);
    }

    if (endpoint_volume != NULL) IAudioEndpointVolume_Release(endpoint_volume);
    if (device != NULL) IMMDevice_Release(device);
    if (enumerator != NULL) IMMDeviceEnumerator_Release(enumerator);

    if (SUCCEEDED(co_hr))
    {
        CoUninitialize();
    }

    return result;
}

static int scanline_wasapi_get_mute_cb(IAudioEndpointVolume *vol, void *userdata)
{
    BOOL muted = FALSE;
    if (FAILED(IAudioEndpointVolume_GetMute(vol, &muted)))
    {
        return -1;
    }
    *(int *)userdata = muted ? 1 : 0;
    return 0;
}

#elif defined(__APPLE__)

#include <CoreAudio/CoreAudio.h>
#include <CoreFoundation/CoreFoundation.h>

static int scanline_coreaudio_resolve_device_id(const char *uid_utf8, AudioDeviceID *out_device_id)
{
    CFStringRef uid_cfstr = CFStringCreateWithCString(NULL, uid_utf8, kCFStringEncodingUTF8);
    if (uid_cfstr == NULL)
    {
        return -1;
    }

    AudioValueTranslation translation;
    translation.mInputData = &uid_cfstr;
    translation.mInputDataSize = sizeof(CFStringRef);
    translation.mOutputData = out_device_id;
    translation.mOutputDataSize = sizeof(AudioDeviceID);

    AudioObjectPropertyAddress address = {
        kAudioHardwarePropertyDeviceForUID, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMaster};

    UInt32 size = sizeof(translation);
    OSStatus status = AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, NULL, &size, &translation);
    CFRelease(uid_cfstr);

    if (status != noErr || *out_device_id == kAudioObjectUnknown)
    {
        return -1;
    }
    return 0;
}

/* Fills out_address for the given selector/scope/element. Shared by every CoreAudio property
 * lookup in this file so the scope-from-is_capture mapping lives in exactly one place. */
static int scanline_coreaudio_property_address(AudioObjectPropertySelector selector, int is_capture, AudioObjectPropertyElement element, AudioObjectPropertyAddress *out_address)
{
    out_address->mSelector = selector;
    out_address->mScope = is_capture ? kAudioDevicePropertyScopeInput : kAudioDevicePropertyScopeOutput;
    out_address->mElement = element;
    return 0;
}

static int scanline_coreaudio_get_device_mute(const char *uid_utf8, int is_capture, int *is_muted_out)
{
    /* Pre-set, not left uninitialized: scanline_coreaudio_resolve_device_id's own failure path is
     * short-circuited before this is ever read today, but some OS versions are documented to
     * return noErr without writing *out_device_id for an unknown UID -- defense-in-depth against
     * that, matching the documented CoreAudio idiom. */
    AudioDeviceID device_id = kAudioObjectUnknown;
    if (scanline_coreaudio_resolve_device_id(uid_utf8, &device_id) != 0)
    {
        return -1;
    }

    /* Tries the device's master (element 0) property first, then channel 1 -- a multi-channel
     * device without a master element is common enough (per-channel-only hardware) that a hard
     * failure there would make this "unsupported" far more often than necessary. */
    AudioObjectPropertyAddress address;
    AudioObjectPropertyElement elements[] = {kAudioObjectPropertyElementMaster, 1};
    for (size_t i = 0; i < sizeof(elements) / sizeof(elements[0]); i++)
    {
        scanline_coreaudio_property_address(kAudioDevicePropertyMute, is_capture, elements[i], &address);
        if (!AudioObjectHasProperty(device_id, &address))
        {
            continue;
        }

        UInt32 muted = 0;
        UInt32 size = sizeof(muted);
        if (AudioObjectGetPropertyData(device_id, &address, 0, NULL, &size, &muted) == noErr)
        {
            *is_muted_out = muted ? 1 : 0;
            return 0;
        }
    }

    return -1;
}

#else /* Linux: PulseAudio (primary) + ALSA (fallback, best-effort) + JACK (unsupported) */

/* PulseAudio's own sink/source info already carries a mute flag (ma_pa_sink_info/ma_pa_source_info
 * both have a plain `int mute`, confirmed against the vendored miniaudio.h's own compatible
 * structs) -- reuses the already-open libpulse handle/mainloop/context miniaudio's own PulseAudio
 * backend opened at scanline_audio_context_init time (g_context.pulse.*), via
 * ma_context_get_sink_info__pulse/ma_context_get_source_info__pulse (static helpers a few
 * thousand lines up in the vendored miniaudio.h, same translation unit --
 * MINIAUDIO_IMPLEMENTATION -- so directly callable here) that miniaudio's own device-info path
 * already uses. No second dlopen, no second mainloop, no pactl shell-out, no new production
 * dependency, no new link flags. Caller must already hold g_context_mutex. */
static int scanline_pulse_get_device_mute(const char *device_name, int is_capture, int *is_muted_out)
{
    if (is_capture)
    {
        ma_pa_source_info info;
        memset(&info, 0, sizeof(info));
        if (ma_context_get_source_info__pulse(&g_context, device_name, &info) != MA_SUCCESS)
        {
            return -1;
        }
        *is_muted_out = info.mute ? 1 : 0;
    }
    else
    {
        ma_pa_sink_info info;
        memset(&info, 0, sizeof(info));
        if (ma_context_get_sink_info__pulse(&g_context, device_name, &info) != MA_SUCCESS)
        {
            return -1;
        }
        *is_muted_out = info.mute ? 1 : 0;
    }

    return 0;
}

/* --- ALSA: best-effort fallback (only reached when PulseAudio context-init failed). Not every
 * device exposes a simple-mixer mute switch -- "Master"/"PCM" (playback) or "Capture" (capture),
 * first match wins; no suitable element is a documented "unsupported," not an error. Symbols are
 * dlsym'd off the already-open g_context.alsa.asoundSO handle (no libasound-dev needed, matching
 * this file's existing dlopen-everything convention) -- the simple-mixer API (snd_mixer_*) lives
 * in the same libasound.so.2 miniaudio's own ALSA backend already opened, just not pre-loaded by
 * miniaudio itself since it never needs the mixer API. Opaque handle types declared locally
 * (never dereferenced, only passed by pointer) -- same convention this file already uses for
 * PulseAudio's ma_pa_mainloop/ma_pa_context. */

typedef struct snd_mixer_t snd_mixer_t;
typedef struct snd_mixer_elem_t snd_mixer_elem_t;

typedef int (*scanline_snd_mixer_open_proc)(snd_mixer_t **mixer, int mode);
typedef int (*scanline_snd_mixer_attach_proc)(snd_mixer_t *mixer, const char *name);
typedef int (*scanline_snd_mixer_selem_register_proc)(snd_mixer_t *mixer, void *options, void *classp);
typedef int (*scanline_snd_mixer_load_proc)(snd_mixer_t *mixer);
typedef int (*scanline_snd_mixer_close_proc)(snd_mixer_t *mixer); /* real ALSA prototype returns int */
typedef snd_mixer_elem_t *(*scanline_snd_mixer_first_elem_proc)(snd_mixer_t *mixer);
typedef snd_mixer_elem_t *(*scanline_snd_mixer_elem_next_proc)(snd_mixer_elem_t *elem);
typedef const char *(*scanline_snd_mixer_selem_get_name_proc)(snd_mixer_elem_t *elem);
/* Auditor-caught: an earlier revision here still filtered candidate elements by
 * has_playback_volume/has_capture_volume (a leftover from when this shim also read/wrote the
 * volume itself) -- since this shim now only ever reads the MUTE switch, the direct predicate is
 * has_playback_switch/has_capture_switch instead. A real element can expose a mute switch with no
 * volume control at all (or vice versa); filtering on the wrong capability would report
 * "unsupported" for a device that actually does have a real switch to read. */
typedef int (*scanline_snd_mixer_selem_has_playback_switch_proc)(snd_mixer_elem_t *elem);
typedef int (*scanline_snd_mixer_selem_has_capture_switch_proc)(snd_mixer_elem_t *elem);
/* Mute in ALSA's simple-mixer API is a per-channel "switch," not a volume property -- 1 = on
 * (unmuted), 0 = off (muted). Queried on channel 0 only (snd_mixer_selem_channel_id_t's
 * SND_MIXER_SCHN_FRONT_LEFT is value 0, always valid to query even on a mono element). */
typedef int (*scanline_snd_mixer_selem_get_playback_switch_proc)(snd_mixer_elem_t *elem, int channel, int *value);
typedef int (*scanline_snd_mixer_selem_get_capture_switch_proc)(snd_mixer_elem_t *elem, int channel, int *value);

typedef struct
{
    scanline_snd_mixer_open_proc open;
    scanline_snd_mixer_attach_proc attach;
    scanline_snd_mixer_selem_register_proc selem_register;
    scanline_snd_mixer_load_proc load;
    scanline_snd_mixer_close_proc close;
    scanline_snd_mixer_first_elem_proc first_elem;
    scanline_snd_mixer_elem_next_proc elem_next;
    scanline_snd_mixer_selem_get_name_proc selem_get_name;
    scanline_snd_mixer_selem_has_playback_switch_proc selem_has_playback_switch;
    scanline_snd_mixer_selem_has_capture_switch_proc selem_has_capture_switch;
    scanline_snd_mixer_selem_get_playback_switch_proc selem_get_playback_switch;
    scanline_snd_mixer_selem_get_capture_switch_proc selem_get_capture_switch;
} scanline_alsa_mixer_api;

/* Returns nonzero if every symbol resolved. */
static int scanline_alsa_mixer_api_load(scanline_alsa_mixer_api *api)
{
    ma_log *log = ma_context_get_log(&g_context);
    ma_handle so = g_context.alsa.asoundSO;

#define SCANLINE_DLSYM(field, name) \
    api->field = (void *)ma_dlsym(log, so, name); \
    if (api->field == NULL) return 0;

    SCANLINE_DLSYM(open, "snd_mixer_open")
    SCANLINE_DLSYM(attach, "snd_mixer_attach")
    SCANLINE_DLSYM(selem_register, "snd_mixer_selem_register")
    SCANLINE_DLSYM(load, "snd_mixer_load")
    SCANLINE_DLSYM(close, "snd_mixer_close")
    SCANLINE_DLSYM(first_elem, "snd_mixer_first_elem")
    SCANLINE_DLSYM(elem_next, "snd_mixer_elem_next")
    SCANLINE_DLSYM(selem_get_name, "snd_mixer_selem_get_name")
    SCANLINE_DLSYM(selem_has_playback_switch, "snd_mixer_selem_has_playback_switch")
    SCANLINE_DLSYM(selem_has_capture_switch, "snd_mixer_selem_has_capture_switch")
    SCANLINE_DLSYM(selem_get_playback_switch, "snd_mixer_selem_get_playback_switch")
    SCANLINE_DLSYM(selem_get_capture_switch, "snd_mixer_selem_get_capture_switch")

#undef SCANLINE_DLSYM
    return 1;
}

/* Auditor-caught risk: snd_mixer_attach takes an ALSA CTL name ("hw:0", "default"), not a PCM
 * name -- miniaudio's own ALSA device ids are PCM names in "hw:CARD,DEVICE" form (confirmed
 * against miniaudio.h's own ALSA device-id formatting), so passing device_name straight through
 * only ever worked for the literal "default" id. Derives the CTL form by truncating at the first
 * comma; anything without a comma (including "default" and any id not shaped like "hw:C,D")
 * passes through unchanged. buf must be at least SCANLINE_AUDIO_ID_SIZE bytes. */
static void scanline_alsa_ctl_name_from_pcm_name(const char *pcm_name, char *buf, size_t buf_size)
{
    size_t len = strlen(pcm_name);
    const char *comma = strchr(pcm_name, ',');
    if (comma != NULL)
    {
        len = (size_t)(comma - pcm_name);
    }
    if (len >= buf_size)
    {
        len = buf_size - 1;
    }
    memcpy(buf, pcm_name, len);
    buf[len] = '\0';
}

/* Opens+attaches+loads a mixer on device_name and finds the first suitable element ("Master" then
 * "PCM" for playback, "Capture" for capture). Returns NULL if anything along the way fails or no
 * suitable element exists -- caller must still call api->close(*out_mixer) if *out_mixer != NULL
 * even on a NULL element return (attach/load can succeed with no matching element). */
static snd_mixer_elem_t *scanline_alsa_find_element(const scanline_alsa_mixer_api *api, const char *device_name, int is_capture, snd_mixer_t **out_mixer)
{
    char ctl_name[SCANLINE_AUDIO_ID_SIZE];
    scanline_alsa_ctl_name_from_pcm_name(device_name, ctl_name, sizeof(ctl_name));

    *out_mixer = NULL;
    if (api->open(out_mixer, 0) != 0 || *out_mixer == NULL)
    {
        return NULL;
    }
    if (api->attach(*out_mixer, ctl_name) != 0 || api->selem_register(*out_mixer, NULL, NULL) != 0 || api->load(*out_mixer) != 0)
    {
        return NULL;
    }

    static const char *playback_names[] = {"Master", "PCM"};
    for (snd_mixer_elem_t *elem = api->first_elem(*out_mixer); elem != NULL; elem = api->elem_next(elem))
    {
        const char *name = api->selem_get_name(elem);
        if (name == NULL)
        {
            continue;
        }
        if (is_capture)
        {
            if (strcmp(name, "Capture") == 0 && api->selem_has_capture_switch(elem))
            {
                return elem;
            }
        }
        else
        {
            for (size_t i = 0; i < sizeof(playback_names) / sizeof(playback_names[0]); i++)
            {
                if (strcmp(name, playback_names[i]) == 0 && api->selem_has_playback_switch(elem))
                {
                    return elem;
                }
            }
        }
    }

    return NULL;
}

static int scanline_alsa_get_device_mute(const char *device_name, int is_capture, int *is_muted_out)
{
    scanline_alsa_mixer_api api;
    if (!scanline_alsa_mixer_api_load(&api))
    {
        return -1;
    }

    snd_mixer_t *mixer;
    snd_mixer_elem_t *elem = scanline_alsa_find_element(&api, device_name, is_capture, &mixer);
    int result = -1;
    if (elem != NULL)
    {
        int on = 1; /* ALSA switch convention: 1 = on/unmuted, 0 = off/muted */
        int ok = is_capture ? api.selem_get_capture_switch(elem, 0, &on) : api.selem_get_playback_switch(elem, 0, &on);
        if (ok == 0)
        {
            *is_muted_out = on ? 0 : 1;
            result = 0;
        }
    }
    if (mixer != NULL)
    {
        api.close(mixer);
    }
    return result;
}

#endif

int scanline_audio_get_device_mute(const char *device_id, int is_capture, int *is_muted_out)
{
    if (device_id == NULL || is_muted_out == NULL)
    {
        return -1;
    }

#if defined(_WIN32)
    wchar_t device_id_w[SCANLINE_AUDIO_ID_SIZE];
    if (MultiByteToWideChar(CP_UTF8, 0, device_id, -1, device_id_w, SCANLINE_AUDIO_ID_SIZE) <= 0)
    {
        return -1;
    }
    return scanline_wasapi_with_endpoint_volume(device_id_w, scanline_wasapi_get_mute_cb, is_muted_out);
#elif defined(__APPLE__)
    return scanline_coreaudio_get_device_mute(device_id, is_capture, is_muted_out);
#else
    scanline_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        scanline_mutex_unlock(&g_context_mutex);
        return -1;
    }

    int result;
    if (g_context.backend == ma_backend_pulseaudio)
    {
        result = scanline_pulse_get_device_mute(device_id, is_capture, is_muted_out);
    }
    else if (g_context.backend == ma_backend_alsa)
    {
        result = scanline_alsa_get_device_mute(device_id, is_capture, is_muted_out);
    }
    else
    {
        result = -1; /* JACK: no OS mixer concept applies */
    }

    scanline_mutex_unlock(&g_context_mutex);
    return result;
#endif
}
