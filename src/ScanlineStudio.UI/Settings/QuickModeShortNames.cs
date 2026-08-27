using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.UI.Settings;

/// <summary>Compact button captions for the quick-mode grid -- <see cref="SstvModeDefinition.DisplayName"/>
/// itself for 33 of the 43 modes already fits the grid button's 17px-tall/10pt/no-trim cell
/// (`SC2-180` is the widest DisplayName that ships today and already renders fine there); only the
/// 10 modes below have a longer human name and need a shorter code. Not a loc-key table: these are
/// protocol-defined mode designators (the same in every locale, like "PD120" or "MR73"), not
/// translatable UI chrome -- matching this codebase's own existing precedent of binding a
/// protocol-defined designator directly with no loc-key indirection (e.g. the TX mode ComboBox,
/// <c>TxControlsPaneView.axaml</c>, binds <c>SstvModeDefinition.DisplayName</c> the same way).
/// The context-menu popup itself has room for the full
/// <see cref="SstvModeDefinition.DisplayName"/> and does not use this table.</summary>
public static class QuickModeShortNames
{
    private static readonly Dictionary<string, string> Overrides = new()
    {
        ["martin-m1"] = "M1",
        ["martin-m2"] = "M2",
        ["scottie-s1"] = "SC1",
        ["scottie-s2"] = "SC2",
        ["scottie-dx"] = "SCDX",
        ["robot-36"] = "R36",
        ["robot-72"] = "R72",
        ["p3"] = "P3",
        ["p5"] = "P5",
        ["p7"] = "P7",
    };

    public static string GetShortName(SstvModeDefinition mode) =>
        Overrides.TryGetValue(mode.Id, out var shortName) ? shortName : mode.DisplayName;
}
