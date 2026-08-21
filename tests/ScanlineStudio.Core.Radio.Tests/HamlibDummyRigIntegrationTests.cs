using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>
/// Real interop test: drives <see cref="HamlibRadioProtocol"/> via a real, system-installed
/// <c>libhamlib</c> (discovered through the actual "bring-your-own-libhamlib" pipeline --
/// <see cref="HamlibRuntime"/>/<see cref="HamlibLibraryLocator"/>/<see cref="HamlibNative"/>, no fakes)
/// against Hamlib's own hardware-free "Dummy" rig backend (<c>RIG_MODEL_DUMMY</c> = 1, confirmed
/// against a local Hamlib source clone's <c>riglist.h</c>: <c>RIG_DUMMY=0</c>,
/// <c>RIG_MAKE_MODEL(0,1)=1</c> -- the same model <c>RigctldDummyRigIntegrationTests</c> already
/// exercises via a real <c>rigctld</c>). This is what retires <see cref="HamlibRadioProtocolTests"/>'
/// fake-native-shim caveat -- real interop against real Hamlib code, not a hand-written fake.
///
/// Best-effort: skipped (not failed) when no real <c>libhamlib</c> is discoverable on this machine --
/// same pattern as <c>RigctldDummyRigIntegrationTests</c> (xUnit 2.x has no built-in runtime skip
/// without an extra package; these show as "passed," not "skipped," when unavailable).
/// </summary>
public class HamlibDummyRigIntegrationTests
{
    private const uint DummyModel = 1; // RIG_MODEL_DUMMY

    private static readonly Lazy<IHamlibRuntime> Runtime = new(
        () => new HamlibRuntime(new NativeLibraryLoader(), overridePath: null));

    [Fact]
    public async Task PollAsync_ReadsTheDummyRigsRealDefaultState()
    {
        if (!Runtime.Value.IsAvailable)
        {
            return;
        }

        var sut = new HamlibRadioProtocol(Runtime.Value.Native, DummyModel);

        var state = await sut.PollAsync(CancellationToken.None);

        // Real, verified Dummy-backend defaults (rigs/dummy/dummy.c) -- same rig, same defaults
        // RigctldDummyRigIntegrationTests already confirmed via rigctld's own wire protocol.
        Assert.Equal(145_000_000, state.FrequencyHz);
        Assert.Equal(RadioMode.Fm, state.Mode);

        await sut.DisposeAsync();
    }

    [Fact]
    public async Task SetFrequencyAsync_ThenPollAsync_ReflectsTheRealChange()
    {
        if (!Runtime.Value.IsAvailable)
        {
            return;
        }

        var sut = new HamlibRadioProtocol(Runtime.Value.Native, DummyModel);

        await sut.SetFrequencyAsync(7_074_000, CancellationToken.None);
        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(7_074_000, state.FrequencyHz);

        await sut.DisposeAsync();
    }

    [Fact]
    public async Task SetModeAsync_ThenPollAsync_ReflectsTheRealChange()
    {
        if (!Runtime.Value.IsAvailable)
        {
            return;
        }

        var sut = new HamlibRadioProtocol(Runtime.Value.Native, DummyModel);

        await sut.SetModeAsync(RadioMode.Usb, CancellationToken.None);
        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(RadioMode.Usb, state.Mode);

        await sut.DisposeAsync();
    }

    [Fact]
    public async Task SerialPortConfigured_RealRigTokenLookupAndSetConf_UtfMarshalingRoundTripsAgainstRealHamlib()
    {
        // Closes a real coverage gap flagged by Tier A Batch 9 chunk 9d (docs/functional-audit-playbook.md):
        // every other test in this file constructs HamlibRadioProtocol with NO serial port/baud/ptt
        // type, so HamlibRadioProtocol.cs's `if (value is null) return;` guard means rig_token_lookup
        // and rig_set_conf -- HamlibNative.cs's only real string-marshaling call sites
        // (ToUtf8NullTerminated) -- were never exercised against real Hamlib anywhere in this suite.
        // The Dummy backend accepts rig_pathname via rig_set_conf without actually opening a serial
        // port (confirmed: PollAsync below succeeds), so a bogus-but-plausible path is safe here.
        if (!Runtime.Value.IsAvailable)
        {
            return;
        }

        var sut = new HamlibRadioProtocol(Runtime.Value.Native, DummyModel, serialPort: "/dev/ttyDUMMY0");

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(145_000_000, state.FrequencyHz);
        Assert.Equal(RadioMode.Fm, state.Mode);

        await sut.DisposeAsync();
    }

    [Fact]
    public async Task Capabilities_PttUnsupportedOnTheDummyRig_IsProbedCorrectly()
    {
        if (!Runtime.Value.IsAvailable)
        {
            return;
        }

        var sut = new HamlibRadioProtocol(Runtime.Value.Native, DummyModel);

        var state = await sut.PollAsync(CancellationToken.None);

        // Real, verified Dummy-backend behavior: rigctld's 't' command (a thin wrapper straight over
        // rig_get_ptt, no translation) returns "RPRT -11" on this backend -- i.e. rig_get_ptt itself
        // returns -RIG_ENAVAIL (soft) at the raw C level RigctldDummyRigIntegrationTests already
        // confirmed. Same underlying Hamlib backend, same result expected here.
        Assert.False(sut.Capabilities.HasFlag(RadioCapabilities.PttControl));
        Assert.False(state.IsTransmitting);

        await sut.DisposeAsync();
    }
}
