namespace Yoniq.Core.Localization;

/// <summary>Persisted culture choice — <c>null</c>/absent means "use the default English boot culture"
/// (see <see cref="JsonLocalizationService"/>'s own doc comment: it always constructs into English;
/// restoring a persisted non-English culture is the composition root's job, reading this section and
/// calling <c>SetCultureAsync</c> once at startup).</summary>
public sealed record LocalizationSettings
{
    public const string SectionKey = "Localization";

    public string? CultureCode { get; init; }
}
