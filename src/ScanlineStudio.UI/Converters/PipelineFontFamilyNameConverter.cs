using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ScanlineStudio.UI.Converters;

/// <summary>Maps the RENDERING PIPELINE's own font family name (<c>ITransmitImagePreparer
/// .AvailableFontFamilies</c>, a plain SixLabors.Fonts name-table name — "DejaVu Sans Mono",
/// "Barlow") to the matching <c>avares://…#Family</c> resource reference the CANVAS needs
/// (Phase 4, spec/15-template-designer.md). Deliberately NOT a bare-name binding
/// (<c>FontFamily="{Binding FontFamily}"</c>) — Avalonia resolves a bare family name as a SYSTEM
/// font lookup, which could coincidentally succeed on a dev machine that happens to have the same
/// font installed system-wide (this project's own LICENSES.md notes DejaVu Sans Mono came from
/// exactly that: an installed <c>fonts-dejavu-core</c> package) while silently falling back to a
/// different font everywhere else — the two names here MUST resolve to this app's own BUNDLED
/// copies, not whatever the OS happens to have. Shares the same two <c>avares://</c> URIs already
/// defined as <c>IndustryMonoFontFamily</c>/<c>IndustryBodyFontFamily</c> in
/// <c>AtomsTokens.axaml</c> — not re-declared here as a third source of truth, this converter just
/// knows which pipeline name maps to which existing resource key's own URI string.</summary>
public sealed class PipelineFontFamilyNameConverter : IValueConverter
{
    public static readonly PipelineFontFamilyNameConverter Instance = new();

    private const string DejaVuSansMonoUri = "avares://ScanlineStudio.UI/Assets/Fonts/DejaVuSansMono#DejaVu Sans Mono";
    private const string BarlowUri = "avares://ScanlineStudio.UI/Assets/Fonts/Barlow#Barlow";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "Barlow" => new FontFamily(BarlowUri),
        _ => new FontFamily(DejaVuSansMonoUri),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
