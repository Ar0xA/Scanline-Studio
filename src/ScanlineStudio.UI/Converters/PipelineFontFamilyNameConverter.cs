using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ScanlineStudio.UI.Converters;

/// <summary>Maps the RENDERING PIPELINE's own font family name (<c>ITransmitImagePreparer
/// .AvailableFontFamilies</c>, a plain SixLabors.Fonts name-table name — "DejaVu Sans Mono",
/// "Barlow", or an OS-installed system font name) to the <c>FontFamily</c> the CANVAS needs
/// (Phase 4, spec/15-template-designer.md; system-font support added later). The two bundled names
/// are pinned to this app's own <c>avares://…#Family</c> resource copies rather than a bare-name
/// binding (<c>FontFamily="{Binding FontFamily}"</c>) — Avalonia resolves a bare family name as a
/// SYSTEM font lookup, which could coincidentally succeed on a dev machine that happens to have the
/// same font installed system-wide (this project's own LICENSES.md notes DejaVu Sans Mono came from
/// exactly that: an installed <c>fonts-dejavu-core</c> package) while silently falling back to a
/// different font everywhere else — those two names MUST resolve to this app's own BUNDLED copies,
/// not whatever the OS happens to have. Shares the same two <c>avares://</c> URIs already defined
/// as <c>IndustryMonoFontFamily</c>/<c>IndustryBodyFontFamily</c> in <c>AtomsTokens.axaml</c> — not
/// re-declared here as a third source of truth, this converter just knows which pipeline name maps
/// to which existing resource key's own URI string. Any OTHER name is a real system font (it came
/// from <c>ITransmitImagePreparer.AvailableFontFamilies</c>'s own system-font enumeration, the same
/// source <c>ResolveFontFamily</c> uses for the actual transmitted pixels) — passed through as a
/// bare-name system lookup so the canvas preview matches what will really be transmitted, rather
/// than every non-bundled name collapsing onto DejaVu Sans Mono.</summary>
public sealed class PipelineFontFamilyNameConverter : IValueConverter
{
    public static readonly PipelineFontFamilyNameConverter Instance = new();

    private const string DejaVuSansMonoUri = "avares://ScanlineStudio.UI/Assets/Fonts/DejaVuSansMono#DejaVu Sans Mono";
    private const string BarlowUri = "avares://ScanlineStudio.UI/Assets/Fonts/Barlow#Barlow";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "Barlow" => new FontFamily(BarlowUri),
        null or "" or "DejaVu Sans Mono" => new FontFamily(DejaVuSansMonoUri),
        string systemFontName => new FontFamily(systemFontName),
        _ => new FontFamily(DejaVuSansMonoUri),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
