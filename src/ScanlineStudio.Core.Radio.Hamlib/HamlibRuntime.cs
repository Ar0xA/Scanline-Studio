namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// Real <see cref="IHamlibRuntime"/>. Runs <see cref="HamlibLibraryLocator"/> + the version gate
/// <b>eagerly in the constructor</b> -- deliberately not lazily on first <see cref="Native"/> access,
/// which would put the first ~6 <c>NativeLibrary.TryLoad</c> attempts plus <c>rig_version()</c> inline
/// on whatever thread first calls <c>HamlibProtocolFactory.Create</c> (the UI thread, if a future
/// Application-layer caller invokes <c>IRadioController.ConnectAsync</c> from a click handler --
/// <c>RadioController.ConnectAsync</c> awaits a synchronously-completing <c>DisconnectAsync</c> when
/// nothing is connected yet, so <c>ResolveProtocol</c> genuinely runs inline). Whoever constructs a
/// <see cref="HamlibRuntime"/> controls exactly when that I/O happens.
/// </summary>
internal sealed class HamlibRuntime : IHamlibRuntime
{
    private readonly IHamlibNative? _native;
    private readonly IReadOnlyList<string>? _unavailableAttempts;

    /// <param name="nativeFactory">Defaults to the real <see cref="HamlibNativeFactory"/>; tests
    /// substitute a fake that ignores the (meaningless, in a test) handle value and returns a scripted
    /// <see cref="IHamlibNative"/> instead -- see <see cref="IHamlibNativeFactory"/>'s own doc
    /// comment.</param>
    public HamlibRuntime(INativeLibraryLoader loader, string? overridePath, IHamlibNativeFactory? nativeFactory = null)
    {
        var factory = nativeFactory ?? new HamlibNativeFactory();
        try
        {
            var locator = new HamlibLibraryLocator(loader, overridePath);
            var (handle, resolvedPath) = locator.Locate();
            var native = factory.Create(loader, handle);
            var version = native.RigVersion();

            if (!HamlibVersionGate.IsSupported(version))
            {
                _unavailableAttempts =
                [
                    $"{resolvedPath}: unsupported Hamlib version '{version ?? "(null)"}' -- " +
                    "only major version 4 is supported",
                ];
                return;
            }

            _native = native;
        }
        catch (HamlibUnavailableException ex)
        {
            _unavailableAttempts = ex.Attempts;
        }
    }

    public bool IsAvailable => _native is not null;

    public IHamlibNative Native => _native ?? throw new HamlibUnavailableException(_unavailableAttempts!);
}
