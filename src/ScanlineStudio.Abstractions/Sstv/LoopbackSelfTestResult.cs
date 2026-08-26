using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>How a Loopback self-test decode ended -- see <see cref="LoopbackSelfTestResult"/>.</summary>
public enum LoopbackSelfTestOutcome
{
    /// <summary>Every scanline of the encoded mode decoded back.</summary>
    Completed,

    /// <summary>The decode stopped before the last scanline, for a reason other than Auto Stop (e.g.
    /// a genuine decode-fidelity problem the self-test exists to surface).</summary>
    Incomplete,

    /// <summary>The user's own Auto Stop setting fired mid-decode and abandoned the reception -- a
    /// distinct outcome from <see cref="Incomplete"/> so a caller doesn't read this as a false
    /// encoder/decoder bug when it's actually the user's own setting doing what it's configured to do.</summary>
    AbandonedByAutoStop,
}

/// <summary>Result of <see cref="ScanlineStudio.Application.ISstvSessionService.RunLoopbackSelfTestAsync"/>
/// -- see that member's own doc comment for the full design.</summary>
/// <param name="Image">Deep copy of the decoded image at whatever point decoding stopped (see
/// <paramref name="Outcome"/>) -- never a live decoder-owned alias.</param>
/// <param name="DetectedModeId">The mode the self-test's own decoder actually locked onto, which a
/// caller should compare against the mode it asked to encode -- a wrong-mode lock would otherwise read
/// as a false "success" (a complete decode of the WRONG mode still reports <see
/// cref="LoopbackSelfTestOutcome.Completed"/>). <see langword="null"/> if no mode was ever detected.</param>
/// <param name="Outcome">How the decode ended.</param>
public sealed record LoopbackSelfTestResult(IImageSource Image, string? DetectedModeId, LoopbackSelfTestOutcome Outcome);
