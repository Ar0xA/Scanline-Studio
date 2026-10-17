using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Drm;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>Builds <see cref="DrmReceiver"/> instances.</summary>
public sealed class DrmReceiverFactory(ILoggerFactory? loggerFactory = null) : IDrmReceiverFactory
{
    /// <inheritdoc/>
    public bool IsSupportedRate(int captureRate) => DrmReceiver.IsSupportedRate(captureRate);

    /// <inheritdoc/>
    public IDrmReceiver Create(int captureRate) => new DrmReceiver(captureRate, loggerFactory);
}
