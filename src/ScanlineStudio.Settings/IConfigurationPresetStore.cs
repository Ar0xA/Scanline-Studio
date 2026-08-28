namespace ScanlineStudio.Settings;

/// <summary>Configurations-preset backlog, Phase 2 (2026-08-28) -- save/switch/clone/delete/rename
/// named, full-application-configuration presets, functionally like WSJT-X's own Configurations
/// feature. A preset is one JSON file, the SAME <see cref="AppSettings"/> shape <c>settings.json</c>
/// already uses, stored at a FIXED path independent of the live, possibly-relocated config directory
/// (see <see cref="ConfigurationPresetStore"/>'s own doc comment for why -- a relocation moving
/// <c>settings.json</c> does not know about, and would silently orphan, a <c>presets/</c> subfolder
/// living alongside it).
///
/// Deliberately a SEPARATE class from <see cref="JsonSettingsStore"/> (not one more member on
/// <see cref="ISettingsStore"/> itself, same "narrow side-channel, don't widen the interface every
/// fake has to implement" reasoning as <see cref="ISettingsFileRelocator"/>'s own doc comment) --
/// presets are a genuinely separate concern from "where does the live settings.json live," with a
/// different physical location and no shared locking domain.</summary>
public interface IConfigurationPresetStore
{
    /// <summary>Configurations-preset backlog, Phase 4 (2026-08-28): validates a candidate preset
    /// name against the EXACT same rule <see cref="SavePresetAsync"/>/<see cref="ClonePresetAsync"/>/
    /// <see cref="RenamePresetAsync"/> already enforce (by throwing <see cref="ArgumentException"/> on
    /// an invalid one), exposed here as a non-throwing check so a UI-layer name-entry prompt can
    /// validate as the user types without needing its own hand-copied blocklist -- a second,
    /// independently-maintained copy of this rule would risk silently diverging from the one the
    /// store actually enforces. Returns <see langword="true"/> and a <see langword="null"/>
    /// <paramref name="error"/> for a valid name; <see langword="false"/> and a human-readable message
    /// otherwise -- <paramref name="errorMessage"/> is that message. Does NOT check for a collision
    /// against an existing preset -- that's a different, STORE-STATE question this method deliberately
    /// doesn't answer (see <see cref="ClonePresetAsync"/>/<see cref="RenamePresetAsync"/>'s own
    /// collision-rejection docs).</summary>
    bool TryValidatePresetName(string name, out string? errorMessage);

    /// <summary>Enumerates saved presets, returning display names (NOT filenames -- the file
    /// extension is an implementation detail). Empty if the presets directory doesn't exist yet
    /// (nothing saved so far) -- never throws for that case.</summary>
    Task<IReadOnlyList<string>> ListPresetsAsync(CancellationToken ct = default);

    /// <summary>Loads a preset's content by name (case-insensitive), sanitized (see
    /// <see cref="ConfigurationPresetStore"/>'s own doc comment for what sanitizing strips) --
    /// <see langword="null"/> if no preset with this name exists, matching
    /// <see cref="AppSettingsSectionExtensions.GetSection{T}"/>'s own nullable-on-absence
    /// convention. Never throws for "not found."</summary>
    Task<AppSettings?> LoadPresetAsync(string name, CancellationToken ct = default);

    /// <summary>Creates a NEW preset, or OVERWRITES an existing one with this exact name (case-
    /// insensitive) -- deliberately not collision-rejecting, unlike <see cref="ClonePresetAsync"/>/
    /// <see cref="RenamePresetAsync"/> below: re-saving over an already-existing name is the normal,
    /// expected "update this preset with my current settings" action, not an accidental collision.
    /// <paramref name="content"/> is sanitized before writing (allowlist/exclude-list filtering, the
    /// active-preset marker stripped, schema version stamped to current -- see
    /// <see cref="ConfigurationPresetStore"/>'s own doc comment). Written atomically (temp file, then
    /// rename) so a crash mid-write never leaves a truncated, unrecoverable preset file. Throws
    /// <see cref="ArgumentException"/> for an invalid name (empty, or containing a path-traversal/
    /// filesystem-reserved character) -- validated and REJECTED, never silently sanitized/mutated
    /// (a silently-rewritten name would desync from what the user actually typed, breaking
    /// <see cref="ListPresetsAsync"/>'s own display and every later name-keyed lookup).</summary>
    Task SavePresetAsync(string name, AppSettings content, CancellationToken ct = default);

    /// <summary>Duplicates an existing preset under a new name -- implemented as load -> sanitize ->
    /// save (the SAME sanitizing pass <see cref="SavePresetAsync"/> uses), NOT a raw file copy, so a
    /// hand-edited source preset file can never bypass the sanitizing invariant via Clone. Throws
    /// <see cref="InvalidOperationException"/> if <paramref name="sourceName"/> doesn't exist, or if
    /// <paramref name="newName"/> ALREADY exists (case-insensitive) -- collision-REJECTED, matching
    /// this app's own established convention everywhere else a name/path can collide (Hamlib reload
    /// rejection, settings-file relocation's own "destination conflict" rejection). Throws
    /// <see cref="ArgumentException"/> for an invalid <paramref name="newName"/>, same rule as
    /// <see cref="SavePresetAsync"/>.</summary>
    Task ClonePresetAsync(string sourceName, string newName, CancellationToken ct = default);

    /// <summary>Deletes a preset by name (case-insensitive) -- a no-op, not an error, if no preset
    /// with this name exists (matches this class's own "not found is not exceptional for a read/
    /// delete, only for a rename/clone TARGET-collision" posture).</summary>
    Task DeletePresetAsync(string name, CancellationToken ct = default);

    /// <summary>Renames an existing preset. Throws <see cref="InvalidOperationException"/> if
    /// <paramref name="oldName"/> doesn't exist, or if <paramref name="newName"/> ALREADY exists
    /// (case-insensitive) -- same collision-rejection convention as <see cref="ClonePresetAsync"/>.
    /// Throws <see cref="ArgumentException"/> for an invalid <paramref name="newName"/>, same rule as
    /// <see cref="SavePresetAsync"/>. Does NOT touch which preset (if any) the LIVE settings.json
    /// considers active -- that marker lives outside this store entirely (see
    /// <see cref="ConfigurationPresetStore"/>'s own doc comment); a caller renaming the currently-
    /// active preset is responsible for updating that marker itself.</summary>
    Task RenamePresetAsync(string oldName, string newName, CancellationToken ct = default);
}
