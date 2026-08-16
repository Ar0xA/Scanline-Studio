using System.Text.Json.Serialization;

namespace ScanlineStudio.Application;

/// <summary>The TX template editor's "ready rack" (spec/15-template-designer.md Phase 5) — an
/// ordered, up-to-9-slot subset of saved templates, recalled by number key (1-9). Entirely separate
/// from <see cref="ITemplateStore.ListAsync"/>'s own full list: that returns EVERY saved template,
/// this is which ones are PINNED and in what order, and that ordering has to persist across app
/// restarts (plan-review finding — entirely unaccounted for in the original draft).</summary>
public sealed record ReadyRackSettings
{
    public const string SectionKey = "ReadyRack";

    /// <summary>Index 0 = slot 1 (recalled by the "1"/NumPad1 key), and so on up to index 8 = slot
    /// 9. Never more than 9 entries — <c>TxImageEditorPaneViewModel</c>'s own pin command is
    /// responsible for enforcing that, this record just stores whatever it's given.</summary>
    public IReadOnlyList<string> PinnedTemplateIds { get; init; } = [];
}

[JsonSerializable(typeof(ReadyRackSettings))]
public sealed partial class ReadyRackSettingsJsonContext : JsonSerializerContext;
