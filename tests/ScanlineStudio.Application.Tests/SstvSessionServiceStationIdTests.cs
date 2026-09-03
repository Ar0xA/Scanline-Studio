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
            audioEngine, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, decoder, encoder, new MacroTextResolver(),
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), new FakeCwIdDecoder(), NullLogger<SstvSessionService>.Instance);
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
        // Main.cpp:7021-7025: sys.m_CWID == 2 -> OutputMMV() (sound-file ID). `cwEnabled`'s own
        // check is `== CwIdMode.Cw` specifically, so SoundFile mode must never fall through to
        // CW-ID -- independent of whether a sound-file path is even configured (it isn't here).
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.SoundFile, CwText = "DE %m" });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.False(encoder.LastStationIdOptions!.CwEnabled);
    }

    [Fact]
    public async Task TransmitAsync_CwIdModeSoundFileButNoPathConfigured_SoundFileIdStaysDisabled()
    {
        // CwIdMode.SoundFile alone isn't enough -- matches legacy's own
        // `!sys.m_MMVID.IsEmpty()` gate (CwIdMode.SoundFile's own doc comment).
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.SoundFile, SoundFileMmvPath = null });

        await service.TransmitAsync(TestMode, TestImage);

        var resolved = encoder.LastStationIdOptions!;
        Assert.False(resolved.SoundFileIdEnabled);
        Assert.Null(resolved.SoundFileSamples);
    }

    [Fact]
    public async Task TransmitAsync_CwIdModeSoundFileWithMissingFile_IdEnabledButSamplesStayNull()
    {
        // The file doesn't need to exist for SoundFileIdEnabled (a cheap, I/O-free "is configured"
        // signal) to be true -- only the actual SAMPLES resolution (a real file read) fails silently,
        // matching this port's "unreadable file -> silent no-op, just logged" convention.
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.SoundFile, SoundFileMmvPath = "/nonexistent/path.mmv" });

        await service.TransmitAsync(TestMode, TestImage);

        var resolved = encoder.LastStationIdOptions!;
        Assert.True(resolved.SoundFileIdEnabled);
        Assert.Null(resolved.SoundFileSamples);
    }

    [Fact]
    public async Task TransmitAsync_CwIdModeSoundFileWithRealFileAtMatchingRate_ResolvesExactSamples()
    {
        // Encoder's SampleRate (FakeSstvEncoder) is 11025Hz by default -- SampTable index 0 is also
        // 11025Hz (ComLib.cpp:68), so this exercises the "already matches" fast path: raw copy,
        // /32768.0 normalize only, no resample/IIR filter.
        var tempFile = Path.GetTempFileName();
        try
        {
            byte[] mmvBytes = [0x55, 0xAA, 0x00, 0x00, 0x10, 0x27, 0xF0, 0xD8]; // magic, index 0=11025Hz, samples [10000, -10000]
            await File.WriteAllBytesAsync(tempFile, mmvBytes);

            var (service, encoder, _, settingsStore) = CreateService();
            WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.SoundFile, SoundFileMmvPath = tempFile });

            await service.TransmitAsync(TestMode, TestImage);

            var resolved = encoder.LastStationIdOptions!;
            Assert.True(resolved.SoundFileIdEnabled);
            Assert.NotNull(resolved.SoundFileSamples);
            Assert.Equal(new[] { 10000 / 32768f, -10000 / 32768f }, resolved.SoundFileSamples!.Value.ToArray());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GetStationIdTransmitOptionsAsync_SoundFileConfigured_SetsFlagButNeverReadsTheFile()
    {
        // Plan-review round 2b design decision: the read-only preview must NOT trigger the real
        // file read/resample/IIR pass (would run on every Options-dialog close for no reason) --
        // SoundFileIdEnabled alone (cheap, no I/O) is what a summary should read instead.
        // Code-review nit: points at a REAL, valid, readable .mmv file (not a nonexistent path) so
        // this assertion is actually load-bearing -- a nonexistent path can't distinguish "gated
        // off" from "read and failed," since SoundFileSamples would be null either way.
        var tempFile = Path.GetTempFileName();
        try
        {
            byte[] mmvBytes = [0x55, 0xAA, 0x00, 0x00, 0x10, 0x27]; // magic, index 0=11025Hz, 1 sample
            await File.WriteAllBytesAsync(tempFile, mmvBytes);

            var (service, encoder, _, settingsStore) = CreateService();
            WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.SoundFile, SoundFileMmvPath = tempFile });

            var resolved = await service.GetStationIdTransmitOptionsAsync();

            Assert.True(resolved.SoundFileIdEnabled);
            Assert.Null(resolved.SoundFileSamples);
            Assert.Null(encoder.LastStationIdOptions); // no TransmitAsync call happened
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>ui_transition_plan.md step 8 (T2-2): ValidateStationIdSoundFileAsync shares its
    /// parse core (TryParseSoundFile) with TryResolveSoundFileSamples above -- these tests cover the
    /// validation-only surface (duration/failure reason), the real-audio resolution above already
    /// covers the shared exists/size/header logic didn't regress via TransmitAsync's own path.</summary>
    [Fact]
    public async Task ValidateStationIdSoundFileAsync_MissingFile_ReturnsFileNotFoundFailure()
    {
        var (service, _, _, _) = CreateService();

        var result = await service.ValidateStationIdSoundFileAsync("/nonexistent/path.mmv");

        Assert.False(result.IsValid);
        Assert.Equal(SoundFileIdValidationFailure.FileNotFound, result.Failure);
        Assert.Null(result.DurationSeconds);
    }

    [Fact]
    public async Task ValidateStationIdSoundFileAsync_FileOverSizeCap_ReturnsFileTooLargeFailure()
    {
        // A sparse file report the right Length without actually allocating/writing 32MB+ --
        // TryParseSoundFile's own size check runs BEFORE File.ReadAllBytes, so the padding is never
        // read.
        var tempFile = Path.GetTempFileName();
        try
        {
            const long maxSoundFileIdBytes = 32 * 1024 * 1024; // SstvSessionService.MaxSoundFileIdBytes
            using (var stream = new FileStream(tempFile, FileMode.Create))
            {
                stream.SetLength(maxSoundFileIdBytes + 1);
            }

            var (service, _, _, _) = CreateService();

            var result = await service.ValidateStationIdSoundFileAsync(tempFile);

            Assert.False(result.IsValid);
            Assert.Equal(SoundFileIdValidationFailure.FileTooLarge, result.Failure);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ValidateStationIdSoundFileAsync_UnplayableHeader_ReturnsUnplayableHeaderFailure()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, [0x01]); // shorter than ParseHeader's own 4-byte minimum

            var (service, _, _, _) = CreateService();

            var result = await service.ValidateStationIdSoundFileAsync(tempFile);

            Assert.False(result.IsValid);
            Assert.Equal(SoundFileIdValidationFailure.UnplayableHeader, result.Failure);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ValidateStationIdSoundFileAsync_ValidFile_ReturnsRealDurationFromOriginalSampleRate()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Same fixture as TransmitAsync_CwIdModeSoundFileWithRealFileAtMatchingRate_ResolvesExactSamples
            // above: magic, index 0=11025Hz, 2 samples -> 2/11025s duration. Deliberately NOT the
            // encoder's own SampleRate (8000Hz in this file's CreateService) -- duration must come
            // from the file's OWN original rate, not any TX target rate (this method's own interface
            // doc comment).
            byte[] mmvBytes = [0x55, 0xAA, 0x00, 0x00, 0x10, 0x27, 0xF0, 0xD8];
            await File.WriteAllBytesAsync(tempFile, mmvBytes);

            var (service, _, _, _) = CreateService();

            var result = await service.ValidateStationIdSoundFileAsync(tempFile);

            Assert.True(result.IsValid);
            Assert.Equal(SoundFileIdValidationFailure.None, result.Failure);
            Assert.Equal(2.0 / 11025.0, result.DurationSeconds!.Value, precision: 9);
        }
        finally
        {
            File.Delete(tempFile);
        }
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
    public async Task TransmitAsync_CwWpmPositiveButBelowLegitimateRange_FallsBackToDocumentedDefault()
    {
        // Tier B audit finding (Area 3): a corrupted/hand-edited settings.json value like 1 WPM used
        // to pass the old `> 0` check untouched -- MillisecondsPerDotFromWpm(1) = 1110ms/dot, which
        // keys PTT for minutes after every image with none of TuneAsync's own duration backstop on
        // this path. Below the Options dialog's own legitimate range (10-50), so it must fall back to
        // the documented default exactly like the zero/negative case above.
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwText = "TEST", CwWpm = 1 });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(StationIdSettings.DefaultCwWpm, encoder.LastStationIdOptions!.CwWpm);
    }

    [Fact]
    public async Task TransmitAsync_CwToneFrequencyAboveLegitimateRange_FallsBackToDocumentedDefault()
    {
        // Tier B audit finding (Area 3): a corrupted/hand-edited settings.json value like 40000 Hz
        // used to pass the old `> 0` check untouched, reaching CwMorseGenerator unclamped -- well
        // above this file's encoder Nyquist, aliasing to an arbitrary on-air tone appended to every
        // transmission. Above the Options dialog's own legitimate range (100-3000 Hz), so it must
        // fall back to the documented default exactly like the zero/negative case above.
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwText = "TEST", CwToneFrequencyHz = 40000 });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(StationIdSettings.DefaultCwToneFrequencyHz, encoder.LastStationIdOptions!.CwToneFrequencyHz);
    }

    // Tier B audit finding (Area 3, nit): a +Infinity CwToneFrequencyHz would also reach the same
    // `is >= 100 and <= 3000` upper-bound check above and fall through to the default -- `+Infinity
    // >= 100` is true, but `+Infinity <= 3000` is false, so the `and` still rejects it. Not given a
    // dedicated test,
    // since System.Text.Json's default writer throws on +Infinity BEFORE this test's own
    // WithStationIdSettings helper (WithSection -> Serialize) can even construct the scenario
    // (confirmed by trying); reaching it for real would need a hand-crafted settings.json bypassing
    // normal round-trip serialization, which the auditor's own review flagged as unverified and not
    // required to close this finding -- the finite-but-absurd cases above already prove the fix.

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

    [Fact]
    public void StationIdDecoded_IsAPurePassThroughOfTheDecodersOwnEvent()
    {
        // CW-ID/FSK station-ID subsystem Phase 5: ISstvSessionService.StationIdDecoded's own doc
        // comment says this is a pure pass-through, NOT self-filtered here -- the operator-callsign
        // self-filter lives in the UI-layer consumer (RxImagePaneViewModel), which fetches the
        // operator's own callsign via GetOperatorCallsignAsync below instead. Verifies that split by
        // proving THIS layer forwards every raised event unfiltered, including one that happens to
        // carry the operator's own callsign.
        var (service, _, decoder, settingsStore) = CreateService();
        WithOperatorSettings(settingsStore, new OperatorSettings { Callsign = "W1AW" });
        var received = new List<FskStationIdDecodedInfo>();
        service.StationIdDecoded += info => received.Add(info);

        decoder.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "W1AW"));

        Assert.Single(received);
        Assert.Equal("W1AW", received[0].Callsign);
    }

    [Fact]
    public async Task GetOperatorCallsignAsync_ReturnsConfiguredCallsign()
    {
        var (service, _, _, settingsStore) = CreateService();
        WithOperatorSettings(settingsStore, new OperatorSettings { Callsign = "W1AW" });

        var callsign = await service.GetOperatorCallsignAsync();

        Assert.Equal("W1AW", callsign);
    }

    [Fact]
    public async Task GetOperatorCallsignAsync_NoOperatorSectionConfigured_ReturnsNull()
    {
        var (service, _, _, _) = CreateService();

        var callsign = await service.GetOperatorCallsignAsync();

        Assert.Null(callsign);
    }

    [Fact]
    public async Task GetOperatorCallsignAsync_NormalizesTheStoredCallsign()
    {
        // Auditor code-review finding on Phase 5 (real bug, fixed): a decoded FSK station-ID
        // callsign is ALWAYS normalized by construction (StationIdCallsignNormalizer.Normalize runs
        // before every transmission, including this port's own). Returning the operator's callsign
        // AS STORED (e.g. "  w1aw  ") instead of normalized would make the self-filter comparison in
        // RxImagePaneViewModel silently fail against a real decoded "W1AW" for any operator whose
        // stored callsign wasn't already uppercase/trimmed -- OperatorSettings.Callsign itself is
        // left as-typed in storage (used for macros/display/QRZ elsewhere), so this method must
        // normalize fresh on every call.
        var (service, _, _, settingsStore) = CreateService();
        WithOperatorSettings(settingsStore, new OperatorSettings { Callsign = "  w1aw  " });

        var callsign = await service.GetOperatorCallsignAsync();

        Assert.Equal("W1AW", callsign);
    }

    [Fact]
    public async Task GetOperatorCallsignAsync_EmptyCallsign_StaysEmpty_NotNull()
    {
        var (service, _, _, settingsStore) = CreateService();
        WithOperatorSettings(settingsStore, new OperatorSettings { Callsign = "" });

        var callsign = await service.GetOperatorCallsignAsync();

        Assert.Equal("", callsign);
    }

    [Fact]
    public async Task GetStationIdTransmitOptionsAsync_ResolvesTheSameWayTransmitAsyncWould_WithNoSideEffects()
    {
        // Phase 6 (Options/Transmit-tab UI wiring): GetStationIdTransmitOptionsAsync is the exact
        // same resolution TransmitAsync itself uses (see that method's own doc comment) -- a
        // read-only preview for the Transmit tab's Identification summary card. Must not transmit
        // anything (no encoder call, no TX-encode side effect) just from being called.
        var (service, encoder, _, settingsStore) = CreateService();
        WithStationIdSettings(settingsStore, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwText = "DE %m", CwWpm = 22, FskIdTxEnabled = true });
        WithOperatorSettings(settingsStore, new OperatorSettings { Callsign = "W1AW" });

        var resolved = await service.GetStationIdTransmitOptionsAsync();

        Assert.True(resolved.CwEnabled);
        Assert.Equal("DE W1AW", resolved.CwResolvedText);
        Assert.Equal(22, resolved.CwWpm);
        Assert.True(resolved.FskIdEnabled);
        Assert.Equal("W1AW", resolved.Callsign);
        Assert.Null(encoder.LastStationIdOptions); // no TransmitAsync call happened
    }
}
