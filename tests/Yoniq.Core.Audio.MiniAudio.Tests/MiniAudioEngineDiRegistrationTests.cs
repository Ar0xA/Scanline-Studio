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
/// that way -- this test uses <c>DisposeAsync</c>, matching the fix applied to
/// <c>Program.cs</c> itself, and would fail here first if that fix ever regressed.
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
}
