using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ScanlineStudio.Settings;

/// <summary>Configurations-preset backlog, Phase 2 (2026-08-28). See
/// <see cref="IConfigurationPresetStore"/> for the caller-facing contract.
///
/// <b>Fixed, non-relocating path.</b> Presets live at
/// <c>Path.Combine(AppConfigPaths.GetDefaultConfigDirectory(), "presets")</c> -- deliberately
/// <see cref="AppConfigPaths.GetDefaultConfigDirectory"/>, NOT <see cref="AppConfigPaths.ConfigDirectory"/>
/// (which DOES honor a live <see cref="AppLocationOverrides"/> relocation). Plan-review finding:
/// <see cref="JsonSettingsStore.RelocateAsync"/> moves EXACTLY ONE file (<c>settings.json</c> itself,
/// via <c>Path.GetFileName</c>) and its own destination-conflict check only looks at that one file --
/// a <c>presets/</c> subfolder stored under the live config directory would be silently ORPHANED by a
/// relocation (left at the old path, invisible at the new one). Not a new relocation destination
/// anyone has to remember either: <see cref="AppLocationOverrides"/> itself already lives at this
/// same fixed, non-relocating location unconditionally.
///
/// <b>Sanitizing.</b> Every read AND write path funnels through <see cref="Sanitize"/> -- a single
/// helper, not two independently-maintained filters (plan-review finding: stripping only on write
/// would let a hand-edited or stale preset file inject state on read that write-side stripping never
/// caught). It: (1) excludes 3 UI-layout-only sections (<c>WindowGeometry</c>/<c>TxPaneUi</c>/
/// <c>RxPaneUi</c> -- plain string literals, not the actual <c>ScanlineStudio.UI</c>-owned types,
/// since THIS project sits below <c>ScanlineStudio.UI</c> in the layering and can never reference
/// them) plus the active-preset marker section (see below) -- everything else stays IN, matching the
/// user's own "everything" scope decision, so a FUTURE settings section is included by default
/// without this store needing to know about it; (2) stamps <see cref="AppSettings.SchemaVersion"/> to
/// <see cref="AppSettings.CurrentSchemaVersion"/> (not whatever a loaded preset happened to carry).
///
/// <b>The active-preset marker.</b> <c>ScanlineStudio.Application</c>'s own <c>ConfigurationPresetSettings</c>
/// type (section key <c>"ConfigurationPreset"</c>, duplicated here as <see cref="ActivePresetSectionKey"/>
/// since THIS project cannot reference that Application-layer type) tracks which preset the LIVE
/// settings.json currently considers active -- that marker lives ONLY in the live settings.json,
/// never inside a preset FILE (a saved/cloned preset must not carry forward a stale "I am named X"
/// self-reference). <see cref="ActivePresetSectionKey"/>'s string value MUST stay in sync with that
/// type's own <c>SectionKey</c> constant -- there is no way to share it directly given the layering
/// constraint; a mismatch here would silently stop stripping the marker.</summary>
public sealed partial class ConfigurationPresetStore : IConfigurationPresetStore, IDisposable
{
    /// <summary>Must match <c>ScanlineStudio.Application.ConfigurationPresetSettings.SectionKey</c>
    /// exactly -- see this class's own doc comment for why it can't be shared directly. Public (not
    /// internal) so a consumer/test outside this assembly can reference the same literal rather than
    /// re-typing it.</summary>
    public const string ActivePresetSectionKey = "ConfigurationPreset";

    private static readonly string[] ExcludedSectionKeys = ["WindowGeometry", "TxPaneUi", "RxPaneUi", ActivePresetSectionKey];

    // Cross-platform-safe by construction, not by relying on Path.GetInvalidFileNameChars() (which
    // under-rejects on Linux -- that platform's own invalid set is effectively just '\0' and '/',
    // leaving many OS-legal-but-surprising characters unrejected). A fixed blocklist behaves
    // identically regardless of which OS actually saved the file, which matters if the same config
    // directory is later used on a different platform (e.g. a synced folder).
    private static readonly char[] InvalidNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<ConfigurationPresetStore> _logger;
    private readonly string _presetsDirectory;

    public ConfigurationPresetStore(ILogger<ConfigurationPresetStore> logger, string? presetsDirectory = null)
    {
        _logger = logger;
        _presetsDirectory = presetsDirectory ?? GetDefaultPresetsDirectory();
    }

    // Code-review round-1 finding: public (not private) so a test can pin the DEFAULT location
    // without touching the filesystem -- every existing test injects a temp directory via the ctor's
    // optional parameter, so nothing previously proved the real default still resolves to this fixed,
    // non-relocating path (a one-token change back to AppConfigPaths.ConfigDirectory would silently
    // reintroduce the round-1 orphaning blocker with every test still green). Public, not internal --
    // this project has no InternalsVisibleTo wired to its test project (same reasoning already
    // applied to ActivePresetSectionKey above).
    public static string GetDefaultPresetsDirectory() => Path.Combine(AppConfigPaths.GetDefaultConfigDirectory(), "presets");

    /// <summary>Every operation here is quick file I/O (no long-running/uncancellable native call
    /// held under <see cref="_lock"/>, unlike e.g. <c>HamlibProtocolFactory</c>'s own reload gate) --
    /// disposing the semaphore at clean shutdown is safe, matching <see cref="JsonSettingsStore.Dispose"/>'s
    /// own identical choice for its analogous file lock.</summary>
    public void Dispose() => _lock.Dispose();

    public async Task<IReadOnlyList<string>> ListPresetsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_presetsDirectory))
            {
                return [];
            }

            return Directory.EnumerateFiles(_presetsDirectory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null)
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AppSettings?> LoadPresetAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = FindExistingPresetFile(name);
            if (path is null)
            {
                return null;
            }

            return await ReadPresetFileAsync(path, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SavePresetAsync(string name, AppSettings content, CancellationToken ct = default)
    {
        ValidateName(name);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Overwrite the EXISTING file (matching whatever casing it was originally saved with) if
            // this name already exists case-insensitively -- otherwise create fresh with the
            // caller's own casing. Deliberately not collision-rejecting (see this method's own
            // interface doc comment).
            var path = FindExistingPresetFile(name) ?? GetPresetFilePath(name);
            await WritePresetFileAsync(path, content, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClonePresetAsync(string sourceName, string newName, CancellationToken ct = default)
    {
        ValidateName(newName);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sourcePath = FindExistingPresetFile(sourceName);
            if (sourcePath is null)
            {
                throw new InvalidOperationException($"No preset named '{sourceName}' exists to clone.");
            }

            if (FindExistingPresetFile(newName) is not null)
            {
                throw new InvalidOperationException($"A preset named '{newName}' already exists.");
            }

            var content = await ReadPresetFileAsync(sourcePath, ct).ConfigureAwait(false);
            await WritePresetFileAsync(GetPresetFilePath(newName), content, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeletePresetAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = FindExistingPresetFile(name);
            if (path is not null)
            {
                File.Delete(path);
                Log.PresetDeleted(_logger, name);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RenamePresetAsync(string oldName, string newName, CancellationToken ct = default)
    {
        ValidateName(newName);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var oldPath = FindExistingPresetFile(oldName);
            if (oldPath is null)
            {
                throw new InvalidOperationException($"No preset named '{oldName}' exists to rename.");
            }

            // Code-review round-1 finding: a CASE-ONLY rename ("field day" -> "Field Day") used to be
            // impossible -- FindExistingPresetFile(newName) is case-insensitive, so it found the
            // SOURCE file itself and rejected the rename as "already exists." Comparing the found
            // path against oldPath (not just null-checking it) lets a case-only rename through while
            // still rejecting a genuine collision against a DIFFERENT preset.
            var collidingPath = FindExistingPresetFile(newName);
            if (collidingPath is not null && !string.Equals(collidingPath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"A preset named '{newName}' already exists.");
            }

            var newPath = GetPresetFilePath(newName);
            // Code-review round-2 finding: an IDENTICAL rename (oldName == newName, same casing) now
            // reaches here (the case-only-rename fix above no longer rejects it, since collidingPath
            // == oldPath) -- File.Move(p, p) is a needless no-op call, avoided explicitly rather than
            // relying on it happening to succeed.
            if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
            {
                return;
            }

            File.Move(oldPath, newPath);
            Log.PresetRenamed(_logger, oldName, newName);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Case-insensitive lookup that returns the file's ACTUAL on-disk path (whatever casing
    /// it was originally saved with) -- a plain <see cref="File.Exists"/> against a candidate path
    /// built from the CALLER's casing would only match on a case-sensitive filesystem (Linux) when
    /// the casing happens to match exactly, silently missing a real collision that a case-
    /// INSENSITIVE filesystem (Windows/macOS) would have caught -- enumerating and comparing names
    /// directly behaves identically regardless of the underlying filesystem's own case sensitivity.</summary>
    private string? FindExistingPresetFile(string name)
    {
        if (!Directory.Exists(_presetsDirectory))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(_presetsDirectory, "*.json"))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    private string GetPresetFilePath(string name) => Path.Combine(_presetsDirectory, name + ".json");

    private async Task<AppSettings> ReadPresetFileAsync(string path, CancellationToken ct)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var settings = await JsonSerializer.DeserializeAsync(stream, AppSettingsJsonContext.Default.AppSettings, ct).ConfigureAwait(false)
                ?? new AppSettings();

            // Code-review round-1 finding: this must be checked BEFORE Sanitize, which stamps
            // SchemaVersion to current -- checking after would make a mismatch permanently
            // undetectable (Sanitize itself erases the exact evidence this warning needs), silently
            // defeating the whole point of stamping-not-trusting a loaded preset's own version.
            if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
            {
                Log.PresetSchemaVersionMismatch(_logger, path, settings.SchemaVersion, AppSettings.CurrentSchemaVersion);
            }

            return Sanitize(settings);
        }
    }

    private async Task WritePresetFileAsync(string path, AppSettings content, CancellationToken ct)
    {
        Directory.CreateDirectory(_presetsDirectory);

        // Atomic write (temp file + rename), same pattern as JsonSettingsStore.SaveAsync -- a crash
        // mid-write must never leave a truncated, unrecoverable preset file.
        var tempPath = path + ".tmp";
        var stream = File.Create(tempPath);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, Sanitize(content), AppSettingsJsonContext.Default.AppSettings, ct).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
        Log.PresetSaved(_logger, path);
    }

    private static AppSettings Sanitize(AppSettings settings)
    {
        var sections = settings.Sections
            .Where(kvp => !ExcludedSectionKeys.Contains(kvp.Key, StringComparer.Ordinal))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        return new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion, Sections = sections };
    }

    private static void ValidateName(string name)
    {
        if (!TryValidateNameCore(name, out var error))
        {
            throw new ArgumentException(error, nameof(name));
        }
    }

    /// <summary>See <see cref="IConfigurationPresetStore.TryValidatePresetName"/>'s own doc comment --
    /// the public, non-throwing entry point <see cref="ValidateName"/> above is now built on top of,
    /// not a second copy of this same logic.</summary>
    public bool TryValidatePresetName(string name, out string? errorMessage) => TryValidateNameCore(name, out errorMessage);

    private static bool TryValidateNameCore(string name, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errorMessage = "Preset name cannot be empty.";
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            errorMessage = "Preset name cannot contain '..'.";
            return false;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c) || InvalidNameChars.Contains(c))
            {
                errorMessage = $"Preset name cannot contain '{c}'.";
                return false;
            }
        }

        errorMessage = null;
        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Configuration preset saved: {Path}")]
        public static partial void PresetSaved(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Information, Message = "Configuration preset deleted: {Name}")]
        public static partial void PresetDeleted(ILogger logger, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "Configuration preset renamed: {OldName} -> {NewName}")]
        public static partial void PresetRenamed(ILogger logger, string oldName, string newName);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration preset {Path} has schema version {PresetVersion}, current is {CurrentVersion} -- no migration applied")]
        public static partial void PresetSchemaVersionMismatch(ILogger logger, string path, int presetVersion, int currentVersion);
    }
}
