/* Hardware-free regression for ASTRA-013. Compile with -lm -ldl -lpthread on Linux. */
#include "../../../src/ScanlineStudio.Core.Audio.MiniAudio/native/scanline_audio.c"
#include <assert.h>
#include <pthread.h>

typedef struct notification_test {
    scanline_audio_capture_session capture;
    scanline_audio_playback_session playback;
    ma_uint32 acknowledged;
    ma_uint32 published;
} notification_test;

static void publish(notification_test *test)
{
    ma_device device;
    ma_device_notification notification;
    memset(&device, 0, sizeof(device));
    memset(&notification, 0, sizeof(notification));
    notification.type = ma_device_notification_type_stopped;
    notification.pDevice = &device;
    device.pUserData = &test->capture;
    capture_session_notification_callback(&notification);
    device.pUserData = &test->playback;
    playback_session_notification_callback(&notification);
}

static void consume(notification_test *test)
{
    assert(scanline_audio_capture_session_check_and_clear_stopped(&test->capture) == 1);
    assert(scanline_audio_playback_session_check_and_clear_stopped(&test->playback) == 1);
    assert(scanline_audio_capture_session_check_and_clear_stopped(&test->capture) == 0);
    assert(scanline_audio_playback_session_check_and_clear_stopped(&test->playback) == 0);
}

static void *producer(void *context)
{
    notification_test *test = context;
    for (ma_uint32 i = 1; i <= 10000; ++i) {
        while (ma_atomic_load_32(&test->acknowledged) != i - 1) { }
        publish(test);
        ma_atomic_store_32(&test->published, i);
    }
    return NULL;
}

int main(void)
{
    notification_test test;
    memset(&test, 0, sizeof(test));
    assert(scanline_audio_capture_session_check_and_clear_stopped(NULL) == 0);
    assert(scanline_audio_playback_session_check_and_clear_stopped(NULL) == 0);
    publish(&test);
    publish(&test); /* notifications coalesce until consumed */
    consume(&test);
    publish(&test); /* a later producer must survive the previous clear */
    consume(&test);
    pthread_t worker;
    assert(pthread_create(&worker, NULL, producer, &test) == 0);
    for (ma_uint32 i = 1; i <= 10000; ++i) {
        while (ma_atomic_load_32(&test.published) != i) { }
        consume(&test);
        ma_atomic_store_32(&test.acknowledged, i);
    }
    assert(pthread_join(worker, NULL) == 0);
    puts("capture/playback atomic notifications: 10000 handoffs passed");
    return 0;
}
