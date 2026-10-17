namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>Receiver behaviour switches, fixed for the life of a <see cref="DrmReceiver"/>. The default is our improved
/// receiver; the <c>ReproduceQsstv*</c> switches restore QSSTV's behaviour for baselines and measurements.</summary>
internal sealed record DrmRxOptions
{
    public static DrmRxOptions Default { get; } = new();

    /// <summary>Trust a wrapped 8-bit PPI byte count as QSSTV does (see <c>DrmSourceDecoder.RetryWrappedPpiCount</c>).</summary>
    public bool ReproduceQsstvPpiWrap { get; init; }

    /// <summary>Read the RS1 size surplus byte through a signed char as QSSTV x86 does (see <c>RsFileDecoder</c>).</summary>
    public bool ReproduceQsstvSignedSurplus { get; init; }

    /// <summary>P6b-4 candidate under measurement; off = QSSTV (see <c>DrmChannelEstimator.SnrTimeoutStartsFull</c>).</summary>
    public bool SnrTimeoutStartsFull { get; init; }

    /// <summary>P6b-4 candidate under measurement; off = QSSTV (see <c>DrmChannelEstimator.FacCounterResetsOnGoodFrame</c>).</summary>
    public bool FacCounterResetsOnGoodFrame { get; init; }

    /// <summary>Every QSSTV RX behaviour this receiver improves on, for an exact-port baseline.</summary>
    public static DrmRxOptions Qsstv { get; } = new() { ReproduceQsstvPpiWrap = true, ReproduceQsstvSignedSurplus = true };
}
