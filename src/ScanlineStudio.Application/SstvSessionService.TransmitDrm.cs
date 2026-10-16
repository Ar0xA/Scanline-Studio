using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Drm.Tx;

namespace ScanlineStudio.Application;

public sealed partial class SstvSessionService
{
    // Optional so test and tool constructions without DRM keep working; Host DI always supplies both.
    private readonly IDrmTransmitter? _drmTransmitter;
    private readonly IJpeg2000Codec? _jpeg2000Codec;

    public async Task<DrmTxResult> TransmitDrmAsync(IImageSource image, string fileName, DrmTxSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Callsign);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.TargetJp2Bytes);

        if (_drmTransmitter is not { } transmitter || _jpeg2000Codec is not { } codec)
        {
            const string NotConfigured = "DRM transmit is not configured in this build.";
            Log.DrmTxUnavailable(_logger, NotConfigured);
            throw new InvalidOperationException(NotConfigured);
        }

        // Rejected before the stamp is cleared, so a transmission already on air is never re-stamped.
        // PlayWithPttAsync's own single-flight guard still decides a race that slips past this check.
        if (Volatile.Read(ref _transmitInFlight) != 0)
        {
            throw new InvalidOperationException("A transmit or tune is already in progress.");
        }

        // The first IsAvailable read loads the native library; keep that off the caller's thread.
        if (!await Task.Run(() => codec.IsAvailable, ct).ConfigureAwait(false))
        {
            var reason = codec.UnavailableReason ?? "unknown reason";
            Log.DrmTxUnavailable(_logger, reason);
            throw new InvalidOperationException($"DRM transmit is unavailable: the JPEG 2000 codec (OpenJPEG) could not be loaded. {reason}");
        }

        // DrmTxSettings is a public record, so the offset is clamped here as well as in the UI.
        double levelOffsetDb = DrmTxSettings.ClampLevelOffsetDb(settings.LevelOffsetDb);
        Log.DrmTxStarting(_logger, settings.Mode, settings.Bandwidth, settings.Qam, settings.Protection, settings.Interleave, settings.ReedSolomon, image.Width, image.Height, settings.TargetJp2Bytes, levelOffsetDb);
        var (transmission, jp2Bytes) = await Task.Run(() => PrepareDrmTransmission(transmitter, codec, image, fileName, settings), ct).ConfigureAwait(false);
        Log.DrmTxPrepared(_logger, transmission.OnAirName, jp2Bytes, transmission.FrameCount, transmission.DisplayDuration.TotalSeconds);

        // Mono at sample/32768, as the SSTV TX: Pwr scales it the same way. QSSTV writes stereo with right = 0.
        // The SSTV clock offset and TX BPF/LPF are not applied: QSSTV's DRM output bypasses them.
        try
        {
            // Pre-scaled, not a pump parameter: DRM blocks are fresh arrays, so the shared PTT path stays untouched.
            var samples = ApplyLevelOffset(transmission.ReadSamplesAsync(ct), levelOffsetDb, ct);
            await PlayWithPttAsync(samples, transmitter.SampleRate, ct, totalSamplesEstimate: transmission.SampleCount).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SafeLog(() => Log.DrmTxCancelled(_logger, transmission.OnAirName));
            throw;
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.DrmTxFailed(_logger, transmission.OnAirName, ex));
            throw;
        }

        Log.DrmTxFinished(_logger, transmission.OnAirName, transmission.FrameCount);
        return new DrmTxResult(jp2Bytes, transmission.OnAirName);
    }

    public async Task<DrmEstimate> EstimateDrmAsync(IImageSource image, string fileName, DrmTxSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Callsign);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.TargetJp2Bytes);
        var (transmitter, codec) = RequireDrm();
        ct.ThrowIfCancellationRequested();

        return await Task.Run(
            () =>
            {
                if (!codec.IsAvailable)
                {
                    throw new InvalidOperationException($"DRM transmit is unavailable: the JPEG 2000 codec (OpenJPEG) could not be loaded. {codec.UnavailableReason}");
                }

                byte[] jp2 = EncodeJp2(codec, image, settings.TargetJp2Bytes);
                DrmFrameCount count = transmitter.CountFrames(jp2, fileName, settings);
                var duration = TimeSpan.FromSeconds((double)count.SampleCount / transmitter.SampleRate);
                Log.DrmEstimated(_logger, image.Width, image.Height, jp2.Length, count.FrameCount, duration.TotalSeconds);
                return new DrmEstimate(jp2, duration);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<TimeSpan> EstimateDrmDurationAsync(byte[] jp2, string fileName, DrmTxSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jp2);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(settings);
        var (transmitter, _) = RequireDrm();
        ct.ThrowIfCancellationRequested();

        return await Task.Run(
            () =>
            {
                DrmFrameCount count = transmitter.CountFrames(jp2, fileName, settings);
                return TimeSpan.FromSeconds((double)count.SampleCount / transmitter.SampleRate);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<bool> GetDrmTransmitAvailabilityAsync()
    {
        if (_drmTransmitter is null || _jpeg2000Codec is not { } codec)
        {
            return false;
        }

        // The first IsAvailable read loads the native library.
        bool available = await Task.Run(() => codec.IsAvailable).ConfigureAwait(false);
        if (!available)
        {
            Log.DrmTxUnavailable(_logger, codec.UnavailableReason ?? "unknown reason");
        }

        return available;
    }

    private static float[] Scale(ReadOnlyMemory<float> block, float factor)
    {
        var scaled = new float[block.Length];
        ReadOnlySpan<float> source = block.Span;
        for (int i = 0; i < scaled.Length; i++)
        {
            scaled[i] = source[i] * factor;
        }

        return scaled;
    }

    private (IDrmTransmitter Transmitter, IJpeg2000Codec Codec) RequireDrm()
    {
        if (_drmTransmitter is not { } transmitter || _jpeg2000Codec is not { } codec)
        {
            throw new InvalidOperationException("DRM transmit is not configured in this build.");
        }

        return (transmitter, codec);
    }

    /// <summary>Scales each block by the DRM level offset into a new array; a 0 dB offset passes blocks through.</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<float>> ApplyLevelOffset(
        IAsyncEnumerable<ReadOnlyMemory<float>> blocks, double levelOffsetDb, [EnumeratorCancellation] CancellationToken ct)
    {
        float factor = (float)Math.Pow(10, levelOffsetDb / 20);
        await foreach (ReadOnlyMemory<float> block in blocks.WithCancellation(ct).ConfigureAwait(false))
        {
            if (levelOffsetDb == 0)
            {
                yield return block;
                continue;
            }

            yield return Scale(block, factor);
        }
    }

    public void ForgetDrmTxFileName()
    {
        _drmTransmitter?.ForgetTxFileName();
        Log.DrmTxFileNameForgotten(_logger);
    }

    private (IDrmTransmission Transmission, int Jp2Bytes) PrepareDrmTransmission(
        IDrmTransmitter transmitter, IJpeg2000Codec codec, IImageSource image, string fileName, DrmTxSettings settings)
    {
        byte[] jp2 = EncodeJp2(codec, image, settings.TargetJp2Bytes);

        // QSSTV clears the stamp on every Start (txwidget.cpp:337-341 → drmtx.cpp:339-342): a new name and
        // transport ID per transmission, so receivers never take a repeat for an already received file.
        try
        {
            return (transmitter.Prepare(jp2, fileName, isImage: true, settings, newTxFileName: true), jp2.Length);
        }
        catch (Exception ex)
        {
            Log.DrmTxPrepareFailed(_logger, fileName, jp2.Length, ex);
            throw;
        }
    }

    private byte[] EncodeJp2(IJpeg2000Codec codec, IImageSource image, int targetJp2Bytes)
    {
        int width = image.Width;
        int height = image.Height;
        int stride = width * 3;
        var rgb = new byte[(long)stride * height];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgb24> row = image.GetScanline(y);
            Span<byte> dst = rgb.AsSpan(y * stride, stride);
            for (int x = 0; x < width; x++)
            {
                dst[x * 3] = row[x].R;
                dst[(x * 3) + 1] = row[x].G;
                dst[(x * 3) + 2] = row[x].B;
            }
        }

        int ratio = DrmJp2Ratio.FromTargetSize(width, height, targetJp2Bytes);
        try
        {
            return codec.EncodeRgb24(rgb, width, height, stride, ratio);
        }
        catch (Exception ex)
        {
            Log.DrmJp2EncodeFailed(_logger, width, height, ratio, ex);
            throw;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "DRM TX starting: mode={Mode}, bandwidth={Bandwidth}, {Qam}, protection={Protection}, {Interleave}, RS={ReedSolomon}, image {Width}x{Height}, target {TargetBytes} bytes, level offset {LevelOffsetDb} dB")]
        public static partial void DrmTxStarting(ILogger logger, DrmRobustnessMode mode, DrmBandwidth bandwidth, DrmQamOrder qam, DrmProtectionLevel protection, DrmInterleaving interleave, DrmReedSolomonLevel reedSolomon, int width, int height, int targetBytes, double levelOffsetDb);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM estimate: image {Width}x{Height}, JP2 {Jp2Bytes} bytes, {Frames} frames, {Seconds:F1} s")]
        public static partial void DrmEstimated(ILogger logger, int width, int height, int jp2Bytes, int frames, double seconds);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM TX prepared: '{OnAirName}', JP2 {Jp2Bytes} bytes, {Frames} frames (~{Seconds:F1} s)")]
        public static partial void DrmTxPrepared(ILogger logger, string onAirName, int jp2Bytes, int frames, double seconds);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM TX finished: '{OnAirName}', {Frames} frames")]
        public static partial void DrmTxFinished(ILogger logger, string onAirName, int frames);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM TX cancelled: '{OnAirName}'")]
        public static partial void DrmTxCancelled(ILogger logger, string onAirName);

        [LoggerMessage(Level = LogLevel.Error, Message = "DRM TX failed: '{OnAirName}'")]
        public static partial void DrmTxFailed(ILogger logger, string onAirName, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DRM TX unavailable: {Reason}")]
        public static partial void DrmTxUnavailable(ILogger logger, string reason);

        [LoggerMessage(Level = LogLevel.Error, Message = "JPEG 2000 encode failed for a {Width}x{Height} image at ratio {Ratio}")]
        public static partial void DrmJp2EncodeFailed(ILogger logger, int width, int height, int ratio, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "DRM TX preparation failed for '{FileName}' ({Jp2Bytes} bytes)")]
        public static partial void DrmTxPrepareFailed(ILogger logger, string fileName, int jp2Bytes, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM TX file name stamp cleared")]
        public static partial void DrmTxFileNameForgotten(ILogger logger);
    }
}
