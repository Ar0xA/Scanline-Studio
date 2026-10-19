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
    private static readonly Rgb24 GradientRed = new(255, 80, 80);
    private static readonly Rgb24 GradientBlue = new(90, 150, 255);

    // Half the starters get a bundled backdrop (original art, scripts/generate_template_backdrops.py).
    internal static readonly IReadOnlyDictionary<string, string> Backdrops = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Templates.Default.CqClassic"] = "tower-dusk.png",
        ["Templates.Default.ReplyBigCall"] = "waterfall.png",
        ["Templates.Default.SeventyThreeBig"] = "globe-grid.png",
    };

    internal const string BackdropAssetFileName = "backdrop.png";
    private const int BackdropPixelWidth = 640;
    private const int BackdropPixelHeight = 512;

    private readonly ITemplateStore _templateStore;
    private readonly ISettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private readonly ILogger<DefaultTemplateSeeder> _logger;
    private readonly string _backdropDirectory;

    public DefaultTemplateSeeder(ITemplateStore templateStore, ISettingsStore settingsStore, ILocalizationService localization, ILogger<DefaultTemplateSeeder> logger)
        : this(templateStore, settingsStore, localization, logger, Path.Combine(AppContext.BaseDirectory, "assets", "templates", "backdrops"))
    {
    }

    /// <summary><paramref name="backdropDirectory"/> is a test seam; production reads the backdrops
    /// copied beside the executable.</summary>
    public DefaultTemplateSeeder(ITemplateStore templateStore, ISettingsStore settingsStore, ILocalizationService localization, ILogger<DefaultTemplateSeeder> logger, string backdropDirectory)
    {
        _templateStore = templateStore;
        _settingsStore = settingsStore;
        _localization = localization;
        _logger = logger;
        _backdropDirectory = backdropDirectory;
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
                var templateId = _templateStore.CreateTemplateId(name);
                var document = await WithBackdropAsync(templateId, nameKey, elements, ct).ConfigureAwait(false);
                await _templateStore.SaveAsync(templateId, name, document, ct).ConfigureAwait(false);
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

    // The asset goes into the brand-new template folder before SaveAsync, which renders the thumbnail
    // from it. A missing bundled file degrades to the overlay-only template rather than failing.
    private async Task<PersistedTemplateDocument> WithBackdropAsync(string templateId, string nameKey, IReadOnlyList<PersistedTemplateElement> elements, CancellationToken ct)
    {
        if (!Backdrops.TryGetValue(nameKey, out var backdropFile))
        {
            return new PersistedTemplateDocument(elements);
        }

        var source = Path.Combine(_backdropDirectory, backdropFile);
        if (!File.Exists(source))
        {
            Log.BackdropMissing(_logger, source);
            return new PersistedTemplateDocument(elements);
        }

        var assetPath = _templateStore.GetAssetPath(templateId, BackdropAssetFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(assetPath, FileMode.CreateNew, FileAccess.Write))
        {
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }

        var backdrop = new PersistedImageElement(
            X: 0.5, Y: 0.5, Width: 1.0, Height: 1.0, Z: -1, Locked: true,
            BackdropAssetFileName, ImageFitMode.Cover, PersistedImageSourceKind.File, OriginPayload: null,
            IsBackground: true, NaturalPixelWidth: BackdropPixelWidth, NaturalPixelHeight: BackdropPixelHeight);
        return new PersistedTemplateDocument([backdrop, .. elements]);
    }

    private Task<AppSettings> RecordHandledAsync(HashSet<string> handled, CancellationToken ct) =>
        _settingsStore.UpdateAsync(s =>
        {
            var current = s.GetSection(DefaultTemplatesSettings.SectionKey, DefaultTemplatesSettingsJsonContext.Default.DefaultTemplatesSettings) ?? new DefaultTemplatesSettings();
            // Keys already on disk stay handled, so a stale first load can never shrink the record.
            handled.UnionWith(current.HandledNameKeys ?? []);
            return s.WithSection(DefaultTemplatesSettings.SectionKey, current with { HandledNameKeys = [.. handled.Order(StringComparer.Ordinal)] }, DefaultTemplatesSettingsJsonContext.Default.DefaultTemplatesSettings);
        }, ct);

    // Text and box layers; see Backdrops for the three that also get a picture underneath.
    // Template text is on-air QSO wording, not UI text, so it is not localized.
    internal static IReadOnlyList<(string NameKey, IReadOnlyList<PersistedTemplateElement> Elements)> BuildStarterSet() =>
    [
        ("Templates.Default.CqBanner",
        [
            .. VersionLine(),
            Box(0.5, 0.175, 1.0, 0.22, 2, Black, 0.55),
            Text(0.5, 0.175, 0.92, 0.2, 3, "CQ CQ CQ", 0.16, White, Black, 0.012),
            Box(0.5, 0.9, 1.0, 0.2, 4, Black, 0.55),
            Text(0.5, 0.9, 0.92, 0.18, 5, "DE %m", 0.13, Yellow, Black, 0.012),
        ]),
        ("Templates.Default.CqClassic",
        [
            .. VersionLine(),
            Text(0.22, 0.235, 0.4, 0.34, 2, "CQ", 0.28, Yellow, Black, 0.02, shadow: true),
            Text(0.22, 0.455, 0.34, 0.14, 3, "SSTV", 0.09, White, Black, 0.015),
            Text(0.66, 0.88, 0.64, 0.18, 4, "DE %m", 0.12, White, Black, 0.015, shadow: true),
        ]),
        ("Templates.Default.ReplyRstBanner",
        [
            .. VersionLine(),
            Box(0.5, 0.165, 1.0, 0.2, 2, Navy, 0.7),
            Text(0.5, 0.165, 0.94, 0.18, 3, "{his_call} DE %m", 0.11, White, Black, 0.01),
            Box(0.5, 0.9, 1.0, 0.2, 4, Navy, 0.7),
            Text(0.5, 0.9, 0.94, 0.18, 5, "UR RSV {rsv}", 0.12, Yellow, Black, 0.01),
        ]),
        ("Templates.Default.ReplyBigCall",
        [
            .. VersionLine(),
            Text(0.5, 0.2, 0.92, 0.26, 2, "{his_call}", 0.18, Yellow, Black, 0.02, shadow: true),
            Box(0.5, 0.9, 1.0, 0.2, 3, Black, 0.55),
            Text(0.27, 0.9, 0.5, 0.18, 4, "RSV {rsv}", 0.11, White, Black, 0.01),
            Text(0.75, 0.9, 0.46, 0.18, 5, "DE %m", 0.11, Yellow, Black, 0.01),
        ]),
        ("Templates.Default.SeventyThreeBanner",
        [
            .. VersionLine(),
            Text(0.26, 0.155, 0.48, 0.16, 2, "TNX QSO", 0.1, White, Black, 0.015, shadow: true),
            Box(0.5, 0.9, 1.0, 0.2, 3, DarkRed, 0.7),
            Text(0.5, 0.9, 0.94, 0.18, 4, "73 {his_call} DE %m", 0.11, White, Black, 0.01),
        ]),
        ("Templates.Default.SeventyThreeBig",
        [
            .. VersionLine(),
            Text(0.5, 0.165, 0.92, 0.18, 2, "TNX QSO {his_call}", 0.1, White, Black, 0.015, shadow: true),
            Text(0.8, 0.74, 0.36, 0.38, 3, "73", 0.3, Yellow, Black, 0.02, shadow: true, rotation: -8),
            Text(0.3, 0.9, 0.56, 0.18, 4, "DE %m", 0.11, White, Black, 0.015, shadow: true),
        ]),
    ];

    // Thin strip across the top naming the sending software, like legacy's "MMSSTV %v" starters.
    private static PersistedTemplateElement[] VersionLine() =>
    [
        // Light red and blue so the gradient stays readable on the dark strip over any picture.
        Box(0.5, 0.0325, 1.0, 0.065, 0, Black, 0.85),
        new PersistedTextElement(0.5, 0.0325, 0.56, 0.06, 1, Locked: false, "Scanline Studio %v", 0.05, White, Font, StrokeColor: null, StrokeThickness: 0,
            GradientEnabled: true, GradientKind: TextGradientKind.Horizontal, GradientStartColor: GradientRed, GradientEndColor: GradientBlue, Bold: true),
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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Starter backdrop '{Path}' is missing; saving that template without it")]
        public static partial void BackdropMissing(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Starter template '{Name}' could not be saved; it will be retried next launch")]
        public static partial void SaveFailed(ILogger logger, string name, Exception ex);
    }
}
