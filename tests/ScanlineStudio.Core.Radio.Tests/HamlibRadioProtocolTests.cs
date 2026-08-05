using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Fixture-driven tests against <see cref="HamlibRadioProtocol"/>, scripted via
/// <see cref="FakeHamlibNative"/>. Error codes used throughout are real Hamlib
/// <c>rig_errcode_e</c> values, negated, matching <see cref="IHamlibNative"/> callers' real contract
/// (spec/03-cat-layer.md's design decision on soft/hard classification): -1 = <c>-RIG_EINVAL</c>
/// (soft), -6 = <c>-RIG_EIO</c> (hard), -11 = <c>-RIG_ENAVAIL</c> (soft).</summary>
public class HamlibRadioProtocolTests
{
    [Fact]
    public async Task PollAsync_FullCapabilities_ReturnsFullyPopulatedState()
    {
        var native = new FakeHamlibNative
        {
            Frequency = 14_074_000,
            Mode = 1UL << 2, // RIG_MODE_USB
            Ptt = 1,
        };
        var sut = new HamlibRadioProtocol(native, model: 1);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(14_074_000, state.FrequencyHz);
        Assert.Equal(RadioMode.Usb, state.Mode);
        Assert.True(state.IsTransmitting);
        Assert.Equal(
            RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency |
            RadioCapabilities.ReadMode | RadioCapabilities.SetMode | RadioCapabilities.PttControl,
            sut.Capabilities);
    }

    [Fact]
    public async Task PollAsync_PttNotAvailable_MarksCapabilityAbsent_AndNeverPolledAgain()
    {
        var native = new FakeHamlibNative { RigGetPttCode = -11 }; // -RIG_ENAVAIL, soft
        var sut = new HamlibRadioProtocol(native, model: 1);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.False(sut.Capabilities.HasFlag(RadioCapabilities.PttControl));
        Assert.False(state.IsTransmitting);
        Assert.Equal(1, native.CallLog.Count(c => c == "rig_get_ptt")); // only the connect-time probe
    }

    [Fact]
    public async Task PollAsync_SoftErrorOnGetFreq_ThrowsRadioProtocolException()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);
        await sut.PollAsync(CancellationToken.None); // connect + probe succeed first

        native.RigGetFreqCode = -1; // -RIG_EINVAL, soft

        await Assert.ThrowsAsync<RadioProtocolException>(() => sut.PollAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PollAsync_HardErrorOnGetFreq_ThrowsPlainException_NotRadioProtocolException()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);
        await sut.PollAsync(CancellationToken.None);

        native.RigGetFreqCode = -6; // -RIG_EIO, hard

        var ex = await Assert.ThrowsAsync<IOException>(() => sut.PollAsync(CancellationToken.None));
        Assert.IsNotType<RadioProtocolException>(ex);
    }

    [Fact]
    public async Task SetFrequencyAsync_HappyPath_UpdatesNativeState()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);

        await sut.SetFrequencyAsync(7_074_000, CancellationToken.None);

        Assert.Equal(7_074_000, native.Frequency);
    }

    [Fact]
    public async Task SetModeAsync_HappyPath_UpdatesNativeState()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);

        await sut.SetModeAsync(RadioMode.Cw, CancellationToken.None);

        Assert.Equal(1UL << 1, native.Mode); // RIG_MODE_CW
    }

    [Fact]
    public async Task SetModeAsync_UnmappableMode_ThrowsArgumentOutOfRangeException()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sut.SetModeAsync(RadioMode.Unknown, CancellationToken.None));
    }

    [Fact]
    public async Task SetPttAsync_HappyPath_UpdatesNativeState()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);

        await sut.SetPttAsync(true, CancellationToken.None);

        Assert.Equal(1, native.Ptt);
    }

    [Fact]
    public void Constructor_UnrecognizedPttType_ThrowsArgumentOutOfRangeException()
    {
        var native = new FakeHamlibNative();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HamlibRadioProtocol(native, model: 1, pttType: "NOT_A_REAL_TYPE"));
    }

    [Fact]
    public async Task Connect_RigInitReturnsNullHandle_ThrowsIOException()
    {
        var native = new FakeHamlibNative { RigInitHandle = 0 };
        var sut = new HamlibRadioProtocol(native, model: 1);

        await Assert.ThrowsAsync<IOException>(() => sut.PollAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Connect_RigOpenFails_CallsRigCleanup_ThenThrows()
    {
        var native = new FakeHamlibNative { RigOpenCode = -6 }; // -RIG_EIO, hard
        var sut = new HamlibRadioProtocol(native, model: 1);

        await Assert.ThrowsAsync<IOException>(() => sut.PollAsync(CancellationToken.None));

        Assert.Contains("rig_cleanup", native.CallLog);
        Assert.True(native.CallLog.IndexOf("rig_open") < native.CallLog.IndexOf("rig_cleanup"));
    }

    [Fact]
    public async Task Connect_NativeRejectsConfigToken_ThrowsIOException_AndCleansUp()
    {
        var native = new FakeHamlibNative();
        native.Tokens.Remove("ptt_type"); // simulate the loaded library not recognizing this token

        var sut = new HamlibRadioProtocol(native, model: 1, pttType: "RTS");

        await Assert.ThrowsAsync<IOException>(() => sut.PollAsync(CancellationToken.None));
        Assert.Contains("rig_cleanup", native.CallLog);
    }

    [Fact]
    public async Task Connect_AppliesConfigInOrder_BeforeOpen()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(
            native, model: 1, serialPort: "/dev/ttyUSB0", baudRate: 9600, pttType: "RTS");

        await sut.PollAsync(CancellationToken.None);

        var log = native.CallLog;
        Assert.Equal("rig_init", log[0]);
        var openIndex = log.IndexOf("rig_open");
        Assert.True(openIndex > 0);
        var beforeOpen = log.Take(openIndex).ToList();
        Assert.Contains("rig_token_lookup:rig_pathname", beforeOpen);
        Assert.Contains("rig_token_lookup:serial_speed", beforeOpen);
        Assert.Contains("rig_token_lookup:ptt_type", beforeOpen);
        Assert.Contains(beforeOpen, c => c.StartsWith("rig_set_conf:/dev/ttyUSB0", StringComparison.Ordinal));
        Assert.Contains(beforeOpen, c => c.StartsWith("rig_set_conf:9600", StringComparison.Ordinal));
        Assert.Contains(beforeOpen, c => c.StartsWith("rig_set_conf:RTS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connect_NoOptionalConfig_SkipsConfCallsEntirely()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);

        await sut.PollAsync(CancellationToken.None);

        Assert.DoesNotContain(native.CallLog, c => c.StartsWith("rig_token_lookup", StringComparison.Ordinal));
        Assert.DoesNotContain(native.CallLog, c => c.StartsWith("rig_set_conf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeAsync_ClosesThenCleansUp_InOrder_AfterConnecting()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);
        await sut.PollAsync(CancellationToken.None); // establishes the connection

        await sut.DisposeAsync();

        var log = native.CallLog;
        Assert.True(log.IndexOf("rig_open") < log.IndexOf("rig_close"));
        Assert.True(log.IndexOf("rig_close") < log.IndexOf("rig_cleanup"));
    }

    [Fact]
    public async Task DisposeAsync_WithoutEverConnecting_DoesNotCallCloseOrCleanup()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);

        await sut.DisposeAsync();

        Assert.DoesNotContain("rig_close", native.CallLog);
        Assert.DoesNotContain("rig_cleanup", native.CallLog);
    }

    [Fact]
    public async Task DisposeAsync_ThenAnyPublicMethod_ThrowsObjectDisposedException()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sut.PollAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentPollAndSet_NeverReenterNativeCallsSimultaneously()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);
        await sut.PollAsync(CancellationToken.None); // connect + probe once, fast

        native.CallDelay = TimeSpan.FromMilliseconds(50); // slow enough for a race window to matter

        var pollTask = sut.PollAsync(CancellationToken.None);
        var setTask = sut.SetFrequencyAsync(7_000_000, CancellationToken.None);
        await Task.WhenAll(pollTask, setTask);

        Assert.False(native.Reentered);
    }
}
