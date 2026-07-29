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

/* Spike/gate test only (not the production capture path -- see piece Audio 5 for that): opens
 * the named capture device (by the id string yoniq_audio_enumerate_devices returned), captures
 * for duration_ms milliseconds, and writes the peak absolute sample value observed to *peak_out.
 * A silent device (or one that never actually delivers data) reports peak_out == 0. Returns 0 on
 * success, nonzero if the device could not be opened or started. */
int yoniq_audio_spike_capture_test(const char *device_id, int duration_ms, float *peak_out);

#ifdef __cplusplus
}
#endif

#endif
