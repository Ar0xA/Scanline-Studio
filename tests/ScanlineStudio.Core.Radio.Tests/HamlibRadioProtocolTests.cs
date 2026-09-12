using System.Runtime.InteropServices;
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
    // RIG_LEVEL_SWR/ALC/RFPOWER_METER/STRENGTH -- verified directly against rig.h
    // (CONSTANT_64BIT_FLAG(28/29/32/30)).
    private const ulong LevelSwr = 1UL << 28;
    private const ulong LevelAlc = 1UL << 29;
    private const ulong LevelStrength = 1UL << 30;
    private const ulong LevelRfPowerMeter = 1UL << 32;

    [Fact]
    public async Task PollAsync_FullCapabilities_ReturnsFullyPopulatedState()
    {
        var native = new FakeHamlibNative
        {
            Frequency = 14_074_000,
            Mode = 1UL << 2, // RIG_MODE_USB
            Ptt = 1,
        };
        native.LevelValues[LevelSwr] = 1.2f;
        native.LevelValues[LevelAlc] = 50f;
        native.LevelValues[LevelRfPowerMeter] = 0.75f;
        native.LevelIntValues[LevelStrength] = -9;
        var sut = new HamlibRadioProtocol(native, model: 1);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(14_074_000, state.FrequencyHz);
        Assert.Equal(RadioMode.Usb, state.Mode);
        Assert.True(state.IsTransmitting);
        Assert.Equal(1.2f, state.SwrRatio);
        Assert.Equal(50f, state.AlcLevel);
        Assert.Equal(75f, state.PowerPercent); // 0.75 fraction -> 75%
        // STRENGTH capability is negotiated (probe succeeded, default LevelCodes entry is 0/success),
        // but the value itself stays null this poll -- IsTransmitting is true, and SignalStrengthDb
        // is RX-only (opposite gating from the three TX-only meters just asserted above).
        Assert.Null(state.SignalStrengthDb);
        // FakeHamlibNative's RigGetMode defaults its width out-param to 0 (RIG_PASSBAND_NORMAL) when
        // not scripted -- maps to null, not a real 0 Hz reading, per RadioState.BandwidthHz's own
        // doc comment.
        Assert.Null(state.BandwidthHz);
        Assert.Equal(
            RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency |
            RadioCapabilities.ReadMode | RadioCapabilities.SetMode | RadioCapabilities.PttControl |
            RadioCapabilities.SwrMeter | RadioCapabilities.AlcMeter | RadioCapabilities.PowerMeter |
            RadioCapabilities.SignalMeter | RadioCapabilities.ReadBandwidth | RadioCapabilities.SetBandwidth,
            sut.Capabilities);
    }

    [Fact]
    public async Task PollAsync_NonZeroPassband_PopulatesBandwidthHz()
    {
        var native = new FakeHamlibNative { Mode = 1UL << 2, Width = new CLong(2400) }; // RIG_MODE_USB
        var sut = new HamlibRadioProtocol(native, model: 1);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(2400, state.BandwidthHz);
    }

    [Fact]
    public async Task SetBandwidthAsync_PreservesCurrentMode_PassesRequestedWidthVerbatim()
    {
        // 1UL << 6 is deliberately NOT one of HamlibToMode's mapped values (code-review correction:
        // 1UL << 3 used here originally IS RIG_MODE_LSB, one of the 11 mapped values, so a
        // round-trip-through-HamlibToMode/ModeToHamlib implementation would have produced an
        // identical result and this test would not have caught it) -- proves the mode round-trips
        // through its raw ulong bit-flag, never through the dictionaries (see SetBandwidthAsync's own
        // doc comment: a round-trip through them would throw or silently change the operating mode as
        // a side effect of a bandwidth-only set).
        var native = new FakeHamlibNative { Mode = 1UL << 6 };
        var sut = new HamlibRadioProtocol(native, model: 1);

        await sut.SetBandwidthAsync(1800, CancellationToken.None);

        Assert.Equal(1UL << 6, native.Mode);
        Assert.Equal(1800, native.Width.Value);
        Assert.Equal(["rig_get_mode", "rig_set_mode"], native.CallLog.TakeLast(2));
    }

    [Fact]
    public async Task SetBandwidthAsync_NullRequest_SendsPassbandNormalSentinel()
    {
        // Width starts non-zero (code-review correction: FakeHamlibNative.Width defaults to 0
        // already, so the original version of this test passed even with SetBandwidthAsync as a
        // complete no-op) -- proves the call genuinely reset it to the RIG_PASSBAND_NORMAL sentinel,
        // not merely that it was already 0.
        var native = new FakeHamlibNative { Mode = 1UL << 2, Width = new CLong(2400) };
        var sut = new HamlibRadioProtocol(native, model: 1);

        await sut.SetBandwidthAsync(null, CancellationToken.None);

        Assert.Equal(0, native.Width.Value); // RIG_PASSBAND_NORMAL
        Assert.Equal(["rig_get_mode", "rig_set_mode"], native.CallLog.TakeLast(2));
    }

    [Fact]
    public async Task PollAsync_WhileNotTransmitting_NeverReadsTxMetersEvenIfCapable_ButReadsSignalStrength()
    {
        var native = new FakeHamlibNative { Ptt = 0 };
        native.LevelValues[LevelSwr] = 1.2f;
        native.LevelIntValues[LevelStrength] = -9;
        var sut = new HamlibRadioProtocol(native, model: 1);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.False(state.IsTransmitting);
        Assert.Null(state.SwrRatio);
        Assert.Null(state.AlcLevel);
        Assert.Null(state.PowerPercent);
        Assert.Equal(-9, state.SignalStrengthDb);
        // Exactly 5 -- the one-time connect probe (4: SWR/ALC/RFPOWER_METER/STRENGTH, which runs
        // regardless of TX state, to negotiate capabilities up front) plus the per-poll STRENGTH read
        // (RX-only, so it DOES fire here) -- the per-poll SWR/ALC/RFPOWER_METER reads must never fire
        // while not transmitting.
        Assert.Equal(5, native.CallLog.Count(c => c.StartsWith("rig_get_level:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PollAsync_WhileTransmitting_NeverReadsSignalStrengthEvenIfCapable()
    {
        // Mirrors PollAsync_WhileNotTransmitting_NeverReadsTxMetersEvenIfCapable_ButReadsSignalStrength
        // above, but for the opposite (RX-only) gating direction.
        var native = new FakeHamlibNative { Ptt = 1 };
        native.LevelIntValues[LevelStrength] = -9;
        var sut = new HamlibRadioProtocol(native, model: 1);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.True(state.IsTransmitting);
        Assert.Null(state.SignalStrengthDb);
    }

    [Fact]
    public async Task PollAsync_SignalStrengthUnavailable_MarksCapabilityAbsent_LeavesFieldNull()
    {
        var native = new FakeHamlibNative { Ptt = 0 };
        native.LevelCodes[LevelStrength] = -11; // -RIG_ENAVAIL, soft -- probed as "not supported"
        var sut = new HamlibRadioProtocol(native, model: 1);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.False(sut.Capabilities.HasFlag(RadioCapabilities.SignalMeter));
        Assert.Null(state.SignalStrengthDb);
    }

    [Fact]
    public async Task PollAsync_SignalStrengthReadSoftErrorsMidSession_YieldsNull_DoesNotThrow()
    {
        var native = new FakeHamlibNative { Ptt = 0 };
        native.LevelIntValues[LevelStrength] = -9;
        var sut = new HamlibRadioProtocol(native, model: 1);
        await sut.PollAsync(CancellationToken.None); // connect + probe, STRENGTH supported

        native.LevelCodes[LevelStrength] = -11; // -RIG_ENAVAIL, soft -- this read now fails

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Null(state.SignalStrengthDb);
    }

    /// <summary>Real, easy-to-miss native-marshaling bug class this exists to guard against:
    /// <c>value_t</c> is a genuine C union (rig.h) -- <c>RIG_LEVEL_STRENGTH</c> is documented "arg
    /// int (dB)", NOT float like SWR/ALC/RFPOWER_METER, and reading an int-typed level's raw bytes
    /// back out through the FLOAT arm (<see cref="IHamlibNative.RigGetLevel"/>) would silently
    /// reinterpret its bit pattern as an unrelated IEEE-754 value instead of throwing or producing an
    /// obviously-wrong number. <see cref="FakeHamlibNative"/> models the two arms as genuinely
    /// separate dictionaries (<c>LevelValues</c> float, <c>LevelIntValues</c> int) specifically so a
    /// test can prove <see cref="HamlibRadioProtocol"/> reads STRENGTH through
    /// <see cref="IHamlibNative.RigGetLevelInt"/>, not <see cref="IHamlibNative.RigGetLevel"/> -- if
    /// the implementation ever regressed to the float arm, this value would come back as
    /// <c>LevelValues.GetValueOrDefault(LevelStrength, 0f)</c> = <c>0</c>, not <c>-9</c>, since only
    /// <c>LevelIntValues</c> is populated here.</summary>
    [Fact]
    public async Task PollAsync_SignalStrength_ReadsThroughTheIntArm_NotTheFloatArm()
    {
        var native = new FakeHamlibNative { Ptt = 0 };
        native.LevelIntValues[LevelStrength] = -9;
        var sut = new HamlibRadioProtocol(native, model: 1);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(-9, state.SignalStrengthDb);
    }

    [Fact]
    public async Task PollAsync_SwrMeterUnavailable_MarksCapabilityAbsent_LeavesFieldNull()
    {
        var native = new FakeHamlibNative { Ptt = 1 };
        native.LevelCodes[LevelSwr] = -11; // -RIG_ENAVAIL, soft -- probed as "not supported"
        var sut = new HamlibRadioProtocol(native, model: 1);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.False(sut.Capabilities.HasFlag(RadioCapabilities.SwrMeter));
        Assert.Null(state.SwrRatio);
    }

    [Fact]
    public async Task PollAsync_MeterReadSoftErrorsMidSession_YieldsNullForThatMeterOnly_DoesNotThrow()
    {
        // A meter read failing intermittently must never abort the whole poll the way a freq/mode
        // failure does -- meters are far more likely than freq/mode/ptt to soft-error transiently.
        var native = new FakeHamlibNative { Ptt = 1 };
        native.LevelValues[LevelSwr] = 1.2f;
        native.LevelValues[LevelAlc] = 50f;
        var sut = new HamlibRadioProtocol(native, model: 1);
        await sut.PollAsync(CancellationToken.None); // connect + probe, all meters supported

        native.LevelCodes[LevelSwr] = -11; // -RIG_ENAVAIL, soft -- this read now fails

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Null(state.SwrRatio);
        Assert.Equal(50f, state.AlcLevel);
    }

    [Fact]
    public async Task PollAsync_MeterReadHardErrors_ThrowsPlainException_NotRadioProtocolException()
    {
        var native = new FakeHamlibNative { Ptt = 1 };
        native.LevelValues[LevelSwr] = 1.2f;
        var sut = new HamlibRadioProtocol(native, model: 1);
        await sut.PollAsync(CancellationToken.None);

        native.LevelCodes[LevelSwr] = -6; // -RIG_EIO, hard

        var ex = await Assert.ThrowsAsync<IOException>(() => sut.PollAsync(CancellationToken.None));
        Assert.IsNotType<RadioProtocolException>(ex);
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
    public async Task Connect_ProbeCapabilitiesHardErrors_ClosesAndCleansUpTheRig()
    {
        // Regression test for chunk 3c round 1's blocker 1: a hard error during ProbeCapabilities (the
        // SECOND CallAsync in EnsureConnectedAsync, running AFTER rig_open already succeeded) used to
        // leave the rig open with _connected still false -- DisposeAsync's own "if (_connected)" guard
        // then skipped teardown entirely, and the next EnsureConnectedAsync overwrote _rig with a
        // fresh rig_init, leaking the previous struct AND holding the serial port for the process
        // lifetime. A plain "wrong baud rate" is enough to reach this -- no race required.
        var native = new FakeHamlibNative { RigGetFreqCode = -6 }; // -RIG_EIO, hard -- fails the very
                                                                    // first probe (ReadFrequency)
        var sut = new HamlibRadioProtocol(native, model: 1);

        await Assert.ThrowsAsync<IOException>(() => sut.PollAsync(CancellationToken.None));

        var log = native.CallLog;
        Assert.True(log.IndexOf("rig_open") < log.IndexOf("rig_close"));
        Assert.True(log.IndexOf("rig_close") < log.IndexOf("rig_cleanup"));
        Assert.Equal(RadioCapabilities.None, sut.Capabilities);

        // The next connect attempt must not see a stale open handle -- confirms _rig was actually
        // released, not just that the log lines appeared in order.
        native.RigGetFreqCode = 0;
        await sut.PollAsync(CancellationToken.None);
        Assert.Equal(2, log.Count(c => c == "rig_init"));
    }

    [Fact]
    public async Task DisposeAsync_QueuedBehindASlowCall_ThenALaterQueuedCaller_ThrowsObjectDisposedException_NeverResurrectsTheRig()
    {
        // Regression test for chunk 3c round 1's blocker 2: DisposeAsync's own `finally
        // { _lock.Release(); }` used to hand the semaphore slot straight to whatever call queued
        // behind it, without re-checking _disposed -- so a caller already queued on _lock.WaitAsync
        // BEFORE DisposeAsync ran fell into EnsureConnectedAsync with _connected already reset to
        // false, and rig_init/rig_open'd a BRAND NEW rig on a disposed protocol. For SetPttAsync(true)
        // that meant a physically keyed transmitter on a handle nothing would ever close again, while
        // the caller saw only an ObjectDisposedException out of its own Release() and concluded the
        // key had failed.
        // TT1-13 (BACKLOG.md): this used to sequence the race below with two Task.Delay(20) calls,
        // assuming rather than enforcing that the slow poll had the lock before PTT queued, and that
        // PTT had queued before Dispose ran. yoniq-auditor plan-review found the assumption itself was
        // sound (the synchronous prefix of an async call, including an uncontended SemaphoreSlim
        // acquire, really does run on the calling thread before the method returns its Task) but that
        // removing the delays without replacing them would trade a VISIBLE flake for an INVISIBLE one:
        // AcquireAsync has two different disposed checks (pre-wait at :591, post-wait at :600-604), and
        // if PTT ever raced past Dispose instead of queuing behind it, it would hit the pre-wait check
        // instead -- still throwing ObjectDisposedException, still leaving rig_init at 1, still not
        // reaching rig_set_ptt, so every assertion this test had would still pass while testing the
        // WRONG code path. Fixed with two changes: a real gate instead of a sleep, and a second
        // assertion that discriminates which of the two checks actually fired.
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1);
        await sut.PollAsync(CancellationToken.None); // establishes the connection

        // OnCallStarting fires only once native calls only ever run while _lock is held (see
        // FakeHamlibNative.OnCallStarting's own doc comment), so this firing is direct proof _lock is
        // held -- not an inference from timing. It then blocks the native call on releaseGate, holding
        // _lock for exactly as long as this test needs rather than for a fixed wall-clock guess.
        var callStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseGate = new ManualResetEventSlim(initialState: false);
        native.OnCallStarting = () =>
        {
            native.OnCallStarting = null; // one-shot: only the slow poll's FIRST native call blocks --
                                           // the other RigGetMode/RigGetPtt calls this same PollAsync
                                           // makes, and DisposeAsync's own teardown calls, must run
                                           // freely once this gate opens.
            callStarted.TrySetResult();
            releaseGate.Wait();
        };

        Task<RadioState>? slowPollTask = null;
        Task? pttTask = null;
        var disposeTask = default(ValueTask);
        try
        {
            slowPollTask = sut.PollAsync(CancellationToken.None); // acquires _lock, blocks on the gate
            await callStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // _lock is now provably held

            // Queued BEFORE DisposeAsync is ever called -- passes its own initial disposed check while
            // _disposed is still false, then blocks on _lock.WaitAsync() behind the slow poll above.
            // IsCompleted false here is the enforcement, not an assumption: _lock is still held (the
            // gate above has not been released), so a completed task here would mean this call did NOT
            // queue behind the slow poll -- which the rest of this test must not silently pass through.
            pttTask = sut.SetPttAsync(true, CancellationToken.None);
            Assert.False(pttTask.IsCompleted);

            disposeTask = sut.DisposeAsync();
        }
        finally
        {
            // Always -- including if an assertion above threw -- so the native call blocked inside
            // OnCallStarting is never leaked, whatever the outcome of this test.
            releaseGate.Set();
        }

        await slowPollTask;
        var pttException = await Assert.ThrowsAsync<ObjectDisposedException>(() => pttTask);

        // Discriminates AcquireAsync's PRE-wait disposed check (ObjectDisposedException.ThrowIf's
        // ObjectName is the namespace-qualified type name) from its POST-wait one (ObjectName is the
        // short "HamlibRadioProtocol" from `nameof`, verified empirically against this exact call --
        // see AcquireAsync's own doc comment). Only the post-wait throw proves PTT actually queued
        // behind Dispose and got the disposed re-check after waking, which is this test's whole point.
        Assert.Equal(nameof(HamlibRadioProtocol), pttException.ObjectName);

        await disposeTask;

        Assert.Equal(1, native.CallLog.Count(c => c == "rig_init")); // never resurrected
        Assert.DoesNotContain("rig_set_ptt", native.CallLog); // the queued PTT call never reached
                                                               // the native layer
    }

    [Fact]
    public async Task Connect_AppliesConfigInOrder_BeforeOpen()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(
            native, model: 1, serialPort: "/dev/ttyUSB0", baudRate: 9600, pttType: "RTS", pttPort: "/dev/ttyUSB1");

        await sut.PollAsync(CancellationToken.None);

        var log = native.CallLog;
        Assert.Equal("rig_init", log[0]);
        var openIndex = log.IndexOf("rig_open");
        Assert.True(openIndex > 0);
        var beforeOpen = log.Take(openIndex).ToList();
        Assert.Contains("rig_token_lookup:rig_pathname", beforeOpen);
        Assert.Contains("rig_token_lookup:serial_speed", beforeOpen);
        Assert.Contains("rig_token_lookup:ptt_type", beforeOpen);
        Assert.Contains("rig_token_lookup:ptt_pathname", beforeOpen);
        Assert.Contains(beforeOpen, c => c.StartsWith("rig_set_conf:/dev/ttyUSB0", StringComparison.Ordinal));
        Assert.Contains(beforeOpen, c => c.StartsWith("rig_set_conf:9600", StringComparison.Ordinal));
        Assert.Contains(beforeOpen, c => c.StartsWith("rig_set_conf:RTS", StringComparison.Ordinal));
        Assert.Contains(beforeOpen, c => c.StartsWith("rig_set_conf:/dev/ttyUSB1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connect_NoPttPort_SkipsPttPathnameConfCall()
    {
        var native = new FakeHamlibNative();
        var sut = new HamlibRadioProtocol(native, model: 1, pttType: "RIG");

        await sut.PollAsync(CancellationToken.None);

        Assert.DoesNotContain("rig_token_lookup:ptt_pathname", native.CallLog);
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
