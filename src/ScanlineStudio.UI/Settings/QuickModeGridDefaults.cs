namespace ScanlineStudio.UI.Settings;

/// <summary>Single source of truth for the quick-mode grid's default 16 slot assignments -- used as
/// the fallback for both <see cref="TxPaneUiSettings.QuickModeGridIds"/> and
/// <see cref="RxPaneUiSettings.QuickModeGridIds"/> so the two grids can't silently drift apart on a
/// fresh install. Matches the 16 buttons this grid shipped with before it became reassignable (same
/// order, same <c>SstvModeDefinition.Id</c> values) -- an existing install upgrading to this feature
/// sees no change until the user right-clicks a button.</summary>
public static class QuickModeGridDefaults
{
    public static readonly IReadOnlyList<string> Ids =
    [
        "scottie-s1", "scottie-s2", "scottie-dx", "martin-m1",
        "martin-m2", "r24", "pd240", "robot-36",
        "robot-72", "pd50", "pd90", "pd120",
        "pd160", "pd180", "pd290", "sc2-180",
    ];
}
