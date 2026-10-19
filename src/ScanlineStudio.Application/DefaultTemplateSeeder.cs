using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

/// <summary>Installs the starter templates (two CQ, two reply/RST, two 73) once, the way legacy
/// YONIQ's <c>LoadDefTemp</c> seeded <c>def1..5.mtm</c> on first start. The designs are original,
/// not copies of the legacy <c>.mtm</c> files.</summary>
public interface IDefaultTemplateSeeder
{
    /// <summary>Adds every starter template not yet handled. Never overwrites: each template gets a
    /// fresh id, and a name that already exists is skipped. Each starter's key is recorded once added
    /// or skipped, so a deleted or renamed starter never comes back; a failed save is retried next
    /// launch. A later release adds new starters by adding new keys. Returns the number added.</summary>
    Task<int> SeedIfNeededAsync(CancellationToken ct = default);
}

public sealed partial class DefaultTemplateSeeder : IDefaultTemplateSeeder
{
    private const string Font = "Barlow";

    private static readonly Rgb24 White = new(255, 255, 255);
    private static readonly Rgb24 Black = new(0, 0, 0);
    private static readonly Rgb24 Yellow = new(255, 214, 0);
    private static readonly Rgb24 Navy = new(16, 48, 112);
    private static readonly Rgb24 DarkRed = new(112, 16, 16);

    private readonly ITemplateStore _templateStore;
    private readonly ISettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private readonly ILogger<DefaultTemplateSeeder> _logger;

    public DefaultTemplateSeeder(ITemplateStore templateStore, ISettingsStore settingsStore, ILocalizationService localization, ILogger<DefaultTemplateSeeder> logger)
    {
        _templateStore = templateStore;
        _settingsStore = settingsStore;
        _localization = localization;
        _logger = logger;
    }

    public async Task<int> SeedIfNeededAsync(CancellationToken ct = default)
    {
        var starters = BuildStarterSet();
        var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = settings.GetSection(DefaultTemplatesSettings.SectionKey, DefaultTemplatesSettingsJsonContext.Default.DefaultTemplatesSettings);
        var handled = new HashSet<string>(section?.HandledNameKeys ?? [], StringComparer.Ordinal);
        if (starters.All(s => handled.Contains(s.NameKey)))
        {
            return 0;
        }

        // A real write before any template: it throws while settings.json is unreadable, so nothing
        // is seeded against defaults (which would bring back starters the user deleted).
        await RecordHandledAsync(handled, ct).ConfigureAwait(false);

        var existing = await _templateStore.ListAsync(ct).ConfigureAwait(false);
        var existingNames = new HashSet<string>(existing.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var failed = false;
        foreach (var (nameKey, elements) in starters)
        {
            if (handled.Contains(nameKey))
            {
                continue;
            }

            var name = _localization.GetString(nameKey);
            if (!existingNames.Add(name))
            {
                Log.SkippedExisting(_logger, name);
                handled.Add(nameKey);
                continue;
            }

            try
            {
                await _templateStore.SaveAsync(_templateStore.CreateTemplateId(name), name, new PersistedTemplateDocument(elements), ct).ConfigureAwait(false);
                added++;
                handled.Add(nameKey);
                await RecordHandledAsync(handled, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed = true;
                Log.SaveFailed(_logger, name, ex);
            }
        }

        await RecordHandledAsync(handled, ct).ConfigureAwait(false);
        Log.Seeded(_logger, added, failed);
        return added;
    }

    private Task<AppSettings> RecordHandledAsync(HashSet<string> handled, CancellationToken ct) =>
        _settingsStore.UpdateAsync(s =>
        {
            var current = s.GetSection(DefaultTemplatesSettings.SectionKey, DefaultTemplatesSettingsJsonContext.Default.DefaultTemplatesSettings) ?? new DefaultTemplatesSettings();
            // Keys already on disk stay handled, so a stale first load can never shrink the record.
            handled.UnionWith(current.HandledNameKeys ?? []);
            return s.WithSection(DefaultTemplatesSettings.SectionKey, current with { HandledNameKeys = [.. handled.Order(StringComparer.Ordinal)] }, DefaultTemplatesSettingsJsonContext.Default.DefaultTemplatesSettings);
        }, ct);

    // Overlays only (no image assets), so they sit on top of whatever picture is loaded.
    // Template text is on-air QSO wording, not UI text, so it is not localized.
    internal static IReadOnlyList<(string NameKey, IReadOnlyList<PersistedTemplateElement> Elements)> BuildStarterSet() =>
    [
        ("Templates.Default.CqBanner",
        [
            Box(0.5, 0.12, 1.0, 0.24, 0, Black, 0.55),
            Text(0.5, 0.12, 0.92, 0.22, 1, "CQ CQ CQ", 0.16, White, Black, 0.012),
            Box(0.5, 0.9, 1.0, 0.2, 2, Black, 0.55),
            Text(0.5, 0.9, 0.92, 0.18, 3, "DE %m", 0.13, Yellow, Black, 0.012),
        ]),
        ("Templates.Default.CqClassic",
        [
            Text(0.22, 0.2, 0.4, 0.34, 0, "CQ", 0.28, Yellow, Black, 0.02, shadow: true),
            Text(0.22, 0.42, 0.34, 0.14, 1, "SSTV", 0.09, White, Black, 0.015),
            Text(0.66, 0.88, 0.64, 0.18, 2, "DE %m", 0.12, White, Black, 0.015, shadow: true),
        ]),
        ("Templates.Default.ReplyRstBanner",
        [
            Box(0.5, 0.1, 1.0, 0.2, 0, Navy, 0.7),
            Text(0.5, 0.1, 0.94, 0.18, 1, "{his_call} DE %m", 0.11, White, Black, 0.01),
            Box(0.5, 0.9, 1.0, 0.2, 2, Navy, 0.7),
            Text(0.5, 0.9, 0.94, 0.18, 3, "UR RSV {rsv}", 0.12, Yellow, Black, 0.01),
        ]),
        ("Templates.Default.ReplyBigCall",
        [
            Text(0.5, 0.14, 0.92, 0.26, 0, "{his_call}", 0.18, Yellow, Black, 0.02, shadow: true),
            Box(0.5, 0.9, 1.0, 0.2, 1, Black, 0.55),
            Text(0.27, 0.9, 0.5, 0.18, 2, "RSV {rsv}", 0.11, White, Black, 0.01),
            Text(0.75, 0.9, 0.46, 0.18, 3, "DE %m", 0.11, Yellow, Black, 0.01),
        ]),
        ("Templates.Default.SeventyThreeBanner",
        [
            Text(0.26, 0.09, 0.48, 0.16, 0, "TNX QSO", 0.1, White, Black, 0.015, shadow: true),
            Box(0.5, 0.9, 1.0, 0.2, 1, DarkRed, 0.7),
            Text(0.5, 0.9, 0.94, 0.18, 2, "73 {his_call} DE %m", 0.11, White, Black, 0.01),
        ]),
        ("Templates.Default.SeventyThreeBig",
        [
            Text(0.5, 0.1, 0.92, 0.18, 0, "TNX QSO {his_call}", 0.1, White, Black, 0.015, shadow: true),
            Text(0.8, 0.74, 0.36, 0.38, 1, "73", 0.3, Yellow, Black, 0.02, shadow: true, rotation: -8),
            Text(0.3, 0.9, 0.56, 0.18, 2, "DE %m", 0.11, White, Black, 0.015, shadow: true),
        ]),
    ];

    private static PersistedTextElement Text(double x, double y, double w, double h, int z, string text, double size, Rgb24 color, Rgb24 stroke, double strokeThickness, bool shadow = false, double rotation = 0) =>
        new(x, y, w, h, z, Locked: false, text, size, color, Font, stroke, strokeThickness,
            ShadowColor: shadow ? Black : null, ShadowOffsetX: 0.012, ShadowOffsetY: 0.012, RotationDegrees: rotation, Bold: true);

    private static PersistedBoxElement Box(double x, double y, double w, double h, int z, Rgb24 fill, double opacity) =>
        new(x, y, w, h, z, Locked: false, fill, BorderColor: null, BorderThickness: 0, Opacity: opacity);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Starter templates: {Added} added, any failed: {Failed}")]
        public static partial void Seeded(ILogger logger, int added, bool failed);

        [LoggerMessage(Level = LogLevel.Information, Message = "Starter template '{Name}' skipped: a template with that name already exists")]
        public static partial void SkippedExisting(ILogger logger, string name);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Starter template '{Name}' could not be saved; it will be retried next launch")]
        public static partial void SaveFailed(ILogger logger, string name, Exception ex);
    }
}
