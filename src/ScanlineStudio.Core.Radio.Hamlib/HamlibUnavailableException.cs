namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// Thrown when no usable <c>libhamlib</c> could be found and loaded (spec/03-cat-layer.md's
/// "Discovery order"), or a loaded library failed the version gate. Carries every candidate/reason
/// tried, since the eventual cross-backend demotion-to-rigctld caller (not built yet -- deferred to
/// the Application layer, see spec/03) needs something concrete to log rather than a bare message.
/// </summary>
public sealed class HamlibUnavailableException : Exception
{
    public IReadOnlyList<string> Attempts { get; }

    public HamlibUnavailableException(IReadOnlyList<string> attempts)
        : base(BuildMessage(attempts))
    {
        Attempts = attempts;
    }

    private static string BuildMessage(IReadOnlyList<string> attempts) =>
        attempts.Count == 0
            ? "Hamlib is unavailable."
            : "Hamlib is unavailable. Attempts:" + string.Concat(attempts.Select(a => "\n  - " + a));
}
