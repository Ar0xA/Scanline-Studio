using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>
/// Phase 4 (CW-ID/FSK station-ID subsystem): <see cref="SstvSessionService.TransmitAsync"/>'s
/// settings-resolution logic -- loading <see cref="StationIdSettings"/>/<see cref="OperatorSettings"/>
/// and building a <see cref="StationIdTransmitOptions"/> right before encoding, including the
/// settings-boundary WPM/tone-frequency validation (Phase 1 code-review finding). Verified via
/// <see cref="FakeSstvEncoder.LastStationIdOptions"/> rather than decoding real audio -- the DSP-level
/// wiring itself (footer selection, segment append, callsign/NR normalization) is already covered by
/// <c>AnalogFmSstvEncoderStationIdWiringTests</c> in <c>ScanlineStudio.Core.Sstv.Tests</c>; this only
/// needs to prove the Application-layer resolution logic feeds the right values in.
/// </summary>
public sealed class SstvSessionServiceStationIdTests
{
    private static readonly SstvModeDefinition TestMode = new(
        Id: "test",
        DisplayName: "Test",
        VisCode: 0,
        ImageWidth: 1,
        ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    private static readonly IImageSource TestImage = new ArrayImageSource(1, 1, new Rgb24[1]);

    private static (SstvSessionService Service, FakeSstvEncoder Encoder, FakeSstvDecoder Decoder, FakeSettingsStore SettingsStore) CreateService()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var encoder = new FakeSstvEncoder();
        var decoder = new FakeSstvDecoder();

        var service = new SstvSessionService(
            audioEngine, deviceEnumerator, settingsStore, decoder, encoder, new MacroTextResolver(),
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);
        return (service, encoder, decoder, settingsStore);
    }

    private static FakeSettingsStore WithStationIdSettings(FakeSettingsStore store, StationIdSettings settings)
    {
        store.Settings = store.Settings.WithSection(StationIdSettings.SectionKey, settings, StationIdSettingsJsonContext.Default.StationIdSettings);
        return store;
    }

    private static FakeSettingsStore WithOperatorSettings(FakeSettingsStore store, OperatorSettings settings)
    {
        store.Settings = store.Settings.WithSection(OperatorSettings.SectionKey, settings, OperatorSettingsJsonContext.Default.OperatorSettings);
        return store;
    }

    [Fact]
    public async Task TransmitAsync_NoSectionsConfigured_ResolvesToNothingEnabled()
    {
        var (service, encoder, _, _) = CreateService();

        await service.TransmitAsync(TestMode, TestImage);

        var resolved = encoder.LastStationIdOptions;
        Assert.NotNull(resolved);
        Assert.False(resolved!.CwEnabled);
        Assert.False(resolved.FskIdEnabled);
        Assert.Null(resolved.NrRstText);
    }

    [Fact]
    public async Task TransmitAsync_CwIdModeOffWithText_StaysDisabled()
    {
        // Main.cpp:7021: sys.m_CWID == 1 is the real gate -- CwText being non-empty alone must not
        // enable CW-ID (mirrors legacy's own tri-state, not just "text present -> on").
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.Off, CwText = "DE %m" });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.False(encoder.LastStationIdOptions!.CwEnabled);
    }

    [Fact]
    public async Task TransmitAsync_CwIdModeCwWithEmptyText_StaysDisabled()
    {
        // Main.cpp:6969: OutputCWID's own !sys.m_CWIDText.IsEmpty() gate, checked on the RAW field.
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwText = "" });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.False(encoder.LastStationIdOptions!.CwEnabled);
    }

    [Fact]
    public async Task TransmitAsync_CwIdModeSoundFile_StaysDisabled_NotAccidentallyTreatedAsCwOn()
    {
        // Main.cpp:7021-7025: sys.m_CWID == 2 -> OutputMMV() (sound-file, out of v1 scope).
        // StationIdTransmitOptions has no field for it at all -- must resolve to CwEnabled=false,
        // silently transmitting nothing, matching legacy's own unconfigured-sound-file behavior
        // (`!sys.m_MMVID.IsEmpty()` failing) rather than accidentally falling through to CW-ID.
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.SoundFile, CwText = "DE %m" });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.False(encoder.LastStationIdOptions!.CwEnabled);
    }

    [Fact]
    public async Task TransmitAsync_CwIdResolvedTextLongerThan77Chars_IsCapped()
    {
        // Auditor code-review finding on Phase 4 (real, fixed in round 1, refined in round 2): legacy
        // caps plain literal (non-macro) MACRO-RESOLVED CW-ID text at 77 chars, not 78 --
        // MacroText's own break condition (Main.cpp:10829, `if (n >= (size-1)) break;` with
        // `size = sizeof(bf)-2 = 78`) stops once n reaches 77. Checked on the RESOLVED text (after
        // macro substitution), not the raw configured field.
        var (service, encoder, _, settingsStore) = CreateService();
        var longText = new string('X', 100);
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwText = longText });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(77, encoder.LastStationIdOptions!.CwResolvedText.Length);
        Assert.Equal(new string('X', 77), encoder.LastStationIdOptions!.CwResolvedText);
    }

    [Fact]
    public async Task TransmitAsync_CwIdEnabled_ResolvesMacroTokensAgainstOperatorSettings()
    {
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwText = "DE %m" });
        WithOperatorSettings(settingsStore, new OperatorSettings { Callsign = "W1AW" });

        await service.TransmitAsync(TestMode, TestImage);

        var resolved = encoder.LastStationIdOptions!;
        Assert.True(resolved.CwEnabled);
        Assert.Equal("DE W1AW", resolved.CwResolvedText);
    }

    [Fact]
    public async Task TransmitAsync_CwWpmUnset_FallsBackToDocumentedDefault()
    {
        // Settings-boundary validation (Phase 1 code-review finding): must resolve to the SAME
        // effective value whether CwWpm is left null (unset) or explicitly set to the documented
        // default -- proves the fallback actually applies the right number, not just "some" number.
        var (serviceA, encoderA, _, settingsStoreA) = CreateService();
        WithStationIdSettings(settingsStoreA, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwText = "TEST", CwWpm = null });
        await serviceA.TransmitAsync(TestMode, TestImage);

        var (serviceB, encoderB, _, settingsStoreB) = CreateService();
        WithStationIdSettings(settingsStoreB, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwText = "TEST", CwWpm = StationIdSettings.DefaultCwWpm });
        await serviceB.TransmitAsync(TestMode, TestImage);

        Assert.Equal(StationIdSettings.DefaultCwWpm, encoderA.LastStationIdOptions!.CwWpm);
        Assert.Equal(encoderB.LastStationIdOptions!.CwWpm, encoderA.LastStationIdOptions!.CwWpm);
    }

    [Fact]
    public async Task TransmitAsync_CwWpmZeroOrNegative_FallsBackToDocumentedDefault_NotInfinityOrThrow()
    {
        // A corrupted/hand-edited settings.json could contain 0 or a negative WPM -- must not reach
        // CwMorseGenerator.MillisecondsPerDotFromWpm (which would return Infinity/negative for those
        // inputs) or abort the transmission; falls back to the same default as "unset."
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwText = "TEST", CwWpm = -5 });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(StationIdSettings.DefaultCwWpm, encoder.LastStationIdOptions!.CwWpm);
    }

    [Fact]
    public async Task TransmitAsync_CwToneFrequencyZeroOrNegative_FallsBackToDocumentedDefault()
    {
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwText = "TEST", CwToneFrequencyHz = 0 });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(StationIdSettings.DefaultCwToneFrequencyHz, encoder.LastStationIdOptions!.CwToneFrequencyHz);
    }

    [Fact]
    public async Task TransmitAsync_NrRstEnabledUnset_DefaultsToTrue_MatchingLegacysOwnDefault()
    {
        // LogFile.cpp:378: Log.m_LogSet.m_FSKNR defaults to 1 (enabled), the one field on this
        // record whose legacy default is "on" rather than the CLR default.
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { FskIdTxEnabled = true, NrRstEnabled = null, NrRstText = "599123" });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal("599123", encoder.LastStationIdOptions!.NrRstText);
    }

    [Fact]
    public async Task TransmitAsync_NrRstEnabledExplicitlyFalse_PassesNullRegardlessOfConfiguredText()
    {
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { FskIdTxEnabled = true, NrRstEnabled = false, NrRstText = "599123" });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Null(encoder.LastStationIdOptions!.NrRstText);
    }

    [Fact]
    public async Task TransmitAsync_FskIdEnabled_PassesThroughOperatorCallsignUnnormalized()
    {
        // Normalization (uppercase/trim/16-char cap) happens inside AnalogFmSstvEncoder, not here --
        // this layer just passes the operator's callsign through as-configured (see
        // StationIdTransmitOptions' own doc comment for why the split is there).
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { FskIdTxEnabled = true });
        WithOperatorSettings(settingsStore, new OperatorSettings { Callsign = "  w1aw  " });

        await service.TransmitAsync(TestMode, TestImage);

        var resolved = encoder.LastStationIdOptions!;
        Assert.True(resolved.FskIdEnabled);
        Assert.Equal("  w1aw  ", resolved.Callsign);
    }

    [Fact]
    public async Task StartReceivingAsync_FskIdRxEnabledTrue_SetsDecoderStationIdDecodeEnabled()
    {
        // Auditor code-review finding on Phase 4 (real gap, fixed): StationIdSettings.FskIdRxEnabled
        // (the m_fskdecode port-equivalent) was a dead setting -- nothing ever read it. This is the
        // wiring that makes it live: StartReceivingAsync must apply it to the decoder on every RX
        // start (not just once at DI-construction time), matching ISstvDecoder.StationIdDecodeEnabled's
        // own "deliberately live-settable" contract.
        var (service, _, decoder, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { FskIdRxEnabled = true });

        await service.StartReceivingAsync();

        Assert.True(decoder.StationIdDecodeEnabled);
    }

    [Fact]
    public async Task StartReceivingAsync_FskIdRxEnabledUnset_LeavesDecoderStationIdDecodeDisabled()
    {
        // Matches legacy's own m_fskdecode default (sstv.h:708, zero-init) -- an absent/default
        // section must not accidentally turn RX station-ID decode on.
        var (service, _, decoder, _) = CreateService();

        await service.StartReceivingAsync();

        Assert.False(decoder.StationIdDecodeEnabled);
    }
}
