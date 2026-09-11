namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Opt-in gate for the decode MEASUREMENT harnesses — the probes that report a table of per-mode
/// numbers through <c>Assert.Fail</c> because that is how xUnit surfaces output, and therefore
/// "fail" on every platform by design.
///
/// <para><b>Why they needed gating.</b> They were the odd ones out: the other 23 harnesses of this
/// kind already gate themselves off by default. Ungated, they make a clean run unreadable — six
/// permanent red entries that mean nothing is wrong — and they dominate runtime. On the first full
/// Windows run they accounted for roughly 28 of the suite's 35 minutes.</para>
///
/// <para>Set <c>SCANLINE_RUN_DECODE_MEASUREMENTS=1</c> to run them. Several additionally need
/// <c>SCANLINE_SOURCE_BMP</c>; those keep their own second gate.</para>
/// </summary>
public sealed class RequiresDecodeMeasurementFactAttribute : FactAttribute
{
    public const string OptInVariable = "SCANLINE_RUN_DECODE_MEASUREMENTS";

    public RequiresDecodeMeasurementFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Skip = $"Set {OptInVariable}=1 to run the decode measurement harnesses. They report "
                + "through the failure message by design, so they always 'fail' when run.";
        }
    }
}
