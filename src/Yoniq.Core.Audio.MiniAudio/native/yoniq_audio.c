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

#include "yoniq_audio.h"
#include <string.h>
#include <stdlib.h>

/* Second-opus-review fix: the mutex itself is now statically initialized and NEVER destroyed --
 * SRWLOCK_INIT/PTHREAD_MUTEX_INITIALIZER are valid, lockable states with no init/destroy call
 * needed at all. The previous version called yoniq_mutex_init/_destroy from
 * yoniq_audio_context_init/_uninit, which meant: (1) every other entry point below checked
 * g_context_initialized *before* locking, a TOCTOU window where a concurrent
 * yoniq_audio_context_uninit could destroy a mutex this thread was about to lock (locking a
 * destroyed pthread_mutex_t/CRITICAL_SECTION is undefined behavior), and (2)
 * pthread_mutex_destroy/DeleteCriticalSection on a currently-locked mutex is itself undefined
 * behavior. Making the mutex permanently valid removes both problems at once: every entry point
 * below now locks FIRST and checks g_context_initialized *while holding the lock*, so the flag
 * check and the context-uninit that clears it can never interleave. */
#if defined(_WIN32)
#include <windows.h>
typedef SRWLOCK yoniq_mutex;
#define YONIQ_MUTEX_INIT SRWLOCK_INIT
static void yoniq_mutex_lock(yoniq_mutex *m) { AcquireSRWLockExclusive(m); }
static void yoniq_mutex_unlock(yoniq_mutex *m) { ReleaseSRWLockExclusive(m); }
#else
#include <pthread.h>
typedef pthread_mutex_t yoniq_mutex;
#define YONIQ_MUTEX_INIT PTHREAD_MUTEX_INITIALIZER
static void yoniq_mutex_lock(yoniq_mutex *m) { pthread_mutex_lock(m); }
static void yoniq_mutex_unlock(yoniq_mutex *m) { pthread_mutex_unlock(m); }
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
 * becomes a real-world problem, not a hypothetical one. */
static yoniq_mutex g_context_mutex = YONIQ_MUTEX_INIT;

int yoniq_audio_context_init(char *backend_name_out)
{
    yoniq_mutex_lock(&g_context_mutex);

    if (g_context_initialized)
    {
        yoniq_mutex_unlock(&g_context_mutex);
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
        yoniq_mutex_unlock(&g_context_mutex);
        return (int)result;
    }

    g_context_initialized = 1;

    if (backend_name_out != NULL)
    {
        const char *name = ma_get_backend_name(g_context.backend);
        size_t len = strlen(name);
        if (len >= YONIQ_AUDIO_BACKEND_NAME_SIZE)
        {
            len = YONIQ_AUDIO_BACKEND_NAME_SIZE - 1;
        }
        memcpy(backend_name_out, name, len);
        backend_name_out[len] = '\0';
    }

    yoniq_mutex_unlock(&g_context_mutex);
    return 0;
}

void yoniq_audio_context_uninit(void)
{
    yoniq_mutex_lock(&g_context_mutex);
    if (g_context_initialized)
    {
        ma_context_uninit(&g_context);
        g_context_initialized = 0;
    }
    yoniq_mutex_unlock(&g_context_mutex);
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
         * yoniq_audio_get_native_formats/capture_session_open/playback_session_open call would
         * have failed unconditionally (device_id_to_string already handled coreaudio going the
         * other way -- enumeration alone would have looked fine, masking this). */
        strncpy(out_id->coreaudio, device_id, sizeof(out_id->coreaudio) - 1);
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
    yoniq_audio_device_info *out_devices;
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

    yoniq_audio_device_info *out = &state->out_devices[state->count];
    device_id_to_string(pContext->backend, &pInfo->id, out->id, YONIQ_AUDIO_ID_SIZE);

    size_t name_len = strlen(pInfo->name);
    if (name_len >= YONIQ_AUDIO_NAME_SIZE)
    {
        name_len = YONIQ_AUDIO_NAME_SIZE - 1;
    }
    memcpy(out->name, pInfo->name, name_len);
    out->name[name_len] = '\0';

    out->is_default = pInfo->isDefault ? 1 : 0;

    state->count++;
    return MA_TRUE;
}

int yoniq_audio_enumerate_devices(int is_capture, yoniq_audio_device_info *out_devices, int max_count)
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
     * real TOCTOU race against a concurrent yoniq_audio_context_uninit. Also shares the mutex with
     * get_native_formats/session-open, none of which are individually thread-safe against each
     * other either. */
    yoniq_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        yoniq_mutex_unlock(&g_context_mutex);
        return -1;
    }

    ma_result result = ma_context_enumerate_devices(&g_context, enum_callback, &state);
    yoniq_mutex_unlock(&g_context_mutex);
    if (result != MA_SUCCESS)
    {
        return -1;
    }

    return state.count;
}

int yoniq_audio_get_native_formats(const char *device_id, int is_capture, yoniq_audio_native_format *out_formats, int max_count)
{
    ma_device_info info;

    yoniq_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        yoniq_mutex_unlock(&g_context_mutex);
        return -1;
    }

    /* g_context.backend must also be read only while holding the lock: it's part of the same
     * context state a concurrent uninit could otherwise be tearing down mid-read. */
    ma_device_id id;
    if (string_to_device_id(g_context.backend, device_id, &id) != 0)
    {
        yoniq_mutex_unlock(&g_context_mutex);
        return -1;
    }

    ma_result result = ma_context_get_device_info(&g_context, is_capture ? ma_device_type_capture : ma_device_type_playback, &id, &info);
    yoniq_mutex_unlock(&g_context_mutex);
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

int yoniq_audio_spike_capture_test(const char *device_id, int duration_ms, float *peak_out)
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
    yoniq_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        yoniq_mutex_unlock(&g_context_mutex);
        return -1;
    }

    if (string_to_device_id(g_context.backend, device_id, &id) != 0)
    {
        yoniq_mutex_unlock(&g_context_mutex);
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
    yoniq_mutex_unlock(&g_context_mutex);
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
 * function's own doc comment in yoniq_audio.h for why this exists independent of any real device.
 */

struct yoniq_audio_ring
{
    ma_pcm_rb rb;
    int channels;
};

yoniq_audio_ring *yoniq_audio_ring_create(int capacity_frames, int channels)
{
    if (capacity_frames <= 0 || channels <= 0)
    {
        return NULL;
    }

    yoniq_audio_ring *ring = (yoniq_audio_ring *)malloc(sizeof(yoniq_audio_ring));
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

void yoniq_audio_ring_destroy(yoniq_audio_ring *ring)
{
    if (ring != NULL)
    {
        ma_pcm_rb_uninit(&ring->rb);
        free(ring);
    }
}

int yoniq_audio_ring_write(yoniq_audio_ring *ring, const float *data, int frame_count)
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

int yoniq_audio_ring_read(yoniq_audio_ring *ring, float *out_data, int frame_count)
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

int yoniq_audio_ring_available_read(yoniq_audio_ring *ring)
{
    if (ring == NULL)
    {
        return -1;
    }

    return (int)ma_pcm_rb_available_read(&ring->rb);
}

/*
 * Piece Audio 5: the real capture path. See yoniq_audio.h's own doc comment on
 * yoniq_audio_capture_session_open for the design.
 */

struct yoniq_audio_capture_session
{
    ma_device device;
    yoniq_audio_ring *ring;
    volatile int stopped; /* set by capture_session_notification_callback, real-time thread; read
                            * and cleared by yoniq_audio_capture_session_check_and_clear_stopped,
                            * managed thread -- a single int written from one side and read/cleared
                            * from the other needs no separate lock (no ordering dependency on
                            * anything else, a torn write of an int-sized value isn't a real
                            * concern on any platform this project targets). */
};

static void capture_session_data_callback(ma_device *pDevice, void *pOutput, const void *pInput, ma_uint32 frameCount)
{
    (void)pOutput;
    yoniq_audio_capture_session *session = (yoniq_audio_capture_session *)pDevice->pUserData;

    /* Drop-newest-when-full is already yoniq_audio_ring_write's own behavior (never blocks, writes
     * only as many frames as currently fit) -- exactly IAudioEngine's documented overrun policy
     * (piece Audio 2): if the managed drain side has fallen behind, the newest incoming frames are
     * dropped here, never corrupting or reordering what's already buffered. */
    yoniq_audio_ring_write(session->ring, (const float *)pInput, (int)frameCount);
}

static void capture_session_notification_callback(const ma_device_notification *pNotification)
{
    if (pNotification->type == ma_device_notification_type_stopped)
    {
        yoniq_audio_capture_session *session = (yoniq_audio_capture_session *)pNotification->pDevice->pUserData;
        session->stopped = 1;
    }
}

yoniq_audio_capture_session *yoniq_audio_capture_session_open(const char *device_id, int sample_rate, int ring_capacity_frames)
{
    /* Second-opus-review fix: flag check + g_context.backend read both moved inside the lock (see
     * g_context_mutex's doc comment) -- two short critical sections (id resolution, then init)
     * rather than one long one, each re-checking g_context_initialized since a concurrent uninit
     * could run in the gap between them. */
    ma_device_id id;
    yoniq_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        yoniq_mutex_unlock(&g_context_mutex);
        return NULL;
    }

    int id_result = string_to_device_id(g_context.backend, device_id, &id);
    yoniq_mutex_unlock(&g_context_mutex);
    if (id_result != 0)
    {
        return NULL;
    }

    yoniq_audio_capture_session *session = (yoniq_audio_capture_session *)malloc(sizeof(yoniq_audio_capture_session));
    if (session == NULL)
    {
        return NULL;
    }

    session->stopped = 0;
    session->ring = yoniq_audio_ring_create(ring_capacity_frames, 1);
    if (session->ring == NULL)
    {
        free(session);
        return NULL;
    }

    ma_device_config config = ma_device_config_init(ma_device_type_capture);
    config.capture.pDeviceID = &id;
    config.capture.format = ma_format_f32;
    config.capture.channels = 1;
    config.sampleRate = (ma_uint32)sample_rate;
    config.dataCallback = capture_session_data_callback;
    /* Wired in here at device-init time, not bolted on later (piece Audio 8): miniaudio's
     * notification callback must be set inside ma_device_config before ma_device_init, there is
     * no way to attach it to an already-initialized device. */
    config.notificationCallback = capture_session_notification_callback;
    config.pUserData = session;

    /* See g_context_mutex's doc comment: init's own source-info lookup touches the shared
     * context's mainloop, concurrently with any enumeration/probing/other session opens.
     * ma_device_start is deliberately NOT covered -- it only touches this device's own separate
     * mainloop once init has succeeded. */
    yoniq_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        yoniq_mutex_unlock(&g_context_mutex);
        yoniq_audio_ring_destroy(session->ring);
        free(session);
        return NULL;
    }

    ma_result init_result = ma_device_init(&g_context, &config, &session->device);
    yoniq_mutex_unlock(&g_context_mutex);
    if (init_result != MA_SUCCESS)
    {
        yoniq_audio_ring_destroy(session->ring);
        free(session);
        return NULL;
    }

    ma_result start_result = ma_device_start(&session->device);
    if (start_result != MA_SUCCESS)
    {
        ma_device_uninit(&session->device);
        yoniq_audio_ring_destroy(session->ring);
        free(session);
        return NULL;
    }

    return session;
}

void yoniq_audio_capture_session_close(yoniq_audio_capture_session *session)
{
    if (session == NULL)
    {
        return;
    }

    ma_device_uninit(&session->device); /* also stops it first */
    yoniq_audio_ring_destroy(session->ring);
    free(session);
}

int yoniq_audio_capture_session_read(yoniq_audio_capture_session *session, float *out_data, int frame_count)
{
    if (session == NULL)
    {
        return -1;
    }

    return yoniq_audio_ring_read(session->ring, out_data, frame_count);
}

int yoniq_audio_capture_session_check_and_clear_stopped(yoniq_audio_capture_session *session)
{
    if (session == NULL)
    {
        return 0;
    }

    int was_stopped = session->stopped;
    session->stopped = 0;
    return was_stopped;
}

/*
 * Piece Audio 6: the real playback path -- see yoniq_audio.h's own doc comment on
 * yoniq_audio_playback_session_open for the design.
 */

struct yoniq_audio_playback_session
{
    ma_device device;
    yoniq_audio_ring *ring;
    volatile int stopped;         /* see yoniq_audio_capture_session's own comment on this field --
                                    * same single-writer/single-reader reasoning applies here. */
    volatile int underrun_count;
};

static void playback_session_data_callback(ma_device *pDevice, void *pOutput, const void *pInput, ma_uint32 frameCount)
{
    (void)pInput;
    yoniq_audio_playback_session *session = (yoniq_audio_playback_session *)pDevice->pUserData;

    int frames_read = yoniq_audio_ring_read(session->ring, (float *)pOutput, (int)frameCount);
    if (frames_read < 0)
    {
        frames_read = 0;
    }

    if ((ma_uint32)frames_read < frameCount)
    {
        /* Underrun: not enough buffered data to fill this callback. Pad the remainder with
         * silence -- never leave garbage/uninitialized samples in the device's own output
         * buffer. Mono (1 channel), matching this session's own fixed config below. */
        float *output = (float *)pOutput;
        memset(output + frames_read, 0, ((size_t)frameCount - (size_t)frames_read) * sizeof(float));
        session->underrun_count++;
    }
}

static void playback_session_notification_callback(const ma_device_notification *pNotification)
{
    if (pNotification->type == ma_device_notification_type_stopped)
    {
        yoniq_audio_playback_session *session = (yoniq_audio_playback_session *)pNotification->pDevice->pUserData;
        session->stopped = 1;
    }
}

yoniq_audio_playback_session *yoniq_audio_playback_session_open(const char *device_id, int sample_rate, int ring_capacity_frames)
{
    /* See yoniq_audio_capture_session_open's identical pattern and comment. */
    ma_device_id id;
    yoniq_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        yoniq_mutex_unlock(&g_context_mutex);
        return NULL;
    }

    int id_result = string_to_device_id(g_context.backend, device_id, &id);
    yoniq_mutex_unlock(&g_context_mutex);
    if (id_result != 0)
    {
        return NULL;
    }

    yoniq_audio_playback_session *session = (yoniq_audio_playback_session *)malloc(sizeof(yoniq_audio_playback_session));
    if (session == NULL)
    {
        return NULL;
    }

    session->stopped = 0;
    session->underrun_count = 0;
    session->ring = yoniq_audio_ring_create(ring_capacity_frames, 1);
    if (session->ring == NULL)
    {
        free(session);
        return NULL;
    }

    ma_device_config config = ma_device_config_init(ma_device_type_playback);
    config.playback.pDeviceID = &id;
    config.playback.format = ma_format_f32;
    config.playback.channels = 1;
    config.sampleRate = (ma_uint32)sample_rate;
    config.dataCallback = playback_session_data_callback;
    /* Same reasoning as the capture session: must be set here, at config/init time -- miniaudio
     * has no way to attach a notification callback to an already-initialized device. */
    config.notificationCallback = playback_session_notification_callback;
    config.pUserData = session;

    /* See g_context_mutex's doc comment. ma_device_start deliberately not covered -- see the
     * capture session's identical comment. */
    yoniq_mutex_lock(&g_context_mutex);
    if (!g_context_initialized)
    {
        yoniq_mutex_unlock(&g_context_mutex);
        yoniq_audio_ring_destroy(session->ring);
        free(session);
        return NULL;
    }

    ma_result init_result = ma_device_init(&g_context, &config, &session->device);
    yoniq_mutex_unlock(&g_context_mutex);
    if (init_result != MA_SUCCESS)
    {
        yoniq_audio_ring_destroy(session->ring);
        free(session);
        return NULL;
    }

    ma_result start_result = ma_device_start(&session->device);
    if (start_result != MA_SUCCESS)
    {
        ma_device_uninit(&session->device);
        yoniq_audio_ring_destroy(session->ring);
        free(session);
        return NULL;
    }

    return session;
}

void yoniq_audio_playback_session_close(yoniq_audio_playback_session *session)
{
    if (session == NULL)
    {
        return;
    }

    ma_device_uninit(&session->device); /* also stops it first */
    yoniq_audio_ring_destroy(session->ring);
    free(session);
}

int yoniq_audio_playback_session_write(yoniq_audio_playback_session *session, const float *data, int frame_count)
{
    if (session == NULL)
    {
        return -1;
    }

    return yoniq_audio_ring_write(session->ring, data, frame_count);
}

int yoniq_audio_playback_session_pending_frames(yoniq_audio_playback_session *session)
{
    if (session == NULL)
    {
        return -1;
    }

    return yoniq_audio_ring_available_read(session->ring);
}

int yoniq_audio_playback_session_underrun_count(yoniq_audio_playback_session *session)
{
    if (session == NULL)
    {
        return -1;
    }

    return session->underrun_count;
}

int yoniq_audio_playback_session_check_and_clear_stopped(yoniq_audio_playback_session *session)
{
    if (session == NULL)
    {
        return 0;
    }

    int was_stopped = session->stopped;
    session->stopped = 0;
    return was_stopped;
}

int yoniq_audio_resample_f32(const float *input, int input_frame_count, int sample_rate_in,
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
