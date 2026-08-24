namespace ScanlineStudio.Abstractions.Radio;

/// <summary>Options-dialog-facing seam for listing the system's serial ports (CAT and PTT port
/// pickers, spec/03-cat-layer.md's linked-Hamlib backend). Lives here rather than only in the
/// optional <c>ScanlineStudio.Core.Radio.Hamlib</c> module so <c>ScanlineStudio.UI</c> can depend on
/// it without taking a direct reference to that module (the UI-layering rule enforced by
/// <c>UiLayeringArchitectureTests</c>) -- same pattern as <see cref="IHamlibDiscoveryService"/>, the
/// real implementation is registered into DI from <c>ScanlineStudio.Host</c>'s <c>Program.cs</c>.
/// </summary>
public interface ISerialPortEnumerator
{
    /// <summary>Lists the system's currently-present serial port names (e.g. <c>COM3</c> on Windows,
    /// <c>/dev/ttyUSB0</c> on Linux). A fast, local, non-blocking enumeration -- no device I/O, safe
    /// to call synchronously. Never throws: a platform/permission failure (e.g. macOS, where the
    /// underlying API is documented unsupported) returns an empty list rather than propagating,
    /// since the serial port fields are editable ComboBoxes -- manual entry is always the
    /// fallback.</summary>
    IReadOnlyList<string> GetPortNames();
}
