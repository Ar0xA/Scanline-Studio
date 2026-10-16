using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;

namespace ScanlineStudio.UI.Services;

/// <summary>
/// Builds a self-contained copy of a bundled form page and opens it in the default browser.
/// The page template (<c>feedback/[lang/]bug.html</c> or <c>feedback.html</c>) has placeholders
/// for the shared <c>form.css</c>/<c>form.js</c> and for the app version, OS and runtime; the
/// filled copy is written to a per-user directory (see <see cref="DefaultOutputDirectory"/>), because a
/// shared temp directory such as <c>/tmp</c> can be owned by another user. The copy is filled on every open so a
/// mid-session language switch picks the right template.
/// </summary>
public sealed partial class FeedbackFormLauncher : IFeedbackFormLauncher
{
    private readonly IUrlLauncher _urlLauncher;
    private readonly ILocalizationService _localization;
    private readonly ILogger<FeedbackFormLauncher> _logger;
    private readonly string _assetDirectory;
    private readonly string _outputDirectory;
    private readonly string _appVersion;
    private readonly string _operatingSystem;
    private readonly string _runtime;

    public FeedbackFormLauncher(
        IUrlLauncher urlLauncher,
        ILocalizationService localization,
        ILogger<FeedbackFormLauncher> logger)
        : this(
            urlLauncher,
            localization,
            logger,
            Path.Combine(AppContext.BaseDirectory, "feedback"),
            DefaultOutputDirectory(),
            GetAppVersion(),
            $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            RuntimeInformation.FrameworkDescription)
    {
    }

    /// <summary>Test seam: every path and reported value is injectable.</summary>
    public FeedbackFormLauncher(
        IUrlLauncher urlLauncher,
        ILocalizationService localization,
        ILogger<FeedbackFormLauncher> logger,
        string assetDirectory,
        string outputDirectory,
        string appVersion,
        string operatingSystem,
        string runtime)
    {
        _urlLauncher = urlLauncher;
        _localization = localization;
        _logger = logger;
        _assetDirectory = assetDirectory;
        _outputDirectory = outputDirectory;
        _appVersion = appVersion;
        _operatingSystem = operatingSystem;
        _runtime = runtime;
    }

    /// <summary>Linux: the app's existing visible <c>~/Pictures/ScanlineStudio</c> folder, because a snap-confined
    /// browser (Ubuntu's default Firefox) cannot read hidden folders such as <c>~/.local/share</c>.
    /// Elsewhere, and when no Pictures folder is known: the per-user local app-data folder.</summary>
    public static string DefaultOutputDirectory()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return OperatingSystem.IsLinux() && !string.IsNullOrEmpty(pictures)
            ? Path.Combine(pictures, "ScanlineStudio", "Feedback")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanlineStudio", "feedback");
    }

    public static string FileNameFor(FeedbackForm form) => form switch
    {
        FeedbackForm.Bug => "bug.html",
        FeedbackForm.Feedback => "feedback.html",
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, null),
    };

    public async Task OpenAsync(FeedbackForm form, CancellationToken cancellationToken = default)
    {
        try
        {
            var fileName = FileNameFor(form);
            var templatePath = HelpGuidePathResolver.Resolve(
                _assetDirectory, _localization.CurrentCulture.TwoLetterISOLanguageName, fileName);
            var template = await File.ReadAllTextAsync(templatePath, cancellationToken).ConfigureAwait(false);
            var style = await File.ReadAllTextAsync(Path.Combine(_assetDirectory, "form.css"), cancellationToken).ConfigureAwait(false);
            var script = await File.ReadAllTextAsync(Path.Combine(_assetDirectory, "form.js"), cancellationToken).ConfigureAwait(false);

            var page = Fill(template, style, script, _appVersion, _operatingSystem, _runtime);

            Directory.CreateDirectory(_outputDirectory);
            var outputPath = Path.Combine(_outputDirectory, fileName);
            await File.WriteAllTextAsync(outputPath, page, cancellationToken).ConfigureAwait(false);

            Log.FormOpening(_logger, form, outputPath);
            _urlLauncher.Open(outputPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.FormOpenFailed(_logger, form, ex);
        }
    }

    /// <summary>Version/OS/runtime only ever land in HTML text or attribute positions, so HTML
    /// encoding is enough; the style and script are trusted bundled assets inserted as-is.</summary>
    public static string Fill(string template, string style, string script, string appVersion, string operatingSystem, string runtime) =>
        template
            .Replace("{{FORM_STYLE}}", style, StringComparison.Ordinal)
            .Replace("{{FORM_SCRIPT}}", script, StringComparison.Ordinal)
            .Replace("{{APP_VERSION}}", WebUtility.HtmlEncode(appVersion), StringComparison.Ordinal)
            .Replace("{{OS}}", WebUtility.HtmlEncode(operatingSystem), StringComparison.Ordinal)
            .Replace("{{RUNTIME}}", WebUtility.HtmlEncode(runtime), StringComparison.Ordinal);

    // typeof(...).Assembly, not GetEntryAssembly: the latter is the test runner under xunit.
    private static string GetAppVersion()
    {
        var assembly = typeof(FeedbackFormLauncher).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Opening {Form} form from {Path}")]
        public static partial void FormOpening(ILogger logger, FeedbackForm form, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not prepare the {Form} form")]
        public static partial void FormOpenFailed(ILogger logger, FeedbackForm form, Exception ex);
    }
}
