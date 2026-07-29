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

static ma_context g_context;
static int g_context_initialized = 0;

int yoniq_audio_context_init(char *backend_name_out)
{
    if (g_context_initialized)
    {
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

    return 0;
}

void yoniq_audio_context_uninit(void)
{
    if (g_context_initialized)
    {
        ma_context_uninit(&g_context);
        g_context_initialized = 0;
    }
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
        /* WASAPI's id is a wchar_t[64], not a string in our own ABI's UTF-8 convention -- real
         * Windows device-id handling is a separate, later concern (Audio 2's "device-Id
         * representation" decision); unreachable from this Linux-first spike. */
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
    if (!g_context_initialized)
    {
        return -1;
    }

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

    ma_result result = ma_context_enumerate_devices(&g_context, enum_callback, &state);
    if (result != MA_SUCCESS)
    {
        return -1;
    }

    return state.count;
}

int yoniq_audio_get_native_formats(const char *device_id, int is_capture, yoniq_audio_native_format *out_formats, int max_count)
{
    if (!g_context_initialized)
    {
        return -1;
    }

    ma_device_id id;
    if (string_to_device_id(g_context.backend, device_id, &id) != 0)
    {
        return -1;
    }

    ma_device_info info;
    ma_result result = ma_context_get_device_info(&g_context, is_capture ? ma_device_type_capture : ma_device_type_playback, &id, &info);
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
    if (!g_context_initialized)
    {
        return -1;
    }

    ma_device_id id;
    if (string_to_device_id(g_context.backend, device_id, &id) != 0)
    {
        return -2; /* unsupported backend for this spike */
    }

    spike_capture_state state;
    state.peak = 0.0f;

    ma_device_config config = ma_device_config_init(ma_device_type_capture);
    config.capture.pDeviceID = &id;
    config.capture.format = ma_format_f32;
    config.capture.channels = 1;
    config.sampleRate = 44100; /* arbitrary for this spike -- Audio 2 decides the real policy */
    config.dataCallback = spike_capture_data_callback;
    config.pUserData = &state;

    ma_device device;
    if (ma_device_init(&g_context, &config, &device) != MA_SUCCESS)
    {
        return -3;
    }

    if (ma_device_start(&device) != MA_SUCCESS)
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
