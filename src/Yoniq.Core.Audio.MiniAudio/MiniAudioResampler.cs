namespace Yoniq.Core.Audio.MiniAudio;

/// <summary>
/// Piece Audio 6b: a device-free, one-shot wrapper around miniaudio's own <c>ma_resampler</c> --
/// the same resampling code miniaudio's data converter uses internally to bridge a requested
/// sample rate (piece Audio 5/6's <c>sampleRate</c> parameter) to a real device's native rate.
/// Exists so a CI-safe test can measure whether that resampler meaningfully degrades the existing
/// SSTV round trip, without opening any real audio device.
/// </summary>
internal static class MiniAudioResampler
{
    /// <param name="input">Mono f32 samples at <paramref name="sampleRateIn"/>.</param>
    /// <param name="lpfOrder">Linear resampler low-pass filter order: -1 for miniaudio's own
    /// default (4), 0 to disable filtering, or an explicit order.</param>
    public static unsafe float[] Resample(ReadOnlySpan<float> input, int sampleRateIn, int sampleRateOut, int lpfOrder = -1)
    {
        // Generous capacity: expected output length plus slack for the resampler's own latency
        // and rounding, so a single native call always has room -- if it didn't, the shim itself
        // reports failure (-1) rather than silently truncating.
        var expectedOutputFrames = (long)Math.Ceiling(input.Length * (double)sampleRateOut / sampleRateIn);
        var capacity = (int)expectedOutputFrames + 4096;
        var output = new float[capacity];

        int written;
        fixed (float* inputPtr = input)
        fixed (float* outputPtr = output)
        {
            written = NativeAudio.yoniq_audio_resample_f32(inputPtr, input.Length, sampleRateIn, sampleRateOut, lpfOrder, outputPtr, capacity);
        }

        if (written < 0)
        {
            throw new InvalidOperationException($"Native resample from {sampleRateIn}Hz to {sampleRateOut}Hz failed (input length {input.Length}, output capacity {capacity}).");
        }

        return output[..written];
    }
}
