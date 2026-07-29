using Microsoft.Extensions.DependencyInjection;
using Yoniq.Abstractions.Audio;

namespace Yoniq.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Engine 6: proves the exact DI shape <c>Yoniq.Host/Program.cs</c> registers
/// (<c>AddSingleton&lt;IAudioEngine, MiniAudioEngine&gt;()</c>,
/// <c>AddSingleton&lt;IAudioDeviceEnumerator, MiniAudioDeviceEnumerator&gt;()</c>) actually
/// resolves and disposes cleanly through a real <see cref="ServiceProvider"/> -- not by
/// duplicating <c>Program.cs</c>'s own registration lines as an assumption, but by running the
/// real thing this project's own Opus plan-review pass specifically flagged as unverified:
/// <see cref="IAudioEngine"/> is <see cref="IAsyncDisposable"/>-only (no <see cref="IDisposable"/>),
/// and <see cref="ServiceProvider"/>'s synchronous <c>Dispose()</c> throws for a singleton shaped
/// that way -- this test uses <c>DisposeAsync</c>, matching the fix applied to `Program.cs` itself.
///
/// Round-2-engine-review correction: an earlier revision of this comment claimed this test "would
/// fail here first if that fix ever regressed" in `Program.cs` -- not true, since neither test here
/// references `Program.cs` at all. What this file actually proves is the underlying *failure mode*
/// `Program.cs`'s fix avoids (see <see cref="SynchronousDispose_OnServiceProviderWithResolvedAudioEngine_Throws"/>
/// below), not a guard against a regression in that specific file.
/// </summary>
public class MiniAudioEngineDiRegistrationTests
{
    [Fact]
    public async Task IAudioEngine_And_IAudioDeviceEnumerator_ResolveAndDisposeCleanly()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAudioEngine, MiniAudioEngine>();
        services.AddSingleton<IAudioDeviceEnumerator, MiniAudioDeviceEnumerator>();

        await using var provider = services.BuildServiceProvider();

        // Registered by type, not an eagerly-constructed instance -- resolving is what actually
        // constructs MiniAudioEngine (initializing the native context for real) for the first time.
        var engine = provider.GetRequiredService<IAudioEngine>();
        Assert.IsType<MiniAudioEngine>(engine);

        var enumerator = provider.GetRequiredService<IAudioDeviceEnumerator>();
        Assert.IsType<MiniAudioDeviceEnumerator>(enumerator);

        // Resolving again must return the SAME instance (a real singleton, not a fresh construct
        // per resolve -- constructing a second MiniAudioEngine would double-acquire the native
        // context, which is safe per its own ref-counting but would defeat the point of "one
        // engine instance" as a composition-root concept).
        Assert.Same(engine, provider.GetRequiredService<IAudioEngine>());

        // The real proof: disposing the ServiceProvider itself (await using above) must not throw
        // -- this is the exact failure mode ("type only implements IAsyncDisposable") an ordinary
        // synchronous `using`/`Dispose()` would hit instead.
    }

    // Round-1-engine-review finding: the test above only exercised the async path -- it never
    // actually proved the failure mode it describes exists, so a regression to synchronous
    // Dispose() in Program.cs would NOT have been caught here despite the doc comment's claim.
    // This makes that claim true: a synchronous Dispose() on a ServiceProvider holding an
    // IAsyncDisposable-only singleton must throw once that singleton has actually been
    // constructed (i.e. resolved).
    [Fact]
    public void SynchronousDispose_OnServiceProviderWithResolvedAudioEngine_Throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAudioEngine, MiniAudioEngine>();

        // Deliberately not `await using`/disposed at all: provider.Dispose() below throws before
        // ever reaching MiniAudioEngine.DisposeAsync, so this test's MiniAudioContext acquisition
        // is never released -- a harmless, understood leak (the process-wide native context just
        // stays initialized for the rest of this test run, which every other test's own
        // Acquire/Release calls already tolerate) accepted specifically to prove this one
        // exception path fires for real, not asserted from documentation alone.
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAudioEngine>(); // must be resolved to actually construct MiniAudioEngine

        Assert.Throws<InvalidOperationException>(() => provider.Dispose());
    }
}
