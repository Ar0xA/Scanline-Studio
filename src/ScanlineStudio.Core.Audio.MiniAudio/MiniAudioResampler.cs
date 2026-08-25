namespace ScanlineStudio.Core.Audio.MiniAudio;

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
        // Round-1 code-review finding (Tier A Batch 9 chunk 9c): the overflow guard below only
        // ever caught an OVER-large output -- a non-positive rate makes `expectedOutputFrames`
        // negative, which passes the `> int.MaxValue` check and truncates to a negative `capacity`,
        // the exact "opaque exception instead of a clear one" failure mode that guard exists to
        // prevent (it would throw from `new float[negativeCapacity]` instead). Unreachable today
        // (this method is `internal`, single caller, constant real sample rates), but validated
        // explicitly rather than left as an implicit assumption the arithmetic below depends on.
        if (sampleRateIn <= 0 || sampleRateOut <= 0)
        {
            throw new ArgumentException($"Sample rates must be positive (got {sampleRateIn}Hz -> {sampleRateOut}Hz).", nameof(sampleRateIn));
        }

        // Generous capacity: expected output length plus slack for the resampler's own latency
        // and rounding, so a single native call always has room -- if it didn't, the shim itself
        // reports failure (-1) rather than silently truncating.
        var expectedOutputFrames = (long)Math.Ceiling(input.Length * (double)sampleRateOut / sampleRateIn) + 4096;
        // Opus-review fix: the previous `(int)expectedOutputFrames + 4096` truncated the long to
        // int *before* adding the slack, so a sufficiently long upsampled input (beyond ~2^31
        // output frames -- not reachable at any SSTV size today, but a silent wraparound-to-negative
        // is a much worse failure mode than a clear, actionable exception) would wrap negative and
        // throw an opaque OverflowException from `new float[...]` instead of this explicit check.
        if (expectedOutputFrames > int.MaxValue)
        {
            throw new ArgumentException(
                $"Resampling {input.Length} frames from {sampleRateIn}Hz to {sampleRateOut}Hz would require an output buffer larger than a .NET array can hold ({expectedOutputFrames} frames).",
                nameof(input));
        }

        var capacity = (int)expectedOutputFrames;
        var output = new float[capacity];

        int written;
        fixed (float* inputPtr = input)
        fixed (float* outputPtr = output)
        {
            written = NativeAudio.scanline_audio_resample_f32(inputPtr, input.Length, sampleRateIn, sampleRateOut, lpfOrder, outputPtr, capacity);
        }

        if (written < 0)
        {
            throw new InvalidOperationException($"Native resample from {sampleRateIn}Hz to {sampleRateOut}Hz failed (input length {input.Length}, output capacity {capacity}).");
        }

        return output[..written];
    }
}
