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

    public async Task TransmitDrmAsync(IImageSource image, string fileName, DrmTxSettings settings, CancellationToken ct = default)
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

        Log.DrmTxStarting(_logger, settings.Mode, settings.Bandwidth, settings.Qam, settings.Protection, settings.Interleave, settings.ReedSolomon, image.Width, image.Height, settings.TargetJp2Bytes);
        var (transmission, jp2Bytes) = await Task.Run(() => PrepareDrmTransmission(transmitter, codec, image, fileName, settings), ct).ConfigureAwait(false);
        Log.DrmTxPrepared(_logger, transmission.OnAirName, jp2Bytes, transmission.FrameCount, transmission.DisplayDuration.TotalSeconds);

        // Mono at sample/32768, as the SSTV TX: Pwr scales it the same way. QSSTV writes stereo with right = 0.
        // The SSTV clock offset and TX BPF/LPF are not applied: QSSTV's DRM output bypasses them.
        try
        {
            await PlayWithPttAsync(transmission.ReadSamplesAsync(ct), transmitter.SampleRate, ct, totalSamplesEstimate: transmission.SampleCount).ConfigureAwait(false);
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
    }

    public void ForgetDrmTxFileName()
    {
        _drmTransmitter?.ForgetTxFileName();
        Log.DrmTxFileNameForgotten(_logger);
    }

    private (IDrmTransmission Transmission, int Jp2Bytes) PrepareDrmTransmission(
        IDrmTransmitter transmitter, IJpeg2000Codec codec, IImageSource image, string fileName, DrmTxSettings settings)
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

        int ratio = DrmJp2Ratio.FromTargetSize(width, height, settings.TargetJp2Bytes);
        byte[] jp2;
        try
        {
            jp2 = codec.EncodeRgb24(rgb, width, height, stride, ratio);
        }
        catch (Exception ex)
        {
            Log.DrmJp2EncodeFailed(_logger, width, height, ratio, ex);
            throw;
        }

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

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "DRM TX starting: mode={Mode}, bandwidth={Bandwidth}, {Qam}, protection={Protection}, {Interleave}, RS={ReedSolomon}, image {Width}x{Height}, target {TargetBytes} bytes")]
        public static partial void DrmTxStarting(ILogger logger, DrmRobustnessMode mode, DrmBandwidth bandwidth, DrmQamOrder qam, DrmProtectionLevel protection, DrmInterleaving interleave, DrmReedSolomonLevel reedSolomon, int width, int height, int targetBytes);

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
