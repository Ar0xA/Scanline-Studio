using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>User-reported gap: the app never called <c>rig_set_debug</c>, so Hamlib ran at its own
/// compiled-in TRACE default -- flooding stderr with per-call entry/exit traces and CAT frame hex
/// dumps on every poll. Fixed by resolving and calling it once, in <see cref="HamlibNative"/>'s own
/// constructor, before any other Hamlib call.</summary>
public class HamlibNativeTests
{
    private static readonly Lazy<IHamlibRuntime> Runtime = HamlibAvailabilityProbe.Runtime;

    [RequiresHamlibFact]
    public void Constructor_RealLibrary_ResolvesAndCallsRigSetDebugWithoutThrowing()
    {
        // This sandbox's real libhamlib.so.4 DOES export rig_set_debug (confirmed via `nm -D`), so
        // this exercises the real, non-tolerant path.

        // Runtime.Value.Native is already a constructed HamlibNative -- if rig_set_debug resolution
        // or the constructor's own call to it threw, HamlibRuntime.IsAvailable would be false
        // (HamlibRuntime's own doc comment: HamlibUnavailableException from the factory is caught).
        // This assertion is the real proof: reaching it at all means construction succeeded.
        Assert.True(Runtime.Value.IsAvailable);
    }

    [RequiresHamlibFact]
    public void Constructor_LibraryMissingRigSetDebugExport_StillConstructsAndRigSetDebugIsANoOp()
    {
        // User-reported gap's own explicit requirement: a Hamlib build lacking rig_set_debug must
        // not take out Hamlib support entirely over a logging nicety. Verified against the REAL
        // installed library (every export except rig_set_debug gets a genuinely valid function
        // pointer via HidingLoader below) rather than fabricated addresses -- an earlier version of
        // this test used bogus nint(1) pointers for the 17 other mandatory exports and reliably
        // crashed the test host, since Marshal.GetDelegateForFunctionPointer's thunk generation
        // touches the target address even when the delegate itself is never invoked.

        // Re-locates the same real library Runtime already proved available, so this test has its
        // own handle to construct a SECOND HamlibNative against (deliberately not reusing
        // Runtime.Value.Native, which was already built with the real, non-hiding loader).
        var hidingLoader = new HidingLoader(new NativeLibraryLoader(), "rig_set_debug");
        var (handle, _) = new HamlibLibraryLocator(hidingLoader, overridePath: null).Locate();

        var sut = new HamlibNative(hidingLoader, handle);

        // The real proof: RigSetDebug is safe to call even though the export was never resolved --
        // a null-conditional no-op, not a NullReferenceException.
        sut.RigSetDebug(3);
    }

    /// <summary>Wraps a real <see cref="INativeLibraryLoader"/>, passing every export through
    /// unchanged EXCEPT the one named in <paramref name="hiddenExportName"/>, which it reports as
    /// missing -- lets a test genuinely exercise "this one export is absent" without fabricating
    /// function pointers for anything else.</summary>
    private sealed class HidingLoader(INativeLibraryLoader inner, string hiddenExportName) : INativeLibraryLoader
    {
        public bool TryLoad(string libraryPath, out nint handle, out string? errorDetail) =>
            inner.TryLoad(libraryPath, out handle, out errorDetail);

        public bool TryGetExport(nint handle, string name, out nint address)
        {
            if (name == hiddenExportName)
            {
                address = 0;
                return false;
            }

            return inner.TryGetExport(handle, name, out address);
        }
    }
}
