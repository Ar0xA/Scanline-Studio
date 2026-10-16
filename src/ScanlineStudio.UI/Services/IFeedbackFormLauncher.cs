namespace ScanlineStudio.UI.Services;

/// <summary>The two bundled web3forms pages under <c>feedback/</c> beside the executable.</summary>
public enum FeedbackForm
{
    Bug,
    Feedback,
}

/// <summary>Opens a bundled bug/feedback form in the OS default browser. web3forms' free plan
/// rejects non-browser posts (HTTP 403), so the browser page does the submit, not the app.</summary>
public interface IFeedbackFormLauncher
{
    /// <summary>Never throws: a missing asset or I/O failure is logged and the click does nothing.</summary>
    Task OpenAsync(FeedbackForm form, CancellationToken cancellationToken = default);
}
