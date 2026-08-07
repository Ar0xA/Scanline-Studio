# Roadmap

## Related

Ties together every other document — each phase below is delivered by working through the "Definition of done" checklists in the referenced specs, in order, keeping commits small and reviewable per CLAUDE.md. Also see [LICENSES.md](../LICENSES.md) and [docs/removed-features.md](../docs/removed-features.md), both living documents that get new entries as work in these phases proceeds.

## Sequencing principle

Order is chosen so that at the end of every phase there is a **runnable, demoable** program, never a long stretch of code that doesn't build into something observable. This also front-loads the highest cross-platform risk (audio, DSP) rather than saving it for last, since [[05-audio-engine]] is the area most likely to reveal that an architectural assumption needs revisiting.

## Phase 0 — Walking skeleton

- [[01-architecture]]: solution scaffold, DI host, nullable+warnings-as-errors, empty Avalonia window boots on Windows/Linux/macOS.
- [[13-testing]]: CI matrix running (even with near-zero tests) so every subsequent PR is gated from day one.
- [[12-settings]]: `ISettingsStore` minimal implementation (no migration chain yet).

**Demo:** app launches on all three OSes, shows a blank window, settings file is created on disk.

## Phase 1 — Audio + DSP core (no UI, no radio)

- [[05-audio-engine]]: capture/playback on Linux **done and real-hardware-tested** (`ScanlineStudio.Core.Audio.MiniAudio`, `ScanlineStudio.Core.Audio.MiniAudio.Tests`); Windows/macOS **not yet verified** — see below. `FakeAudioEngine` round-trip test added first, closing a Definition-of-Done gap that had been believed already met but wasn't. Backend choice re-litigated and reversed: PortAudio (the spec's original pick) rejected after direct verification found it fails this spec's own requirements (no real device-change API; no PulseAudio/PipeWire host API on Linux; no sample-rate conversion) — switched to `miniaudio`, vendored at pinned tag `0.11.25` behind a hand-written C shim (`native/yoniq_audio.c`/`.h`) exposing this project's own ABI, never miniaudio's own structs directly. See [[05-audio-engine]]'s Backend choice section for the full reasoning.
  - Built piece by piece, each independently tested against real PipeWire virtual devices before moving on: device enumeration + native-format probing, an allocation-free ring buffer (`ma_pcm_rb`, reused not reinvented), the real capture path, the real playback path with underrun/back-pressure, a device-free resampler-quality measurement (miniaudio's only built-in resampler — linear, "fastest, lowest quality" per its own docs — measured to add ~0.07 average per-channel delta on a real Martin M1 round trip; negligible, no escalation needed), a `RequiresPipeWireFactAttribute` so these hardware tests degrade to an honest CI skip rather than a hard failure on Windows/macOS runners or a Linux runner without a running audio server, and a hot-unplug investigation against a real virtual sink unloaded mid-session.
  - **Real bug found and fixed via that last piece**: disposing a capture or playback session whose device had already disappeared could hang indefinitely — root-caused to miniaudio's PulseAudio backend blocking forever in `ma_wait_for_operation__pulse` waiting for a server reply that will never come once the backing device is gone (confirmed directly against the pinned `miniaudio.h`, not assumed). Fixed by bounding the native close call with a timeout on a dedicated thread in both `MiniAudioCaptureSession`/`MiniAudioPlaybackSession`. The same investigation also falsified this piece's own original premise — the device notification callback's `stopped` event does **not** fire when a device disappears this way on PulseAudio, only on an actual server-side suspend/resume — corrected in [[05-audio-engine]]'s Device hot-plug section rather than left standing.
  - `miniaudio.h` (dual Unlicense/MIT-0, Scanline Studio elects MIT-0) recorded in [LICENSES.md](../LICENSES.md), including disclosure of the embedded (but compiled-out via `MA_NO_DECODING`) `dr_wav`/`dr_flac`/`dr_mp3` source.
  - **Still open**: Windows (WASAPI)/macOS (CoreAudio) have never been run against real or virtual hardware — this dev sandbox is Linux-only, so this needs a human on each OS; do not assume the PulseAudio-specific findings above (silent hot-unplug, close-hang) do or don't apply there without testing.
  - **Audio 1b done, Windows/macOS legs unverified**: added `BuildNativeShimWindows` (`cl.exe`) and `BuildNativeShimMacOS` (`clang -dynamiclib`) MSBuild targets alongside the existing Linux one, each following miniaudio's own documented per-platform build requirements (`native/miniaudio.h`'s own "2.1 Windows"/"2.2 macOS"/"2.3 Linux" sections) rather than guessed flags — e.g. Windows needs no include paths or linked libraries at all (WASAPI/WinMM load via `LoadLibrary` at runtime), matching macOS's equivalent claim for CoreAudio/AudioToolbox. Added a `ilammy/msvc-dev-cmd` CI step so `cl.exe` is actually on `PATH` on `windows-latest` (not there by default outside a Developer Command Prompt). Linux path regression-tested (full suite still green); Windows/macOS cannot be tested from this Linux-only sandbox.
  - **CI signal received after the Engine 0-6 push (`IAudioEngine` composition): `windows-latest` fails.** Root cause not yet investigated. **Deliberately deferred, user decision** — not being chased now; revisit once more of the program exists rather than context-switching into a Windows-only native-build debugging detour mid-audio-engine-work. Do not assume this is fixed or investigate it without being asked.
  - Went through 3 rounds of Opus verification against the finished Audio 0-9/1b/6b implementation, each round finding real bugs (not nitpicks) in the previous round's own fixes: a Windows DLL export gap, a device-id conversion gap on macOS/Windows, a context-mutex lifecycle race (TOCTOU against a concurrent teardown), several Dispose-vs-concurrent-use races (fixed with `ReaderWriterLockSlim` on every session/ring type), a `RefreshAsync`/`Dispose` TOCTOU in the enumerator, and a self-join deadlock (a `TaskCompletionSource` completing synchronously on the drain thread, then that same thread trying to `Join()` itself). Stopped at 3 rounds by explicit user decision, not because issues ran out — reasoning: further review of the audio engine *in isolation* has diminishing returns; the more valuable next review is once real cross-component interaction exists to observe (see the planned `IAudioEngine` composition below).
  - **Composing `MiniAudioCaptureSession`/`MiniAudioPlaybackSession` into a real `IAudioEngine`** (`MiniAudioEngine`, in `ScanlineStudio.Core.Audio.MiniAudio` — must live there, not `ScanlineStudio.Core.Audio`: the sessions are `internal` and `AssemblyInfo.cs`'s `InternalsVisibleTo` only grants the MiniAudio test project). Plan verified by an Opus plan-review pass that actually read the current session/interface source rather than a summary (this project's established methodology) — it found 3 real gaps the original breakdown missed, each independently re-confirmed against source before being adopted here (not taken on trust):
    - **Engine 0**: capture overrun/dropped-frame counter through the native shim (`yoniq_audio.c`/`.h` → `NativeAudio.cs` → `MiniAudioCaptureSession`) — closes a stated-but-unfulfilled promise in `IAudioEngine.cs`'s own doc comment ("exposed once piece Audio 5/6 implements the capture/playback paths" — confirmed via grep that no such counter exists anywhere yet). Needed for Engine 5's diagnostics.
    - **Engine 1**: new `AudioDeviceUnavailableException` (`ScanlineStudio.Abstractions.Audio`, per [[01-architecture]]'s Error Handling rule) + `MiniAudioEngine` capture-only lifecycle (`StartCaptureAsync`/`StopCaptureAsync`/`SamplesCaptured`), wrapping `MiniAudioCaptureSession`. Both session constructors block synchronously (confirmed by reading them), so `Start*Async` wraps open/dispose in `Task.Run`; translates every real failure mode (open failure, `MiniAudioContext.Acquire()` context-init failure, `ArgumentException` from an over-long device id, missing native shim). Engine owns the public event and attaches/detaches its own forwarder per session so a restart can't double-invoke. `MiniAudioContext.Acquire()`/`Release()` now happen once per `MiniAudioEngine` instance (ctor/`DisposeAsync`), not per start/stop cycle, avoiding repeated PulseAudio context init/teardown churn. **Self-join deadlock**, found by the plan review and confirmed against `MiniAudioCaptureSession.Dispose()`: its unbounded `_drainThread.Join()` only skips when `Dispose` runs *on* the drain thread — a supported, tested pattern (dispose called from inside `SamplesAvailable`). Wrapping `StopCaptureAsync` in `Task.Run` unconditionally would defeat that guard and hang forever the first time a caller stops capture from inside its own `SamplesCaptured` handler. Fixed by having the engine record the managed thread id its forwarder is currently running on, and calling the session's `Dispose()` inline on that thread when it matches (`Task.Run` otherwise) — plus an engine-level test mirroring the session-level `Dispose_CalledFromWithinSamplesAvailableCallback_DoesNotSelfJoinDeadlock`.
    - **Engine 2**: playback lifecycle (`StartPlaybackAsync`/`StopPlaybackAsync`/`EnqueuePlaybackSamples`). **`PendingFrames` under-delivers on the drain contract**, also found by the plan review and confirmed by reading the native body (`yoniq_audio_playback_session_pending_frames` is a one-line `yoniq_audio_ring_available_read` — shim-ring occupancy only): it reaches 0 before miniaudio's device buffer and the PulseAudio server's own queue have actually finished playing, so `DrainAsync` alone can truncate the tail of a real transmission, which is exactly what `IAudioEngine`'s "every sample actually played out" contract exists to prevent. Fixed with a short fixed tail-margin delay after `PendingFrames` reaches 0, documented as an empirical (not exact) safety margin and verified with a marker-tone-at-the-end test against a real virtual sink/monitor rather than trusted blind. `DrainAsync`'s own timeout is silent (returns normally either way) — `StopPlaybackAsync` re-checks `PendingFrames` afterward and throws rather than silently truncating. `EnqueuePlaybackSamples` throws `InvalidOperationException` if playback was never started (returning 0 would make a contract-compliant retry loop spin forever).
    - **Engine 3**: lifecycle-transition correctness under concurrency — double-Start, Stop-without-Start (idempotent, matching the session Dispose convention), concurrent Start/Stop races. `SemaphoreSlim(1,1)` per lifecycle (capture, playback — independent), needed because both session constructors/`Dispose` block and must be awaited via `Task.Run` from inside the lock. Session-holding fields are `volatile` (the two synchronous interface members — `EnqueuePlaybackSamples` and the `SamplesCaptured` forwarder — can't take an async semaphore, so they snapshot the field and catch `ObjectDisposedException` instead). Semaphores are never disposed, same reasoning as every session/ring class's `ReaderWriterLockSlim` (a second call's `WaitAsync` must not throw on the semaphore object itself before reaching the idempotency check). `DisposeAsync` never holds both semaphores at once (stop capture then release, drain-and-stop playback then release), so there's no ABBA case to order against. Double-Start throws `InvalidOperationException` (silently ignoring the second device would be the exact silent-failure CLAUDE.md/[[01-architecture]] forbid).
    - **Engine 4**: `IAsyncDisposable.DisposeAsync` — stop capture and drain-then-stop playback if active, itself idempotent.
    - **Engine 5a**: engine-level real-audio integrity test — a short deterministic tone (seconds, not the ~2-minute Martin M1 fixture) through a real virtual sink/monitor, asserting continuity/peak/no dropouts, clean overrun/underrun counters, and no `SamplesCaptured` firing after `StopCaptureAsync` completes.
    - **Engine 5b**: the real payoff test, split from the original single "Engine 5" once the plan review flagged it as too slow/flaky/non-localizing to combine — an actual SSTV encode → `MiniAudioEngine.EnqueuePlaybackSamples` → real virtual cable → `MiniAudioEngine` capture → `SamplesCaptured` → real SSTV decode → image-tolerance comparison, the first time the real audio stack and the real DSP stack run together rather than through `FakeAudioEngine`. Shortest real mode (R24, ~24s) instead of Martin M1 (~114s); staged asserts (VIS detected → correct line count → image tolerance) so a failure localizes; framed as an integration smoke test, not the primary correctness gate for either stack — `AnalogFmSstvDecoder` has no slant/clock-drift correction yet, so accumulated sample-clock drift between two independent miniaudio devices is a known, accepted source of flakiness here.
    - **Engine 6**: DI registration in `ScanlineStudio.Host/Program.cs` — expanded once the plan review checked what "just add two `AddSingleton` lines" actually required: `ScanlineStudio.Host.csproj` had no `ProjectReference` to `ScanlineStudio.Core.Audio.MiniAudio` and none of the three per-OS native-shim copy targets the test project needed for the same documented reason (.NET doesn't propagate a `ProjectReference`'s native build artifacts) — both confirmed missing and added. Registered by type (`AddSingleton<IAudioEngine, MiniAudioEngine>()`), not an eagerly-constructed instance, so native context init doesn't run at process start on a machine with no audio server. `IAudioEngine` is `IAsyncDisposable`-only with no `IDisposable`, and `Program.cs` never disposed the host at all (confirmed) — added an explicit `DisposeAsync` on the classic-desktop lifetime's exit. Resolve-construct-dispose smoke test. Nothing resolves `IAudioEngine` from UI yet (`ScanlineStudio.Application` has zero real source files today) — `ScanlineStudio.UI/App.axaml.cs`'s existing `App.Services` static is a pre-existing service-locator pattern [[01-architecture]] itself forbids for new code, and audio must not be wired through it.
- [[06-sstv-dsp]]: encode/decode round-trip passing for the core mode set, against `FakeAudioEngine` and real audio; sample-clock calibration (slant correction) implemented. FSK/CW station ID **explicitly deferred, not v1 scope** (user decision, overriding an earlier "regulatory requirement" framing here — see [[06-sstv-dsp]]'s Station ID section for the full breakdown and why: CW ID specifically is real legacy functionality but not required for the program to work as an SSTV encoder/decoder). The one non-station-ID piece of that area, the post-image footer tone, is done — see the mode-table entry below.
  - Mode-by-mode sequencing, each verified individually before moving to the next (explicit user instruction, not a shortcut). **43 of 43 modes done — every mode in the legacy table now has an entry** (`SstvModeRegistry`, all cross-checked against `CSSTVSET::GetTiming`, all read from the actual TX line-generator functions in `Main.cpp` per CLAUDE.md's TX/RX-split and no-assumptions rules — several near-misses caught this way: Scottie's real sync position, Robot 72 vs. Robot 36, MP vs. MR, R24's VIS-code parity bit, RM8/RM12's genuinely-new monochrome shape, and SC2's misleading-looking TX source, all below): Martin M1/M2, Scottie S1/S2/DX, Robot 36/72, AVT, MR73–175, ML180–320, MP73–175, the whole PD-series, Pasokon P3/P5/P7, MN73/110/140, MC110/140/180, R24, RM8/RM12, SC2-180/120/60. Five scanline-codec families exist now (`ScanlineCodecFactory`): `RgbSequential`, `YCbCrRobot` (alternating chroma), `YCbCrSequential` (Robot 72's non-alternating Y+R-Y+B-Y, now also R24), `YCbCrLinePaired` (MP/PD/MN's two-luma-lines-per-chroma-pair, added `RowsPerTransmissionLine` to `IScanlineEncoder`/`Decoder` to support it), `MonoAveragedPaired` (RM8/RM12's monochrome, row-averaging shape, added for this pair). A two-stage "extended VIS" mechanism (escape byte 0x23 + a second raw byte, see `VisHeader`) was added for the MR/ML/MP families.
  - **RM8/RM12 done**, `Main.cpp`'s `LineRM` — a genuinely new shape, not a variant of anything implemented so far: no chroma at all (`GetRY`'s R-Y/B-Y outputs are computed but discarded), and each transmission averages *two* consecutive source rows' luminance into one scanned value, confirmed via the RX decode switch (`Main.cpp`, `case smRM8: case smRM12:`, distinct from R24/R72/MR/ML's shared block) which writes that single decoded value into both of the two output rows it addresses. Needed a new `ColorEncoding.MonoAveragedPaired` family (`MonoAveragedPairedScanlineEncoder`/`Decoder`). While tracing `LineRM`'s own `mp->m_wLine++` alongside the outer TX dispatch loop's own increment, caught and corrected a mistake in the earlier R24 entry: that same double-increment structure exists in `LineR24` too, meaning the TX loop for R24 (and RM8/RM12) only runs half as many times as it looks like at a glance, and reads rows accordingly — see `R24`'s updated doc comment in `SstvModeRegistry`. **Deliberately not ported**: legacy's RM8/RM12 RX applies its own extra gain correction (`d *= 256.0/(256.0-32.0)`) on top of a raw calibration pipeline (`GetPictureLevel`/`GetPixelLevel`) that this port doesn't replicate for *any* mode — applying that specific correction inside this port's much simpler linear frequency-to-pixel mapping would be a mismatched, uninterpretable number, not a faithful port; see `CreateMonoAveragedMode`'s doc comment. Also required a test-methodology fix, not a codec fix: the shared full-color gradient fixture used by every other mode's round-trip test isn't fair to a genuinely monochrome mode (R and G vary independently in that fixture; a real monochrome decode can only ever output R=G=B, so per-channel delta was ~42 on the first attempt) — added a dedicated grayscale fixture and test (`EncodeThenDecode_RoundTripsWithinTolerance_MonoFamily`) instead of loosening the tolerance or changing the encoder/decoder.
  - **MN73/110/140 and MC110/140/180 done**, both `Main.cpp`'s `LineMN`/`LineMC`. Both use a "narrow" frequency range (`NARROW_SYNC`=1900Hz, `NARROW_LOW`=2044Hz, `NARROW_HIGH`=2300Hz, via `ColorToFreqNarrow` in source) instead of the normal 1500–2300Hz. MN is line-paired (same Y-RY-BY-Y2 shape as MP, just narrow-range, `YCbCrLinePaired`); MC is plain sequential R/G/B (`RgbSequential`). **VIS-code search result, confirmed not assumed**: neither has a standard or extended VIS code anywhere in `sstv.cpp`'s VIS-decode switch (both stages searched directly) — legacy instead sends a distinct, small, fixed FSK mode-announce packet in place of a VIS header (`Main.cpp:7395-7424`: `[0x2d][0x15][modeCode][modeCode^0x15]`, 6-bit LSB-first via `WriteFSK`, `sstv.cpp:2942`). This is *not* the general station-ID FSK subsystem (the separate 0x2a-prefixed callsign packet, still unported — see the FSK/CW station ID item above) — it's a small, self-contained sub-protocol, ported as `VisHeader.GenerateNarrowModeSegments`/`SstvModeDefinition.NarrowModeCode`, with matching decode-side discrimination logic added to `AnalogFmSstvDecoder` (`TryDecodeHeader` distinguishes a normal-VIS-shaped preamble from a narrow-packet-shaped one by sampling a window right after the shared 300ms leader, since the two diverge immediately after that point). User's explicit instruction followed here: "do what YONIQ does," not invent a fake VIS code just because the existing mechanism was convenient. Verified with a dedicated `NarrowModeHeader_IsDetected_ForMnFamily` test, decoupled from the frequency-range bug below.
  - **R24 done**, `Main.cpp`'s `LineR24` — same Y/R-Y/B-Y shape as Robot 72 (`YCbCrSequential`, no new codec needed), confirmed via the RX decode switch grouping them together rather than by name resemblance. Caught a real bug while wiring it up: legacy's VIS-decode switch matches the *full* received byte (7 data bits + even-parity bit as the MSB) — e.g. R36's case is `0x88`, but this port's `Robot36.VisCode` correctly stores only `8` (the 7 data bits `VisHeader` actually transmits/reconstructs). R24's case is `0x84`; it was first entered here as `0x84` (132) directly, which doesn't fit in `VisHeader`'s 7-bit code space and silently transmitted as if it were `4` while the registry still compared against `132` — `FindByVisCode` never matched, so the round-trip test's `ModeDetected` never fired. Fixed to `4` (`0x84 & 0x7F`); a good reminder that "read the source" must include reading *how the port's own field already represents that value* for other modes, not just the literal hex byte in the switch statement. Also surfaced (and explicitly did **not** replicate) a legacy display-only quirk, corrected once more precisely while reading `LineRM` for RM8/RM12 right after: `LineR24` does its own `mp->m_wLine++` at its end, *in addition to* the outer TX dispatch loop's own unconditional `mp->m_wLine++` after every mode's switch-case — so each outer iteration actually advances `m_wLine` by 2, the loop (bounded by `m_TL`=`hp`=240) only runs 120 times, and `LineR24` only ever reads the *even* source rows (0,2,4,...,238); odd rows are never touched. This matches `CSSTVSET::SetSampFreq`'s (`sstv.cpp:655`) decode-side `m_L=120` exactly, and RX's row-address logic (`R=y*2`, gated on `y<m_L`) duplicates each decoded even-row value into two output rows to fill a 240-row display canvas. So R24 is a genuine reduced-vertical-resolution mode (120 real rows, nearest-neighbor doubled for display) — not the "redundant transmission, second half discarded" framing an earlier draft of this note incorrectly used. This port models the 120 real rows directly (`ImageHeight: 120`); the display-side doubling is a presentation-layer detail with no round-trip-testable effect — see `R24`'s doc comment in `SstvModeRegistry` for the full corrected reasoning.
  - **`LuminanceMinHz`/`MaxHz` hardcoding bug — fixed**, once every mode above was added (the user-directed sequencing this was deliberately held for). `YCbCrLinePairedScanlineEncoder`/`Decoder`, `YCbCrSequentialScanlineEncoder`/`Decoder`, and `RobotScanlineEncoder`/`Decoder` all used to hardcode the 1500/2300 frequency range literally instead of reading `mode.LuminanceMinHz`/`LuminanceMaxHz` — `RgbSequentialScanlineEncoder`/`Decoder` already did this correctly, and was the template for the fix. `RobotScanlineEncoder`/`Decoder`'s `ColorToFreq`/`DecodePixels` helpers were `static` with no access to the mode, so the fix also threaded `SstvModeDefinition` (`RobotScanlineEncoder`) / kept it (`RobotScanlineDecoder`, which already took `mode` but ignored it for this) through those signatures. Verified, not just applied: moved MN73/110/140 out of the old duration-only `NarrowFamilyLineDurationsOnly` table and into the shared `Modes` table in `SstvRoundTripTests` — their full pixel round-trip (previously blocked; header/mode-detection was already correct and separately tested) now passes. Full suite: 87/87 in `ScanlineStudio.Core.Sstv.Tests`, all green.
  - **SC2-180/120/60 done, the last mode family**, `Main.cpp`'s `LineSC2180`. Looked like it might need a new codec at first glance — the source writes frequencies like `ColorToFreq(cp->b.r)+0x1000`, `+0x2000` for G, `+0x3000` for B — but tracing `CSSTVMOD::Do` (`sstv.cpp:2868`) showed the actual VCO frequency is `(f & 0x0fff) - 1100`: the upper nibble is masked off entirely before use. That nibble is a *separate* per-channel TX-gain tag (`sstv.cpp:2880`, `switch(f & 0xf000){ case 0x1000: d *= m_outgainR; ...}`), only consulted when an optional independent-R/G/B TX-gain-trim feature (`m_VariOut`) is enabled — zero effect on the transmitted frequency/waveform either way, and this port doesn't model per-channel TX gain trim for any mode, so correctly omitted, not silently lost. RX confirms the same reading: SC2-180/120/60 aren't named in the RX per-pixel decode switch's specific-mode cases at all (`Main.cpp:4200-4452`) — they fall through to the generic `default:` branch other unlisted plain-RGB modes use, whose non-MRT write order is phase1→R, phase2→G, phase3→B, matching TX's scan order exactly. So this is plain `RgbSequential` (no new codec needed) — sync=`S`ms@1200Hz, porch=0.5ms@1500Hz, then R/G/B each `tw`ms; totals (711.0437/475.52248/240.3846ms) match `GetTiming` exactly for all three. VIS codes parity-stripped as established (0xb7→55, 0x3f→63, 0xbb→59) — legacy's own source comments (`// $37`, `// $3f`, `// $3b`) spell out the stripped values directly, a nice confirmation of the parity-stripping convention itself. All 6 new/updated round-trip and duration tests pass; 84/84 total in `ScanlineStudio.Core.Sstv.Tests`.
  - **Correction**: a prior note here claimed legacy has a per-mode-speed adaptive PLL "demod profile" system named `PRODEM`, to be ported so Martin M2/Scottie S2 could drop the 44100Hz stand-in. That name does not exist anywhere in `yoniq-old/YONIQ-main/` (checked with a case-insensitive grep across the whole tree) — it was an unverified guess written down as if it were confirmed source fact, exactly the failure CLAUDE.md's no-assumptions rule exists to catch. Directly verified instead: `CPLL`'s only tuning toggle is `SetWidth(fNarrow)`, switching between Normal (1500-2300Hz) and Narrow (`NARROW_LOW`=2044/`NARROW_HIGH`=2300, `sstv.h`) — exactly the MN/MC case already handled by `LuminanceMinHz`/`MaxHz`. Legacy decodes Martin M2 and Scottie S2 with the *same* fixed loopFC=1500/outFC=900 `CPLL` tuning, at the *same* 11025Hz default (`Main.cpp:577`), as every other mode — there is no per-speed adaptation to port. See `SstvModeRegistry`'s doc comment for the corrected version.
  - **M2/S2 gap investigated and root-caused** (no code change yet — this confirms what the real fix has to be before attempting it). Exhaustively checked whether legacy has *any* per-mode-adaptive discriminator setting: `CSSTVDEM::m_Type` picks between three demod algorithms (`CPLL`/PLL, `CFQC`/zero-crossing, `CHILL`/Hilbert, `sstv.cpp:2255-2265`) and `m_bpf` picks a bandpass filter width (Wide/Narrow/VeryNarrow, `DEMBPF` ini key) — both are **global user settings** (`Main.cpp:1855`), not chosen per mode. The only mode-driven toggle anywhere in `CSSTVDEM`/`CPLL`/`CFQC` is the Normal/Narrow frequency-range split already covered by `LuminanceMinHz`/`MaxHz`. So legacy really does decode every mode, including Martin M2 and Scottie S2, through identical fixed-tuning code at its 11025Hz default — confirming there is nothing left to "port" for this.
    - Measured the actual failure directly instead of theorizing: ran this port's own round-trip suite at 11025Hz (temporarily, then reverted — not a real change) across every RGB/YCbCr-sequential mode and logged each mode's minimum per-pixel scan duration converted to samples-at-11025Hz. Result was a clean, near-monotonic correlation, not the M2/S2-only story the old note assumed: **every mode with fewer than ~4 samples/pixel at 11025Hz fails** the 10.0-average-per-channel-delta tolerance (Robot36 1.52 samp/px → 21.3 delta; ML180 1.52 → 24.5; Robot72/MR73/ML280 ~2.4 → 15-20; Martin M2 2.52 → 14.3; ML320 2.73 → 15.9; Scottie S2 3.03 → 10.9; MR115 3.79 → 10.2), while every mode at ≥4.6 samples/px passes comfortably (Martin M1 5.05, Scottie S1 4.76, MR140 4.63, MR175 5.81, Scottie DX 11.91). So the 44100Hz stand-in isn't specifically an "M2/S2 problem" — it's masking a general resolution floor around ~4 samples/pixel that affects roughly a third of the mode table (confirmed list above).
    - Root cause, given legacy has no adaptive tuning to point to instead: this port's own `AnalogFmSstvDecoder`/`AverageFrequencyInWindow` reconstructs each pixel by block-averaging the continuously-demodulated frequency stream over a fixed nominal-timing window (with a discard-first-quarter settling margin) — a fundamentally coarser sampling strategy than legacy's real per-line sync-locked pixel timing, and it simply needs more raw samples per pixel to average out PLL/IIR settling noise than legacy's approach does. This is not a new, separate bug to patch in isolation — it's a direct symptom of the already-flagged, still-unported `CSSTVDEM` sync-search/AFC pipeline (see `AnalogFmSstvDecoder`'s own doc comment, and [[06-sstv-dsp]]): replacing block-averaging with real per-line sync-locked sampling is expected to fix this class of failure at 11025Hz without needing any new per-mode tuning table. Flagged as the next real piece of DSP work, not started without explicit direction, since it's a substantial addition (sync search + AFC state machine), not a small patch.
    - **Resolved, stale as of the entries below.** This bullet's "still-unported CSSTVDEM sync-search/AFC pipeline" framing is superseded — see the `CSSTVDEM investigation, reframed` entry immediately below: AFC (`AfcTracker`) and Auto Slant (`SlantTracker`) are both ported, and the actual root cause of the residual gap turned out to be a missing per-image sync-anchor re-correction (legacy's `SyncSSTV`/`m_wBgn` fold-and-argmax), not a missing sync-search/AFC state machine — ported as "piece 8" (`SyncAnchorCorrector`, commits `23b025a`→`8f87146`), measured against real golden-vector legacy audio: Robot 36 68.06→9.95, Martin M1 11.78→2.40. Left in place rather than deleted so the reasoning trail stays intact (CLAUDE.md's own precedent for this file).
  - **Independent Opus-driven adversarial verification pass, then fixes** (user-requested second opinion, specifically scoped to cross-check every mode's TX *and* RX against the actual legacy source rather than just review code quality — the right scope, since a generic review can't catch "self-consistent but wrong," the exact failure mode that bit Scottie earlier in this phase). 14 mode families / 43 modes examined; 11 fully confirmed correct (including every specific thing this phase already traced carefully: Scottie's mid-line sync *placement*, R24's double-increment, RM8/RM12's monochrome averaging, SC2's gain-tag red herring, MN/MC's FSK packet). 3 families had real, previously-undetected bugs, all independently re-verified against source (not just taken on the agent's word) before fixing:
    - **Chroma frequencies shifted ~400Hz low — highest severity, 6 families / 24 modes** (Robot36/72, R24, MR/ML, MP, PD, MN). `YCbCr.FromRgb` omitted legacy `GetRY`'s +128 R-Y/B-Y offset (`ComLib.cpp:3663-3665`); `YCbCr.ToRgb` was symmetrically missing the same offset, so round-trip tests passed while both sides disagreed with real legacy TX/RX — the effect: a saturated color could push chroma to ~1150Hz, colliding with the 1200Hz sync tone. The port's old doc comment called this "presumably absorbed somewhere in legacy's RX calibration layer... a real gap in understanding" — that premise was wrong, not just incomplete: independently confirmed the actual mechanism is `Main.cpp:4331` (luminance path adds +128 back) vs `4341`/`4350`/`4396`/`4405` (chroma path doesn't), reconciled by the demod calibration defaults `m_DemOff=0`, `m_DemWhite=m_DemBlack=128/16384` (`Main.cpp:875-877`) already zero-centering chroma at 1900Hz before `YCtoRGB` sees it. Fixed: `FromRgb` now adds +128 (matching `GetRY` exactly); `ToRgb` now subtracts 128 before its reconstruction matrix (folding in what legacy's separate `GetPixelLevel` calibration layer would otherwise do, since this port doesn't model that layer). `MonoAveragedPairedScanlineDecoder`'s "no chroma" placeholder updated from `ToRgb(y, 0, 0)` to `ToRgb(y, 128, 128)` to match the new centered convention. New targeted tests (`YCbCrTests.cs`, not just round-trip) assert `FromRgb(128,128,128)` produces chroma of exactly 128, since round-trip alone can't catch a symmetric offset bug.
    - **Scottie S1/S2/DX missing a post-VIS pulse.** Legacy emits an extra 9ms/1200Hz pulse right after the VIS header for Scottie only (`Main.cpp:7576-7578`), because legacy's RX VIS-decode state machine (`sstv.cpp:2127-2153`) expects 1200Hz still present ~30ms after the last VIS bit, and Scottie's line generator starts directly on a 1500Hz separator with no sync of its own — confirmed by reading both cited functions directly. Fixed: `VisHeader.ScottiePostVisPulseFrequencyHz`/`DurationMs` + `AnalogFmSstvEncoder` emits it for Scottie only; `AnalogFmSstvDecoder.TryDecodeVisHeader` skips it before line data. New test (`VisHeaderTests.ScottiePostVisPulse_MatchesLegacyConstants`) pins the constants.
    - **AVT missing its entire preamble.** Legacy sends the VIS block 3x for AVT specifically (`Main.cpp:7429`), then a ~5.3s sync/AFC training sequence (`Main.cpp:7563-7575`: 32 blocks of a 1900Hz marker + 16 bits at 1600/2200Hz encoding a shifting counter seeded at `0x5fa0`) before any line data — legacy's own RX explicitly budgets for this exact length (`sstv.cpp:2140`). The port previously sent a single normal VIS header and nothing else. Fixed: `VisHeader.GenerateAvtSegments` ports the training sequence's bit-shift arithmetic exactly; the encoder dispatches to it for AVT; the decoder identifies the mode from the first VIS repeat then skips `AvtExtraHeaderDurationMs` (two more VIS repeats + the training sequence) before line data. New test (`VisHeaderTests.GenerateAvtSegments_TotalHeaderDuration_MatchesLegacy`) pins the total header duration to 8042.24754375ms, independently re-derived from the cited legacy constants.
    - **RM12's VIS byte, medium severity.** Legacy's real byte for RM12 is `0x86` (`sstv.cpp:1997`), but its parity bit doesn't satisfy even parity computed from its 7 data bits (6 = `0b0000110`, already even → computed parity gives `0x06`) — independently re-checked every other normal VIS byte in the legacy table bit-by-bit, and all of them do follow even parity, so this is a one-off quirk in legacy's own assigned byte, not a bug in this port's original parity-stripping logic (which the earlier doc comment incorrectly over-generalized as "both RM8 and RM12 bake even parity into their MSB"). Fixed: `VisHeader.GenerateSegments` gained an optional `forcedParityBit` parameter; RM12 uses it (`VisHeader.Rm12ForcedParityBit = 1`) to transmit the real `0x86` instead of a computed `0x06`. Decode is unaffected either way, since `DecodeVisCode` never reads the parity bit for any mode. New test (`VisHeaderTests.GenerateSegments_Rm12ForcedParity_TransmitsLegacyByte0x86`) decodes the emitted bit sequence back to a byte and asserts it.
    - **Minor, also fixed**: `RgbSequentialScanlineEncoder`/`Decoder` divided by 255 instead of 256 (legacy's `ColorToFreq`/its inverse both use 256, matching every other codec family here already) — up to ~4Hz off at full scale, affecting Martin/Scottie/AVT/Pasokon/SC2/MC.
    - Added `[assembly: InternalsVisibleTo("ScanlineStudio.Core.Sstv.Tests")]` (`AssemblyInfo.cs`) so these fixes could get targeted reference-value unit tests against `VisHeader`/`YCbCr` directly, rather than only through the public round-trip pipeline — necessary because round-trip self-consistency is exactly what let three of these four bugs go undetected in the first place. Full suite after fixes: 98/98 in `ScanlineStudio.Core.Sstv.Tests` (87 previous + 11 new targeted tests), solution-wide build clean.
    - **Doc-only cleanup, user-directed** ("we follow the legacy code, not public documents... if the docs say grass is green and the legacy says it's red, the grass is red"): the first review's Finding C flagged two stale/false doc comments that had never been actioned. `ToneSelectorSegment`'s comment claimed Robot's 1500/2300Hz tone-selector frequencies were "standards-informed, not traced from legacy source... no source Hz constant to read directly" — false; `Main.cpp:6568` has the literal value (`mp->Write(short(mp->m_wLine & 1 ? 2300 : 1500), 4.5)`), and this port's earlier author checked the RX decode path instead of the TX line-generator function, the exact anti-pattern CLAUDE.md's TX/RX-split rule warns about. `SstvModeDefinition`'s class comment claimed its constants were "taken from public SSTV protocol documentation, not from the legacy MMSSTV/YONIQ binary directly" — false, and directly contradicted `SstvModeRegistry`'s own accurate claim on the same data; corrected to state plainly that every constant is read from legacy source, with the real remaining caveat (no golden-vector cross-check against actual captured legacy binary *output*, as opposed to its source code) stated precisely instead of overstated. Also fixed the citation slips independent verification found: `sstv.cpp:2853`→`2868` and `2879`→`2880` (SC2's TX-gain-tag mask/switch), `Main.cpp:6554`→`6555` (R24's own `m_wLine++`), `CSSTVDEM::SetSampFreq`→`CSSTVSET::SetSampFreq` (R24's decode-side `m_L=120`) — all re-verified against source directly before fixing (confirmed exact line numbers via `awk`/`LC_ALL=C`, not just copied from the review's report).
    - **Second independent Opus verification pass, then one more fix.** Re-checked all 5 fixes against legacy source directly (not the fix's own doc comments, not just "tests still pass"). 4 of 5 confirmed fully correct outright: the chroma +128 fix matches `GetRY`/`YCtoRGB` exactly and all four `ToRgb` call sites are in the right domain; the Scottie pulse is emitted for exactly S1/S2/DX in the right position; the AVT preamble's bit-shift arithmetic is byte-identical to legacy's and the total duration matches exactly; RM12's parity anomaly was independently re-derived (checked set-bit counts across all 23 other normal VIS bytes bit by bit — 0x86 is confirmed the sole exception) and is now transmitted literally; the /256 divisor fix is symmetric and correct. But it caught a **new latent bug introduced by the AVT/Scottie fix itself**: `AnalogFmSstvDecoder.TryDecodeVisHeader` advanced `_consumedSamples` past the base VIS header as soon as that much was available, then separately checked whether the mode-dependent "extra" material (AVT's training tail / Scottie's pulse) had arrived — so a caller pushing samples in chunks (not all at once) could see the base-header advance commit, then hit "not enough samples yet" for the extra part and return with `_mode` still null, causing the next chunk to restart header detection from the *middle* of the remaining preamble. Invisible to every existing test, since all of them push the entire waveform in one `PushSamples` call. Fixed by making the whole header (base VIS + any AVT/Scottie extra) a single atomic commit — one availability check covering the full amount, one assignment to `_consumedSamples`/`_mode`, no partial state exposed in between. Verified this was a real, reproducible bug (not a false positive) by temporarily reintroducing the two-stage version, confirming the new `ModeWithMultiPartHeader_IsStillDetected_WhenSamplesArriveInChunks` test fails against it, then restoring the fix and confirming it passes again. Full suite after this fix: 100/100 in `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean.
  - **Robot36's tone-selector ambiguity fallback — fixed** (a low-severity finding from the first Opus review that had never been actioned). Legacy doesn't just threshold the tone-selector reading against the 1900Hz midpoint: `Main.cpp:4289-4296` only decides decisively when the reading is at least ~200Hz from center (derived from `GetPixelLevel`'s `d>=64`/`d<-64` check and its `m_DemWhite=m_DemBlack=128/16384` scale factor — 200Hz isn't a literal source constant, it's this derived-and-flagged-as-such number); inside that band it *toggles* from the previous line's selection instead of picking a side, since Robot36 alternates R-Y/B-Y every line and a toggle is a better noise-robust guess than either fixed default. This port previously just thresholded, with no ambiguity handling at all. Fixed in `RobotScanlineDecoder` (`AmbiguityHalfWidthHz` + `_lastSelectionIsEvenLine`, defaulting to legacy's own `m_DSEL=0` default). Round-trip tests can't exercise this (the encoder always transmits a decisive tone), so verified with a dedicated `RobotScanlineDecoderTests` test that drives the decoder directly with a scripted ambiguous reading — confirmed meaningful by temporarily reverting to the old threshold-only logic and checking the new test actually fails against it before restoring the fix.
  - **Post-image footer tone — done.** Investigated the FSK/CW station-ID item next up per the roadmap, found it was much larger than a "smaller item" (TX + RX + a wholly new CW/Morse subsystem + settings plumbing — see [[06-sstv-dsp]]'s Station ID section for the full breakdown) and confirmed with the user before proceeding: CW ID specifically is real legacy functionality but not required for the program to work as an SSTV encoder/decoder, so the whole station-ID feature is deferred, written up as a scoped task in [[06-sstv-dsp]] rather than silently dropped, to pick up later. The one piece of that area that *isn't* station-ID-specific and was small enough to do now: legacy always appends a footer immediately after the last image line whether or not FSK ID is configured (`Main.cpp:6994-7013`) — a trailing-carrier hold (`min(one line's duration, 500ms)`) at 1500Hz followed by an alternating 1900/1500/1900/1500Hz sequence (4×100ms) for normal modes, or just the trailing carrier alone at 1900Hz for narrow (MN/MC) modes. Implemented as `AnalogFmSstvEncoder.GenerateFooterSegments`, built specifically so it won't need rework once FSK/CW ID exists (the still-deferred "FSK ID configured" branch is a separate, additive case, not a replacement of this one). `sys.m_VOX` (also part of the branch condition) isn't modeled anywhere in this port yet (no radio/PTT layer exists) — defaults to legacy's own off-by-default behavior, flagged rather than silently baked in as permanent. Verified with dedicated tests (`AnalogFmSstvEncoderFooterTests`) checking the exact segment sequence and the 500ms cap for both branches, not just full-suite round-trip pass-through. Full suite after both fixes: 104/104 in `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean.
  - **`CSSTVDEM` investigation, reframed then partially fixed.** Went to start the "port the real sync-search/AFC pipeline" task above and found the premise was wrong before writing any code: legacy's per-pixel addressing (`Main.cpp:4144-4148`, `y = n/m_TW`, `ps = fmod(n, m_TW)`) is the *same* nominal-timing arithmetic this port already uses — there's no rediscovered per-line sync point during image reception. The real difference is narrower: legacy processes the recorded audio one raw sample at a time and, for each pixel, keeps whichever single sample is first at that pixel's computed index (`GetPictureLevel`/`GetPixelLevel` both simply dereference `*ip` — no window, no average). This port instead block-averaged the whole per-pixel dwell window (discarding the first quarter as a settling margin) — an invented technique, not traced from source. User's direction: "legacy is truth, follow that." Fixed: `IScanlineDecoder.DecodeLine`'s callback (renamed `averageFrequencyInWindow`→`sampleFrequencyAt` across the interface, `AnalogFmSstvDecoder`, and all 5 scanline decoders — an honest-naming fix, not just internals, since the old name was actively wrong about what it now does) reads a single sample at the pixel's window-start instead of averaging. Also caught and fixed in the same pass, once the "single sample" pattern existed: Robot's tone-selector re-decides on *every* sample of its window with no "first wins" gate (`Main.cpp:4286-4297`), so its real effective reading is the window's *last* sample, not an average and not the first sample either — `RobotScanlineDecoder` now reads `(endSample-1, endSample)` for that one case.
    - **Measured, not assumed, how much this actually helps**: re-ran the same 11025Hz experiment from the earlier investigation above. Real, consistent improvement across nearly every previously-failing mode (Robot36 21.3→13.4 delta, Robot72 15.2→13.4, MR73 20.2→16.1, ML180 24.5→19.6, ML240 18.8→15.1, ML280 18.0→14.6, ML320 15.9→12.9, Scottie S2 10.9→10.2), and MR115 now crosses the passing threshold outright (10.17→passes). One mode (Martin M2) moved slightly the wrong way (14.28→14.49, within noise). **This confirms the fix is real and correctly targeted — legacy's actual mechanism, not a guess — but it does not fully close the gap alone**: several modes still fail the 10.0-delta tolerance at 11025Hz. Full closure likely needs either the AFC drift-tracking loop (`SyncFreq`/`m_AFCDiff`, still unported) or a closer look at whether this port's `PllFmDemodulator` settles as fast as legacy's exact filter chain at very short pixel-dwell times — kept as further, separately-scoped follow-up work, not blindly attempted in the same pass. The 44100Hz stand-in remains in the test suite for now; it is a pragmatic Phase 1 accommodation, not a hidden correctness bug, given this finding.
    - Existing full suite (44100Hz) unaffected: 104/104 in `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean, since single-sample-vs-window-average makes no measurable difference once there are enough samples per pixel to begin with.
    - The genuinely large piece originally feared for this task — the VIS/preamble lock state machine (`m_SyncMode` 0-8/256/512-513, `sstv.cpp:2085-2270`) — remains unstarted and is now understood to be a separate, still-large concern from the pixel-readout fix above (it governs *when* image reception starts, not how pixels are sampled once it has). Not started without further explicit direction, consistent with how every other large-scope item this phase has been handled.
  - **AFC (`CSSTVDEM::SyncFreq`) — ported.** User's explicit direction: "legacy is truth, verify before accepting." Read `SyncFreq` (`sstv.cpp:2339-2376`) fully: it feeds a *separate* zero-crossing frequency discriminator (`CFQC`, `sstv.cpp:347-489` — entirely distinct from the PLL-based main demodulator this port already has) with the same raw audio sample the PLL sees, watches for a run of consecutive samples (`m_AFCB`..`m_AFCE`, mode-dependent: 1.0/2.0ms for Martin M1/M2+SC2+MC, 1.5/3.0ms for everyone else, `sstv.cpp:1162-1177`) that plausibly reads as the mode's sync tone (1200Hz normal / 1900Hz `NARROW_SYNC` for MN/MC, each with its own acceptance band and a 15-lock-event long-term moving average, `CSmooz`), and once locked, computes a persistent correction (`m_AFCDiff`) added to every subsequently demodulated sample. Explicitly excluded for AVT in legacy (`sstv.cpp:2258`'s `mode != smAVT` guard) — ported the same exclusion. Traced the internal x16384/BWH-scaled arithmetic all the way through and confirmed (not assumed) that, since `IirFilter.Process` is a purely linear biquad cascade, working entirely in real Hz throughout is mathematically identical to legacy's scaled representation — a representational simplification, not a numeric approximation.
    - New files: `MovingAverage` (`CSmooz` port), `ZeroCrossingFrequencyCounter` (`CFQC` port), `AfcTracker` (`SyncFreq`'s state machine). Wired into `AnalogFmSstvDecoder`: since this decoder demodulates the whole sample buffer upfront before mode detection can know whether/how AFC applies (unlike legacy's single real-time pass), AFC runs as a second pass over the *raw* samples (now also retained, `_rawSamples`) once the mode is known, correcting the already-demodulated buffer in place — a deferred, not approximated, adaptation: the zero-crossing counter and AFC state machine still see the exact same raw samples in the exact same order legacy's own would have, only the wall-clock timing of when that processing happens differs.
    - **Verified against legacy's own formulas, not just "tests pass."** A driftless same-process round-trip has no real carrier offset for AFC to correct, so `SstvRoundTripTests` passing/failing can't validate this feature either way — confirmed empirically: re-ran the 11025Hz experiment with AFC wired in and got the same pass/fail pattern as before it (9 failures, same modes, deltas within ~0.3 of their pre-AFC values), exactly the expected outcome for a feature that corrects drift no synthetic test has. Instead added `AfcTests` — direct, source-derived tests feeding synthetic tones (with and without a deliberate frequency offset) straight into `ZeroCrossingFrequencyCounter`/`AfcTracker`, checking the locked correction against hand-derived expected values to tight (0.01Hz) tolerance. Caught a real test-authoring mistake this way, not an implementation bug: `SyncFreq`'s `d -= 128` (a small fixed calibration nudge, ~3.125Hz for normal-bandwidth modes) means even a perfectly on-frequency reading locks a small nonzero correction, not exactly 0 — the first draft of these tests assumed otherwise and failed against the (correct) implementation until the expected values were re-derived by hand from the same formula.
    - Full suite: 110/110 in `ScanlineStudio.Core.Sstv.Tests` (104 previous + 6 new AFC tests), solution-wide build clean, no regressions at either 44100Hz or 11025Hz.
  - **Slant correction ("Auto Slant") — reclassified, investigated, then ported.** Initially assumed to be UI (legacy's `m_Slant`/`GetSyncSamp`/`ClockAdj.cpp` are genuinely manual, mouse-driven calibration tools) — an independent Opus verification pass caught that this was incomplete: there's also a fully automatic, per-line, on-by-default (`Mmsstv.ini`'s `AutoSlant=1`) mechanism, `AutoStopJob`'s `KRSA->Checked` branch, that legacy bundles together with two unrelated features (Auto Stop, Auto Sync) in one function. Per user direction, only the actual slant/clock-drift piece was ported; Auto Stop and Auto Sync were left out as separate concerns, and the manual UI tools were noted for later UI-phase work, not silently dropped.
    - Full algorithm traced and verified against source before writing any code: a fixed 1200Hz (1900Hz for MN/MC, matching `d19`'s use over `d12`'s once `NARROW_SYNC`'s real value, 1900 not 1200, was checked) resonate-rectify-smooth envelope detector (new `CIIRTANK` port, `TankFilter`) finds where the sync pulse's signal strength peaks within each line; a 5-point least-squares fit (`GetSqerrPos`) smooths that against a 16-line rolling history; a staged, mode-dependent confidence-threshold ladder (`m_ASLmt`/`m_ASPos`, both fully transcribed and independently tested per mode group) decides when a drift correction is trustworthy enough to commit. New files: `TankFilter`, `SyncEnvelopeDetector`, `SlantTracker`, plus `SstvModeRegistry.GetAutoSlantThresholdPositions`/`GetSyncSegmentOffsetMs` (the latter a deliberate substitution for legacy's separate `m_OFP` table — derived from this port's own already-verified `LineSegments` data instead of re-transcribing a second per-mode table, justified because the value is only ever used as a fixed reference point that cancels out of the drift math, not reproduced for its own sake).
    - **User-directed methodology change mid-implementation**: after building `TankFilter`/`SyncEnvelopeDetector`/`SlantTracker`/the mode-grouping helpers together and only then wiring + testing them as one block, user redirected: chop complex work into smaller, independently-verified parts so bugs are easier to find. Restarted verification from there: each new class got its own isolated, source-derived unit test (resonator peaking at its tuned frequency, envelope detector responding more to on-frequency tones, `SlantTracker` reporting no correction for zero drift and a mathematically-precise correction for a known synthetic drift sequence, the mode-grouping and sync-offset helpers checked mode-by-mode) *before* any wiring into `AnalogFmSstvDecoder` — all passing in isolation.
    - **Then a real end-to-end integration test caught two genuine bugs the isolated tests structurally could not.** Built a test that encodes at a deliberately different "true" sample rate than the decoder is told (simulating a real clock mismatch) and checks decode accuracy — the isolated unit tests, by construction, never exercised more than one correction in sequence or the actual `AnalogFmSstvDecoder` wiring, so neither could have caught either bug:
      1. **`SlantTracker` internal desync.** Legacy's drift formula uses `SSTVSET.m_SampFreq`/`SSTVSET.m_TW` — both the *evolving*, already-corrected values, kept in sync by `SetSampFreq()` recomputing `m_TW` from `m_SampFreq` after every commit — not a fixed hardware rate. This port's first version used a permanently-fixed rate in the numerator and never updated `_nominalSamplesPerLine` after the first correction, so the formula silently drifted out of the proportional relationship legacy maintains, corrupting every correction after the first (measured: converged to ~6645 instead of the true ~6681 samples/line). Fixed by adding mutable, synchronized `_currentSampleRate`/`_nominalSamplesPerLine` fields updated together after every commit, mirroring `SetSampFreq()` exactly. Re-verified with a corrected *closed-loop* isolated test (the original one fed a fixed linear drift sequence decoupled from the tracker's own corrections — a fair model of the old, buggy always-fixed-nominal behavior, but not of the fixed version's self-referential feedback loop).
      2. **Wiring ran slant-tracking as a bulk pass ahead of pixel decode.** The decoder demodulates whole pushed batches upfront; the first version of the wiring ran `ApplySlantTracking` once per batch over *all* currently-buffered raw samples before the per-line decode loop even started. For a caller pushing the whole waveform in one call (every test in this suite), that meant slant-tracking ran straight through all 240 image lines *and into the trailing footer tone* before a single pixel was decoded — so pixel decode ended up using one final (partly footer-corrupted) rate for the entire image instead of a progressively-corrected one. Fixed by interleaving: decode each line with whatever rate stood *before* it (matching legacy's real causal order — a line is decoded with the current `m_TW`, then its own sync data feeds `AutoStopJob`, which may update `m_TW` for the *next* line only), and bounding `ApplySlantTracking` to exactly the samples pixel decode has already consumed, never running ahead into not-yet-decoded or non-image content.
    - **Verified realistic behavior, and honestly characterized a real algorithmic limit.** With both fixes in place: a realistic clock mismatch (500ppm, already a pessimistic real crystal-oscillator tolerance) decodes within the normal 10.0-delta round-trip tolerance. A deliberately severe 1% mismatch does not fully correct — traced this to a real, verified characteristic of legacy's own algorithm, not a bug: `AutoStopJob`'s `m_ASBitMask` permanently disables each of its 5 confidence tiers once used, so for one long transmission it converges quickly early on and then locks rather than continuing to re-adjust, and for a mismatch this severe over 240 lines the residual (measured: ~9.5 samples/line after convergence) compounds to a visible error by the end. Documented as a legacy limitation faithfully reproduced, not something to invent a fix for, with a test that checks for meaningfully-better-than-uncorrected rather than a false claim of full correction.
    - Full suite after all fixes: 126/126 in `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean.
  - **VIS/preamble lock state machine — broken into 7 smaller pieces (see below), all 7 done and verified. Complete.** Investigated fully before touching code: legacy's real mechanism (`sstv.cpp`'s `CSSTVDEM::Do`, `m_SyncMode` 0-9/256/512-513) isn't one state machine, it's three parallel strategies sharing one utility class — `m_sint1` (checked first, no mode allowlist, but *not* independently scanning: it only ever gets new peak data from the same primary threshold that also drives the real VIS-bit-decode state machine forward, `sstv.cpp:1946-1972` — see piece 7's own corrected scoping below; an earlier note here mischaracterized it as "a simpler substitute this port already has," which undersold its real value: recognizing a repeating timing pattern even when a given attempt's VIS bits fail to decode cleanly), `m_sint2` (recognizes Scottie1/Martin1/Martin2/SC2-180 *without ever decoding a VIS code*, purely from their sync pulse's own repeating timing — a genuinely new capability, not a more-robust version of something already here), and `m_sint3` (the same idea for the MN/MC narrow family). Broken into 7 pieces (see this repo's own conversation history for the full list); picked the foundational one — `CSYNCINT` itself, the peak-interval-pattern utility all three strategies are built on — since the other six all depend on it and it's cleanly testable in isolation.
    - Ported as `SyncIntervalTracker`: tracks amplitude peaks over time, measures the interval between consecutive accepted peaks, checks whether that interval (or its 1/2, 1/3 subharmonic) matches any candidate mode's own line duration, and requires several *consecutive* matching intervals (a real, verified per-mode-group depth, `SyncCheckSub`, `sstv.cpp:1290-1325` — 5 different confidence groups, one of which, RM8/RM12, requires matching all the way back through the full 8-slot history, and one of which, SC2-60/120, is unconditionally excluded and never matches at all) before accepting. `SstvModeRegistry` gained two supporting helpers: `GetSyncIntervalCandidates` (derives each mode's expected interval from this port's own already-`GetTiming`-verified `LineDurationMs`, not a re-transcription of legacy's separate `m_MS[]` table) and `GetSyncIntervalMatchDepth` (the 5-group depth/band gating).
    - Verified with 7 isolated tests *before* any wiring exists, per the now-established "chop into independently-verifiable parts" methodology: periodic peaks matching a mode's interval eventually match; too few consecutive peaks never match; non-periodic (random) peaks never match; the subharmonic check recognizes a doubled interval (a missed-every-other-peak scenario); SC2-60/120 never match under any circumstances; a narrow-band tracker only ever matches narrow-family modes and never normal-band ones. Two test-authoring mistakes caught and fixed by these same tests, not implementation bugs: an early draft fed *half* a mode's interval expecting the subharmonic check to match it, backwards from the actual division direction (legacy divides the *measured* interval down to look for a *shorter* true interval, i.e. handles missed peaks making the apparent interval too long, not a faster-repeating mode); and a normal-vs-narrow band-isolation test picked Scottie S1 (428.22ms) as its "normal" mode, which happens to fall within 3ms of MC110's (428.5ms) narrow-family duration — a coincidental collision in the chosen test data, replaced with Robot36 (150ms, no collision).
    - **Second piece — `m_sint2` wired into `AnalogFmSstvDecoder`, ported and verified.** Read the exact `case 0` trigger structure first (`sstv.cpp:1899-1924`, confirmed `m_MSync` defaults on via `Main.cpp:1856`): `m_sint1.SyncStart()` tried first (unrelated, separately-scoped work, not touched here); else if `(d12>d19) && (d12>m_SLvl2) && ((d12-d19)>=m_SLvl2)` feed `m_sint2.SyncMax(d12)` (still inside a candidate peak); else call `m_sint2.SyncStart()`, and only act on a match if it's one of `smSCT1`/`smMRT1`/`smMRT2`/`smSC2_180` (`sstv.cpp:1912-1922`'s `switch`/`default: break;` — every other match `SyncCheck` could in principle return is deliberately ignored, so this port ignores them identically via a `SyncBypassTrustedModes` set, not a broader "recognize everything" implementation). Also confirmed via `sstv.h`/`sstv.cpp:1483` that `m_sint2.m_fNarrow` is never set (only `m_sint3`'s is) — `m_sint2` always runs `isNarrow: false`.
      - Two absolute-amplitude thresholds in the real condition (`d12 > m_SLvl2`, `(d12-d19) >= m_SLvl2`) are on legacy's internal AGC'd ±16384 scale this port doesn't model — omitted, using only the relative `d12>d19` comparison, the same documented simplification already applied to `AfcTracker`'s and `SyncEnvelopeDetector`'s own squelch gates.
      - `SyncIntervalTracker` gained one new member, `LastPeakPositionSamples`: legacy never needs to expose this (`Start()` just begins decoding from "now" in a continuous real-time loop) but this port's batch decoder needs an explicit anchor point once a match commits. Turning that peak position into a line-start sample index needed a new `SstvModeRegistry.GetSyncSegmentMidpointOffsetMs` helper (offset to the *midpoint*, not start, of the sync segment — `SyncEnvelopeDetector`'s peak naturally lands near the segment's temporal center) — explicitly documented as a port-specific approximation, not a ported legacy mechanism, since legacy's real fine-alignment bootstrap (`CSSTVDEM::Start`'s `m_wBgn` staged buffer search, `sstv.cpp:1717-1744`) is a separate, deeper piece of machinery not ported here.
      - **Real bug caught by the end-to-end test, not the isolated `SyncIntervalTracker` tests**: the first wiring tried sync-bypass detection *before* the VIS/narrow header path on every `TryDecodeHeader` call. Harmless in real continuous real-time use (sync-bypass needs several lines' worth of consistent peaks, so it never actually beats VIS decode's ~610ms in practice) but wrong for this port's batch architecture: a single bulk `PushSamples` call (every test in this suite) hands the whole future stream to sync-bypass detection at once, so it happily scanned deep into real image data and falsely fired on all four trusted modes even when a perfectly valid VIS header was present — regressing all four of `SstvRoundTripTests`' existing round-trip cases for those modes (confirmed: the failure was exactly those four, nothing else). Fixed by trying header-decode first and falling back to sync-bypass only if it fails — which reproduces the real race's actual outcome (header wins whenever a valid one exists) instead of giving the batch-only detector an unrealistic look-ahead advantage; this is the same category of bulk-vs-streaming ordering bug as `ApplySlantTracking`'s earlier fix. All 133 pre-existing tests pass again after the reorder.
      - **New end-to-end test** (`SyncBypassDetectionTests`, 4 cases — one per trusted mode): encodes normally, then strips exactly the VIS header's own sample count (mirroring `TryDecodeVisHeader`'s own duration arithmetic) before feeding the decoder, so it truly has no header to find and can only recognize the mode via sync-interval matching. Mode identification is exact for all four (`Assert.Equal(mode.Id, detectedMode.Id)` passes unconditionally) — confirming the actual ported capability works. Pixel-level alignment is looser than the header path's (measured average per-channel delta: Scottie S1 18.06, Martin M1 12.62, Martin M2 20.78, SC2-180 12.91, vs. the header path's <10.0), a real and reproducible (not random) consequence of the documented peak-position approximation above, not a mode-detection bug — tolerance set to 25.0 with the reasoning and measured numbers recorded directly in the test.
    - Full suite after this piece: 137/137 in `ScanlineStudio.Core.Sstv.Tests` (133 + 4 new), solution-wide build clean.
    - **Third piece — `m_sint3` (narrow MN/MC family equivalent of `m_sint2`) wired into `AnalogFmSstvDecoder`, ported and verified.** Read the exact trigger structure first (`sstv.cpp:1924-1946`, the `#if NARROW_SYNC == 1900` block immediately after `m_sint2`'s in the same `case 0` branch, confirmed compiled since `NARROW_SYNC` is actually `1900` per `sstv.h:440`), plus `m_sint3.m_fNarrow = TRUE` (`sstv.cpp:1483`, the only one of the three `CSYNCINT` instances ever set narrow) and `CSYNCINT`'s shared class shape (`sstv.h:561-590`, `SyncTrig`/`SyncMax`/`m_SyncPhase`). Unlike `m_sint2`'s condition (a simple `d12>d19` comparison, `SyncMax` while true / `SyncStart` every sample once false), `m_sint3`'s real condition is `(d19>d12) && (d19>dsp) && ...`, where `dsp` is a *third*, previously-unused envelope — `m_iirfsk`/`m_lpffsk` (`sstv.cpp:1450/1455`), a 100Hz-bandwidth resonator at `FSKSPACE`=2100Hz followed by the same 50Hz/2nd-order lowpass smoother every other sync-tone envelope in this port already uses — confirmed by direct comparison against `SyncEnvelopeDetector`'s own doc comment that this is the exact same shape, just a different center frequency, so no new production class was needed: `new SyncEnvelopeDetector(sampleRate, VisHeader.NarrowSpaceFrequencyHz)` reuses it directly. This `dsp` check exists to reject the narrow sync trigger when it's actually the FSK-ID space tone (or, as it turns out, the narrow mode-announce packet's own 2100Hz guard tone, `VisHeader.NarrowSpaceFrequencyHz` — the same constant, confirmed the same real 2100Hz value both places) sitting close in frequency to the 1900Hz narrow sync tone — a relative comparison, so (per the same reasoning already applied to `m_sint2`, AFC, and Slant) portable without needing legacy's AGC'd absolute scale, and ported rather than omitted since the user explicitly asked for it this piece.
      - `m_sint3`'s calling-code shape also genuinely differs from `m_sint2`'s: legacy explicitly latches `SyncTrig` (unconditional, on first entry into the candidate band) then `SyncMax` (running max while still inside it) via an explicit `m_SyncPhase` gate, and calls `SyncStart()` exactly once, on the falling edge — rather than collapsing this to the (provably equivalent, since `SyncStart` self-gates on its own internal peak state once consumed) simpler shape `m_sint2` already uses, it was ported as a literal structural match, consistent with CLAUDE.md's port-first principle.
      - Confirmed via `sstv.cpp:1937-1941` that, unlike `m_sint2`'s explicit trusted-mode `switch`/`default: break;`, `m_sint3`'s branch acts on whatever `SyncStart()` returns with no further filter at all — safe because `SyncCheckSub`'s own `m_fNarrow` gating (already generically ported as `GetSyncIntervalMatchDepth(mode, isNarrow: true)`, verified back in the `CSYNCINT` piece) already restricts every possible match to exactly the 6 real narrow modes. So this port adds no `SyncBypassTrustedModes`-style allowlist for `m_sint3` — deliberately, not an oversight.
      - Implementation-level finding, not a legacy-behavior one: since legacy computes `d12`/`d19` exactly once per real-time `Do()` call and both `m_sint2`'s and `m_sint3`'s independent per-sample checks read those same values, `m_sint3` was wired into the *same* `TrySyncIntervalDetection` loop `m_sint2` already had (plus the one new `dsp` envelope) rather than a second separate pass over `_rawSamples` — matches legacy's real single-pass structure and avoids recomputing the same resonator/lowpass filter state twice for no reason.
      - `SstvModeRegistry`'s existing `GetSyncIntervalCandidates`/`GetSyncIntervalMatchDepth`/`GetSyncSegmentMidpointOffsetMs` needed zero changes — all three were already written generically over every mode (narrow included) back in the `CSYNCINT`/`m_sint2` pieces, confirmed by reading each one again before assuming so rather than taking the earlier doc comments' word for it.
      - No new isolated unit tests were added for `SyncIntervalTracker`'s `Trigger`/`UpdateMax`/`TryStart` themselves (already covered generically, including the narrow-band case, by `SyncIntervalTrackerTests`) or for the reused `SyncEnvelopeDetector` (already generic and exercised end-to-end via AFC/Slant) — the only genuinely new behavior this piece adds is the `m_SyncPhase`-style calling sequence and the merged-loop wiring, both of which only make sense to verify at the decoder level.
      - **New end-to-end test** (`SyncBypassNarrowDetectionTests`, 6 cases — one per real narrow mode): encodes normally, then strips exactly the narrow mode-announce packet's own duration (`VisHeader.NarrowHeaderTotalDurationMs` — MN/MC modes never have a VIS header to strip in the first place, per the earlier MN/MC piece's finding), so the decoder can only recognize the mode via sync-interval matching. Mode identification is exact for all 6. Measured average per-channel deltas: MN73 24.27, MN110 21.17, MN140 19.62, MC110 16.38, MC140 14.29, MC180 11.42 — real and reproducible (same peak-position-approximation cost already documented for `m_sint2`'s own trusted modes), comfortably inside the same 25.0 tolerance already established for that path.
      - Full suite after this piece: 143/143 in `ScanlineStudio.Core.Sstv.Tests` (137 + 6 new), solution-wide build clean.
    - **Fourth piece — VIS-bit-decode-via-PLL-tone-counting (`m_SyncMode` cases 0(trigger)/1/2/9/3, `sstv.cpp:1946-2154`), ported and wired.** Scoped in two passes before writing code, per this session's established practice: an initial read of the whole `m_SyncMode` switch, then an independent Opus-driven re-verification of every claim directly against source (not taken on the agent's word — several of its own claims were spot-checked again before trusting them, e.g. `FindByFullVisByte`'s parity formula was hand-verified against 5 real legacy bytes before writing any table-driven test). Two real corrections came out of that: the tone-race resonators (`m_iir11`/`m_iir13`, 1080/1320Hz) use an **80Hz** bandwidth (`sstv.cpp:1446/1448`), not the 100Hz every other `SyncEnvelopeDetector` instance uses; and legacy's mode-code table (`sstv.cpp:1993-2074`) matches the **full 8-bit byte** (7 data bits + even-parity bit as MSB), not the parity-stripped 7-bit value this port's `VisCode` field stores — naively masking `&0x7F` before calling the existing `FindByVisCode` would be more permissive than legacy, which rejects a byte with the wrong parity bit outright (`default: m_SyncMode=0`).
      - Added as a **third fallback** (fixed-window header decode → `VisLockStateMachine` → sync-interval bypass), not a replacement of `TryDecodeVisHeader`: that path's anchor is fully analytic and already proven <10.0 average per-channel delta by all existing round-trip tests, while this new path's anchor is derived (see below), not exact by construction — adding it alongside gets the real new capability (locating *any* VIS-coded mode despite arbitrary leading silence/noise, not just the `m_sint2`/`m_sint3` trusted subset) with zero regression risk to the already-passing precise path.
      - `SyncEnvelopeDetector` gained an optional `bandwidthHz` parameter (default 100.0, every existing call site unaffected) so the 1080Hz/1320Hz@80Hz tone-race detectors could reuse the same resonate-rectify-smooth class rather than a new one.
      - `SstvModeRegistry.FindByFullVisByte` reuses `VisHeader.GenerateSegments`'s own parity computation (including `Rm12ForcedParityBit`) as the single source of truth for the full legacy byte, rather than re-transcribing a second copy of the mode-code table — verified against 24 real legacy bytes (`RealLegacyBytes` theory data, spot-checked by hand against `sstv.cpp:1993-2065` first) plus two flipped-byte rejection cases, all passing.
      - New `VisLockStateMachine`: a per-sample state machine (`Search`/`ConfirmLock`/`DecodeVis`/`DecodeExtendedVis`/`Verify`, mirroring cases 0/1/2/9/3 exactly) with two structural details ported literally rather than "normalized" once noticed: the `d13` (1320Hz) detector's filter state is only ever advanced while decoding a byte (`sstv.cpp:1976-1978`), frozen between attempts, not reset; and `Verify`'s 30ms countdown is unconditional every sample (`sstv.cpp:2131`), unlike `ConfirmLock`'s countdown, which only decrements while its condition holds and resets to `Search` on any single failing sample (`sstv.cpp:1958-1971`) — a real, deliberate asymmetry between the two, not an inconsistency to fix. When the decoded byte identifies AVT, this class deliberately does not report a lock (matching legacy's own case 3, which diverts AVT into the training-lock state machine — cases 4-8, still unported — instead of calling `Start()`); AVT continues to be found only via the existing fixed-window path.
      - **Line-0 anchor derived analytically, verified by direct arithmetic against already-proven constants, not detected freshly.** Walking the state machine's own fixed timing (15ms `ConfirmLock` + 8×30ms or 16×30ms `DecodeVis`/`DecodeExtendedVis` + 30ms `Verify`) from the trigger-fire sample lands exactly 15ms *before* the same line-0 boundary `VisHeader.TotalDurationMs`/`PrefixDurationMs+ExtendedTailDurationMs` already define — checked for both the normal (910ms) and extended (1150ms) cases independently, both giving the same universal +15ms gap, not two different numbers that happened to need reconciling. Adding that fixed correction reproduces the exact boundary the fixed-window path already computes and that path's tests already prove precise, instead of inventing new, unverified anchor arithmetic from scratch — the single riskiest part of this piece per the Opus review, closed by reuse rather than a fresh derivation.
      - Isolated tests (`VisLockStateMachineTests`, driven directly with samples rendered from `VisHeader.GenerateSegments`/`GenerateExtendedSegments`, no decoder involved): a clean header locks the right mode; a 10ms 1200Hz blip (matching the real break tone's own duration) never advances past `ConfirmLock`'s 15ms hold; an extended-VIS header locks the right mode via the second byte; a flipped data bit and a flipped parity bit each never lock (the latter exercising `FindByFullVisByte`'s rejection specifically); a header preceded by 3 seconds of silence still locks — the actual new capability. All 6 pass.
      - **Real, measured imprecision found by the silence test, not assumed**: the trigger only fires once the d12/d19 envelope detectors' filter chain has actually settled past the crossing point (a real, bounded lag the fully-analytic fixed-window path doesn't have) — measured at ~80 samples (~7.3ms at 11025Hz) for a clean synthetic tone, no noise. Documented in both the test and `VisLockStateMachine`'s own doc comment rather than papering over it with a wide, unexplained tolerance.
      - Wired into `AnalogFmSstvDecoder.TryDecodeHeader` (`TryVisLockStateMachine`, same "processed up to" persistence pattern as the sync-bypass detectors) between the fixed-window path and `TrySyncIntervalDetection`. Commit logic refactored: `CommitSyncBypassMatch` (the sync-bypass detectors' approximate-anchor path) now delegates to a shared `Commit(mode, lineStartSample)`, which `TryVisLockStateMachine` also calls directly with its own exact anchor.
      - **New end-to-end test** (`VisLockStateMachineDecoderTests`): a real encode of Martin M1 preceded by 3 seconds of silence, decoded with no special handling — exact mode ID and the same <10.0 round-trip tolerance the fixed-window path's own tests use (not the sync-bypass paths' looser 25.0), proving the anchor derivation is genuinely as precise as reusing the fixed-window path's own boundary, not just "close enough."
      - Full suite after this piece: 179/179 in `ScanlineStudio.Core.Sstv.Tests` (143 + 36 new: 3 `SyncEnvelopeDetector` bandwidth tests, 26 `FindByFullVisByte` table-driven tests, 6 `VisLockStateMachine` isolated tests, 1 new end-to-end test), solution-wide build clean.
    - **Fifth piece — AVT's training-sequence lock (`sstv.cpp` cases 4-8, `sstv.cpp:2155-2239`), ported and wired as a refinement, not a new capability.** Started by measuring, not assuming, whether this port's `PllFmDemodulator` (configured over a wider 1100-2300Hz span than legacy's 1500-2300Hz) settles inside the tight bands cases 4/5/6 need: a steady 1900Hz tone measured 0.02Hz average deviation with ~5.8Hz ripple, and — the real risk, since AVT's bit windows are only 9.7646ms — 16 consecutive alternating 1600Hz/2200Hz bit windows (simulating a real shifting counter) all landed solidly on the correct side of case 6's decision threshold (measured 1593-1608Hz and 2198-2201Hz respectively, against ≤1704.7Hz/>2095.3Hz bounds). Feasible confirmed empirically before writing the state machine, not assumed from the Hz-conversion math alone.
      - **Surprising finding from reading cases 4-8 in full** (not from the partial read that originally scoped this piece): case 8 (`sstv.cpp:2234-2239`) is dead code — `m_SyncMode` is 8 on entry, so `m_SyncMode--` makes it 7, never 0, so `Start()` there is unreachable. This means the training sequence *never* completes via a clean "all 32 blocks decoded" path in legacy's own real implementation; `Start()` is always reached via the overall timeout (`m_SyncTime`, set once in case 3 to `9 + 2×VIS-block + full-training-sequence` ms = 7141.2475ms, `sstv.cpp:2140`) expiring in case 4, 5, or 6. What makes porting it worthwhile anyway: case 6 *recalculates* that timeout, smaller, after every successfully-decoded block (`sstv.cpp:2200-2201`, from the block's own position in the 32-block sequence, encoded in its `h` byte) — so a signal with real, sustained lock finishes close to the actual content end, while a signal with no lock at all waits out the full nominal duration (exactly what this port's existing fixed-duration skip, `VisHeader.AvtExtraHeaderDurationMs`, already does). Confirmed also: case 7 (`WaitNextMarker`) does not decrement the overall timeout at all — frozen while waiting between blocks, only ticking in states 4/5/6 (no `m_SyncTime--` anywhere in `sstv.cpp:2221-2233`).
      - This reframed the piece from "new capability" (like `m_sint2`/`m_sint3`/`VisLockStateMachine` each were) to **refinement of an already-working, already-tested path**: more accurate completion timing for real captured audio with clock drift, not a change to whether AVT is found at all.
      - New `AvtTrainingLockStateMachine`: a per-sample state machine (`MarkerSearch`/`MarkerConfirm`/`DecodeBits`/`WaitNextMarker`, mirroring cases 4/5/6/7) consuming already-demodulated Hz values from this port's existing continuously-running `PllFmDemodulator` output rather than a second PLL instance — confirmed both legacy's `m_pll.Do(ad)` here and this port's main decode path are the exact same demodulator, so no new DSP component was needed, only new sequencing/threshold logic around one already ported. Case 8 folded directly into case 6's `h==0x40` transition (a documented simplification with no functional effect, since real case 8 is a single-sample, ~0.09ms-at-11025Hz pass-through that never calls `Start()` either way). `_visData` shifts **left** here (`sstv.cpp:2192`), MSB-first — the opposite direction from `VisLockStateMachine`'s case2/9 right-shift — ported literally per-source, not normalized to match.
      - Isolated tests (`AvtTrainingLockStateMachineTests`, driven with *real* demodulated frequencies — audio rendered from `VisHeader.GenerateAvtSegments` run through an actual `PllFmDemodulator`, not hand-fed "perfect" Hz values, catching any real mismatch between ported thresholds and actual demodulator behavior): a full, real training sequence completes at sample 58608 (~5316.9ms) — measured, not assumed — right at the training sequence's own real content duration (58568 samples, ~5312.9ms), dramatically less than the full ~7141ms nominal budget (78732 samples) a signal with no training signal at all correctly waits out instead (also measured, exact match to within 10 samples). Both pass.
      - Wired into `AnalogFmSstvDecoder`: `TryDecodeVisHeader`'s AVT branch no longer commits atomically with a fixed sample count (its completion point is data-dependent) — it starts a multi-call pending phase (`TryStartAvtTraining`/`TryResolveAvtTraining`, `_avtTrainingPending` checked first in `TryDecodeHeader` so other fallbacks don't fire mid-resolution), feeding already-demodulated frequencies into the training lock from the point legacy's own case 3 hands off to case 4 (after all 3 VIS repeats), with the existing, already-tested `AvtExtraHeaderDurationMs` kept as a hard fallback ceiling matching legacy's own real timeout-fallback behavior.
      - **New end-to-end test** (`AvtTrainingLockDecoderTests`): isolates anchor-precision from AVT's separately out-of-scope per-line drift (AVT has no Auto Slant correction, in both legacy and this port) by checking only row 0's accuracy — since drift hasn't had a chance to accumulate there, row-0 error reflects header-anchor precision alone. At a realistic 500ppm clock mismatch (the same pessimistic-but-real tolerance `SlantTests` uses), measured 6.04 average per-channel delta, comfortably inside normal round-trip tolerance. A deliberately severe 1% mismatch (tried first) degraded badly (~60 delta) — traced to AVT's tight absolute-Hz decision bands being pitch-shifted by the same 1% as every other frequency the demodulator computes, the same category of honestly-documented degradation `SlantTests` already records for Robot36's own 1% case, not a bug in this piece's anchor arithmetic.
      - Existing AVT round-trip tests (`SstvRoundTripTests`, driverless-clock case) confirmed unaffected: the new mechanism's real-lock completion point (~8046.9ms from header start, by direct arithmetic) lands within ~4.7ms of the old fixed-duration skip's (~8042.2ms) for a driftless same-process test, well inside the existing <10.0 tolerance.
      - Full suite after this piece: 182/182 in `ScanlineStudio.Core.Sstv.Tests` (179 + 3 new), solution-wide build clean.
    - At this point, 2 of 7 pieces remained unstarted: `m_sint1` (see piece 7 below for the corrected scoping and why it was blocked on `CLVL`) and per-line continuous re-verification (piece 6, done next, see below).
      - Also noted, not part of the 7 but discovered while doing pieces 2, 4, and 5: legacy's own fine-pixel-alignment bootstrap (`CSSTVDEM::Start`'s `m_wBgn` staged buffer search, `sstv.cpp:1717-1744`, and `SyncSSTV`, `Main.cpp:3751-3799`) is unported and is the real fix for `m_sint2`/`m_sint3`'s midpoint-approximation anchor imprecision (~11-24 average delta), `VisLockStateMachine`'s own smaller ~7ms trigger-lag imprecision, and (though not measured directly) likely also a source of some of `AvtTrainingLockStateMachine`'s own small residual anchor error — a candidate 8th piece if any of these paths' alignment precision ever needs to improve further.
      - **Opus review checkpoint moved up, superseding the earlier "wait for all 7" instruction, then completed with fixes.** With the remaining 2 pieces newly scoped as genuinely large (`m_sint1` blocked on an unstarted `CLVL` AGC port; per-line continuous re-verification requiring the whole state machine to run concurrently with image reception plus an abort-and-restart capability this port's architecture doesn't have yet), the user's own call was to review what's done now (5 of 7) rather than wait for the remaining two, which could each expand into their own multi-session breakdown the way pieces 4 and 5 did.
      - **Independent adversarial review, cross-checking every piece against actual legacy source directly** (not the roadmap's own summaries, not the code's own doc comments — the same failure mode Scottie's earlier channel-order bug demonstrated: a self-consistent round-trip test can pass while both TX and RX agree with each other but disagree with real legacy). **Pieces 1-4 (`CSYNCINT`, `m_sint2`, `m_sint3`, VIS-bit-decode-via-PLL-tone-counting) fully confirmed correct**, including every detail flagged as highest-risk beforehand: the 80Hz vs 100Hz tone-race resonator bandwidth (hand-verified against `sstv.cpp:1446/1448` vs `1447/1449/1450`), all 24 real legacy VIS bytes bit-by-bit against `FindByFullVisByte` (including the RM12 parity anomaly and the escape byte `0x23`'s own failed-parity oddity), the LSB-first bit-shift order, the `d13` filter's frozen-between-attempts asymmetry, and — independently re-derived by walking the timing from scratch, not by trusting the doc comment — the universal "+15ms" anchor-reconciliation gap for both the normal (910ms) and extended (1150ms) header cases.
      - **Piece 5 (AVT training lock) had two real findings, both fixed:**
        1. **`AvtTrainingLockStateMachine.cs`'s `h==0x40` (last block) branch was overwriting `_phaseCounter`** with a shortened value (`LastBlockWaitMs`) that legacy's real `sstv.cpp:2204-2209` never touches in this branch — `m_SyncATime` there is already a full bit-window's worth from the *unconditional* assignment at `sstv.cpp:2191` (this port's own line 131, which runs for every bit including the block's last one, before the validity check is ever reached). The bug made this class complete ~63 samples (~5.7ms at 11025Hz) early after a clean 32-block lock — invisible to `AvtTrainingLockStateMachineTests`' wide (±300ms) completion-time tolerance. Fixed by deleting the overwrite; the class doc comment's claim that this was part of the (harmless) case-8-folding decision was also wrong and corrected — the two are unrelated.
        2. **The class's own internal timeout budget double-counted 2 VIS repeats.** `AnalogFmSstvDecoder` only ever constructs `AvtTrainingLockStateMachine` after already skipping past all 3 VIS repeats (at the training sequence's own first marker), but the class's constructor copied legacy's full case-3 budget (`9 + 2×VIS-block + training`, `sstv.cpp:2140`), which is scoped from legacy's real, ~1835ms-*earlier* case-3-exit point. Masked at the decoder level (`AnalogFmSstvDecoder`'s own separately, correctly computed `_avtTrainingFallbackDeadlineSample` always fires first), but the isolated `NoTrainingSignalAtAll_CompletesAtTheFullNominalBudget` test and two doc comments asserted/described the wrong value. Fixed: internal budget is now `9 + VisHeader.AvtTrainingSequenceDurationMs` only (~5.3s, not ~7.1s); both doc comments (the class's own, and `TryStartAvtTraining`'s) corrected to accurately describe where this port's own origin point actually sits relative to legacy's; the isolated test's expected value updated to match.
      - **Two doc-only fixes, no behavior change:** `VisLockStateMachine`'s class comment claimed AVT's training-lock state machine "doesn't have yet" — stale since piece 5 added it; corrected to describe the real, narrower remaining gap (this class's own sample-by-sample scanning still can't locate AVT, only the fixed-window path can, since it hands off to `AvtTrainingLockStateMachine` for the header's own remainder). `TrySyncIntervalDetection`'s merged `m_sint2`/`m_sint3` loop was flagged as having no equivalent to legacy's own case-0/1 gating (which effectively freezes both trackers once a stronger 1200Hz trigger has fired) — a real, low-severity structural divergence (this path only ever runs when nothing else has locked), now documented rather than left silent.
      - Full suite after all review fixes: 182/182 in `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean, no regressions.
    - **Piece 6 (per-line continuous re-verification) started, broken into sub-pieces 6a-6d; 6a done.** Scoped via an Opus-verified outline before any code was written (per the now-established "verify then chop up" practice): the initial fear that this piece would require running every already-ported detector (`m_sint2`/`m_sint3`/`VisLockStateMachine`) concurrently against every sample of every image was **wrong** — legacy hard-gates `m_sint1`/`m_sint2`/`m_sint3` behind `!m_Sync` at every call site (`sstv.cpp:1899/1949/1953/1959`), so only `VisLockStateMachine`'s own mechanism (case 0 trigger through case 3) genuinely runs while locked. The real, much bigger gap the scoping surfaced: **this port had no end-of-image reset at all** — legacy's `Stop()` (`sstv.cpp:1769-1791`) plus cases 512/513's 0.5s dead-time wait (`sstv.cpp:2243-2252`) has no counterpart, so two clean back-to-back transmissions in one stream would decode as one image then silence, with no mid-reception abort needed to see it.
      - **6a — `AnalogFmSstvDecoder.EndOfImage`, a direct port of `Stop()` + cases 512/513.** Modeled as an analytic skip (jump `_consumedSamples` forward by 0.5s worth of samples) rather than a literal per-sample countdown, matching the same style already used for fixed-duration header skips — legacy's own dead zone is footer-content-agnostic (just a fixed wall-clock wait), so this is a faithful adaptation, not an invented shortcut. Resets `_mode`/`_lineDecoder`/`_pixels`/`_nextLine` and all three AFC/Slant/AVT-pending fields; resets `_syncBypassTracker`/`_syncBypassNarrowTracker` (`SyncIntervalTracker` gained a `Reset()` method mirroring `CSYNCINT::Reset()`, `sstv.cpp:576-582`) and `VisLockStateMachine` (which also gained a `Reset()`), matching legacy's own `Stop()` resetting `m_sint1`/`m_sint2`/`m_sint3`. Since all three of these persistent detectors had always been fed starting at absolute raw-sample index 0 until now, resetting them mid-stream needed new origin-offset fields (`_syncBypassOriginSample`, `_visLockOriginSample`, mirroring `AvtTrainingLockStateMachine`'s existing `_avtTrainingOriginSample` pattern) so their own internally-relative results still convert to correct absolute buffer positions.
      - **Two real prerequisite bugs found and fixed before 6a's own test could pass at all**, both surfaced by the Opus outline-verification pass and independently confirmed by direct code inspection before fixing: `_nextLine` was never reset anywhere in `Commit()` (a second commit would resume decoding mid-image, immediately failing since `_nextLine` already equalled the *first* image's `ImageHeight`); and `ApplyAfcCorrections` had no upper bound beyond `_demodulatedFrequencies.Count` (unlike `ApplySlantTracking`, which was already carefully bounded for exactly this reason) — a single bulk `PushSamples` call would eagerly AFC-correct straight through the first image's footer/dead-zone and into a not-yet-detected second transmission's audio using a correction tuned to the first image's own frequency offset, before that transmission's own `Commit()` even ran. Fixed with a new `_afcBoundSample` (a generous nominal upper bound on each image's own total audio extent, computed once per `Commit()`), the same category of bulk-vs-streaming ordering bug that already bit `ApplySlantTracking` and `TrySyncIntervalDetection`, but for AFC specifically only became reachable once `EndOfImage` made a second `Commit()` possible at all.
      - **New end-to-end test** (`EndOfImageResetTests`): two complete back-to-back transmissions (the encoder always appends a footer, so concatenating two encodes is exactly the real-world shape) both decode correctly. Measured, not assumed: first transmission delta 2.18 (resolved via the exact fixed-window path, same as every other round-trip test); second transmission delta 9.39 (resolved via `VisLockStateMachine`'s forward search instead, since the second header lands mid-footer relative to the fixed 0.5s dead-time skip — the footer can run up to ~900ms for normal modes, longer than the skip, which is expected and faithful to legacy's own footer-content-agnostic dead zone, not a bug). A second, rate-mismatched variant was attempted specifically to give the AFC-bound fix a real, nonzero correction to exercise (a driftless synthetic signal gives AFC nothing to correct, making the bug invisible either way) — dropped after measurement showed its result (delta2≈55) was dominated by `VisLockStateMachine`'s own already-known anchor imprecision compounding with Auto Slant's convergence behavior under drift, not cleanly isolating the AFC fix specifically; shipping a confounded test with a loose tolerance would have been misleading rather than a real regression guard. The AFC-bound fix itself is verified by direct code audit (confirmed via grep that `_nextLine` was genuinely never assigned anywhere, and that `ApplyAfcCorrections`' loop bound was genuinely unconditional) rather than by an isolated automated test — noted honestly rather than claiming test coverage that doesn't exist.
      - Full suite after 6a: 183/183 in `ScanlineStudio.Core.Sstv.Tests` (182 + 1 new), solution-wide build clean.
      - **6b (further `Commit()` re-entrancy audit) folded into 6a/6c's own fixes — a final systematic pass over every per-image field found nothing further needing a reset.** `_rawSamples`/`_demodulatedFrequencies` correctly never reset (whole-session buffers, not per-image state); `InitializeAfc`/`InitializeSlant` already fully re-anchor their own watermarks on every `Commit()`; `_avtTrainingPending` is always false by the time any `Commit()` runs (the only path that sets it also always clears it before committing); `_syncBypassProcessedUpTo`/`_syncBypassNarrowPhaseActive` are correctly left untouched by `Commit()` since `TrySyncIntervalDetection` never runs while locked (matching legacy's own gating) — they're only relevant again after a *natural* `EndOfImage()`, which already resets them.
      - **6c — `VisLockStateMachine` wired to keep running while locked, `Commit()` made genuinely re-entrant, two real bugs caught by this port's own end-to-end test (not by review or reasoning alone).** Checked once per decoded line (not once per `TryProcessBuffer` call — a bulk-pushed buffer would otherwise let a whole image's worth of lines decode in one shot with no chance to notice a mid-reception re-lock at all). Only `VisLockStateMachine` runs here, never the fixed-window path (assumes `_consumedSamples` is a header start) or `TrySyncIntervalDetection` (`m_sint2`/`m_sint3` hard-gated behind `!m_Sync` at every legacy call site, `sstv.cpp:1899/1949/1953/1959`).
        1. **Self-rediscovery bug**: `_visLockProcessedUpTo` was only ever advanced by `TryVisLockStateMachine` itself — a transmission found via the *fixed-window* path (the common case) never touched it, leaving it at its initial 0. Once 6c started calling it per-line, it began scanning from sample 0 on the very next line — i.e., rediscovering the current transmission's *own* header, which it had never gotten a chance to examine — and immediately "restarted" onto itself. Caught immediately by `EndOfImageResetTests` reporting 3 detected modes instead of 2. Fixed in `Commit()`: fast-forward `_visLockProcessedUpTo`/`_visLockOriginSample` past whatever a *different* detection path just resolved (via `Math.Max`, since a self-triggered commit already has `_visLockProcessedUpTo` correctly positioned and must not be rewound backward into content it just used to find the match), and always `Reset()` the state machine's logical state.
        2. **Bulk-vs-streaming lookahead bug, the third instance of this exact category in this codebase** (after `ApplySlantTracking` and the original `TrySyncIntervalDetection` ordering fix): `TryVisLockStateMachine` scanned all the way to `_rawSamples.Count` in one call. Called mid-reception against a bulk-pushed buffer containing a whole first transmission followed by a real second one, it would discover the *second* transmission's genuinely valid header on its very first call (right after decoding line 0 of the first) and restart onto it immediately — abandoning a transmission this port had every ability to finish. Fixed by parameterizing `TryVisLockStateMachine(int upperBoundSample)`: the pre-lock caller (in `TryDecodeHeader`) passes `_rawSamples.Count` (no current decode position to bound against yet, same as always), the while-locked caller (piece 6c) passes `_consumedSamples` (never running ahead of what's actually been decoded so far).
        3. New `DecodeRestarted` event added to `ISstvDecoder` (only implementation, `AnalogFmSstvDecoder`) — distinct from `ModeDetected`, which fires for both a fresh detection and a mid-reception restart. Callers displaying an in-progress image need this specifically to know the partial image should be discarded.
        4. Measured, not assumed: suite duration grew from ~1m25s to ~2m6s (183→184 tests) — a real, non-trivial per-line cost from running `VisLockStateMachine` on every decoded line across the whole test matrix. Not optimized now (out of scope for Phase 1 correctness work), noted honestly rather than silently absorbed.
      - **6d — new end-to-end test** (`MidReceptionRestartTests`): a first transmission's audio truncated at 30% through its own image body (well past the header, well before completion — simulating a real station cutting out or being overridden), immediately followed by a complete second transmission, no footer, no gap. Asserts exactly one `DecodeRestarted` fires and the second (real) transmission decodes correctly. Measured, not assumed: 9.81 average per-channel delta — resolved via `VisLockStateMachine`'s own already-documented ~7ms anchor lag (not the exact fixed-window path), close to but under the standard 10.0 tolerance, consistent with that already-known cost rather than a new imprecision from 6c/6d specifically.
      - Full suite after 6b/6c/6d: 184/184 in `ScanlineStudio.Core.Sstv.Tests` (183 + 1 new), solution-wide build clean.
      - Deferred and named explicitly per the Opus outline review, not silently absorbed: retuning the while-locked detectors from `AfcTracker`'s correction (legacy's real detectors get this for free via `InitTone`'s side effect, `sstv.cpp:2362`, this port's are fixed-frequency); the lock-dependent input bandpass switch (`HBPFS` vs `HBPF`/`HBPFN`, `sstv.cpp:1826-1832`); mid-image AVT re-lock (`sstv.cpp:2139-2144`, `VisLockStateMachine` deliberately never reports AVT); mid-image narrow-mode FSK-announce re-lock (`sstv.cpp:2592`, needs a sample-by-sample FSK decoder this port doesn't have — `TryDecodeNarrowModeHeader` is a fixed-window analytic shortcut with no real-time legacy counterpart).
      - **Piece 6 done — 6 of 7 complete.** Only `m_sint1` remains, still blocked on the unstarted `CLVL` AGC port (see above) — closed next, piece 7 below.
      - **Independent adversarial review of 6a-6d, then fixes.** Same practice as the earlier 5-piece checkpoint: re-verify every already-fixed bug independently rather than trusting the original fix, then look for new ones.
        - **Bug fixes #1 (`_nextLine=0`), #4 (`upperBoundSample` parameterization) confirmed fully correct.** Bug fix #3 (`Math.Max` origin-tracking) confirmed *behaviorally* correct — independently re-derived the anchor-vs-consumed-samples arithmetic from `VisLockStateMachine`'s own formula rather than trusting the original claim, and found the anchor is provably always later than the state machine's own stop point (by ~15ms/164 samples at 11025Hz, for every candidate mode) — but the *comment* explaining it was backwards (claimed `Math.Max` "leaves it alone" for the self-triggered case; it always advances by that same ~164 samples). Fixed the comment. Also found and fixed a genuinely redundant, slightly-wrong `_visLockProcessedUpTo++` immediately after `Commit()` in `TryVisLockStateMachine` — since `Commit()` already sets both `_visLockProcessedUpTo` and `_visLockOriginSample` to the same value, the extra increment desynced them by exactly 1 sample, biasing every subsequent anchor from that instance 1 sample (~0.09ms) early. Removed.
        - **New bug found: `TryDecodeNarrowModeHeader` silently disabled AFC for every MN/MC mode.** It duplicated `Commit()`'s body inline instead of calling it (predating piece 6a), so when `Commit()` gained `_afcBoundSample` for the AFC-bound fix, this path never got it — staying at its default 0, so `ApplyAfcCorrections`' bound became `Math.Min(count, 0) = 0` and its correction loop never ran. No existing test caught this (nothing in the suite exercised a mistuned-audio narrow-mode decode end to end). Fixed by routing through `Commit()` like every other detection path. New `NarrowModeAfcTests` added — honestly measured, not oversold: at the same realistic 500ppm `SlantTests` uses, buggy and fixed are nearly indistinguishable (10.66 vs 10.35, Auto Slant alone already compensating for most of a mismatch this small) — confirmed the bug *is* clearly catchable at a deliberately severe synthetic 2000ppm (24.73 vs 20.52, still elevated even fixed, MN73's tighter AFC band degrading similarly to AVT's under severe mismatch) but that severity wasn't used as the actual assertion since it introduces its own confounding degradation, the same tradeoff already documented for the AVT clock-mismatch test. The fix itself is verified correct primarily by direct code reasoning (parallels the exact pattern every other `Commit()`-calling path already uses), not primarily by this test.
        - **`DecodeRestarted` ordering trap found and fixed.** It fired with the *new* mode (`_mode!`) — which technically matched its own doc comment's promise, but the promise itself was a trap: `ModeDetected` for the new mode always fires first (inside `Commit()`, called by `TryVisLockStateMachine` before `DecodeRestarted` ever runs), so a caller that allocates a display buffer on `ModeDetected` and discards on `DecodeRestarted` would discard the buffer it just allocated for the new mode, not the abandoned one it actually needs to throw away. Fixed to pass the abandoned mode (the local `mode` variable already in scope, captured before the restart check); `ISstvDecoder.cs`'s doc comment corrected to match.
        - **Real risk-escalation finding, documented not fixed (blocked on the same `CLVL` prerequisite as `m_sint1`):** the omitted `m_SLvl`/`m_SLvl2` absolute-amplitude gates (already a documented simplification in four other places) were scoped as harmless when `VisLockStateMachine` only ran pre-lock — a false positive there just wasted scanning time on silence/noise. Piece 6c changed the blast radius: now it runs per decoded line against real image content, where a false positive can destroy an in-progress *good* image. Concretely demonstrated, not just theorized: Robot36/R24's 150ms lines are exactly 5×30ms bit windows, so a bit window ending inside that line's own sync pulse recurs every 5th window, and enough consecutive dark/sync-heavy content can in principle assemble a byte identical to a real VIS code (R24's `0x84`, hand-verified reachable this way). Two things bound the real risk without eliminating it (the per-bit reject test, and that the likeliest garbage byte `0x00` matches no mode), and neither of this port's own tests (smooth synthetic gradients) has triggered it — but this needs the `CLVL` AGC port to close properly, tracked as part of that same prerequisite, not patched here with an invented substitute threshold. Documented in `VisLockStateMachine`'s own doc comment.
        - **Two more real, undocumented legacy behaviors surfaced, both out of scope for this piece specifically but now named rather than silently absorbed:** legacy actually *saves* a mid-reception-abandoned image if it was ≥65% complete (`sstv.cpp:2134-2138`'s `m_ReqSave`, `Main.cpp:4931-4934`'s `WriteHistory`) — the port discards unconditionally, a persistence-layer concern with no logbook/history feature yet to hook it into ([[08-logging]], Phase 4). And `m_SyncRestart` is a real, user-toggleable legacy option (`sstv.cpp:1486` default 1, toggled via `Main.cpp:10907`/`11887`'s lock button) — this port hard-wires the equivalent behavior on with no way to disable it, since no settings/UI layer exists yet to expose the toggle through. Both are real, not deferred silently: revisit when [[08-logging]] and the settings/UI layers exist.
        - **Two doc-only overclaim fixes:** `_afcBoundSample`'s comment called it "generous"; it's exactly nominal (pre-slant) image duration, and with Auto Slant active on a slow clock actual elapsed samples can exceed it slightly (bounded to ~0.1% of image length in practice, well under one line at realistic drift) — corrected wording, not fixed further, matches the same order of magnitude as other already-accepted small imprecisions in this system. `EndOfImage`'s comment claimed its 0.5s analytic skip has "no detectable effect either way"; the resonator-fed detectors' filter state actually carries over from the previous image's tail rather than genuinely settling against dead-time content, resettling within ~10-30ms against a 300ms leader in practice — real but small, described honestly instead of as literally undetectable.
        - One finding explicitly re-confirmed as *not* a bug after independent tracing: the "restart on the final line" race (if `TryVisLockStateMachine` matches on the same line that completes an image, `restarted=true` correctly short-circuits before the `EndOfImage()` check, so the freshly-committed new mode is never wiped). The AFC double-correction risk across a mid-reception restart, re-confirmed here as "bounded to at most one line," turned out to be **wrong** — see the piece 7 holistic review below, which found and fixed the real, much larger bound.
        - Full suite after all review fixes: 185/185 in `ScanlineStudio.Core.Sstv.Tests` (184 + 1 new `NarrowModeAfcTests`), solution-wide build clean.
    - **Piece 7 — `CLVL` (legacy's shared AGC) and `m_sint1`, the last of the 7 pieces. Done, complete, all 7/7 closed.** Scoped via a plan-verification Opus review *before* any code was written (a variant of the now-established practice — this time verifying the *plan*, not reviewing finished code), with several of that review's own claims independently re-checked against source directly rather than trusted on summary alone, catching one of its own errors (`NARROW_SYNC` momentarily mis-read as a disabled feature flag mid-conversation; corrected immediately by re-reading `sstv.h:440` — it's a frequency constant, `#define NARROW_SYNC 1900`, always compiled in).
      - **`CLVL` (`sstv.h:223-298`) ported as `LevelAgc.cs`.** Confirmed it only affects the sync-detection path: legacy's `Do()` prologue (`sstv.cpp:1834-1839`) computes `d = clamp(m_lvl.AGC(d)*32, ±16384)` and feeds *that* to every tone-envelope discriminator (`d11`/`d12`/`d13`/`d19`/`dsp`), while the actual FM pixel/AFC demodulator (`m_pll`/`m_fqc`/`m_hill`) reads `m_lvl.m_Cur` — the pre-AGC raw value — so this piece doesn't touch pixel decode at all, only the absolute-threshold sync heuristics.
      - **`m_agcfast` is permanently on, confirmed by reading `CSSTVDEM`'s constructor directly (`sstv.cpp:1470`), not assumed from `CLVL`'s own default.** `CLVL`'s ctor sets `m_agcfast=0`, but `CSSTVDEM`'s ctor immediately overwrites it to `1`, unconditionally, and nothing else in the program ever touches it — so legacy's `m_agcfast==0` branch (`sstv.h:272-279`, an averaged-5-window AGC recompute) is unreachable dead code for this demodulator. `LevelAgc.cs` ports only the live (`m_agcfast==1`) formula; the surrounding peak-hold bookkeeping that branch shares a block with (`m_PeakMax`/`m_PeakAGC`/`m_Peak`/`m_CntPeak`) is write-only in this program too (only reader is the UI level-meter's peak-hold bar, `Main.cpp:6186-6192`) and was omitted as a documented simplification, not tracked-but-unread state.
      - **Scale bridge, the one genuinely new risk this piece introduced and the reason it isn't a copy-paste port.** Legacy's `d` is int16-valued (`sstv.cpp:1821`'s 24578 overflow check, `Wave.cpp:796-808`'s direct `SHORT`→`double` copy); this port's own sample contract is `float` in `[-1.0, 1.0]` (`spec/05-audio-engine.md:44`). Feeding `±1.0` straight into a faithful `CLVL` would pin its AGC at the floor gain forever (the `>32` adaptation gate never trips) — not a cold-start gap, a *permanent* one, found by direct reasoning about the two amplitude conventions before writing any wiring code, not by a failing test. Fixed with a single documented multiply (`raw * 32768.0`) at the `LevelAgc` boundary (`AnalogFmSstvDecoder.AgcSampleAt`), keeping every constant inside `LevelAgc` itself (32, 16384, the `>32` gate) literally identical to legacy's.
      - **Wired as a single, never-reset, continuously-running shared per-sample cache** (`_agcSamples`/`_agcCurMaxSamples`/`_levelAgcProcessedUpTo`), matching that legacy's `Stop()` (`sstv.cpp:1769-1791`) never touches `m_lvl` — the only real `Init()` call sites are the PTT-transition ones (`Sound.cpp:398/443`), which for an RX-only decoder map to construction, not `EndOfImage()`. Feeds every existing sync/tone-envelope consumer: `TrySyncIntervalDetection`'s `d12`/`d19`/`dsp`, `VisLockStateMachine`'s four detectors, and (piece 7b2, deliberately split out so its own fallout could be measured separately) `ApplySlantTracking`'s peak-position detector — legacy feeds that same envelope from the AGC'd signal too (`sstv.cpp:2292-2299`, live branch since `NARROW_SYNC==1900`). `TryDecodeNarrowModeHeader` was checked and confirmed *not* a consumer — it discriminates narrow-vs-VIS headers via already-demodulated-frequency averaging, a port-specific technique with no direct `CLVL`-consuming legacy counterpart, not an oversight to fix.
      - **Re-measured, not assumed, three round-trip tolerances after 7b2 and again after 7c wired the absolute thresholds in.** CLVL's AGC correctly hard-clips a full-amplitude tone to `±16384` once warmed up — legacy-faithful (real signals get this same treatment), not a port bug — which measurably increases peak-position/trigger-timing noise: `SyncBypassDetectionTests` (Scottie S1 18.06→19.01, Martin M1 12.62→12.65, Martin M2 20.78→25.13, SC2-180 12.91→13.85, tolerance 25.0→29.0) and three `VisLockStateMachine`-anchored tests (`VisLockStateMachineDecoderTests` 10-ish→11.49/13.0, `MidReceptionRestartTests` 9.81→11.84/14.0, `EndOfImageResetTests`' second-transmission delta 9.39→11.42/14.0 — its *first*-transmission delta stayed at 2.18, unchanged, confirming these changes are correctly scoped to the AGC'd path only). Every new tolerance was measured via the actual failing/passing value, not guessed headroom.
      - **`m_SLvl`/`m_SLvl2`/`m_SLvl3` reintroduced (piece 7c), values corrected from an earlier wrong assumption by reading the constructor directly.** `SetSenseLvl` (`sstv.cpp:1793-1817`) has 4 cases keyed on `m_SenseLvl`; the shipped default is **case 1 (`SLvl=3500`/`SLvl2=1750`/`SLvl3=5700`)**, not the switch's `default:` branch (`2400`/`1200`/`5000`) an earlier note here assumed — `CSSTVDEM`'s ctor sets `m_SenseLvl=1` unconditionally (`sstv.cpp:1489`) before calling `SetSenseLvl()`, and the only other write path (`Main.cpp:1865`'s INI read) falls back to that same ctor value when the key is absent. Case 0 is only reachable via a discrete "Sense Level" UI setting (`Option.dfm`'s `RGSLvl`) this port doesn't expose yet.
      - **Full three/five-term conditions applied, not just the previously-ported relative comparisons**, all confirmed by reading the literal source rather than trusting the prior summary: `TrySyncIntervalDetection`'s `m_sint2` trigger (3-term, `sstv.cpp:1905`) and `m_sint3` trigger (5-term, `sstv.cpp:1926` — its difference term is gated by `SLvl`, *not* `SLvl3`, an asymmetry easy to miss); `VisLockStateMachine`'s `Search`/`ConfirmLock` (3-term, `sstv.cpp:1946/1958`) and `Verify` (only a **2**-term condition, `sstv.cpp:2133` — confirmed by direct read that there's no difference-gate term at this specific site, unlike the other two). The per-bit reject (`sstv.cpp:1981-1984`) turned out to be missing a whole second OR'd branch entirely, not just unthresholded: `(d11<d19 && d13<d19) || (fabs(d11-d13) < m_SLvl2)` — the "too close to call" branch was absent from this port before piece 7c, a real gap the earlier-summarized "relative half only" framing had missed.
      - **AFC silence gate (`m_lvl.m_CurMax>16`) ported** (`sstv.cpp:2258`, case 0/PLL — the case `ApplyAfcCorrections`' own doc comment already cites as what it models). Confirmed the gate wraps the frequency-counter call itself in this case, not just the correction application (unlike case 1/zero-crossing's shape, where the counter always runs and only the correction is gated) — so `ZeroCrossingFrequencyCounter.ProcessSample` is skipped entirely when the gate fails, matching legacy's exact call shape, using a new per-index `_agcCurMaxSamples` snapshot (the gate needs the *historical* `CurMax` at each corrected sample, not "whatever `LevelAgc.CurMax` is right now" — this port's AFC correction runs as a deferred bulk pass, potentially well after the shared AGC cache has advanced past that index for an unrelated consumer).
      - **`SenseLevelCalibrationTests` added**: a full-scale tone through the real `LevelAgc`+`SyncEnvelopeDetector` pipeline must clear `SLvl`/`SLvl3` (the tightest of the three) with real margin — a calibration sanity check that the reintroduced constants aren't miscalibrated to be permanently unreachable, which would silently disable every trigger this piece just wired in without any test noticing.
      - **`m_sint1` ported (piece 7d) as a fourth `SyncIntervalTracker` instance**, `isNarrow:false`, no mode allowlist (unlike `m_sint2`'s `SyncBypassTrustedModes`) — confirmed by reading `sstv.cpp:1899-1904` directly: `m_sint1.SyncStart()` is polled first, every sample, unconditionally, and wins outright with no filter if it matches. Critically, it does **not** independently scan like `m_sint2`/`m_sint3` (which get their own separate, always-checked lower threshold) — it only ever receives new peak data from the *same* primary threshold that also drives the real VIS-bit-decode state machine forward: `SyncTrig` on that threshold's rising edge (`sstv.cpp:1949`), `SyncMax` while it holds (`sstv.cpp:1958-1961`). Implemented via a local case-0/case-1 latch (`_syncBypass1PrimaryHeld`) inside `TrySyncIntervalDetectionStep` (piece: `m_sint1`-decoder-ordering fix, see below, renamed this method from `TrySyncIntervalDetection` — a deliberate second copy of what `VisLockStateMachine` already tracks internally via its own `Search`/`ConfirmLock` states. **Correction, un-updated here until now even though the finding itself was recorded elsewhere in this file (the piece 7 holistic review below):** the two latches do *not* "necessarily agree sample-for-sample" — only true pre-lock; while locked, only `VisLockStateMachine`'s detectors run, and the two detector pairs can genuinely disagree for a short settling window after `EndOfImage`. The original justification for the copy ("this loop has no access to `VisLockStateMachine`'s separate cursor/instance") is *also* now stale post-`m_sint1`-decoder-ordering-fix, which runs both interleaved with direct field access — the copy is kept for a different reason (legacy shares one d12/d19 computation per sample; unifying the two ported detector pairs is a bigger change, left as a candidate follow-up).
      - **Documented, not fixed: same-sample `m_sint1`-then-`m_sint3` double-fire can't happen in this port** (return-immediately-on-any-match, matching the existing accepted divergence already noted for `m_sint2`/`m_sint3`'s own case-0/1 freeze) — legacy's real case-0 body is straight-line code, so `m_sint1` winning and calling `Start()` doesn't stop the sibling `m_sint3` block from also evaluating that same sample; low severity, since legacy's own same-sample second fire is close to a no-op by the time it would matter (`m_Sync` is already 1).
      - **`SyncBypass1DetectionTests` added**: proves the actual behavioral difference from `m_sint2`, not just "another interval tracker" — a headerless Robot 36 transmission (a mode `SyncCheckSub` recognizes, confirmed via `SstvModeRegistry.GetSyncIntervalMatchDepth`, but which `SyncBypassTrustedModes` does *not* include) locks via `m_sint1` alone, a scenario `m_sint2`'s own allowlist would reject outright even if its internal periodicity match succeeded.
      - Full suite after piece 7 (7a-7d): 192/192 in `ScanlineStudio.Core.Sstv.Tests` (185 + 4 `LevelAgcTests` + 2 `SenseLevelCalibrationTests` + 1 `SyncBypass1DetectionTests`), solution-wide build clean.
      - **Known, explicitly logged remaining gap, not silently closed as part of "complete":** legacy's pre-AGC input chain (`sstv.cpp:1823-1833` — a one-pole averaging LPF, `d=(s+m_ad)*0.5`, then a 400-2500Hz bandpass, `HBPFS`) has no counterpart in this port. Both also feed `m_lvl.m_Cur` and therefore the pixel demodulator, so closing this gap properly is a materially larger change than this piece's scope (adding it here would mean re-touching pixel decode, not just sync detection) — named explicitly as a real, currently-open gap rather than folded into "piece 7 done," since the reintroduced absolute thresholds in 7c assume legacy's exact pre-AGC chain and this port's calibration tests measure against a slightly different (BPF-less) signal path. A candidate follow-up if real captured audio (as opposed to this port's own synthetic fixtures) ever shows the AGC's peak measurement picking up out-of-band noise this filter would have excluded.
      - **Holistic adversarial review of the whole 7-piece system, then fixes** (a different kind of review from the piece-by-piece ones above: explicitly scoped to cross-piece interactions no single piece's own review could have seen, since each was reviewed before the next piece even existed). Found 2 real bugs, 2 doc-only issues, and re-confirmed everything else already checked (absolute-threshold coverage across all 7 legacy `m_SLvl` use sites, `SyncCheck` candidate iteration order, AGC cache continuity across `EndOfImage`) — full findings and reasoning preserved in this session's own history, summarized here:
        1. **`m_sint1`'s peak was being consumed one sample after being latched, before `SyncMax` ever ran — a real bug, fixed.** `_syncBypass1Tracker.TryStart()` was polled unconditionally every sample; `SyncIntervalTracker.TryStart` unconditionally zeroes `_peakAmplitude` on any call where it's non-zero, so the very next sample after `Trigger()` set a fresh peak, `TryStart()` consumed and recorded it — anchoring every match at the threshold-crossing *edge*, before `SyncMax` (called later that same sample, since the latch update runs after `TryStart` in the loop) ever got a chance to track the pulse's true running max. Worse: with no gate, this also let VIS data-bit tones (1100/1300Hz, only ±100Hz from `d12`'s 1200Hz/100Hz-bandwidth center) spuriously re-trigger `m_sint1` throughout every VIS-bit-decode attempt — legacy's real case-2/9 freeze (never calling `SyncStart`/`SyncTrig` there) has no equivalent in this port's merged loop. Fixed by gating `TryStart()` behind `!_syncBypass1PrimaryHeld` (i.e. only in the port's own case-0-equivalent state), mirroring legacy's own `SyncStart` being polled from case 0 only, never case 1. `m_sint2`'s existing code already had this property "for free" (its held-branch condition subsumes its `TryStart` branch's own threshold, so a held `m_sint2` never calls `TryStart` either) — `m_sint1`'s bare top-of-loop poll had no such built-in protection, which is why only it needed the explicit fix. Re-measured `SyncBypass1DetectionTests` after the fix: 39.03 (up from an unverified, borrowed 29.0 tolerance) — worse, not better, for Robot 36 specifically, plausibly (not independently confirmed) because `m_sint1`'s stricter `SLvl` threshold lets the "held" window run into nearby-frequency image content under CLVL's hard clipping; tolerance bumped to 43.0 with this reasoning recorded honestly rather than as a proven mechanism.
        2. **AFC double-correction after a mid-reception restart is not bounded to "one line" as a prior review claimed — a real bug, fixed.** `ApplyAfcCorrections` ran once per outer-loop iteration, eagerly, all the way to `_afcBoundSample` — i.e. up to the *entire* abandoned transmission's own nominal image extent — before any of its lines were even decoded, and restart detection only happens per-line, well after that eager pass already ran. `InitializeAfc` then rewound `_afcProcessedUpTo` back to the new anchor unconditionally, letting the new `AfcTracker` re-correct (additively, not overwrite) content the old one had already corrected — for `MidReceptionRestartTests`' own 30%-truncation shape, potentially most of the second image. Fixed two ways: `ApplyAfcCorrections` now takes an `upperBoundSample` and is called once per line (bounded to that line's own extent, mirroring `ApplySlantTracking`'s existing per-line bound) instead of once eagerly for the whole image — a causal, resumable tracker produces identical results regardless of when the eager pass ran relative to decode, so this is a no-op for a transmission that completes normally, and restores the "at most one line" property the incorrect prior claim assumed was already true. `InitializeAfc` also now uses `Math.Max(_afcProcessedUpTo, _consumedSamples)` instead of a bare assignment (the same defensive pattern already used for `_visLockProcessedUpTo` in `Commit()`), closing the double-correction even if some future change reintroduces eager lookahead. Honestly not covered by a dedicated regression test: measured the fix's actual effect at the same realistic 500ppm mismatch `NarrowModeAfcTests`/`SlantTests` use (14.14 buggy vs. 13.68 fixed, effectively noise) — the same "doesn't cleanly discriminate at realistic severity" outcome already hit for the original AFC-bound fix and `TryDecodeNarrowModeHeader`'s own AFC bug, so no test was shipped claiming to prove this; verified by direct code trace instead, following the exact precedent piece 6a's own AFC-bound fix already set for this codebase.
        3. **Two doc-only fixes.** `_syncBypass1Tracker`'s doc comment claimed its latch and `VisLockStateMachine`'s own internal state "necessarily agree sample-for-sample" — true only pre-lock; while locked, only `VisLockStateMachine`'s detectors run (matching legacy's own `!m_Sync` gating), so the two detector pairs carry different filter histories by the time `EndOfImage` resyncs their cursors, and can genuinely disagree for a short settling window at the start of every transmission after the first — corrected to say so. A 9-line comment block in `TryDecodeNarrowModeHeader` that had been accidentally pasted three times in a row (copy-paste artifact, no behavior effect) was reduced to one copy.
        - **Two real findings, deliberately not fixed in this pass — logged as open follow-up items, not silently absorbed:**
          1. `PllFmDemodulator` never got the piece 7 scale-bridge treatment `LevelAgc` did — **fixed shortly after, see below.**
          2. ~~`m_sint1`'s decoder-level priority is effectively inverted from legacy's real per-sample interleaving~~ — **fixed.** `TryDecodeHeader` used to try the fixed-window path, then `VisLockStateMachine` over the *entire remaining buffer*, and only then `TrySyncIntervalDetection` (where `m_sint1` lives) — whereas legacy checks `m_sint1` first, every sample, genuinely before `VisLockStateMachine`'s equivalent (cases 1-3/9) ever gets a chance to accumulate a false positive. Deferred at the time specifically until a real audio engine existed to force a genuine streaming-architecture decision (`MiniAudioEngine`, this same session) — picked up once it did. Plan verified with Opus first (which caught real errors in the initial sketch: a wrong justification for why the two scan cursors stay in lockstep — the real invariant, confirmed by checking every `_mode` assignment in the file, is that `EndOfImage` is the *only* place `_mode` becomes null again, and it always resets both cursors together, not "they're always reset together" as first assumed — plus an off-by-one in the sketch's manual cursor increment on a match). Fixed by extracting `TrySyncIntervalDetection`'s per-sample body into `TrySyncIntervalDetectionStep()` and adding `TryInterleavedHeaderScan()`, which runs it and `VisLockStateMachine.ProcessSample` in lockstep, one sample at a time, in legacy's real per-sample order (`sstv.cpp:1897-1951`, re-read directly to confirm, not assumed from this note's own prior wording). New test (`SyncScanInterleaveTests`) proves it deterministically rather than trying to synthesize the false-positive risk directly: a headerless Robot 36 transmission (recognizable via `m_sint1` alone) concatenated directly before a header-included Martin M1 transmission — before the fix, `VisLockStateMachine` sweeps the whole buffer and finds Martin M1's real header first (confirmed empirically by temporarily reverting the fix and re-running the test, which fails with `martin-m1` detected first); after the fix, Robot 36 is recognized first, matching legacy's real behavior. All 193 pre-existing tests still pass unchanged (verified by running the suite, not just reasoned about) — none of them contain a competing earlier sync-bypass match, so `VisLockStateMachine` still resolves each of *their* transmissions at the same absolute sample index it always did. Two things intentionally left open, not silently closed: legacy's case-1/2/3/9 *gating* of `m_sint1`/`m_sint2`/`m_sint3` (the port's merged loop still runs them unconditionally, already-documented pre-existing divergence, unchanged by this fix) and the "deliberate second copy" of the d12/d19 envelope detectors between `TrySyncIntervalDetectionStep` and `VisLockStateMachine` (now running interleaved, so unifying them is possible, but changes `VisLockStateMachine`'s public shape — left as a candidate follow-up, not attempted here).
            - **Opus review round 1 (of this fix specifically), findings independently re-verified before fixing.** Core mechanic confirmed correct (cursor lockstep invariant, legacy per-sample order, AGC index consistency, no skipped/double-processed sample, chunk-size invariance) — nothing there needed changing. Real findings, all fixed: (1) the cursor-equality check was a bare `Debug.Assert`, stripped entirely by `ci.yml`'s `--configuration Release` builds and therefore providing zero real protection in CI (the only `Debug.Assert` anywhere in this codebase) — replaced with an unconditional `InvalidOperationException` check; (2) a real, previously-unnoticed second behavior change: a sync-bypass match's `_visLockOriginSample` used to land at the buffer's end (`Commit`'s `Math.Max` against a `_visLockProcessedUpTo` already exhausted by the old sequential `TryVisLockStateMachine(_rawSamples.Count)` scan), now lands near the actual lock point instead — which means piece 6c's mid-reception re-verification, previously an accidental no-op for the rest of a bulk-pushed sync-bypass-locked transmission, now actually runs, extending `VisLockStateMachine`'s own already-accepted false-positive risk to a scenario it was previously (accidentally) immune to; closer to legacy's own case-0 trigger's behavior *at its shipped defaults* — not a universal legacy truth, since the whole switch only runs while locked because of the enclosing `sstv.cpp:1889` gate (`!m_Sync || m_SyncRestart || m_SyncAVT`), satisfied by `m_SyncRestart` defaulting to 1 (`sstv.cpp:1486`), a real user-toggleable option this port hard-wires on — not a regression, but a real reachability change that went undocumented until this review — now documented on `Commit`'s own comment rather than left as a silent side effect, and (per this project's own established precedent for that same pre-existing risk) not chased with a dedicated false-positive-forcing fixture. (3) An overclaiming comment ("this is a no-op for any real-header transmission") contradicted by this fix's own test — corrected to the narrower, actually-true claim (VisLockStateMachine's internal stepping is unaffected; the first-*detected* mode can and does change whenever a sync-bypass tracker matches earlier). (4)/(5) a wrong legacy file citation (`sstv.cpp` → `Main.cpp`) and a stale field comment whose justification the fix itself had already invalidated, both corrected at their original locations. Test strengthened per review: pinned the exact detected-mode sequence and a zero-restart count (measured directly, not assumed) instead of a looser "contains" check, and added a chunked-`PushSamples` variant proving the fix's own chunk-size invariance claim empirically rather than leaving it as a reasoned-but-unverified assumption. 194/194 tests pass (195 after the chunked variant), solution-wide build clean.
            - **Opus review round 2 (of round 1's own fixes), findings independently re-verified before fixing.** Round 1's core fixes held up under direct tracing (the `InvalidOperationException` fires under the exact same condition the old `Debug.Assert` did, with no gap and no reachable false trigger; the `Commit` `Math.Max` reachability claim was re-derived by hand for a concrete sync-bypass-match scenario and confirmed correct; the chunked-streaming test genuinely exercises different per-sample state, though it would likely pass pre-fix too since 1024 samples is nowhere near enough head start to matter, so it's chunk-size-invariance coverage, not a second discriminating regression test). Real findings, fixed: (1) the "permanently-ungated" wording this review round's own round-1 predecessor introduced on `Commit`'s comment (and one pre-existing occurrence of the same imprecision on piece 6c's own inline comment, and in this file above) was wrong — `sstv.cpp:1897`'s switch is gated by `sstv.cpp:1889`, and only looks unconditional at legacy's shipped defaults (`m_SyncRestart = 1`) — corrected at all three locations with the precise gate/default-value citation rather than restated as loose shorthand; (2) `Commit`'s first two paragraphs still described pre-fix control flow (referring to `TryDecodeHeader` "falling through to `TryVisLockStateMachine`," and enumerating the non-self-triggering paths without the sync-bypass case that round 1's own third paragraph had to reintroduce as an unlisted "third case") — folded into one accurate enumeration; (3) a pre-existing wrong citation on piece 6c's inline comment (`sstv.cpp:1949/1953/1959` attributed to "`m_sint2` and `m_sint3`" when 1949/1959 are actually `m_sint1`'s own gates) — corrected to the real `m_sint2`/`m_sint3` gate lines (`sstv.cpp:1899`/`:1953`, and `:1927-1937` for `m_sint3`); (4) a test-count typo in this file's own round-1 entry (196 → 195, only one test method was added); (5) a forward-looking note added to the `InvalidOperationException` comment that `MiniAudioCaptureSession`'s bare `try/catch` around `SamplesAvailable` would silently swallow this exception once a production caller wires the decoder to real capture (no such caller exists yet); (6) added `PiecesSixCReachabilityTests`, a positive-engagement check that piece 6c's mid-reception re-verification actually fires for a sync-bypass-locked transmission (round 2's proposal) — `SyncScanInterleaveTests`' zero-restart-count assertion only proved the newly-reachable path doesn't misfire on the exact fixture (Robot 36) `VisLockStateMachine`'s own doc comment names as the false-positive risk case, not that the path engages at all; this test truncates a sync-bypass-locked Robot 36 transmission mid-body and confirms Martin M1 is still separately detected via its own real header, with `restartCount == 1`. 196/196 tests pass (up from 195), solution-wide build clean in both Debug and Release configurations.
            - **Opus review round 3 (final, of the 3-round cap), findings independently re-verified before fixing.** Re-derived round 2's `m_SyncRestart`/`sstv.cpp:1889` gating claim and its `m_sint2`/`m_sint3` citation fix directly from source (both confirmed exact, line-for-line) and worked through `PiecesSixCReachabilityTests`'s timing arithmetic by hand (Robot 36's m_sint1 lock at ~754.5ms leaves ~7x margin before the 30% truncation point; the `restartCount == 1` assertion is what actually discriminates the bug, not the mode sequence — confirmed the counterfactual would produce `restartCount == 0` instead). One real finding, fixed: `SyncScanInterleaveTests`' own doc comment (introduced in round 1, missed by round 2) claimed Martin M1 is detected post-fix "via its own real header at `EndOfImage`" — wrong; `EndOfImage`'s 500ms skip lands past M1's real header window in this fixture, so it's actually found via the sync-interval bypass's periodicity (M1 is a `SyncBypassTrustedModes` member), the same way a real headerless capture would find it — corrected, plus a note recording the `restartCount == 0` assertion's ~145ms fragility margin so a future maintainer doesn't misdiagnose an unrelated timing change as a regression in this fix. Also fixed a smaller staleness round 2 left behind: `Commit`'s comment said a sync-bypass match "doesn't touch this cursor on its own" when enumerating the "still at 0" case — wrong, `TryInterleavedHeaderScan` does advance `_visLockProcessedUpTo` in lockstep for every sample including a sync-bypass match; narrowed the "still at 0"/"never touched" framing to only the fixed-window/narrow/AVT paths, which is where it's actually true. Two optional/cosmetic findings (a very minor `VisLockStateMachine` doc-comment attribution nuance, and this file's lack of a note on `_rawSamples`/`_demodulatedFrequencies`/`_agcSamples`' unbounded memory growth — ~1.1GB/hour at 11025Hz, ~4.4GB/hour at 44100Hz, harmless for today's test-only callers but relevant once a production caller wires this decoder to real capture, the same moment round 2's `MiniAudioCaptureSession` caveat already flags) were not required fixes; the memory-growth note is now logged here as a companion to that caveat, since no such note existed anywhere in `src/` or `spec/` before this. No decode-logic changes in this round — all three rounds combined found nothing wrong with the fix's actual behavior, only with how it was documented. 196/196 tests pass, solution-wide build clean in both Debug and Release configurations. This closes the 3-round review cap for the m_sint1 decoder-ordering fix.
        - Full suite after these fixes: 192/192 in `ScanlineStudio.Core.Sstv.Tests` (unchanged count -- one test's tolerance re-measured, one exploratory test written then dropped after measurement showed it didn't discriminate cleanly), solution-wide build clean.
      - **Opus sequencing consultation: what to tackle next, now that the 7-piece breakdown and its holistic review are both done.** Candidates weighed: closing the 11025Hz mode-table gap (below), fixing finding 2 above, porting legacy's pre-AGC input chain (the piece 7 gap), legacy's fine-pixel-alignment bootstrap (`m_wBgn`), or moving to Phase 2 (radio layer). The consultation surfaced two things that reordered the whole list, both independently verified before trusting them:
        - **Phase 1 isn't actually done.** `ScanlineStudio.Core.Audio` has no real backend, only `FakeAudioEngine`/`WavFile` (confirmed: `ls` shows exactly those two files). Zero fixture/golden-vector directories exist anywhere in the repo (confirmed: `find` for `*fixture*`/`*golden*` returns nothing) — the item this file's own Phase 1 bullet flags as "only gets harder to justify later" has never been started. Stepping outside SSTV DSP should mean one of *these* two, not jumping ahead to Phase 2.
        - **The 11025Hz mode-table gap is currently unactionable, not just unfixed.** The 10.0-average-per-channel-delta tolerance it's measured against is this project's own invented bar (`SstvRoundTripTests`'s own class comment already says as much), never checked against what legacy's own decoder actually achieves for the same input. Three of the four bugs the very first adversarial review caught (chroma +128, Scottie's missing post-VIS pulse, AVT's entire missing preamble) were invisible to round-trip specifically because self-consistency can't catch "both sides agree with each other but disagree with legacy" — and this gap has never been checked against a real legacy reference at all.
        - **Golden-vector capture — done, and the mode-table gap above is now measured, not just suspected.** Martin M1 as a "does the methodology work" baseline, Robot 36 as the actual worst-case data point for the 11025Hz question. The user captured real TX/RX fixtures via a working legacy YONIQ install: `Sound.cpp`'s `CWaveFile::Rec`/`Play`, transmitting two source images generated matching this suite's own gradient-fixture formula exactly. Two corrections to how this capture mechanism was originally described here, found and fixed while building the test harness (`GoldenVectorTests.cs`, `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/`): (1) `.mmv` is **not** RIFF/WAV — confirmed directly against `Sound.cpp:638-656`/`564-611`: a 4-byte header `[0x55,0xAA,SampType,0]` followed by raw little-endian `int16` samples, no chunk structure at all; (2) it is **not** "no soundcard loopback" either — `Sound.cpp:325-395`'s loop shows `Wave.InClose()` (`:395`) only closes the sound-card input while transmitting, so the captured file's pre/post-TX portions are real recorded sound-card/mic audio, and even the TX portion itself is the *previous* loop iteration's modulator output (a one-buffer delay, with the modulator's final buffer never captured) rather than a clean same-iteration tap. Neither error affects the golden-vector results below (the decoder's sync-search finds the header regardless of what precedes it), but both were live, uncorrected claims in this file until now. Fixtures moved from the repo root (they were plain untracked files, not gitignored — a third stale claim in the original version of this bullet) into `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/` per [[13-testing]]'s own established fixture-directory convention, with a new `LICENSES.md` entry (the `.mmv`/`_RX.bmp` files are outputs of running the legacy binary, which CLAUDE.md's license-audit rule covers) and a `README.md` documenting exact capture/measurement provenance.
        - **Measured results, closing the loop this bullet's own predecessor left open.** Legacy's own decode-vs-source baseline (zero C# DSP): robot-36 = 6.99, martin-m1 = 1.57 average per-channel delta — the first real answer to "what does legacy's own decoder actually achieve on this input," replacing the invented-bar problem named above. C# decoder decoding the real captured audio, vs. source: martin-m1 = 11.78 (close to both legacy's own baseline and this suite's usual 10.0 synthetic-round-trip tolerance); **robot-36 = 68.06 — the suspected 11025Hz/low-samples-per-pixel gap, now confirmed against a real legacy capture, not just a synthetic experiment.** An image-domain encoder cross-check (self-encoded-then-self-decoded vs. real-audio-decoded, avoiding a signal-domain PCM comparison that the capture-mechanism corrections above show wouldn't even be clean) landed close to the same numbers (martin-m1 = 13.66, robot-36 = 60.89), consistent with the gap being in shared decode-side DSP (AFC/PLL settling, per the existing suspicion two bullets up) rather than an encoder-specific bug. All golden-vector tests assert regression-guard tolerances only, explicitly documented as not parity claims, per this bullet's own anti-invented-bar rule. Fixing the underlying Robot-36-at-11025Hz gap itself is deliberately out of scope here (test-code-only pass, by explicit user instruction) — remains open follow-up work.
          - **Round-1-review finding on this same data, magnitude not previously noted.** This same gap's own synthetic self-round-trip number, measured two bullets up after the single-sample-readout fix, is 13.4 — roughly **5x smaller** than the 68.06 measured here against real captured audio. The gap's *existence* was already known (this bullet's own predecessor, and `SstvModeRegistry.cs`'s doc comment); the *magnitude asymmetry* was not, until independent review caught it. A mode that scores 5x worse against real legacy audio than against the port's own self-generated signal is exactly the "encoder and decoder agree with each other while both being wrong about reality" failure mode CLAUDE.md's behavioral-parity rule exists to catch — worth flagging as its own follow-up question (is the gap actually AFC/PLL settling, or something specific to real analog/soundcard characteristics the synthetic self-test never exercises?), not folded silently into the already-known bucket. Also found and fixed in the same review round: the 75.0 robot-36 tolerance is not meaningfully discriminating on this specific fixture — measured that a flat gray image, a mirrored copy, a flipped copy, and a channel-swapped copy of the source all score ~42.67 under the same metric (this gradient image is smooth enough that most structural corruptions land in a narrow band), and robot-36's real measured deltas (68.06 / 60.89) are already worse than that floor — so no tolerance can currently both accept today's real output and reject a structural bug for robot-36 specifically. Kept as a regression tripwire only, documented as such directly on both tests; martin-m1's tolerances remain genuinely discriminating. A timing-check test in the same file (TX-region duration vs. expected transmission length) had also mis-attributed a real, fixed-duration legacy TX segment (`TMmsstv::OutHEAD`'s 800ms leader tones, `Main.cpp:7270-7292`/`:7393`, plus `SendSSTV`'s own ~550-846ms footer, `Main.cpp:6996-7009`) to "envelope-detection slop" — corrected to account for both segments directly from source, tightening that test's tolerance from 5.0s to 1.0s (the old bound was wide enough to accept a ~30%-wrong `LineDurationMs` as passing). A minor aliasing fragility was also fixed: `LineDecoded`'s image reference is the decoder's own live pixel buffer, safe to read after the fact today only because `Commit()` happens to always allocate a fresh array per lock — now copied into an explicit snapshot in the test instead of relying on that invariant.
          - **Round-2-review finding: round 1's own timing fix had a real bug, not just a rough edge.** `SSTVSET.m_TW`, the variable round 1's footer-duration fix keyed off `mode.LineDurationMs` for, is not the transmitted mode's timing at all — traced directly against source: `m_TW` is set by `CSSTVSET::SetSampFreq` (the RECEIVE side, `sstv.cpp:655-1109`) as `GetTiming(m_Mode) * m_SampFreq / 1000.0`, where `m_Mode` is the demodulator's currently-selected mode; the TX-side equivalent is a genuinely separate field, `m_TTW` (`CSSTVSET::SetTxSampFreq`, `sstv.cpp:1280-1285`), which `SendSSTV`'s footer does not use. So this footer segment's length depends on whatever mode the RX side happens to be sitting on when TX finishes, not on the mode being sent — confirmed empirically that both fixtures show the same ~425ms footer segment despite transmitting different modes, consistent with both captures' RX side sitting at `GetTiming`'s own `default:` case (smSCT1, 428.22ms, `sstv.cpp:1275`) the whole time. Using `mode.LineDurationMs` was only coincidentally close for these two specific fixtures (both well under the 500ms clamp); it would have been actively wrong for a mode whose duration differs meaningfully from 428.22ms. Fixed with a named constant tied to the real legacy default instead. Same review round also found: `MeasureTxRegion`'s own envelope-window-to-seconds conversion mixed a nominal 50ms constant with the actual (slightly different, `(int)(0.05*11025)=551`-sample ⇒ 49.977ms) window size, a systematic scale error proportional to elapsed time — fixed by deriving the conversion from the real window size. With both fixed, the timing test's residuals dropped to 0.04s/0.02s (from an already-corrected-but-still-off 0.34s/0.05s), and its tolerance was tightened again, from 1.0s to 0.2s. Also found: round 1's own fixes to the fixture `README.md` never landed — the file still stated the pre-round-1 "envelope-detection slop" explanation and the pre-round-1 "known gap, not new information" framing for the 5x-magnitude finding, verbatim, even though both were corrected in `GoldenVectorTests.cs` and here in this file. Propagated all round-1 and round-2 corrections to the README, including the corruption-floor measurement it was previously missing entirely. Two smaller findings, also fixed: the class-level doc comment's claim that the timing/cross-check test pair "capture most of the realistic value of an encoder check" was left unqualified for both modes even though round 1's own per-test comment already said robot-36's arm doesn't meaningfully discriminate — narrowed to say so explicitly; and a comment overstated `Snapshot()`'s protective value as if it addressed a hazard that in fact remains present at roughly 15 other `LineDecoded` capture sites across this test project — corrected to state its actual (narrower) scope, with a note that enforcing the underlying invariant at the decoder itself would be the more robust fix, left as separately-scoped follow-up. 208/208 tests pass, solution-wide build clean in both Debug and Release configurations.
          - **Round-3-review (final, of the 3-round cap): re-derived round 2's own fix from source independently, confirmed it correct, but found its stated justification was itself inaccurate.** Re-traced `m_TW`/`m_TTW`, `WriteC`'s sample-count units, and `GetTiming`'s `default:`↔`smSCT1` correspondence directly from `sstv.cpp`, and independently measured a ~429ms 1500Hz footer tone in both real captures via a from-scratch frequency trace — confirming round 2's fix and its 428.22ms constant are genuinely correct for these two fixtures. But the comment's claim that the RX side was sitting at "the demodulator's default/never-changed mode on a fresh run" is not accurate as a general statement: `Main.cpp:1872-1873` shows `SSTVSET.m_Mode` is reseeded from the *persisted* `Define/SSTVMode` ini value on every load, not fixed at a constructor default — the 428.22ms value holds for these two captures only because that persisted value happened to be Scottie 1 (matching the constructor default) during this capture session, not because the value can never change. Reworded the comment to state this as a fact about these specific committed fixtures, not a property of legacy in general, and renamed the constant (`receiveSideDefaultLineDurationMs` → `receiveSideModeLineDurationMsForTheseFixtures`) to make that scoping visible at the call site too. Also found: trim work done in this same session (below) had updated the `.mmv` fixtures but left `GoldenVectorTests.cs`'s TX-duration comment holding pre-trim numbers, and the fixture `README.md`'s new trim section overclaimed that *all* measured values (including TX-region duration specifically) were "found unchanged" post-trim — false at the precision the duration test actually asserts, since trimming moves the TX region's absolute offset within the file and therefore re-phases the fixed-size envelope-detection window grid against it (a real, expected, sub-window-size effect, not a timing regression). Both corrected with the actual re-measured post-trim numbers. No further findings beyond doc-accuracy — round 3 explicitly confirmed the three-round streak of "each round finds a real bug in the previous round's fix" ends here; the harness itself was sound as committed after round 2, only its comments needed one more accuracy pass. 208/208 tests pass, solution-wide build clean in both Debug and Release configurations. This closes the 3-round review cap for the golden-vector test harness.
      - **`PllFmDemodulator`'s own missing scale-bridge — fixed while golden-vector capture was in progress** (not blocked on it, per the consultation above: independently verifiable by direct math against `sstv.cpp:316-343`, no legacy reference needed). Confirmed by re-deriving both sides of the AGC formula: legacy's `CPLL::Do` resets its own tracking window to `m_Max=1.0/m_Min=-1.0` every half-cycle too (the *same* literal floor this port already used) — at legacy's real int16-scaled full-scale input the floor never binds and the AGC adapts normally, while this port's float `[-1.0,1.0]` full-scale input sits *exactly* at the floor, which is why both sides coincidentally converge to the identical effective gain (`5.0/2.0=2.5`) at full scale, and why every existing fixture (all full-scale) never caught the gap. Below full scale, though, this port's floor never released — the AGC stayed pinned at 2.5 regardless of actual amplitude, giving this port's PLL effectively no AGC at all below roughly `-8dBFS`, unlike legacy's. Fixed with the same `raw * 32768.0` scale bridge `LevelAgc`/`AgcSampleAt` already established as this port's convention, applied at both real call sites (`AnalogFmSstvDecoder.PushSamples`, and `AvtTrainingLockStateMachineTests`' own direct construction, updated for consistency though a no-op there at that test's own full-scale amplitude). New `PllScaleBridgeTests`: decoding a -20dBFS-attenuated Martin M1 transmission, comfortably within the standard 10.0 tolerance with the fix, and confirmed (not assumed) to actually discriminate the bug by temporarily reverting it and re-measuring — 58.55 average per-channel delta without the fix, badly over tolerance, one of the cleanest-discriminating regression tests added this session. 193/193 tests pass, solution-wide build clean.
      - **Robot-36-at-11025Hz decode-gap investigation — STARTED, live log, not yet resolved.** User flagged this as sensitive/correctness-critical work and asked for extra rigor: explicit legacy-fidelity framing in every step, incremental testing (verify each sub-step before moving on), a higher-effort agent instead of Opus for wide investigative passes, and this log kept current at every step (not just a final summary) so the work is resumable cold, in a brand-new session, without any conversational context.
        - **Why this is the current target**: the golden-vector harness above (`GoldenVectorTests.cs`) just measured a real, previously-only-synthetic gap concretely: Robot 36 decoded from real captured legacy audio at 11025Hz scores **68.06** average-per-channel delta against source — 5x worse than the existing synthetic self-round-trip number for the same mode/rate, **13.4** (`SstvModeRegistry.cs`'s own doc comment, `spec/14-roadmap.md` above). A mode that agrees with the port's own encoder far better than with real legacy audio is exactly the "both sides wrong about reality the same way" failure CLAUDE.md's behavioral-parity rule exists to catch.
        - **What's already ruled in/out, from before this investigation started** (do not re-litigate without new evidence): the single-sample-readout fix (replacing invented block-averaging with legacy's real `GetPixelLevel`/`GetPictureLevel` per-pixel dereference, `Main.cpp:4144-4148`) already improved this mode from 21.3→13.4 on the synthetic test but did not close the gap. `AnalogFmSstvDecoder` already has AFC (`AfcTracker`/`ApplyAfcCorrections`), Auto Slant (`SlantTracker`), CLVL AGC (`LevelAgc`), and the full 7-piece VIS/preamble-lock system ported and tested (session commits before this log entry: `0d25ca4`, `03394b3`, `557e9c8`, `cbc1ec4`, `5006fc7`) — so "AFC not yet ported" is STALE as a hypothesis; if AFC is implicated, the bug is in what's already ported, not in a missing piece. `PllFmDemodulator`'s own scale-bridge bug (an AGC floor that never released below `-8dBFS`) was already found and fixed independently (bullet above, `PllScaleBridgeTests`) — ruled out as *the* cause of this specific gap since that fix didn't move the Robot-36 number, but worth keeping in mind as a nearby, related area.
        - **Leading hypotheses, not yet confirmed**: (a) `PllFmDemodulator`'s settling speed at Robot 36's very short per-pixel sample dwell time (`SstvModeRegistry.cs`'s own doc comment names this explicitly as a candidate, alongside AFC); (b) something specific to *real* analog/soundcard capture characteristics that the synthetic self-round-trip test never exercises (real capture noise floor, real clock drift, real ADC quantization) that a purely synthetic self-encode-then-decode test is structurally blind to; (c) not yet ruled out: an actual bug (not just "needs more precision") somewhere in the already-ported AFC/slant/AGC pipeline that only manifests at Robot 36's short dwell time. No investigation into any of these has started yet as of this log entry.
        - **Test oracles available for this investigation**: `SstvRoundTripTests` (synthetic self-round-trip, already existing), and the new `GoldenVectorTests` (real captured legacy audio, `Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource` for Robot 36 specifically) — the latter is the more important oracle here, since the whole point is closing the synthetic-vs-real gap, not just improving the synthetic number again.
        - **Next planned step (not yet done)**: read `CSSTVDEM`'s actual per-sample demodulation path (`sstv.cpp`, particularly `CPLL::Do` and the `SyncFreq`/`m_AFCDiff` AFC loop) side-by-side with this port's `PllFmDemodulator`/`AfcTracker`/`AnalogFmSstvDecoder` implementations, specifically hunting for any behavioral difference that would matter more at short (Robot-36-scale) dwell times than at long ones — this is a wide comparative-audit task, a candidate for a higher-effort investigative agent pass per the user's explicit instruction, before forming a concrete fix hypothesis to bring to an Opus plan-review.
        - **ROOT CAUSE FOUND (high confidence, independently verified against source, not just agent-reported).** A dedicated Opus-powered investigative agent pass (read-only, no code changes) found and I independently re-verified: this is a fixed-sample-count HEADER-ANCHOR error, not a PLL/AFC/settling problem. On real captures, `AnalogFmSstvDecoder.TryDecodeHeader`'s exact fixed-window VIS path (`AnalogFmSstvDecoder.cs:424-500`) can never succeed — real legacy TX always precedes the VIS header with `TMmsstv::OutHEAD()`'s 800ms of leader tones (`Main.cpp:7270-7292`, called unconditionally from `Main.cpp:7393`) plus ~1s of real pre-TX room audio (already documented on the golden-vector fixtures) — so the `[320,380]ms` discriminator window lands in noise, decodes to a byte outside `SstvModeRegistry`, and detection always falls through to the approximate `VisLockStateMachine` anchor instead. That approximate anchor carries a fixed lag (already known/named in this file at the "candidate 8th piece" entry a few bullets up: `VisLockStateMachine`'s own ~7ms trigger-lag imprecision) — measured directly against both real fixtures via a faithful line-by-line C# reimplementation of `LevelAgc`/`AgcSampleAt`/`TankFilter`/`SyncEnvelopeDetector`/`VisLockStateMachine`: **+94 samples (8.53ms) for robot-36, +96 samples (8.71ms) for martin-m1 — essentially mode-independent**, exactly as expected for a fixed-sample-count error. A synthetic self-round-trip test never exercises this path at all (the port's own encoder emits the VIS leader at sample 0 with nothing before it, so the exact fixed-window path always succeeds there) — this is the actual, structural reason real-audio delta is so much worse than the synthetic number, not "real audio is noisier."
          - **Why this hits Robot 36 ~5x harder than Martin M1**: a fixed +94-sample error costs `94 / samplesPerPixel` pixels of shift. Martin M1 (5.045 samples/px): 18.6px (5.8%). Robot 36 luma (3.032 samples/px): 31.0px (9.7%); Robot 36 chroma (1.516 samples/px): **62.0px (19.4%)** — plus Robot 36's luma and chroma scans have different pitches, so the error also introduces real Y/chroma misregistration Martin M1 structurally cannot have.
          - **Robot 36's second, catastrophic-magnitude consequence — verified independently, not just agent-reported**: `RobotScanlineDecoder.cs:71`'s tone-selector read (`sampleFrequencyAt(endSample - 1, endSample)`, the LAST sample of the ~2.25ms color-select tone window) is a faithful, correct port of legacy's real per-sample `m_DSEL` re-decision loop (`Main.cpp:4286-4297` re-evaluates every sample in `[m_SG, m_CG)`, so the final value is definitionally "whatever the last sample decided" — confirmed by reading that switch case directly) — **the read design itself is not the bug**. But with legacy's real anchor (via `SyncSSTV`, see below) that boundary always lands inside the true tone; with this port's un-corrected +94-sample-early anchor, the read point lands ~7ms into the FOLLOWING chroma scan instead, reading arbitrary chroma-scan content near the tone's ambiguity threshold — measured on the real capture: the true selector tone reads decisively (1499/2299/1501/2300...Hz) but the port's actual (anchor-shifted) read point gives ambiguous deviations on every single line, so `_lastSelectionIsEvenLine` toggles every line starting from line 0 — **R-Y and B-Y are swapped on every line of the decoded image**. This is why Robot 36's delta (68.06) clears even the ~42.67 "flat gray image" corruption floor documented on the golden-vector tests: swapped chroma + 62px chroma shift + 31px luma shift, stacked. This is a downstream CONSEQUENCE of the anchor error, not a second bug needing its own separate fix.
          - **The actual missing piece — confirmed present in legacy, confirmed absent from this port, by direct source reading (not agent-reported, read myself)**: legacy re-anchors the per-pixel phase from a MEASURED sync-pulse envelope peak before ever drawing a single pixel, and this port has no equivalent at all.
            - `TMmsstv::DrawSSTV` (`Main.cpp:4917-4986`) gates ALL pixel drawing behind `dp->m_wBgn`: while it's set, it returns immediately after calling `SyncSSTV()` (`Main.cpp:4980-4982`, `dp->m_wBgn = 1; SyncSSTV(); if (dp->m_wBgn) return;`) — confirmed by reading the function directly.
            - `TMmsstv::SyncSSTV` (`Main.cpp:3751-3799`, confirmed by reading the function directly): once `e` lines' worth of sync-envelope data (`m_B12`, `e` = 4 normally, or 3 under `m_SyncAccuracy && sys.m_UseRxBuff && m_TW >= m_SampFreq`) have been buffered, it FOLDS that envelope across `e` lines modulo `m_TW` into one line's worth of bins, takes the **argmax** bin, subtracts `m_OFP` (a per-mode constant, e.g. 10.7ms for Robot 36, `sstv.cpp:663`), negates, and assigns the result to `SSTVSET.m_IOFS = SSTVSET.m_OFS = dp->m_rBase` (`Main.cpp:3795`) — THIS becomes the real per-pixel timing anchor for the rest of the image, replacing whatever approximate anchor got the decoder into image-mode in the first place. Only then does `m_wBgn` clear and pixels start drawing.
            - `CSSTVDEM::Start()` (`sstv.cpp:1717-1747`, confirmed by reading directly) is what arms this: sets `m_wBgn = 2` (armed, no one-time setup done yet) at the start of a new reception.
            - This mechanism is ALREADY logged in this file as an unported gap (search "m_wBgn" a few bullets up — "legacy's own fine-pixel-alignment bootstrap... is unported and is the real fix for m_sint2/m_sint3's midpoint-approximation anchor imprecision..., VisLockStateMachine's own smaller ~7ms trigger-lag imprecision...") and was explicitly weighed as a candidate next step in the Opus sequencing consultation (also a few bullets up) but deprioritized at the time in favor of the golden-vector capture work that led directly to this investigation. This is that candidate 8th piece, now with a concrete, measured, high-confidence justification for why it matters.
          - **Explicitly ruled out this pass** (re-derived independently, not just agent claims): real-audio noise/ADC characteristics (the captures are clean, band-limited, full-scale, negligible DC — directly measured); PLL settling/filter-chain mismatch at short dwell (a faithful `PllFmDemodulator` replica run on the real capture with a CORRECTED anchor scores 6.05, matching legacy's own 6.99 baseline — the demodulator itself is fine once given the right anchor); AFC/`SyncFreq`/`m_AFCDiff` (no meaningful carrier offset measured in these captures, and the corrected-anchor replica already matches baseline with no AFC modeled at all); `PllFmDemodulator`'s scale-bridge bug (already fixed, irrelevant at this fixture's full-scale amplitude).
          - **Secondary, smaller, independently-source-verified divergences found in the same pass — real, but NOT this symptom's cause, logged for later**: (1) legacy's shipped DEFAULT demodulator is actually the Hilbert-transform path (`CHILL`, `m_Type=2`, `sstv.cpp:1492`, confirmed the ctor default and `Main.cpp` INI-fallback/profile-8 default both agree), not the PLL — this port only implements the PLL path, and `AnalogFmSstvDecoder.cs:34-37`'s comment claiming legacy switches PLL bandwidth between VIS/image is FALSE (already contradicted by this file's own line ~45) and needs correcting separately; (2) `PllFmDemodulator` is configured 1100-2300Hz vs legacy's real `CPLL` 1500-2300Hz (`sstv.cpp:1431`), a 1.5x loop-gain difference; (3) `GetPictureLevel` (luma only) is not a bare dereference — it peak-picks the brighter of two samples `m_KSB` apart (`Main.cpp:4057-4071`, `m_KSB`=1 for Robot 36 at 11025Hz) — `SstvModeRegistry.cs`'s doc comment claiming a bare `*ip` dereference is inaccurate on this specific point (chroma/tone-select DO use the bare read, `GetPixelLevel`, correctly); (4) horizontal pixel pitch uses legacy's `m_KSS` (`m_KS - m_KS/240` for Robot 36, `sstv.cpp:1157`), not `m_KS` — this port's `RobotScanlineDecoder.DecodePixels` uses the equivalent of `m_KS`, a ~0.42% horizontal scale error; (5) legacy's pre-AGC input chain (bandpass filter feeding the demodulator, `sstv.cpp:1824-1833`, already logged elsewhere in this file) is confirmed measurably non-causal for THIS fixture specifically (clean, band-limited, full-scale capture) even though it remains a real, separately-logged gap in general.
          - **Next planned step**: scope a plan to port `CSSTVDEM::Start`'s `m_wBgn` arming + `TMmsstv::SyncSSTV`'s envelope-fold-and-argmax re-anchor into `AnalogFmSstvDecoder` (piece 8 per this file's own existing numbering) — get an Opus plan-review before writing code (explicitly reminding it: follow legacy exactly, no invention, and to stay tightly scoped/no tangents per this project's collaboration note), implement incrementally with both `SstvRoundTripTests` and `GoldenVectorTests` as oracles (test after each sub-step, not just at the end), then the usual up-to-3-round Opus review cycle on the finished piece. Not yet started as of this log entry. **UPDATE**: plan drafted and sent to Opus for plan-review (not yet returned as of this log entry). Draft plan: a new step, tentatively "piece 8", inserted between `Commit()` and the start of per-line decoding -- buffers `e=4` lines' worth of the already-existing `SyncEnvelopeDetector` output (the same d12/d19 infra `ApplySlantTracking` already reuses) starting at `_consumedSamples`, folds into per-mode-line-width bins (mirroring legacy's `m_B12` fold, `Main.cpp:3767-3774`), argmaxes, applies the `n -= m_OFP; n = -n` transform (plus the SCT-family wraparound case; Hilbert-tap term omitted since this port has no Hilbert demodulator), and applies the result as a one-time correction to `_consumedSamples` before any line for the newly-locked image is decoded -- confirmed via full trace of `dp->m_rBase`'s every use (`Main.cpp`/`sstv.cpp`, all citations) that this mirrors legacy's real `m_rBase` semantics (a running absolute pixel-draw sample cursor, corrected once per image before drawing starts, advanced by `SSTVSET.m_WD` per page thereafter -- `Main.cpp:5013`). Open questions sent to Opus: exact `m_OFP` per-mode source/values for Robot 36 and Martin M1, whether the correction is additive/relative to the pre-correction anchor or a replacement, whether hardcoding `e=4` (skipping legacy's UI-config-dependent `e=3` branch) is safe, interaction with `SlantTracker`/piece-6c mid-reception restarts, and how to unit-test the fold-argmax logic in isolation before wiring it in.

**Opus plan-review result, independently re-verified against source before trusting (all confirmed exact):**
- **The draft plan's sign was backwards** -- caught before any code was written. `m_rBase` is a PHASE relative to the fold's own origin (`Start()` zeroes it, confirmed `sstv.cpp:1725-1731`), not an absolute sample cursor -- correct formula is `_consumedSamples += (argmaxBin - ofpSamples)`, not `+= (ofp - argmax)`. The draft's version would have doubled the error in the wrong direction.
- **`SstvModeRegistry.GetSyncSegmentOffsetMs` is NOT usable for this** -- its own doc comment says so explicitly (a deliberate substitute for `SlantTracker`'s relative-only needs, not `m_OFP`'s real value). Needs a NEW per-mode table, transcribed from `sstv.cpp:657-1108`. Verified exact for the two fixtures: Robot 36 `m_OFP=10.7ms` (`sstv.cpp:663`, case `smR36`), Martin M1 `m_OFP=7.2ms` (`sstv.cpp:716`, case `smMRT1`).
- **Real decoy found and verified**: `TMmsstv::AdjustSyncPos` (`Main.cpp:5428-5480`) opens with the IDENTICAL `n -= m_OFP; n = -n` + Scottie-wrap lines as `SyncSSTV`, then adds mode-specific fudge terms (e.g. `smR36/smR72: +0.16ms`, `smMRT1: +0.45ms`) that `SyncSSTV` does NOT have -- confirmed by reading both functions. A grep for the formula could easily land on the wrong one; `SyncSSTV` (`Main.cpp:3751-3799`) is the correct, decoy-free source.
- **`e=4` hardcode was an unnecessary simplification, not a safe one**: both gating settings default ON in legacy (`m_SyncAccuracy=1` `Main.cpp:730`, `sys.m_UseRxBuff=1` `Main.cpp:899`, both confirmed), so the real condition reduces to `LineDurationMs >= 1000 -> e=3, else e=4` -- a one-line exact port, reachable for Scottie DX/PD240/MP140/MP175/MN140 (not either fixture, but a real divergence for other modes if skipped).
- **Real gap found**: mutating `_consumedSamples` after `Commit()` already ran would desync `_slantProcessedUpTo`/`_afcProcessedUpTo`/`_afcBoundSample` (all derived from the PRE-correction anchor inside `Commit`) -- silently, with no test failing loudly, just a subtly worse image. Needs `Commit` restructured so AFC/slant initialize from the FINAL (corrected) anchor, not patched after the fact.
- Restart/`EndOfImage` interaction: confirmed no extra work needed -- every legacy lock path re-arms via `CSSTVDEM::Start()` (`sstv.cpp:1732`), so this runs once per `Commit()` including mid-reception restarts, same as the port's own lifecycle already assumes.
- **Recommended split (adopted)**: 8a = new `GetSyncPeakOffsetMs` per-mode table + pinning test (zero behavior change) -> 8b = pure fold/argmax function + unit tests against synthetic data (zero behavior change) -> 8c = wire into `Commit`'s lifecycle (the only behavior-changing piece), re-measure both golden-vector fixtures. Matches the user's "test early, test often" instruction directly.
- Flagged risk for later: if 8c makes Robot 36 improve but Martin M1 regress by a similar magnitude, that specifically means a group-delay mismatch between this port's `SyncEnvelopeDetector`/`PllFmDemodulator` and legacy's exact filter chain (since `m_OFP` is empirically tuned to legacy's own latency) -- not a sign/logic bug, don't chase it as one.

**Piece 8a done (`23b025a`)**: `SstvModeRegistry.GetSyncPeakOffsetMs`, all 43 modes transcribed from `sstv.cpp:657-1108`, pinning test (`SyncPeakOffsetTests.cs`, 43 per-mode assertions + a coverage test). Zero behavior change, nothing calls it yet. 252/252 tests pass. **Piece 8b done (`161b85c`)**: `SyncAnchorCorrector.ComputeAnchorCorrection`, a pure function extracting `SyncSSTV`'s fold-and-argmax computation, with the corrected sign (`argmaxBin - OFP`, independently re-derived and confirmed by tracing every `m_rBase` use before trusting the review). 5 new unit tests, all passing first try, including a fractional-line-width "creep" test (TW=1653.75) that distinguishes the correct fractional modulus from a naive truncated-integer one -- confirms legacy's real sub-sample creep is reproduced, not smoothed over. Zero behavior change, nothing calls it yet. 257/257 tests pass. **Next step**: piece 8c, wiring this into `AnalogFmSstvDecoder.Commit`'s lifecycle -- the only behavior-changing piece, and the one the plan-review flagged as highest-risk (must restructure `Commit` so AFC/slant-tracking initialization happens from the FINAL corrected anchor, not patched after the fact, to avoid a silent cursor desync). Golden-vector re-measurement (Robot 36 must improve, Martin M1 must not regress) is the real test of this whole piece.

**Piece 8c wired in (uncommitted as of this log entry) -- big win on the actual target, but two new real bugs found by running the full suite immediately after wiring ("test early, test often").** Restructured `Commit()` per the plan: mode/pixels/visLock setup stays immediate, but AFC/slant initialization and `ModeDetected` are now deferred (`_pendingAnchorCorrectionMode`) until `TryResolveSyncAnchorCorrection` (called from `TryProcessBuffer`) applies the correction -- AVT is excluded (matches `SyncSSTV`'s own early-out, `Main.cpp:3754-3758`) and finalizes immediately as before. Uses a DEDICATED, temporary `SyncEnvelopeDetector` instance for the fold (not the one `InitializeSlant` creates), since legacy shares one continuously-running d12/d19 filter for everything while this port's detector is a stateful streaming filter that must see each sample once -- confirmed harmless because `InitializeSlant` already creates a fresh instance every call regardless.

**Measured on the actual golden-vector target: Robot 36 decoder-vs-source 68.06 -> 9.95 (a 6.8x improvement, now BELOW the ~42.67 corruption floor -- genuinely discriminating for the first time), Martin M1 11.78 -> 2.40 (improved too, no regression, consistent with both modes sharing the same root-cause anchor error).** This is the real result the whole piece-8 investigation was aimed at.

**But running the FULL test suite immediately after (not just the golden-vector tests) found 2 real problems, caught before this piece was ever considered done:**
1. **A first hypothesis (fresh `SyncEnvelopeDetector` has no settling time, unlike legacy's continuously-running filter) was tested and DISPROVEN empirically**: added a 2000-sample warm-up prefix (fed to the detector but not accumulated into fold bins) and re-ran the failing tests -- deltas were unchanged (within measurement noise), ruling out filter settling as the cause. Kept the warm-up anyway (harmless, and legacy's real filter genuinely has been running continuously, so it's a faithful improvement even though it wasn't the cause here) but the real bug is still open.
2. **Diagnosed via temporary instrumentation (since removed), per this project's own established "confirm empirically" precedent**: every mode's synthetic self-round-trip gets a small, plausible correction delta (roughly 12-128 samples) EXCEPT the three Scottie modes, which get wildly wrong, huge-magnitude deltas (scottie-s1: -6521, scottie-s2: -4313, scottie-dx: -15644, vs page widths of ~12-46k samples) -- almost certainly a real bug in the Scottie-specific wraparound branch, likely related to Scottie's sync being MID-line (not line-start like most modes, the same real quirk CLAUDE.md's own TX/RX-split incident already warns about) interacting wrong with the `isScottieFamily && delta>0` condition.
3. **Separately, Robot 36's OWN synthetic self-round-trip also fails** (delta ~50, was 13.4) despite its diagnosed correction (delta=27 samples) looking individually unremarkable -- similar magnitude to Martin M1 (28, passes) and Robot 72 (27, passes). Open question: is 27 samples a genuine bug (this port's fold finding a wrong peak, or an off-by-something specific to Robot 36 or to the 44100Hz rate this synthetic suite uses, vs the golden-vector fixtures' 11025Hz), or is it legacy's real, correct, harsh behavior -- Robot 36's own extreme sensitivity (lowest samples/pixel in the whole mode table, a zero-margin tone-selector read already documented as fragile) meaning even a small, legitimate correction is enough to push that same tone-selector read out of its window, the identical failure mode as the ORIGINAL bug this whole piece was fixing, just now self-inflicted by a small, correctly-computed correction instead of a large, wrong one.

**QSSTV cross-check on the Robot 36 diagnosis (secondary reference, inspiration/corroboration only per this file's own precedence rules -- not authoritative, and NOT a green light to change the port to match it):** `QSSTV-main/src/sstv/modes/modebase.cpp:248-289` (states `MB1500`/`MB2300`, the tone-selector read for Robot 12/36) does the odd/even line decision differently from legacy YONIQ/MMSSTV -- it AVERAGES the tone-selector segment's frequency over its whole duration (skipping a 20-sample warm-up, `AVGFRQOFFSET`, `modebase.h:53`), not a single last-sample read. `RobotScanlineDecoder.cs`'s `ToneSelectorSegment` case, by contrast, is a faithful port of legacy's own `Main.cpp:4286-4297` -- read ONLY the segment's very last sample, re-deciding every sample with no "first wins" gate. QSSTV's authors independently chose the more robust design; this is corroborating evidence that legacy's own single-sample read really is a fragile-by-design choice (not an artifact of this port's translation) -- it does NOT mean this port should adopt QSSTV's averaging instead, since CLAUDE.md's port-first rule scopes "preserve behavior" to legacy YONIQ specifically, not to a secondary reference's design improvements. Flagged as a candidate fallback ONLY if the current Opus review round can't find a legacy-faithful fix for the Robot 36 regression -- would need an explicit removal-rule discussion (`docs/removed-features.md`) before adopting, since it's a deliberate behavior change from legacy, not a port.

**Sent to a dedicated high-effort investigative agent (read-only, no code changes)**, per the user's own explicit instruction to use an agent for deep investigation rather than continuing to guess inline -- both questions above, full context in the agent's own prompt. Not yet returned as of this log entry. Piece 8c's code is written and locally present but NOT YET considered done -- do not commit/push until both questions are resolved and the full suite passes clean.

**Both open questions resolved, independently re-derived and cross-checked against source (the investigative agent hit a session limit before returning; re-investigated directly instead of retrying it):**

1. **Scottie wraparound bug -- root cause found and fixed.** This port's `_consumedSamples` anchor is defined as the start of `SstvModeDefinition.LineSegments[0]`, deliberately mirroring each mode's real TX wire order (the earlier Scottie-framing fix this same file already documents). For every mode except Scottie, TX places its sync tone first, so this port's anchor and legacy's own internal "phase 0" (wherever `m_rBase` resets, which `m_OFP`'s small values imply is pinned at/near the tracked sync tone) agree -- confirmed by checking every mode's `LineSegments`: sync segment is index 0 in all of them (Martin, Robot, MR/ML, MP/PD, SC2, MN/MC, R24, RM) except Scottie S1/S2/DX. Scottie's real TX order (`LineSCT`, Main.cpp:6620-6640, already correctly ported into `LineSegments`) is separator-G-separator-B-**SYNC**-separator-R -- the tracked 1200Hz tone sits roughly two-thirds into this port's own line, not at the start. Verified numerically: back-computing the raw argmax bin from the observed wrong deltas (scottie-s1 argmax≈12835 of 18884, s2≈8409 of 12246, dx≈31124 of 46318) lines up almost exactly with (cumulative duration of the separator+G+separator+B segments preceding SYNC) + `m_OFP` for all three modes -- i.e. the fold was finding the sync tone exactly where it physically is; the bug was applying legacy's literal `if(n<0) n+=WD` wraparound trick (calibrated to LEGACY's own sync-relative phase-0) against THIS port's differently-anchored origin. **Fix**: computed the pre-sync-segment offset generically from each mode's own `LineSegments` (foreach segment, accumulate `DurationMs` until hitting the `SyncSegment` whose `FrequencyHz` matches the tracked tone (1900 narrow / 1200 wide), then add legacy's real `m_OFP`) instead of a new hardcoded table -- this is 0 (a no-op, byte-identical to before) for every mode whose tracked sync segment is already first, and only nonzero for Scottie. Removed the now-provably-wrong `isScottieFamily`/wraparound branch from `SyncAnchorCorrector.ComputeAnchorCorrection` entirely rather than patching it -- it was solving a problem that doesn't exist in this port's coordinate convention. All 3 Scottie modes now pass the full round-trip suite (previously -6521/-4313/-15644 sample corrections and ~72 average delta; now passing cleanly).
2. **Robot 36's own synthetic round-trip -- diagnosed as a genuine, pre-existing, legacy-consistent fragility exposed by a now-more-correct anchor, not a new bug.** `RobotScanlineDecoder`'s tone-selector read (`RobotScanlineDecoder.cs:62-79`) is an unmodified, already-reviewed-correct port of legacy's exact ambiguity/toggle mechanism (`Main.cpp:4286-4297`, re-decided on literally the LAST sample of a 1.5ms/~66-sample segment, no averaging, no "first wins" gate -- legacy's own decoder has the identical fragility, not something this port invented). A uniform anchor shift of ~27 samples (individually unremarkable -- Robot 72 and Martin M1 get 27/28 respectively and pass fine, since neither depends on a single last-sample read of a 66-sample segment) is large enough, in a 66-sample-wide window, to push that single decisive read past the segment boundary into the immediately-following chroma scan segment -- misselecting R-Y/B-Y on enough lines to produce the observed ~50 average delta. This is exactly the same failure *shape* as the original golden-vector bug this whole piece existed to fix (Robot 36's own extreme per-pixel precision sensitivity, already flagged elsewhere in this file), just now self-inflicted by a small, correctly-computed residual instead of a large, wrong one. Confirmed this isn't a Scottie-fix side effect: Robot 36 isn't Scottie-family, so today's fix doesn't change its delta at all (still exactly 27, unchanged before/after). Not fixing `RobotScanlineDecoder`'s own tone-selector logic -- legacy really does decode this way, and inventing an averaging/robustness mechanism legacy doesn't have would violate CLAUDE.md's "preserve behavior"/"port first" rule. **Recommendation (not yet applied): raise `EncodeThenDecode_ViaWavFile_RoundTripsWithinTolerance`'s Robot 36 tolerance specifically, with this exact justification inline** -- pending Opus review of this whole diagnosis and the Scottie fix together before finalizing, per the user's standing review-cycle instruction.
- **Next step**: Opus review round 1 of the Scottie fix + this Robot 36 diagnosis (up to 3 rounds, explicit user-mandated stop-and-report if unresolved after 3). Then, if the diagnosis holds, adjust the Robot 36 test tolerance with the justification above, re-run the full suite clean, commit, and ask before pushing.
- [[13-testing]]: fixture directories for audio/SSTV established, including at least one golden-vector fixture captured from the legacy binary (see [[13-testing]]'s golden-vector section) — do this early, since it requires standing up the legacy build once, which only gets harder to justify later in the project.

**Piece 9 (VIS-bit decode / PLL bandwidth mismatch) — plan-reviewed by the `auditor` subagent (2 of the up-to-3 rounds spent; user elected to stop reviewing and move to implementation, with a code-level auditor review once written instead of a 3rd plan round).**

Diagnosis: `AnalogFmSstvDecoder` shares one `PllFmDemodulator` (1100-2300Hz) between per-pixel image decode and VIS-header bit decode. Legacy's real image PLL (`CPLL`) is 1500-2300Hz only (`sstv.cpp:1432`), narrowed further to 2044-2300Hz for the MN/MC narrow-mode family via `CSSTVDEM::SetWidth`/`IsNarrowMode` (`sstv.cpp:1707-1719`, `266-279`) — **correction to an earlier scoping assumption**: this port already DOES support MN/MC (`SstvModeRegistry`'s `Mn73`/`Mn110`/`Mn140`/MC family, `TryDecodeNarrowModeHeader`), so those modes are in scope for step 3's re-verification, not excluded. Legacy's VIS-bit decode never touches the PLL at all — a wholly separate mechanism: two `CIIRTANK` resonant bandpass-envelope detectors at 1080Hz/1320Hz (80Hz bandwidth, NOT the nominal 1100/1300Hz VIS-spec tones — `sstv.cpp:1446/1448`), each rectified + 50Hz/2nd-order-Butterworth-smoothed (`m_lpf11`/`m_lpf13`, `MakeIIR(50,fs,2,0,0)`), decided by `d11>d13` with a `(d11<d19 && d13<d19) || fabs(d11-d13)<SLvl2` weak/ambiguous reject gate (case 2/9, `sstv.cpp:1974-2126`; gate at `1981-1984`, decision at `1987-1988` — corrects an earlier `1969-1994` citation).

This port already has the right building blocks — `TankFilter` (`CIIRTANK` port), `IirFilter` (`CIIR` port), `SyncEnvelopeDetector` (a resonate-rectify-smooth composition of both, already parameterized for 1080/1320Hz per its own doc comment) — and already has a CORRECT implementation of this exact mechanism in `VisLockStateMachine` (a fallback lock path), including the reject gate. The real bug is narrower than first framed: only `AnalogFmSstvDecoder.TryDecodeVisHeader`'s fixed-window bit reads (~lines 1122-1134 first byte, 1152-1160 extended bits) still decide bits from the shared PLL's `AverageFrequencyInWindow` vs `bitMidpointHz`, with no reject-gate equivalent at all.

**Plan (auditor-reviewed):**
1. Extract only the stateless per-bit decision predicate (`d11`/`d13`/`d19`/`SLvl2` → reject or bit) into a helper shared by `VisLockStateMachine` and the new fixed-window code — NOT a full extraction: `VisLockStateMachine`'s continuous hunting-state shape vs. `TryDecodeVisHeader`'s fixed analytic window can't reconcile, and byte-assembly genuinely differs (`VisLockStateMachine` matches the full 8-bit pattern including parity position; `TryDecodeVisHeader` currently decides "extended" from 7 bits) — so detectors and timing stay separate per call site, only the 4-line decision rule is shared.
2. Implement in `TryDecodeVisHeader`: new `SyncEnvelopeDetector` instances for 1080/80, 1320/80, and a DEDICATED 1900/100 (d19, for the gate — the existing `_syncBypass1900Detector` is unsafe to reuse, different indexing/timing via `TryInterleavedHeaderScan`). Required fixes, revised after round 2:
   - **d11/d19 must run continuously across the whole header** (started at/near `headerStart`, never restarted per bit window) — round 2 downgraded the earlier "~2000 samples pre-roll" framing: `headerStart` already gives them ~610ms of leader before bit 0, ~30x past settling, so the real requirement is continuity, not a specific pre-roll length.
   - **d13 must start 15ms BEFORE bit 0**, at the equivalent of legacy's case-2 entry point (`firstBitWindowStart - 0.5 * BitDurationMs`), NOT "at the first bit window" as originally planned — round 2 caught this: legacy's own case 2/9 only starts advancing `m_iir13`/`m_lpf13` at case-2 entry, which is 15ms into the start bit, i.e. before bit 0 begins. This port's own `VisLockStateMachine.cs:186` already gets this right; the new code must match it, since the FIRST bit's gate decision is also the header's first go/no-go check.
   - Detector instances must be reconstructed per `TryDecodeVisHeader` attempt (or otherwise never double-fed) — the method is re-entrant, returning `false` without consuming in 4 places, and a streaming caller can re-invoke it over the same samples.
   - **Decision-offset arithmetic, corrected**: legacy's trigger point already lags real tone onset by the d12 envelope's own group delay, so legacy effectively samples each tone at its exact midpoint. This port's `headerStart` is analytic (zero group delay), so the equivalent offset is `0.5 * BitDurationMs + measuredGroupDelay` — NOT a flat 50-75% guess. **Measuring `SyncEnvelopeDetector`'s actual group delay/settling time at 1080/80 and 1320/80 (step-response test) is now a required part of this step**, not a carried assumption — expected around 73% of the window (15ms + ~7ms), but confirm by measurement, don't assume the number. Document the derivation inline.
   - State the reject path's outcome explicitly: gate failure = `return false` without consuming, same as today, which falls through to `TryInterleavedHeaderScan`/`VisLockStateMachine` (applying the identical predicate). A weak/ambiguous signal can now fail on both paths where it previously always succeeded via the PLL proxy — legacy-correct, but the low-amplitude/noisy fixture tests must assert this intended reject, not just "doesn't crash."
   - Add a pinning test: feed one synthetic header through both `TryDecodeVisHeader` and `VisLockStateMachine` and assert they decode the same byte, now that both share the same predicate — cheap insurance against the two paths drifting apart again.
   Isolate-test this sub-piece alone before touching the PLL (chop-into-pieces methodology).
3. Only then narrow `PllFmDemodulator` (`AnalogFmSstvDecoder.cs:38-39`) from 1100-2300Hz to 1500-2300Hz. Re-verification scope, revised after round 2:
   - Re-run the FULL per-mode synthetic round-trip delta table (not just AVT's lock margin), MN/MC included (their pixel tones, 2044-2300Hz, stay in-band either way, but were wrongly excluded from the original scoping) — narrowing changes the VCO gain (`-bandwidthHz`: -1200 → -800), slowing loop re-acquisition after every sync/porch transition, concentrated exactly where `SampleFrequencyAt` reads pixels (line starts); repo history (`8f87146`) shows some deltas are already marginal.
   - **Re-measure BOTH `GoldenVectorTests.cs` fixtures (martin-m1, robot-36) against real legacy-captured audio, not just synthetic round-trips** — round 2 caught this omission: it's the only real-legacy-audio check in the suite and reads pixels straight off the PLL output, which CLAUDE.md's behavioral-parity rule requires for any DSP-core change. Robot-36's current 68.06 delta is already documented in that file as possibly incomplete AFC/PLL settling — this is the single most informative number this whole piece can produce, could move either direction.
   - **Update `AvtTrainingLockStateMachineTests.cs:124`**, which hardcodes `new PllFmDemodulator(SampleRate, 1100, 2300)` — round 2 caught that this test would silently stay green against a config production no longer uses unless changed in this same step, defeating the point of re-measuring AVT's margin.
   - `AvtTrainingLockStateMachine`'s own doc-commented margin measurement (ripple ~5.8Hz, 16/16 windows) was made at the WIDER band and needs re-measuring.
   - `TrySyncIntervalDetectionStep`, AFC (`ZeroCrossingFrequencyCounter`), and Slant (`SyncAnchorCorrector`) confirmed band-independent (envelope-detector- or raw-sample-based, not PLL-based) — unaffected.
4. Fix stale doc comments — TWO, not one (round 2 caught the second):
   - `AnalogFmSstvDecoder.cs:34-39`: replace with: legacy's `CPLL` is always 1500-2300Hz for every mode except MN/MC (narrowed to 2044-2300 via `SetWidth`/`IsNarrowMode`, `sstv.cpp:1707-1719`/`266-279`); this port still narrows flat to 1500-2300 for ALL modes including MN/MC, not implementing legacy's further MN/MC narrowing (a documented simplification, not a bug); VIS decode never touches the PLL at all; legacy doesn't even feed the PLL during VIS cases 0/1/2/9 (first `m_pll.Do` is case 3, `sstv.cpp:2129`) — this port runs it continuously.
   - `AvtTrainingLockStateMachine.cs:9-12`: currently claims the port's PLL is "configured over a wider 1100-2300Hz span than legacy's 1500-2300Hz" with a measured-at-that-width margin (ripple ~5.8Hz, 16/16 windows) — false the moment step 3 lands; replace with whatever the re-measurement at 1500-2300Hz actually finds.

Off-scope, found by the review, NOT this piece, logged for later only: `TryDecodeNarrowModeHeader`'s FSK bit decode (`AnalogFmSstvDecoder.cs:1072`) has the identical PLL-proxy-instead-of-real-detector shape (legacy decodes via `m_iirfsk`/d19 envelopes, `DecodeFSK`, `sstv.cpp:1855-1858`, not the PLL). Correction from round 1's wording: its CODE survives this plan untouched, but its NUMBERS change once step 3 narrows the PLL (it reads 1900/2100Hz off the same demodulator) — already covered by step 3's MN/MC round-trip re-verification, just not a separate concern. Also pre-existing, not introduced by this plan: `TryDecodeVisHeader` decides "extended VIS" from 7 bits (`VisHeader.cs:21/29`) while legacy requires the full 8-bit `0x23` including parity=0 (`sstv.cpp:2066`) — a byte like `0xA3` would diverge between the two; noted, not fixed here.

Assumptions: `g_dblToneOffset` — RESOLVED by round 2, strike from open questions: only set nonzero (`-1000.0`) under the `-i` CQ100 command-line mode (`Main.cpp:1074-1077`), not the default path; record as an unported optional mode, not a gap in this piece. `IirFilter`/`TankFilter` group delay — STILL UNVERIFIED, and now load-bearing (see step 2's decision-offset item above) rather than incidental; must be measured during implementation, not assumed.

**Steps 1-2 implemented (`VisBitDecision.cs` new, `VisLockStateMachine.cs`/`AnalogFmSstvDecoder.cs` modified). Steps 3-4 (PLL narrowing, doc-comment fixes) deliberately NOT started this pass — isolate-test-first methodology, per the plan.** Full suite: 259/259 (255 pre-existing + 4 new in `VisToneRaceHeaderTests.cs`).

`VisBitDecision.TryDecide` (the shared 4-line predicate) extracted and wired into both `VisLockStateMachine` (pure refactor, behavior-identical) and a new `AnalogFmSstvDecoder.TryDecodeVisDataBits`, which replaces the old PLL-`AverageFrequencyInWindow`-based bit reads in `TryDecodeVisHeader` (both the first-byte loop and the extended-tail loop, which now share one call rather than duplicating the tone-race a second time).

**Three real bugs found and fixed by running the full suite after each sub-step — none anticipated by either plan-review round, confirming CLAUDE.md's "round-trip pass is necessary, not sufficient" warning applies just as much to a brand-new mechanism as to a port of an existing one:**
1. **False-positive lock on real noise** (`GoldenVectorTests`' martin-m1 fixture, which has ~1s of real mic-noise lead-in before the header): the first version had no precondition before racing the d11/d13 detectors, so it raced noise straight into a byte that happened to match a REGISTERED mode (`0x3f` → sc2-120) — a false lock the old PLL-average proxy never hit, purely by luck of what that specific noise averaged to. Fix: added the missing case-0-trigger/case-1-15ms-hold precondition (matching `VisLockStateMachine`'s own Search/ConfirmLock, but as a small local check, not a second full state machine) — the tone race no longer runs at all without first confirming a genuine, sustained, amplitude-gated 1200Hz tone.
2. **False rejection of genuine signal**, found immediately by the fix for #1 breaking every AVT test: a real filtered tone transition isn't instantaneous — measured ~14.5ms (44100Hz) / ~8.7ms (11025Hz) of real resonator/lowpass settling lag between the analytically-idealized 610ms leader→startbit transition and d12 actually overtaking d19, even on a clean, full-amplitude, noise-free synthetic signal. A precondition gate hard-coded to the idealized instant rejected real signal outright. Fix: search for the trigger dynamically (case-0-trigger found wherever it actually occurs, then case-1's 15ms hold checked from there) rather than assuming a fixed offset — also a closer match to legacy's own real behavior, which was never triggering at a hardcoded time either.
3. **Last data bit silently defaulting to 0** (found by AVT's own round-trip test at 11025Hz specifically: VisCode `0x44` decoded as `0x04`, a single-bit difference from R24's code): `lastDecisionSample` was computed via one combined `MsToSamples(bitCount * BitDurationMs)` rounding, while the actual per-bit decision cursor accumulated via `bitCount` separate `MsToSamples(BitDurationMs)` roundings — these two rounded values can differ by a sample or two after several steps, and here the loop bound ended up short of the final decision point, so that bit's array slot was silently left at its C# default (`0`) instead of ever being decided. Fix: loop until `bitCount` bits have actually been decided (or data runs out), not against a separately-computed sample bound — removes the two-roundings-can-diverge class of bug entirely rather than trying to keep them in sync.

New tests (`VisToneRaceHeaderTests.cs`): a synthetic-noise-never-locks regression test for bug #1 (independent of the golden-vector fixture files), and a same-stream-start decode-correctness check across normal/extended/full-8-bit-match VIS byte shapes (martin-m1/mr73/avt) for bug #2's fix.

**Not yet done**: the auditor-recommended `SyncEnvelopeDetector` group-delay measurement (step 2's decision-offset item) was effectively superseded by finding the REAL, larger, empirically-measured lag via bugs #1/#2 above — the dynamic search sidesteps needing a precise number at all, so this is now moot rather than outstanding. The low-amplitude/noisy-fixture testing item is covered by the new noise-regression test; a literal "quiet but clean" amplitude test was skipped as not meaningfully different from full-scale once AGC settles (already documented elsewhere in this file: AGC normalizes toward a hard clip regardless of input level).

**Code-level `auditor` review complete — verdict "equivalent-with-risks."** Core mechanism (frequencies/bandwidths/smoothing/reject-gate/decision, timing arithmetic down to the exact sample) confirmed faithful against `sstv.cpp` directly, not just trusted from this log. Two real findings, both fixed; one flagged and deliberately deferred (documented below so it isn't lost):

1. **Fixed — reject was permanent, not resumable.** Legacy's real reject (`m_SyncMode=0`, `sstv.cpp:1957/1972/1983`) returns to Search and re-triggers on the very next qualifying sample — cheap and self-healing. The first version of `TryDecodeVisDataBits` aborted the whole attempt on ANY reject instead; since `_consumedSamples` never moves on a `false` return, a retry from the next `PushSamples` call recomputed the identical deterministic failure forever. Harmless for every mode with a `VisLockStateMachine` fallback, but AVT's own class doc comment says `VisLockStateMachine` deliberately never reports AVT — making the fixed-window path AVT's ONLY detector, with no second chance. **Fix**: restructured into an outer loop that re-searches for a fresh trigger (starting just past the sample where the reject was detected, advancing the SAME persistent detector instances, not resetting them) whenever a bit-decode rejects. New regression test (`VisToneRaceHeaderTests.SpuriousTriggerOnTheBreakTone_StillRecoversTheRealHeader`) forces the exact scenario the auditor flagged as plausible-but-unconfirmed (an artificially widened 30ms break tone that reliably completes the 15ms hold on its own) and confirms the decoder still recovers the real header afterward.
   - **Fourth real bug, found by the FULL suite after implementing the above (not anticipated)**: the first version of this fix bounded the resume loop by `availableUpTo` alone (whatever's actually been demodulated so far) with no upper ceiling relative to `headerStart` at all — reasoned at the time as "matching legacy's own effectively-unbounded real-time search," but this makes the fixed-window method an untested second full-buffer scanner whenever its first attempt fails: on a multi-transmission stream, it can walk straight through unrelated image content and spuriously match some OTHER registered VIS byte deep inside it. Caught immediately by the existing multi-transmission ordering suite (`PiecesSixCReachabilityTests`, `SyncScanInterleaveTests` — both expect a SECOND transmission to be found via `TryInterleavedHeaderScan`, not spuriously pre-empted by the fixed-window path scanning ahead into it): `["robot-36", "martin-m1"]` expected, `["martin-m1", "martin-m1"]` produced. **Fix**: reintroduced a LOCAL ceiling (idealized 610ms trigger + 200ms retry margin + this attempt's own bitCount-dependent decode duration) — generous enough for legacy-faithful local recovery from a short spurious candidate, not a general-purpose search. This is exactly the same category of lesson CLAUDE.md's Scottie incident already warns about: a change that looks locally correct and passes its own narrow tests can still be wrong until checked against the FULL suite.
2. **Fixed — same rounding-mismatch bug class (the one bug #3 above already fixed) also present in `VisLockStateMachine.cs`'s own anchor calculation** (`Verify` state, ~line 261): the anchor offset was computed as one combined `MsToSamples(sum of ms)`, while the state machine's real `_syncTimeCounter` accumulates via `bitCount` SEPARATE `MsToSamples(BitDurationMs)` resets — confirmed diverging by up to 3 samples for extended VIS at 11025Hz. Smaller consequence than bug #3 (biases the reported anchor by a few samples rather than dropping a bit entirely) and pre-existing rather than introduced by this piece, but the auditor was right that it's the identical pattern. **Fix**: replaced the combined rounding with the same per-step accumulation the class's own real countdown timers use.
3. **New test**: `VisToneRaceHeaderTests.FixedWindowPathAndVisLockStateMachine_AgreeOnTheSameHeader` (martin-m1 normal + mr73 extended) — the plan's own recommended pinning test, not present before this round: feeds one synthetic header through both `TryDecodeVisHeader` (via the full decoder) and `VisLockStateMachine` (fed directly) and asserts they land on the same decoded mode, now that both share `VisBitDecision` but keep independent detectors/timing. (Needed a 3-second trailing-silence margin in the fixture to work at all -- `ModeDetected` only fires once the piece-8 sync-anchor correction resolves, which needs 3-4 full transmission lines of trailing content buffered, not just the header itself; a bare header with no trailing content decodes internally (`_mode` sets correctly) but never fires the event -- a test-fixture gotcha, not a production bug, worth remembering if writing more header-only fixtures later.)

**Deliberately NOT fixed, flagged by the auditor, logged here so it isn't forgotten**: performance. `TryDecodeVisDataBits` constructs 4 fresh `SyncEnvelopeDetector`s and replays from `headerStart` on every single `PushSamples` call while `_mode is null` (i.e. the entire pre-lock idle-listening state) — no persistent cursor, unlike every other detector in this file (`_visLockProcessedUpTo`, `_syncBypassProcessedUpTo`, etc., all of which advance incrementally and never redo already-processed samples). At 44100Hz the auditor estimated up to ~880ms × 4 detectors ≈ 155k filter steps per call even before this session's Risk-1 fix. The resume-on-reject loop added for Risk-1 (now bounded by the reintroduced `searchCeiling`, not the whole buffer -- see the "fourth real bug" note above) keeps the worst case in the same rough ballpark rather than making it unbounded, but it's still real, repeated, from-scratch work on every call while unlocked. Auditor's assessment: "probably still realtime-feasible," not urgent, but a real, known cost -- especially for a streaming caller pushing small chunks frequently while unlocked. **If this needs fixing later**: the natural shape (matching this file's own established pattern) would be a persistent cursor + persistent detector instances for this method too, incrementally advancing rather than replaying from `headerStart` every call -- non-trivial because the current design's "fresh detectors per call" is also what makes it trivially re-entrant/deterministic; a stateful version would need to reason about exactly the resume-after-reject semantics the Risk-1 fix just added, carried across calls instead of within one.

**Next step**: steps 1-2 (including both post-review fixes and the ceiling regression they surfaced) now considered done -- 262/262 tests passing (259 prior + `VisToneRaceHeaderTests`' 3 new tests: the pinning test x2 modes, the spurious-trigger regression).

**Steps 3-4 DONE.** `PllFmDemodulator`'s tracked band narrowed from 1100-2300Hz to legacy's real 1500-2300Hz (`AnalogFmSstvDecoder.cs:38-39`, now `DemodulatorLowHz=1500`); `AvtTrainingLockStateMachineTests.cs:124`'s hardcoded old config updated to match (would otherwise have stayed green against a setup production no longer uses). Both stale doc comments fixed (`AnalogFmSstvDecoder.cs`'s own class-level comment, folded into the same edit as the constant change; `AvtTrainingLockStateMachine.cs:9-12`, updated with freshly re-measured numbers below). 262/262 tests still passing after the narrowing.

**Re-verification, measured directly (not assumed) before AND after narrowing, for comparison:**
- **AVT lock margin re-measured at the new band**: steady-state ripple ~9.0Hz for the bit-1 tone (1600Hz) and ~2.3Hz for bit-0 (2200Hz), both landing with a solid ~100Hz margin against case 6's threshold (`BitOneMaxHz`/`BitZeroMinHz`) -- comfortably wider than the ripple either way. `AvtTrainingLockStateMachineTests`' full-training-sequence test (real, not synthetic-isolated) confirms an actual 32-block lock still completes correctly end to end. Slightly more ripple than the old band's measured ~5.8Hz, but nowhere near the threshold either way -- no functional risk.
- **Full per-mode round-trip delta table, before vs. after narrowing (all 43 modes)**: every single mode's delta IMPROVED, none regressed. Full numbers not reproduced here (measured via a temporary diagnostic, not a permanent test) -- notable ones: avt 2.60→2.01, martin-m1 2.50→1.95, mn73 6.49→4.94 (MN/MC family, explicitly required in this step's scope), robot-36 50.48→50.34 (essentially flat, consistent with its own already-documented extreme fragility being about the tone-selector read, not PLL settling -- expected, not a concern). The auditor's own theorized risk (narrower band → less VCO gain → slower re-acquisition → worse per-pixel settling at line starts) did not dominate in practice for any mode measured; a narrower band's reduced out-of-band content apparently wins out here. `SstvRoundTripTests`' own tolerances (all still 10.0 except Robot 36's 55.0) needed no changes -- every mode has more headroom now, not less.
- **Both `GoldenVectorTests` fixtures re-measured against real captured audio (not just synthetic round-trips, closing the gap CLAUDE.md's behavioral-parity rule requires)**: martin-m1 11.78→1.22 (already improved to ~1.7 by piece 9 steps 1-2 alone, narrowing improved it further), robot-36 68.06→**16.995**, a ~4x improvement. This closes out a divergence a previous revision of `GoldenVectorTests.cs`'s own comment explicitly flagged as needing follow-up investigation ("~5x-worse real-capture result than synthetic self-round-trip... exactly the encoder-and-decoder-agree-while-both-wrong-about-reality failure mode") -- the PLL bandwidth mismatch this whole piece exists to fix WAS that investigation's answer. Robot-36's golden-vector tolerance tightened from 75.0 to **25.0**: the previous 75.0 was explicitly documented as "not meaningfully discriminating" because the real measured delta (68.06) was already worse than this exact source image's own measured ~42.67 corruption floor (flat-gray/mirrored/flipped/channel-swapped scored around there too) -- now that the real delta (16.995) is comfortably BELOW that floor, a tight, genuinely-discriminating tolerance is possible again, matching martin-m1's own already-tight 15.0. Full reasoning inline in `GoldenVectorTests.cs`'s own updated comment.

**Piece 9 complete** (steps 1-4, both plan-review rounds, one code-level review round, re-verification). All committed and pushed except this final entry.

## Piece 10 — `GetPictureLevel` peak-picking

Started as a one-line item ("Robot 36 luma bare read") in the "Secondary, smaller,
independently-source-verified divergences" list below piece 8's own entry. Investigation (reading
`Main.cpp`'s real per-pixel RX decode switch directly, not inferring from the original framing) found
the real scope was far bigger: legacy's `GetPictureLevel` (`Main.cpp:4057-4071`) peak-picks — compares
two raw demodulated samples `m_KSB` apart, keeps whichever is larger — and this applies to nearly every
mode's every channel (all 3 RGB channels for Scottie SCT1/SCT2/Martin/SC2/MC/Pasokon; luma only for the
Robot/R24/MR/ML/PD/MP/MN YCbCr-paired families, chroma always stays bare; RM8/RM12's single channel).
Scottie DX is the ONE mode that never peak-picks anything. This port previously did zero peak-picking
anywhere across all 5 scanline decoders — confirmed by reading every one of them.

User chose "full fix, plan-reviewed first" over a narrower Robot-36-only slice. **4 rounds of auditor
plan review** (a new "ask the auditor directly: is this plan good enough to build now, given a
code-level review will follow?" policy was established mid-piece — see `~/.claude/agents/auditor.md`'s
"Plan reviews" section and the `feedback_plan_review_cadence` memory — soft 3-round backstop, then check
with the user, which is what happened: round 3 gave a clean GO, round 4 was dispatched anyway per this
policy and confirmed it independently):

- **Round 1**: 2 blockers (PD50/PD90 wrongly placed in group A instead of E; missing the universal
  `if(!m_KSB) m_KSB++;` floor) + several risks (Robot's 3 read sites not 2, PD/MP/MN's two luma segments
  both peak-pick, `effectiveSampleRate` not the raw constructor rate, a design footgun in the originally
  proposed two-same-typed-delegate-parameters shape).
- **Round 2**: found the round-1 fix for the line-end boundary was ITSELF wrong twice — the "bare always
  wins past line end" premise was traced to the wrong buffer's clamp (it's actually a floor at each
  mode's own `LuminanceMinHz`, since `-16384` equals `center-BWH` exactly), and the reachability claim
  ("AVT-only") was backwards — the guard is unreachable at EVERY currently-registered mode (max ratio
  ~0.625 at PD290, not AVT), meaning test coverage needed to be a direct synthetic-line unit test, not
  an integration test. Also recommended moving the group-table/Scottie-DX-exception data off
  `SstvModeDefinition` (its own precedent for a compile-error-on-missing-mode guarantee didn't actually
  hold, since the cited nullable fields all have defaults) onto `SstvModeRegistry` as internal functions,
  matching `GetSyncPeakOffsetMs`/`IsFastAfcGroup` precedent — **adopted**.
- **Round 3 (verdict GO)**: independently re-verified rounds 1-2's corrections, confirmed the sign
  direction (legacy picks the HIGHER raw value = higher frequency = brighter, verified via
  `m_Buf[n]=-d`'s sign convention) and the corrected line-end floor. Flagged that the golden-vector
  acceptance criterion needed to be a FIRM "no regression" requirement, not just "deferred" — this repo
  has a real precedent (`8f87146`) of loosening a tolerance instead of investigating a regression.
- **Round 4 (verdict GO, explicit)**: asked directly "is this good enough to build now, given a
  code-level review follows?" — yes, with 2 cheap spec-text clarifications (group C's "no trim" needed
  an explicit sentinel/factor-of-1.0, not a divisor-of-1 that would silently zero `m_KSB`; the
  int-truncation needed to live directly in the formula, not just in prose) and one adopted
  implementation-ordering split (3a: wire the reader in bare-only, provably zero-behavior-change; 3b:
  flip the actual peak-pick sites — a clean bisection point).

**Implementation (all isolate-tested, matching the chop-into-pieces methodology)**:
1. `SstvModeRegistry.GetPeakPickParameters`/`NeverPeakPicks`/`GetKsbSamples` — pure functions, the 5-group
   `m_KSS`/`m_KSB` table transcribed from `sstv.cpp:1110-1179`. Pinned by `PeakPickParametersTests.cs`
   (48 tests: one assertion per mode's group, `AllModesCovered`, hand-computed AVT/PD290 spot-checks
   matching the auditor's own worked examples, universal-floor check). Zero behavior change.
2. `PixelSampleReader.cs` — the new `ReadBare`/`ReadPeakPicked` reader object replacing the old single
   `Func<int,int,double>` delegate `IScanlineDecoder.DecodeLine` used to take (distinctly-named methods
   so a call-site mixup fails to compile, not silently reads the wrong channel — the round-1-flagged
   design fix). Takes a `Func<int,double> rawSampleAt` delegate rather than owning a `List<double>`
   directly, so it stays unit-testable with an arbitrary synthetic source. Pinned by
   `PixelSampleReaderTests.cs` (7 tests). Zero behavior change (nothing called it yet).
3a. Wired the reader into all 5 decoders + `AnalogFmSstvDecoder`'s call site, every call site to
    `ReadBare` only. Provably zero-behavior-change: 317/317 tests bit-identical to the pre-piece-10
    baseline.
3b. Flipped the real peak-pick call sites per the corrected mode/channel table, folded into each
    decoder's existing exhaustive `ChannelName` switch (not a parallel one). Fixed a stale doc comment
    (`IScanlineDecoder`/`AnalogFmSstvDecoder`) that had claimed "GetPictureLevel/GetPixelLevel both
    simply dereference `*ip`" — false; only `GetPixelLevel` is the bare read. Full suite: **317/317
    passing** with peak-picking actually active.

**Re-verification: found a real, small, root-caused side effect — not a bug in this piece.** Both
`GoldenVectorTests` fixtures moved slightly the wrong direction (robot-36 16.995→17.086, martin-m1
1.216→1.284 — both tiny, both comfortably within their 25.0/15.0 tolerances) and most round-trip deltas
increased slightly (+0.1 to +0.9 across ~35 of 43 modes). Investigated rather than accepted at face
value: empirically measured `PllFmDemodulator`'s real transient response to a small tone step (the size
of a typical gradient's per-pixel frequency delta) — it undershoots BELOW the starting tone within ~5
samples, then recovers over 30+ samples; for narrow-pitch modes (e.g. RM12, ~12.7 samples/pixel at
44100Hz) that settling time exceeds a whole pixel's dwell time, so both the bare and peek-ahead samples
land inside the same still-ringing transient. **Root cause traced directly to source**: `CSSTVDEM`'s
constructor sets `m_Type = 2` (`sstv.cpp:1492`), and the demodulator dispatch (`sstv.cpp:2256`, `case 0:
PLL / case 1: Zero-crossing / default: Hilbert`) confirms legacy's REAL shipped default is Hilbert, not
PLL — this port only implements PLL, and its settling dynamics are slower/rougher than whatever
Hilbert's were when legacy's peak-pick heuristic was designed. The peak-pick LOGIC itself is
independently verified correct (4 rounds of source-verified review + 55 dedicated unit tests) — this is
a genuine, small, now-explained interaction with an already-known, already-scoped-out demodulator gap
(see the open-items list's item 5, priority bumped by this finding), not a defect introduced by piece
10. Discussed directly with the user (not silently accepted): decision was to keep the small increase
(documented, root-caused, within tolerance), and NOT implement Hilbert as part of this piece — assessed
as a large, uncertain-payoff undertaking for a currently-small measured gap, better scoped/researched as
its own piece than decided on the strength of one finding. Concrete next step logged: read `CHILL`'s
real implementation and get an actual settling-time comparison before deciding whether to implement it,
rather than leaving "worth it?" as a guess in either direction.

**Piece 10 status: implemented, 317/317 passing, committed (`ee74bfd`), pushed.**

## Piece 11 — `m_KSS`/`m_KS2S` horizontal pixel-pitch fix

Open-items list's item 3 (logged during piece 10 as "connected to piece 10"). Legacy's real per-pixel
x-mapping (`Main.cpp`'s decode switch, e.g. `Main.cpp:4223/4300`) is `x = ps * Width / m_KSS` for
luma/RGB channels, `x = ps * Width / m_KS2S` for chroma (R-Y/B-Y, and Robot's tone-selected combined
"C" channel) — both TRIMMED widths, not the raw scan duration `m_KS`/`m_KS2` this port's decoders were
using (`perPixelDurationMs = scan.DurationMs / mode.ImageWidth`, no trim). A ~0.1%-0.4%-of-scan
horizontal scale error, per group.

**TX/RX verified separately, per CLAUDE.md's rule** (never infer one from the other): read
`TMmsstv::LineR36` (`Main.cpp:6558`) directly — TX writes each pixel at a flat `DurationMs/Width` with
no trim at all. This fix is RX-decode-only.

**Caught a wrong assumption before implementing**: an earlier working note in this project claimed
`m_KS2S` always uses the identical trim divisor as `m_KSS`. Re-verified directly against
`sstv.cpp:1110-1160`'s grouping switch and found this is FALSE for group D (MR73 only) —
`m_KSS = m_KS - m_KS/640` but `m_KS2S = m_KS2 - m_KS2/1024` (`sstv.cpp:1152-1154`), different
divisors. Groups A/B/C/E use the same divisor for both. Caught by re-reading source instead of trusting
the earlier note — exactly the kind of error CLAUDE.md's "no assumptions" rule exists to catch.

**Fix**: extended `PeakPickParameters` (`SstvModeRegistry.cs`) with a second field, `Ks2sTrimFactor`
(equal to `KssTrimFactor` in groups A/B/C/E; `1023/1024` vs `639/640` in group D). Added
`IsChromaChannel(channelName)` (`"RY"`/`"BY"`/`"C"`) and `GetPixelPitchTrimFactor(mode, channelName)`,
reusing the same 5-group switch `GetPeakPickParameters` already implements. All 5 scanline decoders'
`perPixelDurationMs` now multiply by `GetPixelPitchTrimFactor(mode, scan.ChannelName)` before computing
each pixel's sample window.

**User approved implementing directly** (no auditor plan-review round) given this reuses piece 10's
already-tested `GetPeakPickParameters` machinery rather than introducing new architecture.

**Tests**: `PeakPickParametersTests.cs` extended — `Ks2sTrimFactor` column added to the existing
43-mode theory (MR73 is the only row where it differs from `KssTrimFactor`), plus dedicated
`IsChromaChannel`/`GetPixelPitchTrimFactor` tests using MR73's real divergence as the discriminating
case (a test that only exercised matching-groups modes could pass even with the chroma branch wired to
the wrong field). 327/327 passing (was 317).

**Golden-vector re-measurement** (both fixtures are group E — 239/240 trim on both axes): martin-m1
1.284 → 1.438 (small increase, still well inside the 15.0 tolerance), robot-36 17.086 → 14.809
(improved). No regression; small opposite-signed movement is the expected shape for a scale fix (unlike
piece 10's settling-time-driven regression, this doesn't touch demodulator dynamics at all).

**Piece 11 status: implemented, 327/327 passing, committed (`be938d4`), pushed.**

## Piece 12 — RM8/RM12 gain correction

Open-items list's `MonoAveragedPairedScanlineDecoder` RM8/RM12 gain item. Looked small ("multiply by a
constant"), same as piece 11 turned out to be — overturned a previously-documented design decision
instead.

**The bug**: legacy's RX applies an RM-specific gain (`d *= 256.0/(256.0-32.0)`, `Main.cpp:4438`) on
top of `GetPictureLevel`/`GetPixelLevel`'s calibration pipeline, RM8/RM12 only. A prior piece had left
a "NOT ported" comment in `SstvModeRegistry.cs` reasoning this port's simpler linear
frequency-to-pixel formula couldn't faithfully carry the correction, since it doesn't replicate
legacy's actual calibration internals.

**Re-derivation (round 1 of 2 auditor plan-review rounds)**: that premise was wrong. Traced
`GetPixelLevel`'s real chain: `m_Buf[n] = -d` where `d` comes from the discriminator
(`sstv.cpp:2278/2289`); `GetPixelLevel` applies global constants `m_DemOff=0`,
`m_DemWhite=m_DemBlack=128/16384` (`Main.cpp:875-877`). Independently re-derived via TWO different
demodulator paths (`CPLL::Do`'s VCO gain chain, and `CFQC`'s zero-crossing path, `sstv.cpp:280-345` and
`376-488`) that for the demodulator's 1500-2300Hz FM band this reduces to
`GetPixelLevel(freq) = (freq-1900)*128/400`, confirming a prior piece's own derivation
(`RobotScanlineDecoder.cs`'s `AmbiguityHalfWidthHz` constant) rather than re-trusting it blind.
Algebraically, `GetPixelLevel(freq)+128 = (freq-1500)*256/800` — exactly this port's own
`(freq-LuminanceMinHz)*256/(LuminanceMaxHz-LuminanceMinHz)` formula for the standard band RM8/RM12 both
use (confirmed: unmodified `LuminanceMinHz`/`MaxHz` defaults, 1500/2300, in the registry). The two
pipelines were never mismatched, just expressed differently.

**Round-1 finding (real blocker, not just documentation)**: legacy's RM8/RM12 RX branch
(`Main.cpp:4437-4449`) writes gray straight into R=G=B with no `YCtoRGB` matrix call at all — but this
port's existing decoder routed luma through `YCbCr.ToRgb(y[x], 128, 128)`, which applies its own
different studio-to-full-swing expansion (`1.164457*(y-16)`). Naively applying the RM correction to
`y[x]` and still routing through `ToRgb` stacks two different corrections and produces worse output
(worked example: mid-gray 128→130.4 instead of 128; TX-white 235→272.8, clipped to 255, instead of
~250). Fix: bypass `YCbCr.ToRgb` entirely for this decoder, write gray directly, matching legacy's real
branch field-for-field. This also retires a second, separately-wrong previous design decision — an
earlier version of `MonoAveragedPairedScanlineDecoder`'s own doc comment justified routing through
`ToRgb` as "the same path every other Y-bearing family already uses, rather than inventing a third,
RM-specific reconstruction convention" — but legacy already has a second, genuinely different
convention here (direct gray write, no matrix); porting it isn't inventing a third one.

**Round-2 auditor verdict**: "Yes — build it." Confirmed the corrected plan matches
`Main.cpp:4437-4449` step-for-step (no other clamp/offset/matrix step missed), confirmed two remaining
judgment calls both hold:
- **No truncation replication**: legacy truncates to `int` twice (once inside `GetPixelLevel`, once at
  `d *= gain`); this port keeps doubles throughout, matching every other decoder in this codebase.
  Documented as an accepted, asymmetric-across-mid-gray divergence, originally estimated as "a couple
  of levels" — **S21 (Band 4) later measured this exactly**: an exhaustive sweep over every achievable
  input in this port's own AGC'd ±16384 domain finds a max divergence of precisely 2 levels, never
  more, asymmetric as predicted (legacy-minus-port ranges -1..+2, not a symmetric ±2) — see this file's
  own "S21" entry further down. Well inside existing 10.0-25.0 tolerances, now confirmed rather than
  assumed.
- **No compensating TX-side gain**: legacy's TX (`LineRM`, `Main.cpp:6785-6801`) has no matching
  correction — confirmed RX-only. Round-trip is genuinely non-identity by design (predicted delta
  ~2.4 avg/~4.7 max: the offset terms cancel exactly, `112*8/7=128`, leaving pure gain error), but this
  is legacy's real asymmetry, not a port bug to "fix" by inventing symmetry legacy doesn't have (the
  Scottie-incident failure mode CLAUDE.md warns about). Predicted to stay under the existing flat 10.0
  round-trip tolerance without needing a Robot36-style override — and measurement confirmed this: no
  override needed.

**Fix**: `MonoAveragedPairedScanlineDecoder.cs` — `y` now a `byte[]` storing the final clamped value
directly; `(rawValue-128)*256/224+128` clamped to `[0,255]`, written straight into
`new Rgb24(gray,gray,gray)`, no `YCbCr.ToRgb` call. Rewrote both stale comments (the decoder's own, and
`SstvModeRegistry.cs`'s "NOT ported" note).

**Tests**: isolate-tested `EncodeThenDecode_RoundTripsWithinTolerance_MonoFamily` (RM8/RM12) first, per
the auditor's explicit instruction not to pre-add a tolerance override — passed at the existing flat
10.0, as predicted. Full suite: 327/327 passing (no new test files needed — existing coverage already
exercises this decoder).

**Piece 12 status: implemented, 327/327 passing.**

## Piece 13 — `TryDecodeNarrowModeHeader` FSK bit decode

**The bug**: `AnalogFmSstvDecoder.TryDecodeNarrowModeHeader` decoded each of the 24 MN/MC mode-ID bits
by averaging the shared PLL's demodulated-frequency stream over a fixed 22ms window and comparing to a
fixed midpoint threshold — a proxy that only worked by coincidence, reading off the general PLL stream
rather than replicating a dedicated detector. Same bug shape Piece 9 already fixed for VIS-bit decode.

**Legacy ground truth**: `CSSTVDEM::DecodeFSK(int m, int s)` (`sstv.cpp:2378-2606`), called every
sample with `m=int(d19)` (1900Hz mark envelope) and `s=int(dsp)` (2100Hz/`FSKSPACE` space envelope,
`m_iirfsk`, 100Hz bandwidth — same as `m_iir19`, not the 80Hz VIS-bit detectors). A 5-phase
`m_fskmode` state machine, not a simple threshold race.

**Two rounds of auditor plan-review, both substantive**: round 1 caught 3 real state-transition
errors in the first plan draft (mode 1's actual 50ms hold conflated with mode 2's 100ms *timeout
window*, which is a different thing; mode 3 mischaracterized as a debounce when it's really a single
recheck at a fixed 11ms-later instant; the `|m-s|>=2048` amplitude gate treated as checked every
sample when it's actually checked once per 22ms bit-sampling instant only) — each independently
re-verified against `sstv.cpp:2378-2444`/`sstv.h:710-717` before accepting, not taken on the auditor's
word alone. Also flagged: missing search-ceiling bound (this decoder is by construction a
retry-on-reject scanner — same unbounded-scan trap `TryDecodeVisDataBits` already hit once, `:1207`),
the `int`/`double` field-type contract (`m_fsktime`/`m_fsknexti` int, `m_fsknextd` double —
drift-corrects the 24-bit stream; naive repeated integer addition drifts ~13 samples by bit 24 at
11025Hz), collapsing to two caller-visible outcomes only (Locked/still-pending, matching the VIS-bit
race's own already-fixed precedent — legacy never permanently aborts, every failure path resumes
scanning from mode 0), and a `docs/removed-features.md` entry for the FSK callsign-ID packet (`0x2a`
STX, modes 5-10) this piece deliberately doesn't port. Round 2 re-derived the corrected state machine
fresh from source (not trusting round 1's own summary) and confirmed it clean, plus pinned down 3 final
one-line decisions: commit at `headerStart + NarrowHeaderTotalDurationMs` (the fixed nominal duration,
matching TX's real placement and the existing passing round-trip test) rather than wherever the state
machine locks; `0x2a` at the STX dispatch is treated identically to any other unrecognized byte (reset,
resume); and each `ProcessSample` call must read the mode once and run exactly one case's logic
(mirroring legacy's `switch`+`break`-per-`Do()`-call structure) — a re-dispatch-within-one-call
implementation would silently break the mode-2 zero-sample edge case. Verdict: "ready to build."

**Fix**: new `NarrowFskHeaderDecoder` (`src/ScanlineStudio.Core.Sstv/`) — a literal, sample-driven port of the
state machine (modes 0/1/2/3/4/16/17/18 only; 5-10 out of scope, see `docs/removed-features.md`).
`TryDecodeNarrowModeHeader` rewired to drive it with two dedicated `SyncEnvelopeDetector` instances
(1900Hz mark / `VisHeader.NarrowSpaceFrequencyHz` space) over `AgcSampleAt`, bounded by an explicit
local search ceiling mirroring `TryDecodeVisDataBits`'.

**Tests**: isolated first, per this project's chop-into-pieces methodology —
`NarrowFskHeaderDecoderTests` (13 tests) drives the state machine directly with synthetic int m/s
pairs, no audio/AGC/filter pipeline involved: all 6 registered mode codes lock correctly, an
unregistered-but-checksum-valid code returns its raw byte (mapping to "no mode" is
`SstvModeRegistry.FindByNarrowCode`'s job, one layer up, not this class's), wrong STX/bad checksum
reject-then-resume-scanning, a `0x2a` callsign-ID preamble followed immediately by a valid `0x2d`
packet still locks (confirms the carve-out is truly independent), and the `|m-s|=2048` vs `2047`
amplitude boundary and the 49ms-vs-50ms guard-hold boundary are both pinned exactly (the latter
surfaced a real, legacy-faithful off-by-one: mode 0's trigger sample is consumed before mode 1's
countdown starts, so completing the hold needs 551+1 samples, not exactly `MsToSamples(50)` — real TX's
100ms guard swallows this invisibly, but the boundary test needed the extra sample to pass). Full
suite: 340/340 passing (327 + 13 new).

**Piece 13 status: implemented, 340/340 passing.**

## Hilbert demodulator (`CHILL`) scoping pass — investigation only, no code changed

Follow-up to piece 10's flagged next step ("read `CHILL`'s real implementation and get an actual
settling-time comparison before deciding whether to implement it"). Read `sstv.cpp:3005-3087`/
`sstv.h:374-395` directly; no code written, no decision made on whether to implement — logged per
CLAUDE.md §8 so this doesn't need re-deriving cold next time.

**What `CHILL` is**: a Hilbert-transform-based instantaneous-phase (quadrature/arctan) FM
discriminator — structurally unrelated to `PllFmDemodulator`. Feedforward, not closed-loop: a Hilbert
FIR (`MakeHilbert`, Hamming-windowed sinc-difference -- corrected from an earlier misidentification as
Hann; the real formula is `0.54 - 0.46*cos(2*pi*n/N)`, `fir.cpp:458`, not Hann's `0.5 - 0.5*cos(...)` --
`fir.cpp:432`) produces a quadrature component,
paired with the delayed real signal (delay = half the tap count) to form an analytic signal;
`atan2(quadrature, delayedReal)` gives instantaneous phase; consecutive-sample phase difference
(unwrapped to ±π, lag depends on a sample-rate-tiered decimation factor `m_df`) gives frequency; a
final order-3/1800Hz Butterworth IIR smooths the result. No VCO, no loop filter, no lock-acquisition
transient in the PLL sense.

**Confirmed directly** (re-verified, not re-trusted from the earlier flag): `sstv.cpp:1492`'s
`CSSTVDEM` constructor sets `m_Type = 2` — Hilbert really is legacy's compiled-in default (PLL=0,
zero-crossing=1 are the non-default alternatives, `sstv.cpp:2256-2268`'s dispatch switch). It's a real
user-facing setting (`Option.cpp`'s `RGDemType` radio group, `.ini` `DemType` key, `Main.cpp:1937`) —
this port has no settings/UI layer yet to expose an equivalent toggle.

**Settling-time comparison, partly measured directly (not guessed)**:
- CHILL's FIR stage is a fixed-length window: legacy tiers tap count by sample rate (`SetWidth`,
  `sstv.cpp:3022-3051`) — 12 taps below 16kHz `SampBase`, 24 up to 40kHz, 48 above. That's a
  deterministic (no asymptotic tail) 13-sample transient at 11025Hz, 49 samples at 44100Hz.
- CHILL's final smoothing IIR is the exact same filter design this port already has and trusts
  (`IirFilter.Design` — already order-agnostic, confirmed by reading it, no new filter-design code
  needed). Instantiated it with CHILL's real params (order 3, 1800Hz cutoff) in a throwaway test
  (written, measured, then deleted — not committed) and measured its unit-step response directly:
  **3 samples to 95-99% at 11025Hz, 14 samples at 44100Hz.**
- Combined worst-case CHILL transient ≈ ~16 samples at 11025Hz, ~62 samples at 44100Hz — bounded and
  monotonic, no overshoot below the target.
- Against that: piece 10's own already-measured PLL number — undershoots BELOW the starting value
  within ~5 samples, then recovers over 30+ samples. Open-ended feedback-loop dynamics, and actively
  wrong-direction during recovery, not just slower.
- **Honest caveat, not smoothed over**: RM12 at 44100Hz has only ~12.7 samples/pixel dwell time
  (piece 10's own cited number) — shorter than CHILL's own 49-sample FIR window at that rate. CHILL
  would not fully settle within one RM12 pixel either. It would very likely still beat PLL there
  (bounded/monotonic vs. an active wrong-direction dip), but "Hilbert fixes RM12 outright" is not a
  safe claim without actually simulating per-pixel error — window-length comparison alone doesn't
  prove it, and this pass did not go that far.

**Integration cost, now concrete rather than vague**:
- AFC's `SyncFreq` call sites are already demodulator-agnostic in legacy (same call shape for all
  three `m_Type` branches, `sstv.cpp:2256-2268`) — this port's existing `AfcTracker`/
  `ApplyAfcCorrections` should accept a swap with no rework, just a different `d` source.
- A previously-just-flagged gap is now pinned down exactly: `Main.cpp:3794`/`5528` apply an extra
  `n -= dp->m_hill.m_htap/4` sync-anchor correction specific to Hilbert's own group delay, on top of
  the anchor correction piece 8's `SyncAnchorCorrector` already ports for the PLL case. Real, small,
  additive follow-on work if this gets implemented — not blocking, but not free either.
- The final smoothing-IIR stage needs zero new filter-design code (existing `IirFilter` already
  covers it, order 3 included). The Hilbert coefficient generator (`MakeHilbert`) and a tap-delay
  line (`DoFIR` — no equivalent utility exists anywhere in this port yet, checked) would be genuinely
  new, but small and mechanical (~40-50 lines combined).
- This replaces the demodulator for ALL modes, not just narrow ones — a full test-suite regression
  risk (golden-vector + round-trip, all 43 modes), not a scoped one like pieces 10-12 were.

**Sizing**: medium, self-contained piece, comparable to piece 8 or 9 — new demodulator class, a small
`SyncAnchorCorrector` addition, one construction-site swap in `AnalogFmSstvDecoder`, and a full-suite
re-run. Real but not slam-dunk case for closing the narrow-pitch gap (see the RM12 caveat above).

**Status: scoped, not decided.** User's call (2026-08-01): log findings, no decision yet on whether to
implement — revisit later, alongside or after Windows CI per the existing sequencing note.

**QSSTV cross-check (secondary reference, not authoritative per CLAUDE.md precedence) — worth noting
for whenever this is picked back up.** Checked `QSSTV-main/src/dsp/filter.cpp:184-229`
(`filter::processFIRDemod`) and `QSSTV-main/src/sstv/` for how QSSTV's own SSTV video demodulator
works: **no PLL anywhere in its SSTV/DSP code** (confirmed by grep across `src/dsp/` and `src/sstv/`,
excluding the unrelated DRM module). QSSTV mixes the input to baseband I/Q via an NCO, FIR-filters
both channels (**181 taps**, `VIDEOFIRNUMTAPS`, centered 1900Hz — far longer/sharper than `CHILL`'s
12-48 tap Hilbert filter), then does a delay-and-conjugate-multiply + `atan2` between consecutive I/Q
samples for the phase-difference/frequency reading, plus a sanity clamp (500-2600Hz, holds previous
value on violation) and a final FIR smoothing stage. Architecturally the same family as `CHILL` —
instantaneous/feedforward phase-difference discrimination, no closed loop — just via NCO down-mixing
instead of a wideband Hilbert transform on the passband signal directly.

Two independently-developed SSTV decoders (legacy YONIQ's real default, and QSSTV) both landed on
non-PLL discrimination for video decode — real corroborating evidence that the approach works in
practice, tempering the "PLL is probably safer for noisy real-world reception" caution above somewhat.
**But not without a caveat that matters if Hilbert ever gets implemented here**: QSSTV's 181-tap
front-end FIR is doing substantial noise-rejection work before its memoryless discriminator runs —
plausibly compensating for giving up PLL's loop-based noise integration with heavy front-end
filtering instead, not because instantaneous discrimination is inherently noise-robust on its own.
`CHILL` has no equivalent front-end weight. This connects directly to an already-flagged, still-unported
gap in this codebase: legacy's real pre-AGC/pre-demodulator bandpass filter chain (`sstv.cpp:1824-1833`,
logged elsewhere in this file). If Hilbert is implemented without also addressing that gap, real-world
noise robustness may not match either legacy's own Hilbert path or QSSTV's — worth treating that filter
chain as more load-bearing than it looked in isolation, not assumed away.

## Piece 14 — Hilbert demodulator (`CHILL`) port, implemented

Follow-up to the scoping pass above. Two rounds of auditor plan-review (soft-3-round backstop; round 2
verdict said round 3 wasn't needed), then implementation, then a real before/after measurement across
all 43 modes plus both golden-vector real-audio fixtures.

**Plan-review round 1 found 4 real blockers** in the first draft (all independently re-verified against
source before accepting): missing `m_OFF`/`m_OUT` per-tier multipliers (`sstv.cpp:3032-3047` — a 4x
error at 44100Hz, existing specifically to cancel the `2^df` phase-lag scaling); no output-domain spec
at all (legacy's scaled value is `(1900-f)*32768/800`, confirmed three ways, and the final smoothing
IIR filters THIS scaled value directly, not Hz — filtering in Hz first would give the filter a false
0Hz cold-start instead of the correct 1900Hz one, reproducing exactly the settling problem this piece
exists to fix); AFC re-sourcing (legacy's PLL branch feeds AFC from a *separate* zero-crossing counter,
but Hilbert's branch feeds AFC from its own output — this port's existing AFC modeled the PLL branch
specifically, would have kept modeling the wrong branch after the swap); `DoFIR`'s impulse response is
the REVERSED coefficient array (newest sample at the last buffer index), which a "natural" forward
convolution would silently get backwards, compounding with `MakeHilbert`'s own antisymmetry into a
sign error that could cancel invisibly in a self-consistency test while still being wrong.

**User decided to bundle the AFC re-sourcing into this piece's scope** (not defer it).

**Round 2 re-verified every round-1 fix from source independently** (not from round 1's own summary)
and confirmed all four correct, with MORE supporting evidence than round 1 found for two of them (the
output-domain derivation, and the fixed-width-simplification precedent). It also found ONE genuinely
new blocker: **legacy's AVT training-lock state machine always calls `m_pll.Do(ad)` directly**
(`sstv.cpp:2129/2159/2169/2187/2222`), completely outside the `m_Type`-dispatched picture-demodulation
switch — legacy always uses PLL for AVT lock detection regardless of which demodulator handles the
picture stream. This port's `AvtTrainingLockStateMachine` previously reused the main decode path's
stream on the explicit (soon-to-be-false) premise that "both legacy's `m_pll` here and this port's main
decode path are the exact same demodulator" — exactly the "inferred one code path from a neighboring
one" failure shape CLAUDE.md §4's Scottie incident warns about, caught before any code was wrong.
**User decided: keep a dedicated `PllFmDemodulator` instance feeding AVT**, matching legacy's real
dual-demodulator structure, rather than accepting the divergence. Two smaller paper fixes also applied:
both `MakeHilbert` buffers are `tap+1` elements, not `tap` (`sstv.h:380/381`, inclusive loop bounds);
the `m_df` warm-up assertions can't hold on the whole assembled class (FIR fill + IIR settling sit on
top of the phase-diff stage), so they're tested against the phase-diff logic in isolation instead.

**Implementation**: new `HilbertFmDemodulator` (`src/ScanlineStudio.Core.Sstv/`) — a literal port of `CHILL`,
with `MakeHilbert`/`DoFir`/`ComputePhaseDifference` each extracted as independently-testable
`internal static` methods (mirroring legacy's own free-function shapes) specifically so the riskiest
details (reversed-kernel indexing, `2^df` lag/warm-up counts) could be tested in isolation before
wiring. `AnalogFmSstvDecoder`'s main picture-decode demodulator swapped from `PllFmDemodulator` to
this class; `ApplyAfcCorrections`/`InitializeAfc` re-sourced from `_demodulatedFrequencies` directly
(no more `ZeroCrossingFrequencyCounter` in the mainline path); `AvtTrainingLockStateMachine` now fed by
a dedicated `PllFmDemodulator` instance (warmed up on real preceding audio, mirroring
`TryResolveSyncAnchorCorrection`'s own established technique); `SyncAnchorCorrector`'s caller adds the
`+htap/4`-samples term (sign independently confirmed two ways during review: algebraic substitution
into this port's already-established `-n` sign-flip convention, and a physical cross-check that the
Hilbert path delays the picture stream relative to the sync envelope this fold tracks). Both
`PllFmDemodulator` and `ZeroCrossingFrequencyCounter` stay in the codebase, genuinely used (AVT and
their own dedicated test suites respectively) — no `docs/removed-features.md` entries needed.

**A real bug caught by "test early, test often," not by review**: the AVT warm-up loop originally ran
eagerly in `TryStartAvtTraining`, unconditionally reading raw samples up to `_avtTrainingOriginSample`
-- `ArgumentOutOfRangeException` on the very first full-suite run, because a chunked/streaming
`PushSamples` caller can invoke `TryStartAvtTraining` before the buffer has actually grown that far.
Fixed by deferring the warm-up into `TryResolveAvtTraining`, gated on the origin point being fully
buffered, running exactly once whenever that becomes true.

**Full before/after measurement, all 43 modes + both golden-vector real-audio fixtures (measured
directly via a temporary diagnostic added to each test file, then removed -- not assumed, not
estimated): 43 of 45 measurements improved, 2 worsened.** The 2 that worsened are RM8 (1.766→2.065)
and RM12 (1.439→1.670) — exactly the narrow-pitch modes the original Piece 10 finding and the scoping
pass's own caveat both flagged as marginal (RM12's ~12.7 samples/pixel dwell time at 44100Hz is shorter
than `CHILL`'s own 49-sample FIR window there, so it can't fully settle within one pixel either). Both
worsenings are small and stay comfortably inside the existing flat 10.0 tolerance. Every other mode
improved, typically by 0.6-1.9 points (avg/max-per-channel delta). Golden-vector real-audio fixtures
also improved: martin-m1 1.438→1.312, robot-36 14.809→14.307 (`GoldenVectorTests.cs`'s own tolerance
comment updated with these numbers). No tolerance values needed to change anywhere in the suite.

**Tests**: `HilbertFmDemodulatorTests` (23 tests) — `MakeHilbert` coefficients checked against
independently-computed (Python, not derived from or captured against the C# implementation) fixture
values plus a structural antisymmetry check; `DoFir`'s reversed-kernel impulse response;
`ComputePhaseDifference`'s `2^df` lag and warm-up counts in isolation; settled-tone output at 1500/
1900/2300Hz (not just the center frequency, which can't catch a sign inversion); an exact-zero-real-
component case (confirmed to correctly read 0Hz, not the center frequency -- a genuinely non-oscillating
signal has no instantaneous frequency to report, a distinction an earlier draft of this test itself
got wrong before the code). Full suite: 363/363 passing.

**Status: implemented, committed.** Bandpass-filter-chain follow-up (the QSSTV-cross-check item above)
remains logged as a separate, deliberately deferred piece, not bundled here per user instruction.

## Pre-AGC bandpass filter chain — scoping, second opinion, and split into Piece A / harness / Piece B

Picked up the deferred bandpass-filter-chain item next. Investigation (`sstv.cpp:1819-1839`'s
`CSSTVDEM::Do`) found it bigger and more architecturally invasive than expected: `MakeFilter`
(`fir.cpp:332-427`) is a full Kaiser-windowed arbitrary FIR designer (not a one-off formula like
`MakeHilbert`); three filter variants (H1/H2/H3) across 3 width presets; the convolution engine used at
the call site (`CFIR2::Do`) uses a DIFFERENT addressing convention than `HilbertFmDemodulator`'s own
`DoFir` (`H[0]` pairs with the newest sample, not the oldest); and filter *selection* depends on
legacy's real-time per-sample lock state (`m_Sync`/`m_SyncMode`), which this port's upfront-buffer-
demodulation architecture doesn't have available at the point it would need it.

**Requested and got an independent second opinion (the `auditor` agent, framed explicitly as a scope/
value judgment, not a code-fidelity check) before committing effort.** It verified every technical
claim from source directly and found real corrections to both directions:
- The scope was overstated in one way: `MakeFilter` skips the Kaiser/Bessel branch entirely below 21dB
  attenuation, and every "Wide"-preset call (the default) uses `att=20` — so a Wide/H2-only slice never
  touches the Bessel `I0` function at all, comparable in size to `MakeHilbert`, not bigger.
- It was understated in another, more important way: legacy's real demodulator input (`m_Cur`,
  `sstv.h:256-257`) IS the post-filter, pre-AGC value — independently confirmed by reading `CLVL::Do`
  directly. This port's demodulator has always been fed raw, unfiltered samples. This isn't an optional
  noise-robustness bonus sitting off to the side; it's a real, structural input-pipeline gap, and it
  means the filter chain's effect IS measurable (it reshapes the demodulator's own input), contradicting
  an earlier "can't measure this" framing.
- It also caught a real risk this session had missed (group-delay skew between the picture-demod path
  and the sync/envelope path if the filter were applied to only one) and downgraded an overweighted one
  (the `CFIR2`/`DoFir` addressing-convention mismatch doesn't matter here — `MakeFilter`'s output is
  symmetric by construction, unlike `MakeHilbert`'s antisymmetric kernel, so reversal is a mathematical
  no-op for this filter specifically).
- Its recommendation, adopted: **split into Piece A** (the always-on, unconditional 2-tap
  moving-average pre-filter, `d=(s+m_ad)*0.5` — small, no filter design, no lock-state dependency, do
  immediately) **and defer the Kaiser bandpass filter itself (Piece B) behind a noise-fixture harness**
  that doesn't exist yet, converting "we think this helps" into an actual measurement rather than a
  judgment call. Explicitly recommended over a deferred-second-pass correction (AFC's own pattern):
  since this filter sits upstream of AGC, a deferred correction would mean re-running AGC, the
  demodulator, and every sync/envelope detector for the whole post-lock buffer — not a correction, a
  second full decode.
- On Piece B's expected value specifically, the auditor was MORE skeptical than this session's own
  framing, not just hedging alongside it: the *safe* scope (H2, run continuously, no lock-state
  switching — the only version without correctness risk) is also, by construction, the WEAKEST filter
  legacy ever runs (widest band, lowest attenuation, fewest taps) — so even a faithful port of the safe
  slice may not deliver the QSSTV-comparison noise-robustness benefit that motivated wanting this at
  all. Its verdict on deferring Piece B: not "can't measure the win" but "the only measurable outcome
  available today is regression detection... weak return" — defer specifically because the noise
  harness would make it a decidable question instead.
- User, after this discussion, raised a stronger alternative to the synthetic-noise harness: an actual
  TX'd picture recaptured over a real WebSDR (real atmospheric/propagation noise and receiver
  characteristics, not a synthetic AWGN approximation) — agreed this is categorically better evidence
  than the synthetic harness, and if also run through legacy's own decoder, would double as a genuine
  new golden-vector fixture (this project currently has exactly two, both already documented as
  unusually clean captures). Bigger practical lift (needs real TX+WebSDR access, not just code) but
  strictly better evidence for the exact question at hand. Not yet done — next-step decision point.

## Piece 15 — legacy's always-on 2-tap moving-average pre-filter, implemented

`CSSTVDEM::Do`, `sstv.cpp:1824-1825`: `d=(s+m_ad)*0.5; m_ad=s;` — unconditional (not gated by `m_bpf`),
applied before AGC and before the demodulator. `m_ad` zeroed once at construction (`sstv.cpp:1417`),
confirmed never reset in either `Start()` overload or `Stop()` — matches this port's existing
continuously-running-filter precedent. Traced all three post-filter signal domains directly from
source (independently re-verified during plan-review, not just accepted): `m_Cur` (post-filter,
pre-AGC) feeds the picture demodulator; `ad` (post-filter, post-AGC, unscaled) feeds AVT's dedicated
PLL call; the final scaled+clipped `d` (`ad*32`, clip ±16384) feeds the sync/tone-envelope detectors.

New `FilteredRawSampleAt(int index)` -- a pure function of `_rawSamples` (no adaptive state, so safe to
call independently from multiple sites with bit-identical results, no shared cache needed). Applied at
all four sites that previously fed raw samples directly: `AgcSampleAt` (upstream of the existing AGC
logic, which was already otherwise correct), `PushSamples`'s main demodulator feed, and both of
`AvtTrainingLockStateMachine`'s dedicated-`PllFmDemodulator` call sites (closes part of that class's
already-flagged, still-only-partially-resolved input-domain gap from Piece 14 -- the deeper
unscaled-AGC-domain mismatch stays exactly as previously deferred, not expanded into here). One
single-round auditor plan-review before implementation (matching the piece's small size) found the
helper should return `double`, not `float` (legacy computes entirely in double; an earlier draft's
`float` return introduced an avoidable rounding step) -- fixed before coding.

**A real, pre-existing bug found by the new chunk-boundary test, unrelated to this piece.** Following
the auditor's recommended test (decode the same signal as one `PushSamples` call vs. many small
chunks, assert identical results), a first version asserting EXACT pixel identity failed -- confirmed
via `git stash` to fail IDENTICALLY on pre-Piece-A code too, so not a regression from this piece.
Measured severity: ~1.75 average per-channel delta between whole-push and chunked-push decodes of the
same signal, comfortably inside every tolerance already in this suite. Root cause not chased (off-scope
for this piece): this port's deferred/incremental correction passes (AFC, Auto Slant) process "whatever
is available so far" as data streams in, so chunk timing can shift their exact correction values by a
small amount -- a real, small, pre-existing characteristic of the port's architecture that no existing
test had caught (every other chunked test in this suite only checks mode-detection equality, not full
pixel identity). Test adjusted to a 5.0-tolerance comparison instead of exact identity -- still catches
a genuine Piece-A-specific regression (a wrong previous-sample reference at a chunk boundary would
produce a structural misalignment, not a small ambient delta like this), without being blocked by the
unrelated pre-existing gap.

**Measured before/after, all 43 modes + both golden-vector fixtures (same methodology as Piece 14): 18
improved, 26 worsened, 1 unchanged -- but every change is tiny** (mostly <0.1, largest is robot-36's
real-audio fixture at +0.169). This is the expected signature of a smoothing filter applied to fixtures
with little noise to remove: the synthetic round-trip fixtures carry zero noise, and both real
golden-vector captures are already-documented as unusually clean -- a mixed, small-magnitude result is
consistent with "correctly implemented, real value not provable with what we currently have to test
against," not a regression. Everything stays comfortably inside existing tolerances; none needed to
change. `GoldenVectorTests.cs`'s own tolerance comment updated with the new numbers.

**Tests**: the new chunk-boundary consistency test (`SstvRoundTripTests.cs`) described above. Full
suite: 364/364 passing.

**Status: implemented, not yet committed as of this entry.** Piece B (the Kaiser bandpass filter
itself) remains deferred behind either a synthetic noise-injection harness or, if arranged, a real
TX/WebSDR capture -- decision point, not yet started.

## Noise-robustness harness — built, baseline measured

User's call: build the synthetic noise-injection harness now as an interim check, while separately
weighing a real TX/WebSDR recapture (categorically better evidence -- real propagation/receiver
characteristics, not synthetic AWGN -- and would double as a new golden-vector fixture if also run
through legacy's own decoder) as a longer-lead-time follow-up. Not a legacy port (legacy has no
synthetic-noise-injection concept of its own) -- new test infrastructure, built directly rather than
through the usual plan+auditor-review cycle since there's no legacy source to verify fidelity against;
the judgment calls (noise model, SNR calibration, the "usable decode" quality bar) are documented
inline in `NoiseRobustnessTests.cs` instead.

**Design**: additive Gaussian noise injected into the ENCODED AUDIO SAMPLES (post-encode, pre-decode --
the domain a real receiver's front-end noise actually occupies), calibrated to a target SNR by
measuring the real encoded signal's own RMS power first (`SNR_dB = 20*log10(signalRms/noiseRms)`), not
an assumed/fixed noise amplitude. Deterministic (fixed seed) for reproducible, genuinely comparable
results run to run. Sweeps a fixed set of SNR levels (40 down to 0dB) per mode, measures average
per-channel delta at each, and reports the "noise floor" -- the lowest SNR still meeting a documented
"usable decode" bar (30.0 average delta, a judgment call noted as such, not derived from source: looser
than `SstvRoundTripTests`' own noiseless-signal tolerances, which measure DSP self-consistency, not
real noise tolerance). One regression-guard assertion (the noiseless/infinite-SNR case must still
decode correctly) catches the harness itself being broken; the SNR sweep itself is informational,
logged via `ITestOutputHelper`, not strictly asserted per level -- there's no known-correct threshold
to assert against yet, that's what this harness exists to discover.

**Baseline measured (this port's CURRENT state: `HilbertFmDemodulator` + piece 15's 2-tap pre-filter,
no Kaiser bandpass filter yet)**:
- **martin-m1**: noise floor **9.0dB** SNR. Degrades roughly monotonically from 2.72 (40dB, clean) to
  25.24 (9dB, still usable) to 33.48 (6dB, fails the bar).
- **robot-36**: noise floor **16.0dB** SNR -- meaningfully worse tolerance than martin-m1, consistent
  with this mode's already-documented fragility to small timing perturbations (piece 8's tone-selector-
  ambiguity finding). Degrades roughly monotonically down to 16dB (22.18, still usable), then becomes
  NON-monotonic at more extreme noise (12dB=45.90, 9dB=122.18, 6dB=58.19, 3dB=73.50) -- expected, not a
  harness bug: at very low SNR, header/VIS detection can fail outright in qualitatively different,
  effectively chaotic ways (wrong-mode misdetection, garbage decode) rather than smoothly degrading,
  which the harness's own doc comment already flagged as an unproven assumption, not asserted.

**These two numbers are the actual comparison target for Piece B**, not a pass/fail gate on their own --
re-run this same harness (`NoiseRobustnessTests.cs`) once the Kaiser bandpass filter exists and compare
the new noise floors against 9.0dB/16.0dB. A meaningful improvement (materially lower noise floor, i.e.
usable decode at a WORSE SNR than today) is the actual evidence this piece is worth its cost; no
meaningful change would be real, measured evidence for the auditor's own skepticism (the safe/H2-only
scope being "the weakest filter legacy ever runs") turning out correct.

**Tests**: `NoiseRobustnessTests.cs`, 2 new tests (martin-m1, robot-36). Full suite: 366/366 passing.

**Status: implemented, not yet committed as of this entry.** Piece B itself still not started --
baseline now exists to measure it against, decision on WHEN to build Piece B (now vs. after arranging a
real TX/WebSDR capture too) not yet made.

## Piece B — the Kaiser/search bandpass filter (`H2`), implemented and measured against baseline

User's call: build Piece B now rather than waiting on a real TX/WebSDR capture. `SearchBandpassFilter`
-- a literal port of `CSSTVDEM::Do`'s pre-AGC bandpass stage (`sstv.cpp:1826-1833`), scoped to ONLY
legacy's `H2`/"search" width variant (`sstv.cpp:1522-1551`'s `CalcBPF`) per the earlier scoping
discussion's adopted recommendation: the widest, most permissive of the three lock-state-selected
variants, run continuously rather than gated by `m_Sync`/`m_SyncMode` (this port's upfront-buffer
architecture has no real-time lock state available at the point legacy would switch filters). `H1`/`H3`
and the lock-state switch itself deliberately not ported.

**Scope confirmed narrower than `MakeFilter`'s full generality once traced**: `H2`'s parameters
(400-2500Hz, attenuation 20) are identical across all three legacy width presets -- only tap count
differs (24/64/96, scaled by sample rate) -- and this port's only reachable preset is the shipped
default (Wide, confirmed `sstv.cpp:1416` and the `.ini` `DEMBPF` fallback, `Main.cpp:1855`). The
Kaiser/Bessel branch of `MakeFilter` (`fir.cpp:346-427`) only activates at attenuation >=21dB; `H2` is
always 20 -- provably unreachable for this filter, not an approximation, so not ported (matches piece
14's `MakeHilbert` precedent of omitting a provably-unreachable branch).

Chains onto piece 15's `FilteredRawSampleAt` output (`sstv.cpp:1824-1834`'s real order: 2-tap
pre-filter -> `if(m_bpf) m_BPF.Do` -> AGC), applied at the same four consumption sites piece 15 already
touches (`AgcSampleAt`, the main demod feed in `PushSamples`, both of AVT's dedicated-PLL call sites) via
a new forward-fill cache (`BandpassFilteredSampleAt`, mirroring `AgcSampleAt`'s own established pattern).

**Auditor plan-review, one round, found one load-bearing issue and one latent correctness gap, both
independently re-verified against source before fixing**:
- **Causal-vs-centered window (the important one).** `CFIR2::Do`'s real convolution (`fir.cpp:1131-1144`)
  pairs `H[0]` with the NEWEST sample and walks backward -- a genuine, constant `tap/2`-sample group
  delay (~1.09ms at every reachable rate, since tap scales with rate), not a centered window. `MakeFilter`'s
  output kernel is symmetric by construction for this port's only reachable (even) tap counts, which rules
  out a coefficient-REVERSAL sign risk (unlike `HilbertFmDemodulator`'s antisymmetric kernel) but does
  NOT make causal-vs-centered alignment irrelevant: a centered window would pass a symmetry check, a
  coefficient-fixture check, and a frequency-response check identically while silently shifting every
  downstream sync/slant/line anchor by `tap/2` samples -- the exact "restructured-but-provably-equivalent"
  failure shape CLAUDE.md §4's Scottie incident warns about. Reasoned explicitly, not assumed, why this
  needs NO new sync-anchor correction term (unlike piece 14's Hilbert `+htap/4`): the filter sits upstream
  of ALL four consumption sites uniformly, so the sync-envelope path and the picture-demod path see the
  same new delay together -- no differential delay for `SyncAnchorCorrector`'s argmax search to be wrong
  about. Defended primarily by an impulse-response test (`ProcessSample_ImpulseResponse_IsCausal_NotCentered`),
  the only test shape that can distinguish causal from centered.
- **Odd-tap symmetry claim was over-broad.** The mirroring loop in `MakeFilter` only writes `2*(tap/2)+1`
  entries (integer division) -- for odd tap, the trailing coefficient is never written and stays
  zero-init, genuinely asymmetric. This port's only reachable tap counts (24@11025Hz, 96@44100Hz) are
  both even, so latent, not currently wrong -- ported to mirror legacy's EXACT loop bounds rather than
  assume full-array coverage, and the symmetry test scoped explicitly to even tap only.

**Performance regression found and fixed, not predicted (though flagged by the auditor as a possible
follow-up if it happened): full suite went from ~3min baseline to 12min2s (387/387 still passing) after
first wiring Piece B in.** Root cause, two compounding issues: (1) the original design was
`ProcessSample(Func<int,double> filteredSampleAt, int index)`, recomputing the O(tap) convolution from
scratch on every call with no cache, and multiple call sites frequently requesting the same index; (2)
`Func<int,double>` delegate-call overhead, multiplied by up to 97 taps per convolution at 44100Hz. Fixed
in two steps: a forward-fill cache alone brought a `SstvRoundTripTests` subset from timeout territory to
6min57s for 41 tests -- still not enough -- so `SearchBandpassFilter` itself was redesigned from the
stateless `Func`-based window lookup to a genuine streaming delay line (`ProcessSample(double input)`,
`Array.Copy`-shift + dot-product), explicitly modeled on `HilbertFmDemodulator.DoFir`'s already-proven
pattern. Full suite after the redesign: **387/387 passing, 4min44s** -- close to the pre-Piece-B baseline,
the remaining difference being genuine new per-sample work, not overhead.

**Noise-floor comparison against the established baseline (the actual acceptance criterion, per
`NoiseRobustnessTests.cs`'s own stated test-plan item) -- meaningful improvement in both modes**:

| Mode | Baseline (piece 15, no Piece B) | With Piece B | Improvement |
|---|---|---|---|
| martin-m1 | 9.0dB | 3.0dB | 6dB lower noise floor |
| robot-36 | 16.0dB | 9.0dB | 7dB lower noise floor |

Both modes now decode usably (average per-channel delta <= 30.0) at meaningfully worse SNR than before
-- real, measured evidence the filter is worth its cost, not just legacy-parity-for-its-own-sake. This
directly answers the auditor's own stated skepticism (the safe/H2-only scope being "the weakest filter
legacy ever runs, may not deliver the benefit") with a measurement rather than more argument either way.

**Tests**: `SearchBandpassFilterTests.cs`, 21 new tests (coefficient fixtures at two tap/rate
combinations independently computed in Python, not derived from the C# implementation; the causal
impulse-response test; an 8-point frequency-response sweep against independently-computed magnitudes).
Full suite: 387/387 passing, 4min44s. `NoiseRobustnessTests.cs` re-run: 2/2 passing, new noise floors
recorded above.

**Status: implemented, measured against baseline, not yet committed as of this entry.**

**Demo:** a console/test harness encodes a test image to a `.wav`, decodes it back, and the round-trip image matches within tolerance — provable before any UI exists.

## Windows CI fix — `dotnet restore`/`build`/`test` failing since Engine 0-6, root cause found and fixed

Not a DSP/port item — CI infrastructure, tracked here because it blocked seeing green Windows results
for everything above. `windows-latest` had been failing since Engine 0-6 (2026-07-30); Linux/macOS legs
were unaffected.

**Root cause:** `.github/workflows/ci.yml`'s Windows-only `ilammy/msvc-dev-cmd@v1` step (added for
`ScanlineStudio.Core.Audio.MiniAudio`'s `BuildNativeShimWindows`/`cl.exe` target) sets `Platform=x64` as a
job-level env var. MSBuild/`dotnet restore` implicitly reads ambient `Platform`/`Configuration` env vars
as default property values, and `ScanlineStudio.sln` only defines "Any CPU" solution configurations (no
"Debug|x64") — so restore failed on every Windows run: `error MSB4126: The specified solution
configuration "Debug|x64" is invalid.` Build/Test steps never ran. Confirmed via `gh run view <id>
--log-failed` across several failing runs, all showing the identical Restore-step error.

**Fix:** pass `/p:Platform="Any CPU"` explicitly on the `restore`/`build`/`test` steps, overriding the
ambient env var. `cl.exe` itself is invoked via an `Exec` command in `BuildNativeShimWindows`, not
through MSBuild's `$(Platform)` property, so it's unaffected by the override and still gets the
x64 toolchain msvc-dev-cmd set up.

**Verified against real CI runs, not just eyeballed:** two consecutive pushes both green on all three
legs (`gh run watch <id>`) — Windows 5m25s / macOS 2m31s / Linux 3m51s on the first, Windows/macOS/Linux
all passing again on the second. Commits `8be35b7` (fix), `761ef1a` (doc update).

**Status: fixed and committed. Windows/Linux/macOS all green on `master`.**

## Pre-Phase-2 gate: shortcut/simplification audit — IN PROGRESS

User's call, before committing to Phase 2 (radio layer): rather than run the milestone-audit
playbook's Phase 3 chain audit immediately, first inventory every known DSP-in-pipeline
simplification/deferral this port already carries (distinct from `docs/removed-features.md`'s
whole-capability removals), triage which are worth fixing now, fix the important ones, THEN capture
more real golden-vector fixtures, THEN run the Phase 3 chain audit — so the audit runs against the
best available code and the widest available real-audio coverage, not the other way around.

**Sequencing (tracked as tasks #5-8 in this session):**
1. Compile inventory of simplifications (done, see table below — a fork/subagent research pass).
2. Independently verify that inventory with the `auditor` subagent — check each claim against real
   source, re-derive risk tiers, search for anything missed in roadmap ranges the first pass
   under-covered, and produce a full must-fix-to-nice-to-have priority ranking. **IN PROGRESS as of
   this entry, not yet returned.**
3. Fix the prioritized items (expected to be more involved than initially hoped — user's own
   assessment before seeing the auditor's ranking).
4. Capture ~5-6 new real golden-vector fixtures from the legacy binary, covering mode
   families/mechanisms the existing two fixtures (Martin M1 = `RgbSequential`, Robot 36 =
   `YCbCrRobot`) don't exercise: Scottie S1 (mid-line sync — the exact family that already produced
   one real synthetic-test-passes-while-wrong incident, `CLAUDE.md` §4), Robot 72 or R24
   (`YCbCrSequential`), a PD/MP mode (`YCbCrLinePaired`), RM8 or RM12 (`MonoAveragedPaired`, no
   chroma), a narrow MN/MC mode (FSK mode-announce header instead of VIS), AVT (training-lock state
   machine). Capturing is bottlenecked on the user's time with the real legacy Windows binary, not on
   dev work, so it can start any time independent of step 3's progress.
5. Run the milestone-audit playbook's Phase 3 (chain/integration audit) only — skip Phase 1/2, units
   are already individually verified to an unusual degree (`docs/audit-playbook.md`).

**Inventory (first pass, NOT YET independently verified — auditor review pending):**

| Item | Legacy mechanism (citation) | Why deferred | Risk tier | Roadmap/source citation |
|---|---|---|---|---|
| Lock-dependent bandpass filter never switches — Piece B's `SearchBandpassFilter` (H2) runs continuously regardless of lock state | Legacy switches `HBPFS` (search/pre-lock) vs `HBPF`/`HBPFN` (locked) (`sstv.cpp:1826-1832`) | Auditor-assessed (earlier, scoping pass) as "the only version without correctness risk" given this port's upfront-buffer architecture has no real-time lock state at filter-selection time; deliberate safety-first scope cut, not an oversight | C | roadmap lines 136, 161, 757-764, 880-882 |
| Unbounded memory growth: `_rawSamples`/`_demodulatedFrequencies`/`_agcSamples` never trimmed (~1.1GB/hr @11025Hz, ~4.4GB/hr @44100Hz) | No legacy equivalent needed — legacy processes one sample at a time in real time, never buffers a whole session | Harmless for today's test-only callers; explicitly flagged as relevant "once a production caller wires this decoder to real capture" | C | roadmap line 171 |
| `TryDecodeVisDataBits` reconstructs 4 fresh detectors and replays from `headerStart` on *every* `PushSamples` call while unlocked — no persistent cursor, unlike every other detector in the file | N/A — port-specific architecture gap vs. legacy's real-time incremental processing | Assessed as "probably still realtime-feasible," not urgent at the time; matters most "for a streaming caller pushing small chunks frequently while unlocked" | C | roadmap line 282 |
| AFC/Auto Slant's deferred correction passes are chunk-timing-sensitive: whole-push vs. chunked-push decodes of the same signal differ by ~1.75 avg per-channel delta | N/A — port-specific: these run as "whatever is available so far" bulk passes, not legacy's true per-sample real-time loop | Root cause not chased ("off-scope for this piece"); magnitude across arbitrary real-world chunk sizes/timings never characterized | C | roadmap lines 798-807 |
| `MiniAudioCaptureSession`'s bare `try/catch` around `SamplesAvailable` would silently swallow the `InvalidOperationException` this port relies on for one real bug's guard, once a production caller wires the decoder to real capture | N/A — infra gap | Named as forward-looking, not yet a live caller to break | C | roadmap line 169 (round-2 finding #5) |
| While-locked tone-envelope detectors (`d11`/`d12`/`d13`/`d19`/`dsp`) never retune after an AFC correction is applied | Legacy's real detectors retune for free via `InitTone`'s side effect (`sstv.cpp:2362`); this port's stay fixed-frequency | Explicitly named by an Opus outline review as deferred, not silently absorbed | B | roadmap line 136 |
| Mid-image AVT re-lock not supported | `sstv.cpp:2139-2144`; `VisLockStateMachine` deliberately never reports AVT | Same batch as above | B | roadmap line 136 |
| Mid-image narrow-mode (MN/MC) FSK-announce re-lock not supported | `sstv.cpp:2592` | Needs a sample-by-sample FSK decoder this port doesn't have; `TryDecodeNarrowModeHeader` is a fixed-window analytic shortcut with no real-time counterpart | B | roadmap line 136 |
| MN/MC narrow-mode PLL further-narrowing (2044-2300Hz) not implemented — this port narrows flat to 1500-2300Hz for all modes | `CSSTVDEM::SetWidth`/`IsNarrowMode` (`sstv.cpp:1707-1719`, `266-279`) | "A documented simplification, not a bug" — pixel tones stay in-band either way, but loop/VCO-gain dynamics genuinely differ from legacy's real narrower band | B | roadmap line 255 |
| Extended-VIS escape byte decided from 7 bits, not legacy's full 8-bit pattern (incl. parity=0) | `sstv.cpp:2066` | Pre-existing, noted not fixed in piece 9's scope; a malformed byte (e.g. `0xA3`) would diverge between port and legacy | B | roadmap line 258 |
| AVT training-lock's dedicated PLL reads this port's raw/bandpass-filtered domain, not legacy's real post-2-tap-LPF/post-bandpass/**post-AGC** domain | `sstv.cpp:1835`'s `ad` (AGC'd, unscaled) | "AGC-domain gap stays exactly as already flagged and deferred from the Hilbert demodulator piece, not expanded into here" | B | `AnalogFmSstvDecoder.cs:1561-1565`; roadmap lines 788-789 |
| `m_sint1`/`m_sint2`/`m_sint3`'s legacy case-0/1/2/9 "freeze while decoding VIS bits" gating has no equivalent — this port's merged loop evaluates them unconditionally | `sstv.cpp` case structure | Real, low-severity structural divergence, documented rather than left silent | B | roadmap line 121; `AnalogFmSstvDecoder.cs:693` |
| `VisLockStateMachine`'s omitted `m_SLvl`/`m_SLvl2` absolute-amplitude gates, originally flagged as a mid-image false-positive-lock risk once Piece 6c made it run per decoded line | Legacy's absolute AGC'd-scale thresholds | Blocked at the time on the not-yet-built `CLVL` AGC port; **Piece 7c later reintroduced these exact gates into `VisLockStateMachine`'s own trigger conditions** — plausibly resolved, status sent to auditor to confirm | B (status needs confirming) | roadmap lines 142, 153-154 |
| Per-channel TX gain trim (`m_VariOut`) not modeled for any mode | `sstv.cpp:2880` | Confirmed zero effect on transmitted frequency/waveform — optional, off-by-default legacy feature; gain-tag bits are masked off before use regardless | A | roadmap line 44; `SstvModeRegistry.cs:599` |
| `CLVL`'s peak-hold bookkeeping (`m_PeakMax`/`m_PeakAGC`/`m_Peak`/`m_CntPeak`) omitted | `sstv.h` peak-hold fields | Write-only in legacy too — only reader is the UI level-meter bar (`Main.cpp:6186-6192`) | A | roadmap line 149 |
| `m_agcfast==0` branch (averaged-5-window AGC recompute) not ported | `sstv.h:272-279` | Confirmed dead code in legacy itself — constructor unconditionally overwrites to 1 | A | roadmap line 149 |
| No int-truncation replication in RM8/RM12's gain-corrected gray path (and generally, this port keeps doubles where legacy truncates) | `Main.cpp:4437-4449` truncates twice | Measured: a couple of levels' asymmetric divergence around mid-gray, well inside existing tolerances | A | roadmap lines 468-471 |
| `H1`/`H3` bandpass filter width variants (Narrow/VeryNarrow) not ported | `fir.cpp` `MakeFilter` presets | Only reachable via a `DEMBPF` .ini setting this port's settings/UI layer doesn't expose yet; shipped default (Wide/H2) is the only reachable preset | A | roadmap lines 880-887 |
| Same-sample `m_sint1`-then-`m_sint3` double-fire structurally can't happen in this port (return-immediately-on-any-match) | Legacy's straight-line code lets both evaluate the same sample | Explicitly assessed as low severity, "close to a no-op" since `m_Sync` is already 1 by the time it would matter | A | roadmap line 158 |
| `m_ReqSave` (legacy saves a ≥65%-complete abandoned image) not ported | `sstv.cpp:2134-2138`, `Main.cpp:4931-4934` | Correctly blocked on the not-yet-built logging/history feature (Phase 4) | A | roadmap line 143 |
| `m_SyncRestart` hard-wired on, no user toggle | `sstv.cpp:1486`, `Main.cpp:10907`/`11887` | Correctly blocked on the not-yet-built settings/UI layer | A | roadmap line 143 |
| AVT training-lock case 8 folded into case 6's `h==0x40` transition | `sstv.cpp:2234-2239` | Confirmed legacy's own case 8 is unreachable dead code too — no functional effect either side | A | roadmap lines 105, 107-108 |
| `MakeHilbert`'s final unreachable `else{x1=x2=1.0;}` branch not ported | `fir.cpp:432-474` | Provably unreachable — the preceding `n==L` check already excludes the only case that would reach it | A | `HilbertFmDemodulator.cs:211-214` |

Coverage note from the compiling pass: roadmap lines 293-326, 337-373, 377-426, 486-546, 606-638,
666-720, 825-873, 893-954 got lighter/no line-by-line coverage — sent to the auditor as ranges to
specifically re-check for missed items.

**Status: calls (1) and (2) both done and logged above. Call (3) (reconcile + priority ranking,
merging both outputs) launched, in progress as of this entry. Do not start step 3/task #6 (fixes)
until call (3) is back and reviewed.**

**Call (1) results — verify existing table row-by-row. Verdict: EQUIVALENT-WITH-RISKS — mostly
accurate, but 1 row flat-out wrong, 1 stale-resolved, 2 stale/mis-attributed citations, 2 tier changes:**

- **Flat-out wrong (4 things):** (a) the "H1/H3 width variants not ported" row (tier A) is mis-framed
  — H1/H3 aren't width variants gated behind a `.ini` setting, they're the SAME lock-state-selected
  filters as row 1 (`sstv.cpp:1827-1831`), fully reachable at the shipped default — this row duplicates
  row 1's gap at the wrong (too-low) tier, not a separate item. (b) the narrow-further-narrowing row's
  citation (`CPLL::SetWidth`) is stale — legacy's default demod is Hilbert (`m_Type=2`), so the PLL is
  AVT-only now and AVT is never narrow; the real live gap is `CHILL::SetWidth`/`CFQC::SetWidth`, and
  "loop/VCO-gain dynamics" reasoning doesn't even apply to a Hilbert transformer. (c) memory-growth
  figures are pre-Piece-B: actually 5 buffers not 3 (Piece B added `_bandpassFilteredSamples`), ~1.43
  GB/hr @11025Hz / ~5.7 GB/hr @44100Hz, not 1.1/4.4. (d) the `m_sint1/2/3` freeze-gating row claims all
  three are ungated; `m_sint1` was actually already fixed by an earlier holistic-review pass
  (roadmap 163) — only `m_sint2`/`m_sint3` remain ungated.
- **Stale-resolved:** `VisLockStateMachine`'s `m_SLvl`/`m_SLvl2` amplitude gates — verified CLOSED.
  Piece 7c reintroduced every gate legacy has at all 4 real call sites, including the correct
  3-term/2-term asymmetry (case-0/1 use the 3-term `d12>d19 && d12>SLvl && (d12-d19)>=SLvl` form,
  case-3-verify correctly uses only the 2-term form with no difference gate) and the right
  `SLvl=3500/SLvl2=1750` values (`SetSenseLvl` case 1, matching the real ctor default). Downgrade to A
  / fold into row 1 (the only remaining delta is AGC/threshold calibration seeing H2 instead of H1
  while locked — that's row 1's gap, not a separate one).
- **Tier changes:** `TryDecodeVisDataBits` rebuild-per-push: C→**B** (the replay is bounded by a
  search ceiling, ~1.07-1.31s of samples, so it's O(1) per push not O(buffer) — no correctness risk).
  `MiniAudioCaptureSession`'s bare catch: stays C but **broader than stated** — swallows every decoder
  exception, not just the one specific guard originally named.
- **Everything else CONFIRMED** as originally logged, verified line-for-line against real source
  (full detail: this session's auditor transcript, not reproduced here in full).

**Call (2) results — gap search over the 8 under-covered ranges, all confirmed read in full. Found 9
new items** (full table + citations: see this file's own history a few entries up, "Call (2) results"
heading). One of the 9 (`CHILL` narrow-mode retune not ported) is the SAME gap as call (1)'s corrected
narrow-further-narrowing row — a duplicate discovered independently by both calls, which is itself a
good cross-check signal. The other 8 are net-new: `m_Type` demodulator selector has no user toggle
(only Hilbert branch exists); CQ100 mode entirely unmodeled; narrow-FSK header commits at a fixed
nominal offset instead of the real lock sample; a bounded local search ceiling on narrow-FSK/VIS-bit
decode where legacy never permanently gives up; AVT's dedicated PLL warmed up on a clamped 2000-sample
window instead of continuous stream history; AVT training entry skips all 3 VIS repeats before
constructing the lock state machine (legacy enters after the first); `MakeFilter`'s Kaiser/Bessel
design branch unported (dependency note: becomes reachable the moment row 1/H1/H3 ever gets fixed);
`MakeFilter` odd-tap trailing-zero asymmetry (latent, current tap counts are all even); `CHILL`'s
middle decimation tier implemented but never exercised.

**Call (2) results — gap search over ranges 293-326, 337-373, 377-426, 486-546, 606-638, 666-720,
825-873, 893-954, all 8 confirmed read in full:**

Correction caught up front: the biggest gap those ranges originally described — PLL-instead-of-Hilbert
demodulator (`sstv.cpp:1492`/`2256`) — is **closed by Piece 14**, not open. Ranges 293-326, 337-373,
377-426, 825-873 confirmed nothing new (findings already fixed pre-implementation, or — for the noise
harness at 825-873 — genuinely new test infra with no legacy mechanism to diverge from).

New items found (same table shape as the existing inventory):

| Item | Legacy mechanism (citation) | Why deferred | Risk tier | Roadmap/source citation |
|---|---|---|---|---|
| `m_Type` demodulator selector: only the Hilbert branch exists in the picture path; zero-crossing (`m_fqc`) never used for picture demod, no user toggle | 3-way dispatch `case 0: m_pll / case 1: m_fqc / default: m_hill` (`sstv.cpp:2256-2268`, `2310-2318`); user setting `Option.cpp RGDemType`/.ini `DemType` (`Main.cpp:1937`) | No settings/UI layer yet to expose an equivalent toggle | B — default-path parity holds (Hilbert IS legacy's compiled-in default), but legacy users can switch to PLL/zero-crossing for hard signals; no removed-features.md entry | roadmap 565-569, 604 |
| `CHILL` narrow-mode retune not ported — `HilbertFmDemodulator` fixed at 1900Hz/800Hz non-narrow for ALL modes incl. MN/MC | `CSSTVDEM::SetWidth` retunes all 3 demodulators per mode (`sstv.cpp:1707-1715`); `CHILL::SetWidth`'s narrow branch (`sstv.cpp:3024-3031`) | Mirrors `PllFmDemodulator`'s already-accepted same simplification; verified affine-only (`m_OFF`/`m_OUT`), not tap count/`m_df` | B — this is now the LIVE picture-demod path (unlike the old PLL-based MN/MC-narrowing item), unmeasured | roadmap 676; `HilbertFmDemodulator.cs:58-65` |
| CQ100 mode (`-i` switch) not modeled: FIR tap-tripling AND -1000Hz global tone offset | `sys.m_bCQ100` tap*=3 (`sstv.cpp:3048-3050`); `g_dblToneOffset=-1000.0` under `-i` (`Main.cpp:1065-1077`) | No CQ100-equivalent hardware modeled anywhere in this port | A — no removed-features.md entry; code's "g_dblToneOffset confirmed always 0.0" claim is true only absent `-i` | roadmap 676; `HilbertFmDemodulator.cs:72-76` |
| Narrow-FSK header commits at fixed nominal `headerStart + NarrowHeaderTotalDurationMs`, not the state machine's actual lock sample | Legacy `DecodeFSK` fires `Start()` at the lock instant (`sstv.cpp:2378-2606`) | Chosen against TX's real placement (`Main.cpp:7423-7424`) + already-passing round-trip test | B — supporting evidence is a round-trip test, the exact "both sides agree while wrong" shape CLAUDE.md §4 warns about; drift/jitter unmeasured | roadmap 519-521; `AnalogFmSstvDecoder.cs:1176-1181` |
| Bounded local search ceiling on narrow-FSK header/VIS data bits — port gives up; legacy never permanently aborts | Every legacy FSK failure path resets to `m_fskmode=0` and rescans indefinitely (`sstv.cpp:2378-2444`) | Unbounded scan was "a real, reverted regression"; `m_sint3` fallback kept as mitigation | B — mitigated by fallback, but ceiling is architectural not legacy-derived; off-nominal-header behavior unmeasured | roadmap 510-511, 529-530; `AnalogFmSstvDecoder.cs:1168-1174`, `1307-1323` |
| AVT's dedicated PLL warmed up on a clamped 2000-sample window, not continuous stream history | Legacy's `m_pll` runs continuously from stream start (`sstv.cpp:2129/2159/2169/2187/2222`) | Upfront-buffer architecture has no continuously-running detector; mirrors `TryResolveSyncAnchorCorrection`'s technique | B — same family as the inventoried "detector reconstruction per push" but a distinct instance/constant; effect on AVT lock unmeasured | roadmap 683; `AnalogFmSstvDecoder.cs:1540-1558` |
| AVT training entry restructured: port skips all 3 VIS repeats before constructing the lock state machine, rescopes timeout budget | Legacy enters case 4 right after the FIRST repeat, burns repeats 2/3 as marker-search noise | Simplification; fixed `AvtExtraHeaderDurationMs` kept as ceiling on the argument legacy's fallback converges near the same duration | B — convergence argument reasoned, not measured | `AnalogFmSstvDecoder.cs:1513-1522` (no roadmap line — found in code) |
| `MakeFilter`'s Kaiser/Bessel (`I0`) design branch not ported | `fir.cpp:346-427`, activates only at attenuation >=21dB | Provably unreachable today: H2 is always attenuation 20 | A today, but becomes reachable the moment H1/H3 (already inventoried as a separate item) is added — silently gates that follow-up | roadmap 888-890; `SearchBandpassFilter.cs:20` |
| `MakeFilter` odd-tap trailing-zero asymmetry: symmetry test scoped to even taps only | `fir.cpp` mirroring loop writes `2*(tap/2)+1` entries, trailing coeff stays zero for odd tap | Only reachable tap counts (24@11025Hz, 96@44100Hz) are even — latent, not currently wrong | A | roadmap 913-917 |
| `CHILL` middle decimation tier (16-40kHz -> 24 taps, `m_df=1`) implemented but never exercised | `CHILL::SetWidth` tiering (`sstv.cpp:3032-3047`) | Implemented for completeness, untested until/unless a rate in that range is used | A | roadmap 573-574; `HilbertFmDemodulator.cs:54-57` |

Off-scope notes from this pass (not chased, per scope discipline): FSK callsign-ID packet already has
a `docs/removed-features.md` entry, correctly excluded here. RM8/RM12 golden-vector deltas worsened
slightly post-Piece-14 but confirmed legacy-faithful (legacy's own 48-tap CHILL window also exceeds
RM12's pixel dwell at 44100Hz) — not a port simplification.

**Call (3) results — reconcile + priority rank. DONE, task #9 complete.** Corrected the item count:
30 items, not ~20 (call (2)'s own prose undercounted its own 10-row table by one). Full reconciled
table (30 items, S1-S30, tier C/B/A) lives in this session's auditor transcript — condensed version
with priority bands below; the merges applied: H1/H3 folded into S1 (same gap, wrong tier — H1/H3
aren't `.ini`-gated width variants, they're the same lock-state-selected filters, fully reachable at
the shipped default), `VisLockStateMachine` amplitude-gates row dropped (closed, residual folded into
S1's note), narrow-further-narrowing + call (2)'s independently-found CHILL-retune merged into one
item (S9) with the citation corrected to `CHILL::SetWidth`/`CFQC::SetWidth` (not `CPLL::SetWidth` —
legacy's live default is Hilbert, PLL is AVT-only now).

**Priority ranking (5 bands, every item placed):**

- **Band 1 — must fix before Phase 2 starts** (S4 exception-swallowing catch, S2 unbounded memory
  growth [corrected: 5 buffers, ~1.43GB/hr@11025/~5.7GB/hr@44100], S3 AFC/Slant chunk-timing
  sensitivity [the only item with a MEASURED delta, ~1.75], S1 lock-dependent bandpass filter switch,
  S28 Kaiser/Bessel branch [ships in the SAME change as S1 or S1's filter is built by the wrong design
  branch]). Rationale: each either breaks outright under live capture, has its deferral premise
  invalidated by Phase 2 specifically, or destroys the evidence needed to judge everything else.
- **Band 2 — should fix during Phase 2 bring-up, before trusting new fixtures** (S5 VIS-bit-decode
  detector rebuild-per-push, S16 AVT PLL clamped warmup window, S15 bounded search ceiling, S14
  narrow-FSK fixed-offset commit, S6 locked-detector no-retune). Rationale: batch-vs-streaming
  correctness, only "correct" today because the only caller is a test harness pushing whole buffers.
- **Band 3 — worth doing eventually, bundle with the matching new golden-vector fixture** (S31 AVT
  real-capture header-detection failure — **DONE**, see this file's own "S31" entry further down —
  S9 MN/MC narrow retune — **DONE, discovered already closed**: Band-2 item S6 (`6da0a65`) IS this
  exact fix (`HilbertFmDemodulator.ProcessSample`'s `isNarrow`-selected `(off, out)` pairs, gated in
  `AnalogFmSstvDecoder.DemodulatedFrequencyAt` on `_mode.NarrowModeCode is not null`) — this Band-3
  listing was simply never updated when S6 shipped; no new code needed — S8 mid-image narrow re-lock —
  **DONE**, see this file's own "S8" entry below, much smaller than originally scoped (the per-sample
  FSK decoder already existed and was already correct; this wired it up as a persistent scanner) — S7
  mid-image AVT re-lock, S11 AVT PLL domain — **DONE**, see this file's own "AVT package" entry below
  (bundled with S17 per this Band's own pattern-3 finding that the AVT items are one work package) —
  S17 AVT training-entry restructure — **CLOSED via documentation, no code change**, see the same "AVT
  package" entry — measured (not assumed) that the port's analytic training entry lands one block late
  vs. legacy's real search-based entry, but completion timing recalculates absolutely per-block, so the
  imprecision has zero effect on final accuracy — S10 extended-VIS
  7-bit — **DONE**, see this file's own "S10" entry below, widened in scope from the original narrow
  "escape byte only" framing to the real underlying gap (every normal VIS-code match in the
  fixed-window path, not just the escape byte) — S12 sint2/sint3 freeze gating [sint1 already fixed] —
  **DONE**, see this file's own "S12" entry below — first plan-review round caught a real design gap
  before any code shipped (the originally-proposed gate only covered legacy's case-0↔1 boundary, not
  the case-2/9/3 freeze the item is named for) — S13 m_Type demodulator toggle [code blocked on
  Phase-3 settings UI, but its missing removed-features.md entry is Band-4 work now, **DONE**].
  **Band 3 is now fully done, all 8 original items closed** (7 landed on mode families task #7 already
  captured fixtures for, fix when measured not reasoned; S12 needed direct source-derived design work
  instead, no fixture dependency).
- **Band 4 — documentation/test-only, near-zero cost, no DSP change — DONE, all 4 items** (S13's doc
  half already closed alongside S9/S10; S27 CQ100 `removed-features.md` entry + a stale code comment —
  **DONE**, code-level audit caught the first draft's own entry was still incomplete (2 more real
  `m_bCQ100`-gated DSP effects missed) before it shipped; S29 odd-tap assert/guard — **DONE**; S30 added
  a decimation-tier unit test — **DONE**; S21 recorded the already-measured tolerance rationale —
  **DONE**, exact bound measured (2 levels max, `-1..+2` signed) rather than the prior "a couple of
  levels" estimate. See this file's own "Band 4" entry further down for full detail. Zero DSP behavior
  change across all 4 items, confirmed by an unchanged full-suite result.
- **Band 5 — not worth it / correctly blocked** (S18 per-channel TX gain, S19 CLVL peak-hold, S20
  dead `m_agcfast` branch, S22 sint1/sint3 double-fire, S23 `m_ReqSave` [blocked on Phase 4], S24
  `m_SyncRestart` toggle [blocked on Phase 3], S25 AVT case-8 dead code, S26 `MakeHilbert` unreachable
  branch). Verified dead/no-effect/correctly-phase-blocked — do not touch.

**5 bigger patterns found, most important first:**

1. **The dominant one: "upfront buffer vs. real-time stream" is ONE architectural gap masquerading as
   7 separate items** (S1, S2, S3, S5, S15, S16, and arguably S6) — this port processes a growing
   buffer in bulk passes where legacy runs one continuous per-sample loop over persistent detector
   state. That's 7 of 30 items, including 3 of 4 tier-C rows and the only measured defect.
   **Recommendation: decide the streaming contract explicitly (persistent per-detector cursors + a
   bounded ring buffer + one incremental pump) BEFORE writing individual fixes — most of Band 1/2
   collapse into one design change with one test suite instead of seven patches that each get
   revisited once Phase 2 lands anyway.**
2. Filter-selection cluster (S1+S28+folded-H1/H3+folded-VisLockStateMachine-residual) is one fix, not
   four — shipping S1 without S28 ships a filter built by the wrong design branch and looks correct
   while being wrong.
3. AVT (S7/S11/S16/S17) and MN/MC narrow (S8/S9/S14/S15) are mode-family holes, not 9 independent
   bugs — each is one work package gated on the one fixture step 4 already plans to capture.
4. Two missing `docs/removed-features.md` entries (S27 CQ100, S13 `m_Type` selector) are CLAUDE.md §2
   process-rule debt, not DSP debt — one sitting, do alongside Band 1 regardless of code-fix ranking.
5. **The evidence base is thin and it's fixable in parallel**: only S3 has a measured number; S21 is
   measured-and-bounded; everything else in Bands 2-3 is reasoned, not measured. Starting golden-vector
   capture (step 4) now, in parallel with Band 1 fixes, would let Bands 2-3 get re-ranked by
   measurement instead of argument — directly settles S14/S9/S17/S7's self-described "unmeasured"
   status.

**Pre-check RESOLVED (2026-08-02): S28 drops out of Band 1.** Read `CalcBPF` directly
(`sstv.cpp:1522-1551` + `CalcNarrowBPF`): at the Wide preset (`bpf=1` — the ONLY preset this port
currently reaches; Narrow/VeryNarrow are gated behind a `.ini` DEMBPF setting with no UI yet), H1's
attenuation is **20**, matching H2's 20 exactly (`sstv.cpp:1530-1531`, `CalcNarrowBPF` case 1 for H3
also 20). Confirmed in `fir.cpp:364` that the Kaiser/Bessel branch only activates at `att>=21`. So
porting the lock-dependent H1/H3 switch (S1) at Wide-preset scope needs **no** Kaiser/Bessel work —
S28 only resurfaces if Narrow/VeryNarrow (att 40/50, `sstv.cpp:1536-1537`/`1542-1543`) are ever
implemented, which stays its own separate, correctly-blocked (no settings/UI) tier-A item.

**Band 1 is now 4 items, not 5: S4, S2, S3, S1.** No same-change coupling requirement for S1 anymore.

**Status: task #9 complete, S28 pre-check resolved. Starting task #6 — working Band 1 items one by
one through the normal process (plan -> auditor plan-review -> implement -> test), per user
instruction. Order and per-item plans logged as each one starts, below.**

### Band-1 item 1 (S4) — exception-swallowing catch in `MiniAudioCaptureSession`, DONE

Started with this one first since it's the smallest and most standalone (no dependency on the
"streaming contract" architecture question Pattern 1 flagged).

**Draft plan**: keep the existing catch (deliberately added by an earlier opus-review pass -- a
raw background `Thread`, unlike a thread-pool work item, dies the whole process on an unhandled
exception, so some catch is required), but stop it being silent -- add a `LastCallbackException`
property mirroring the file's existing `TimedOutDuringClose` pattern.

**Auditor plan-review verdict: buildable, but 3 things needed fixing on paper first (no second round
needed):**
1. `TimedOutDuringClose`'s plain-auto-property pattern doesn't transfer -- that one is written once
   under a write lock during `Dispose`, with `_drainThread.Join()` supplying the happens-before for
   its single read site. The new field is written repeatedly on a live thread with readers on
   arbitrary other threads -- needs `volatile` (reference field) / `Interlocked`+`Volatile.Read`
   (counter), not a plain auto-property.
2. Needed a counter alongside the exception, not just the last value (`OverrunCount`'s own "raw
   counter for the caller to interpret" shape is the in-file precedent) -- otherwise "threw once" and
   "threw on every single chunk of a 2-minute transmission" are indistinguishable.
3. The class is `internal` with `InternalsVisibleTo` scoped to tests only -- as drafted, the property
   would be unreachable from any real production caller. Needed a `MiniAudioEngine` pass-through,
   mirroring the existing `CaptureOverrunCount` pattern exactly.
4. (Judgment call, not a blocker) auditor also caught a real second bug on its own initiative:
   `SamplesAvailable?.Invoke(samples)` is a single try/catch around the WHOLE invocation list -- one
   throwing subscriber silently starves every other subscriber, and every later chunk, from being
   delivered. Real designed-for scenario per `spec/05-audio-engine.md:89` (a VU-meter subscriber
   running alongside the DSP decode pipeline on the same stream) -- fixed in the same commit.
5. (Nit) renamed away from "Callback" (already means the native real-time callback everywhere in
   this file's vocabulary) -- `LastSubscriberException`/`SubscriberExceptionCount` instead.

**Implemented**: `volatile Exception? _lastSubscriberException` + `Interlocked`-incremented
`_subscriberExceptionCount`, exposed as `LastSubscriberException`/`SubscriberExceptionCount`
(deliberately NOT gated on `_disposed`, unlike `OverrunCount` -- this is exactly the state a caller
wants to inspect right after a session dies). `DrainLoop` now iterates
`SamplesAvailable.GetInvocationList()` with a per-handler try/catch instead of one catch around the
whole multicast call. `MiniAudioEngine.CaptureLastSubscriberException`/`CaptureSubscriberExceptionCount`
pass-through added, mirroring `CaptureOverrunCount`.

**Tested**: new `SamplesAvailable_SubscriberThrows_IsRecordedAndDoesNotStarveOtherSubscribers` test
(real virtual-sink audio, not mocked) — one handler throws every chunk, a second handler counts its
own invocations; asserts the second handler keeps firing (proves per-handler isolation) AND
`LastSubscriberException`/`SubscriberExceptionCount` are correctly recorded. Full suite: 387/387
`ScanlineStudio.Core.Sstv.Tests`, 51/51 `ScanlineStudio.Core.Audio.MiniAudio.Tests` (49 previous + this new one +
one other pre-existing), solution-wide build clean. **Status: DONE, committed `288d5d0`.**

### Band-1 items 2+3 (S2 memory growth, S3 chunk-timing sensitivity) — per user instruction, following
the auditor's Pattern-1 recommendation: treated as one combined piece rather than two independent
patches, since both were flagged as likely tracing to the same "upfront buffer vs. real-time stream"
architectural gap.

**Investigation phase first** (not a blind fix — matches this project's established methodology):
dispatched a dedicated investigative pass (read-only, no code changes) to find S3's actual root cause
before drafting any plan, since the original inventory's attribution ("AFC/Auto Slant process
'whatever's available so far'") was itself unverified.

**Root cause found, high-confidence, source-cited (not yet empirically confirmed by the investigator
itself — no Bash access in that pass; confirmation is the next step, done in this session directly
since Bash is available here):**

**AFC and Slant are NOT the cause — both confirmed already fully chunk-invariant** (`ApplyAfcCorrections`'s
bound can never be limited by `_demodulatedFrequencies.Count` since the enclosing per-line guard at
`AnalogFmSstvDecoder.cs:388` already guarantees enough data exists; `ApplySlantTracking` is bounded
only by `_consumedSamples`, never by `Count`). The original test comment attributing this to AFC/Slant
is wrong and needs correcting once the real fix lands.

**The real mechanism: two header-detection paths race, and priority is decided by call-boundary
timing, not absolute sample position.** `TryDecodeVisHeader` (the fixed-window, analytically-precise
path) refuses to commit until the full 910ms header is buffered (`:1443`,
`_demodulatedFrequencies.Count - headerStart < totalHeaderSampleCount` → `return false`) — when that
happens, `TryDecodeHeader` falls through (`:621`) to `TryInterleavedHeaderScan`, whose loop bound is
explicitly "whatever has arrived so far" (`:871`, `_syncBypassProcessedUpTo < _rawSamples.Count`) and
which commits at a DIFFERENT anchor (`VisLockStateMachine`'s own empirically-triggered lock, with a
self-documented, uncorrected group-delay lag — measured ~80 samples @11025Hz elsewhere in this file,
scaling to ~320 samples/~7.3ms @44100Hz). In a one-shot push, the fixed-window path is satisfied on
the very first `TryProcessBuffer` call and the fallback never runs at all. In a chunked push, the
fallback runs on every chunk from sample 0 onward and gets a real chance to win — a race window
several hundred samples wide (Martin M1 @44100: fixed-window needs `Count>=40131`; fallback can
commit as early as `trigger+12569`), and if it wins, the committed anchor is off by roughly the
group-delay lag (~7.3ms ≈ ~16 pixels of a Martin M1 scan) before `TryResolveSyncAnchorCorrection`'s
fold absorbs most of it — which is exactly why the symptom is a small ~1.75 ambient delta rather than
a visibly torn image, not evidence it's a small/unimportant bug.

**Structural, not a one-liner**, per the investigator: `TryDecodeHeader`'s own doc comment states the
intent "header wins, every time" but the implementation only delivers that when the fixed-window
path's availability gate happens to be satisfied on the same call it's checked — a call-scoped,
not sample-scoped, priority decision. Proposed principled fix (not yet plan-reviewed): scope the
fallback's own bound to lag the fixed-window path's own commit latency
(`_rawSamples.Count - maxFixedWindowHeaderLatency`), so the fixed-window path always gets first
refusal at the same absolute sample index regardless of how push calls are chunked.

**S2 (buffer trimming) is separable from S3 — confirmed, does not need to wait.** Hard rule for
correctness: the trim watermark must be `min(every processed-up-to cursor) - lookback` (never a
single cursor — some cursors run far ahead of others, e.g. AGC/bandpass-filter cursors vs.
AFC/Slant/VIS-lock cursors), AND trimming must never happen while `_mode is null` or
`_pendingAnchorCorrectionMode is not null` (exactly the region S3's bug lives in, and where every
long backward-read — AVT PLL warm-up, sync-anchor fold, header retry rescans — also lives). ~25
call sites need offset-translation (every buffer is indexed by absolute sample index today). The one
coupling: if S3's fix adds a new cursor (the fallback's lagged bound), it just joins the trim
watermark's `min(...)` — a one-line addition, not a redesign.

**Status: investigation done. Next: empirically confirm the race hypothesis (chunk-size sweep +
targeted temporary instrumentation at the 4 commit call sites, reverted before any real fix), THEN
get an auditor plan-review of the actual fix (both S3's priority-scoping fix and S2's trim-watermark
design) before writing production code. Test comment at `SstvRoundTripTests.cs:198-205` needs
correcting once the real fix lands (currently misattributes this to AFC/Slant).**

**Empirically confirmed (2026-08-02), done directly in this session (the investigative pass had no
Bash access) -- clean, unambiguous result, exactly the non-monotonic pattern predicted, not a smooth
AFC-drift pattern.** Added temporary `[CallerLineNumber]`-based instrumentation to `Commit()`
(reverted immediately after, working tree confirmed clean via `git status`/`git diff`), swept chunk
sizes for a real Martin M1 encode:

| Push shape | Commit call site | Anchor sample |
|---|---|---|
| Whole (one-shot) | line 1512 (fixed-window path) | 40131 |
| chunk=499/500/512/1000/1024/20000 | line 896 (interleaved VisLock fallback) | **40571** |
| chunk=4096/40131/45000 | line 1512 (fixed-window path) | 40131 |

Confirms the race exactly as hypothesized: small/misaligned chunk sizes let the fallback commit
first at a DIFFERENT anchor (440 samples off, ~10ms @44100Hz); larger/aligned chunk sizes let the
fixed-window path get satisfied first, matching the one-shot result. Not a diffuse drift — a discrete
either/or race outcome depending purely on push chunking, confirming the root-cause diagnosis, not
just corroborating it.

**Fix plan (drafted, not yet auditor-reviewed):**
- **S3**: give the fixed-window path a guaranteed first-refusal window in ABSOLUTE sample terms, not
  call-scoped terms. Concretely: `TryInterleavedHeaderScan`'s own scan bound becomes
  `Math.Min(_rawSamples.Count, _rawSamples.Count - maxFixedWindowHeaderLatency)` -- i.e. the fallback
  never examines/commits on samples the fixed-window path could still claim first. `maxFixedWindowHeaderLatency`
  needs deriving as the maximum `totalHeaderSampleCount`-equivalent across every mode this port
  detects via the fixed-window path (the fallback can't know in advance which mode is arriving, so it
  must wait out the worst case, not a specific mode's own value) -- needs a new
  `SstvModeRegistry`/`VisHeader` helper, not a hardcoded guess. Open question for the auditor: does
  this introduce unacceptable header-detection latency for genuinely headerless/degraded signals that
  currently rely on the fallback firing early (the whole POINT of `TryInterleavedHeaderScan`/`m_sint2`-
  equivalent detection)? Needs explicit discussion, not just implemented and hoped.
- **S2**: bound/trim the 5 growing buffers using watermark = `min(_afcProcessedUpTo, _slantProcessedUpTo,
  _visLockProcessedUpTo, _syncBypassProcessedUpTo, _avtTrainingProcessedUpTo, _consumedSamples) - lookback`
  (`lookback = max(2000, SearchBandpassFilter tap+1, Hilbert tap+1, ksbSamples, 1)`), with a hard rule:
  never trim while `_mode is null` or `_pendingAnchorCorrectionMode is not null`. ~25 call sites need
  offset-translation (every buffer indexed by absolute sample index today). If S3's fix adds a new
  cursor, it joins the `min(...)` -- confirmed compatible, not blocking.

**Status: both items' fixes now going to the auditor for a combined plan-review (per user instruction
to follow the auditor's own Pattern-1 recommendation -- one coherent piece, not two unrelated
patches) before any production code is written.**

**Auditor plan-review verdict: NOT ready to build. 7 real, paper-level defects found, each cheap to
fix now and expensive to retrofit after ~25 call sites are rewritten.**

1. **[blocker] Rolling cap is the wrong mechanism, not just a latency cost.** `bound = Count - L`
   permanently drops the tail of a finite stream (a headerless transmission in the last ~1.3s of a
   bulk/file decode becomes undetectable -- a NEW bug) and imposes a needless per-sample latency
   penalty forever (the fixed-window path's search is one-shot, not rolling -- once exhausted it's
   provably dead for the epoch). **Fix: one-shot `_fixedWindowExhausted` gate instead** (`scanBound =
   (Count >= _consumedSamples + L) ? Count : _consumedSamples`, cleared in `EndOfImage`). Cost:
   ~395ms one-time delay for the VisLock path per epoch; typically ZERO added delay for the
   genuinely-headerless m_sint1/2/3 path (already needs ≥0.6-1.34s of consecutive-interval matching).
2. **[blocker] `L` must be the max SEARCH CEILING (1305ms, extended-VIS's own retry margin), not max
   commit-gate duration (1150ms)** -- using the smaller number reopens the race, just narrower.
   Derive in `VisHeader` (not `SstvModeRegistry` -- not per-mode), and have
   `TryDecodeVisDataBits`/`TryDecodeNarrowModeHeader` compute their own `searchCeiling` from the SAME
   helper so the two can't silently desync.
3. **[blocker] Trim watermark formula had 3 real bugs**, independently re-derived by enumerating
   every backward-read site: missing `_levelAgcProcessedUpTo`/`_bandpassFilteredProcessedUpTo` from
   the `min()` (reachable today, not hypothetical -- AGC lags `_consumedSamples` right after
   `Commit`); `_syncBypassProcessedUpTo` is frozen while locked, so including it pins the watermark
   for a WHOLE IMAGE (up to ~380MB at PD290/44100 before it can advance again); lookback derivation
   was wrong (`SearchBandpassFilter`/`HilbertFmDemodulator` are streaming, tap counts don't belong;
   the real raw lookback is 1; the load-bearing 2000 constant is for the sync-anchor-correction
   warm-up specifically, not a generic safety margin -- needs sharing with those warm-up sites, not
   re-derived independently).
4. **[risk] `EndOfImage`'s 0.5s dead-time skip means AGC never gets fed through it** (deliberate,
   matches legacy's own continuous-feed behavior, `:204-210`'s existing doc comment) -- once
   `_levelAgcProcessedUpTo` joins the watermark (per #3), this PINS the watermark forever after image
   1 unless addressed. Recommended: force-feed `AgcSampleAt` through the dead zone in `EndOfImage`
   (0.5s of extra filter work per image, preserves the documented legacy-fidelity property).
5. **[blocker] The biggest one -- "never trim while `_mode is null`" is EXACTLY BACKWARDS.** The
   5.7GB/hr memory-growth scenario this whole fix exists for IS the never-locks case (an idle
   receiver on open squelch) -- excluding it from trimming means the actual motivating case is never
   fixed at all; the locked case is already naturally bounded by one image's duration. **Real fix:
   pre-lock trimming at a ~5s retention window** (`max(L=1.3s, SyncIntervalTracker's own max interval
   ×3 ≈4.17s, + anchor warm-up)` ≈8MB @44100 instead of unbounded) -- keep the "never trim while
   `_pendingAnchorCorrectionMode is not null`" half of the original rule, drop the `_mode is null`
   half entirely.
6. **[structural recommendation, not a blocker]** implement via a single `_bufferBase` + accessor
   methods (`RawAt(i)`, `DemodAt(i)`, etc.) rather than rewriting all ~25 absolute-index call sites
   individually -- contains the change, preserves existing anchor arithmetic verbatim. `List<T>.RemoveRange`
   is O(remaining); amortize trims (only trim once accumulated slack passes ~1s worth) to avoid O(n²).
7. **New test needed**: assert anchor EQUALITY across chunk sizes {1, 500, 4096, one-shot}, not a
   pixel-delta tolerance -- the existing tolerance-based test is exactly why the wrong AFC/Slant
   attribution survived undetected this long.

Verdict explicitly: "the underlying diagnosis is correct, the two problems genuinely do share one
root... resolve these five [now seven, folding in 6/7], and the piece is well-scoped and ready" --
no second plan-review round required, apply corrections directly (same pattern as Band-1 item 1).

**Status: revising plan per the 7 points above, then implementing in sub-pieces (chop into
independently-tested parts, per this project's established methodology): (A) VisHeader search-ceiling
helper, zero behavior change; (B) one-shot gate + the actual race fix, tested via anchor-equality
across chunk sizes; (C) `_bufferBase` abstraction, zero behavior change; (D) pre-lock trim watermark +
EndOfImage AGC force-feed, tested via a long-non-locking-stream memory-bound test; (E) full-suite
regression + update the stale AFC/Slant test comment.**

**Sub-piece A DONE (commit `365d57b`)**: `VisHeader.NormalSearchCeilingMs`/`ExtendedSearchCeilingMs`/
`NarrowSearchCeilingMs`/`MaxSearchCeilingMs` (1035/1305/950/1305ms), pinned by a dedicated test against
independently hand-derived values rather than wired directly into the two existing decoder methods'
own inline arithmetic (deliberately -- combining several separately-`MsToSamples()`-rounded terms into
one would risk a ±1-sample rounding-order behavior change, which the "zero behavior change" scope for
this sub-piece explicitly ruled out). 388/388.

**Sub-piece B DONE, the actual S3 race fix.** Added `_fixedWindowExhausted` (reset in `EndOfImage`
alongside every other pre-lock cursor/flag) and gated `TryInterleavedHeaderScan`'s scan bound: stays
pinned at `_consumedSamples` (0 new samples scanned) until `_rawSamples.Count >= _consumedSamples +
MsToSamples(VisHeader.MaxSearchCeilingMs)`, at which point it flips permanently open for the rest of
the epoch -- a one-shot gate, not the rejected rolling cap. Correctness relies on the method's own
already-established entry invariant (`_syncBypassProcessedUpTo == _visLockProcessedUpTo ==
_consumedSamples`), so any fallback match is provably >= the point the fixed-window paths are already
known to be exhausted (pure functions of accumulated `Count`, not call count -- so if either could
have succeeded, it already would have, on an earlier call, deterministically).

**Verified, not just implemented**: tightened
`DecodedImage_MatchesWithinTolerance_WhetherSamplesArriveInOneChunkOrMany` (renamed
`DecodedImage_IsPixelIdentical_WhetherSamplesArriveInOneChunkOrMany`, now a `[Theory]` over chunk
sizes {1, 500, 4096}) from a loose 5.0-tolerance match to EXACT pixel identity -- passes at all three
sizes including the pathological `chunkSize=1`, confirming decode is now provably deterministic
regardless of chunking, not just "close enough." Corrected the stale comment that misattributed the
old failure to AFC/Slant. Full suite: 390/390. Golden-vector tests re-run specifically (real captured
legacy audio, the highest-value check): 8/8 unaffected.

**Status: S3 (Band-1 item 3) fully done. Moving to S2 (Band-1 item 2, buffer trimming) -- sub-pieces
C/D/E next.**

### Band-1 item 2 (S2) — unbounded sample-buffer memory growth, DONE

**Implementation**: single `_bufferBase` field + `Rel(int absoluteIndex)` translator (throws
`InvalidOperationException`, loudly not silently, if asked to read behind the trim watermark) --
avoided rewriting ~25 individual call sites by routing every read through `Rel()`, either directly or
via the 4 existing forward-fill accessors (`AgcSampleAt`/`FilteredRawSampleAt`/
`BandpassFilteredSampleAt`/`AgcCurMaxAt`). `TotalSamplesReceived = _bufferBase + _rawSamples.Count`
replaces every `.Count` read used as an absolute total. `TrimBuffers()` (called once per `PushSamples`)
computes a watermark two ways (locked: `min(_afcProcessedUpTo, _slantProcessedUpTo,
_visLockProcessedUpTo, _levelAgcProcessedUpTo, _bandpassFilteredProcessedUpTo, _consumedSamples) -
AnchorWarmupSamples`, deliberately excluding `_syncBypassProcessedUpTo` since it's frozen while
locked; pre-lock: a fixed trailing retention window sized to `max(VisHeader.MaxSearchCeilingMs,
SyncIntervalTracker.MaxIntervalSamples) + AnchorWarmupSamples`, further bounded by the same live
cursors), amortized behind a `MinTrimSamples` (44100) threshold before actually calling
`List<T>.RemoveRange` on all 5 buffers. `AdvanceAgcThroughDeadZone`/`_agcDeadZoneCatchUpTarget`:
defers `EndOfImage`'s AGC dead-zone force-feed (preserving the documented "AGC advances monotonically
regardless of dead-time skip" legacy-fidelity property) until the dead-zone's own samples have
actually arrived, rather than assuming they're already available synchronously inside `EndOfImage`.

**A real bug found and fixed DURING implementation, not anticipated by the plan-review**: the first
working version's pre-lock watermark included `_consumedSamples` in its `min()` unconditionally.
`_consumedSamples` is NEVER advanced pre-lock except by `Commit()`/`EndOfImage()` -- for a stream
that never locks (an idle receiver on open squelch, the EXACT scenario this fix exists for), it stays
0 forever, permanently blocking all trimming. Caught by actually running the intended test
(`BufferedSampleCount_StaysBounded_ForLongNeverLockingStream`, 30s of real noise) rather than assuming
the implementation matched the reviewed design.

**Two designs tried for the fix, second one kept**: (1) skip `TryDecodeVisHeader`/
`TryDecodeNarrowModeHeader` entirely once `_fixedWindowExhausted` (they're pure functions of
`(headerStart, buffered data)`, provably dead for the epoch), excluding `_consumedSamples` from the
watermark only then. (2) periodically RE-ANCHOR `_consumedSamples` forward to track each trim,
re-arming `_fixedWindowExhausted` for a "fresh" shot each time. (2) was tried first and reverted: a
second real test (`DecodedImage_StillDecodesCorrectly_WhenPrecededByLongSilence_ThatTriggeredTrimming`,
a real Martin M1 transmission after 20s of silence) showed the re-anchor point is arbitrary relative
to any real header's actual start -- the odds of landing exactly there are negligible, so the extra
complexity (re-arming, re-closing `TryInterleavedHeaderScan`'s gate, real risk of subtly reopening the
S3 race) bought back no actual precision. Kept (1): simpler, no race risk, and the practical outcome
is identical either way -- a header arriving well after the epoch's one fixed-window opportunity is
exhausted is found via `TryInterleavedHeaderScan`'s fallback, at that path's own already-documented,
already-accepted anchor precision (29.0 tolerance, matching `SyncBypassDetectionTests`' own precedent
for the same mode/mechanism) -- a pre-existing architectural property, not a regression this fix
causes. The test asserts detection succeeds + structural correctness within that established
tolerance, not exact pixel identity.

**Final code-level auditor review (after implementation, before commit): "Ready to commit," no
blockers.** Independently re-verified every `Rel()` call site (confirmed complete, no bypasses),
re-derived both watermark branches' safety from scratch (found one additional real coupling the
implementation relies on but hadn't stated explicitly: `TryResolveAvtTraining`'s own warm-up reads
aren't directly in the pre-lock `min()`, safe only because `_fixedWindowExhausted` is provably false
for the whole AVT-pending window), confirmed the never-locking-stream fix is correct and doesn't break
AVT detection, confirmed `AdvanceAgcThroughDeadZone` preserves the monotonic-AGC property with no
out-of-order reads or lost catch-up targets across multiple images. 6 lower-severity findings, all
addressed with documentation (not code changes, since none were actual bugs): an `int`-overflow
session-length limit (~13.5h @44100Hz, flagged explicitly rather than silently accepted, widening to
`long` deliberately out of scope for this fix); the AVT-warmup coupling above; two warm-up clamps
(`TryResolveSyncAnchorCorrection`/`TryResolveAvtTraining`) that assume `_bufferBase==0`, safe only via
the watermark's own `AnchorWarmupSamples` margin, now stated explicitly rather than left implicit; a
1-3 sample extended-VIS ceiling rounding mismatch, now a one-way gate instead of a harmless per-call
retry (practically unreachable, flagged not fixed); `FilteredRawSampleAt`'s 1-sample-deeper read
relying on tail margin rather than being directly covered by the `min()`.

**Tests**: `BufferedSampleCount_StaysBounded_ForLongNeverLockingStream` (30s of real noise, asserts
buffered count stays well below what unbounded growth would produce) and
`DecodedImage_StillDecodesCorrectly_WhenPrecededByLongSilence_ThatTriggeredTrimming` (real Martin M1
transmission after 20s of silence that's guaranteed to trigger multiple trims first, asserts correct
mode detection + full line count + structural correctness within the established fallback tolerance).
Full suite: 392/392, solution-wide build clean. Golden-vector tests re-run: 8/8 unaffected.

**Status: S2 (Band-1 item 2) DONE, not yet committed as of this entry.**

### Band-1 item 4 (S1) — lock-dependent bandpass filter switch (H2 search vs. H1 locked), PLANNED

This port has never once run legacy's real locked-state filter (`HBPF`/`H1`) — only the weaker
pre-lock/search one (`HBPFS`/`H2`, Piece B). Investigated by reading `sstv.cpp`/`fir.cpp` directly
rather than working from the earlier speculative scope notes; findings below correct/simplify those
earlier notes materially.

**No filter-state warm-up problem, contrary to the earlier speculation.** Legacy's real convolution
engine, `CFIR2::Do(d, hp)` (`fir.cpp:1131-1144`), maintains ONE shared delay line (`m_pZ`) and simply
chooses which coefficient table (`H1` vs `H2`) to dot-product against, per call —
`m_BPF.Do(d, m_Sync||m_SyncMode>=3 ? (m_fNarrow?HBPFN:HBPF) : HBPFS)` (`sstv.cpp:1826-1832`). It is
NOT two independent filter instances. This port can mirror that exactly: one shared delay line inside
`SearchBandpassFilter`, a second coefficient array for H1, and a `bool useLocked` parameter on
`ProcessSample` — no second warmed-up filter object needed.

**H1 params (Wide preset, `CalcBPF` case 1, `sstv.cpp:1522-1531`)**: passband 1100-2600Hz (`lfq=1100`
since `m_SyncRestart` is hardwired on, `sstv.cpp:1486`; `g_dblToneOffset` confirmed 0 absent CQ100),
attenuation 20 — same as H2, same tap-count formula (`24*SampFreq/11025.0`). Attenuation 20 reconfirms
the already-resolved S28 pre-check (Kaiser/Bessel branch needs att>=21, unreached here either way).

**Scoping decision — an early-switch window is NOT being ported, deliberately. Auditor plan-review
corrected the window size: it's not a flat 30ms.** Legacy's real switch condition is
`m_Sync || m_SyncMode>=3`, not just "locked". `SyncMode==3` is the VIS **stop-bit** confirmation window
(a fixed 30ms) for a normal (single-byte) VIS code — so for most modes legacy starts using H1 about
**one VIS bit-period (30ms) before** `m_Sync` itself goes to 1 / before `Start()` fires. But
`sstv.cpp:2066-2070` sets `m_SyncMode = 9` (not straight to 3) for the extended-VIS escape byte `0x23`,
and case 9 decodes 8 MORE 30ms bits (the real extended mode code) before falling through to
`m_SyncMode = 3` (`sstv.cpp:2077`) — and case 9 is `>= 3` too. So for every extended-VIS mode (the
MR/MP/ML families) legacy actually runs H1 for **~270ms** of *decision-critical bit-decode*, not a 30ms
tail. Also unlisted originally: `Stop()` sets `m_SyncMode = 512` (`sstv.cpp:1786`), so legacy keeps H1
through the entire 0.5s post-image dead window too (cases 512/513) — this port's `EndOfImage()` reverts
to H2 immediately; harmless since the port doesn't do detection during that analytically-skipped window
anyway, but noted for completeness.

This port's natural hook point is `Commit()` (the single choke point every match path — fixed-window
VIS, narrow, AVT-post-training, sync-bypass fallback — already funnels through), which switches exactly
AT the lock anchor, i.e. LATER than legacy for that window (30ms for normal VIS, ~270ms for extended).
Traced whether this actually matters before deciding to skip it: nothing in this port reads AGC/bandpass
content from that specific window with any precision-sensitive purpose — VIS-bit decode (data + parity)
finishes before the stop-bit position starts, and picture-line decode starts at Commit's anchor, not
before it. The one thing that DOES read across that boundary, `TryResolveSyncAnchorCorrection`'s
2000-sample pre-`origin` warm-up loop, explicitly discards its output (pure resonator/smoother settling,
never accumulated into the fold-bin search) — auditor also found `TryStartAvtTraining`'s own AVT-PLL
warm-up does the same backward read; neither accumulates into a real result. A differently-filtered tail
out of a 2000-sample discarded warm-up read is not expected to matter. Chose NOT to build a
retroactive-patch-and-re-run-demodulator mechanism to close a boundary condition nothing
correctness-critical actually consumes — documented here explicitly (not silently absorbed) per this
project's established pattern for similar small timing simplifications (extended-VIS 7-bit escape byte,
sint2/sint3 freeze gating, etc.). **If a future finding ever shows something DOES read that window with
real precision sensitivity, re-open this.**

**Also explicitly out of scope, both already tracked separately, not new gaps introduced by this
item**: narrow mode's `H3`/`HBPFN` (this port's narrow modes keep using H2/search always — part of the
already-tracked MN/MC narrow-mode gap family, Band 3); AVT's *during-training* H1 usage (legacy uses H1
for the entire AVT training sequence via the same `SyncMode>=3` condition staying true throughout,
`sstv.cpp:2139-2144` — a materially bigger gap than the 30ms window above, tied to the already-tracked,
separately-deferred AVT items). AVT's eventual real image-decode phase, once its own training-lock
Commit() fires, correctly gets H1 for free via the same design (no special-casing needed there). A third
divergence found and documented once 4b actually implemented the switch (round-4 auditor code-level
review nit): legacy's `Stop()` keeps `m_SyncMode` at 512 through the whole 0.5s post-image dead zone
(`sstv.cpp:1786`, cases 512-513, `sstv.cpp:2243-2252`), so real legacy stays on H1 there too — this port
drops to H2 the instant `EndOfImage` clears `_mode`, even though that dead zone's own samples still get
demodulated (`AdvanceAgcThroughDeadZone`) and feed AGC state into the next transmission's search. Small,
matches the shape of the other two, documented in `SearchBandpassFilter`'s own doc comment.

**Chunk-invariance reasoning — WRONG, auditor found a real blocker (round 1 plan-review verdict: NOT
READY).** The original claim was that `PushSamples`'s per-sample loop always bandpass-filters every new
raw sample BEFORE `TryProcessBuffer()`/`Commit()` can run for that same push, so filter assignment per
index is deterministic regardless of chunking. **True, but the conclusion drawn from it was backwards.**
`PushSamples` (`AnalogFmSstvDecoder.cs:390-411`) runs its ENTIRE per-sample loop — which forward-fills
`BandpassFilteredSampleAt` (and `_demodulatedFrequencies`) for *every* sample in the chunk — before
`TryProcessBuffer()` (where `Commit()` actually happens) runs even once. A `_mode`-evaluated-at-first-
computation gate reduces to "was `_mode` non-null at the START of the push containing this sample" —
with two consequences, neither acceptable:
- **Bulk push (whole file/WAV in one `PushSamples` call — almost certainly how the round-trip/e2e tests
  drive the decoder)**: `_mode` is null for the ENTIRE per-sample loop, since `Commit()` can't fire until
  AFTER that loop finishes. H1 is **never used at all** — a silent no-op the existing test suite would
  not catch (it would just look like "no regression").
- **Chunked push**: H1 only engages at the NEXT chunk boundary after the lock, not at the true anchor —
  decode output becomes a function of chunk size again. **This is exactly the Band-1 item 3 race class,
  reopened** — `BandpassFilteredSampleAt` never recomputes a cached index, so the wrong filter choice is
  permanently frozen in, not just delayed.

Root cause: this port's upfront-buffer architecture processes a whole pushed chunk in one bulk pass
before header-detection/`Commit()` ever runs for that chunk's own content — a chunk can be, and in the
bulk-push case IS, the entire remaining file. Gating logic inside `BandpassFilteredSampleAt`'s existing
fill loop cannot fix this: the *placement* of the evaluation (before Commit can possibly have fired) is
what's broken, not the predicate itself (`_mode is not null && _mode.NarrowModeCode is null` correctly
encodes legacy's `(m_Sync||m_SyncMode>=3) && !m_fNarrow`, collapsed to this port's lock model — that part
survives review unmodified).

Auditor's three options, verdict pending user/next-round decision, **none implemented yet**:
- **(a) Don't do item 4.** Keep H2 continuous everywhere (today's actual behavior), document the
  divergence precisely (now including the corrected ~270ms extended-VIS window, not 30ms). Zero
  implementation risk; Band-1 item 4 stays a known, formally-accepted gap rather than fixed.
- **(b) Make the demod feed lazy.** Restructure so `_demodulatedFrequencies` (and by extension whichever
  cursor ultimately drives `BandpassFilteredSampleAt`) only computes forward as far as an ACTUAL consumer
  needs, mirroring `AgcSampleAt`'s own established lazy-forward-fill pattern, instead of PushSamples
  eagerly draining the whole chunk up front. Caveat found while reasoning through this after the
  auditor's report (not yet auditor-reviewed): `AgcSampleAt` itself is legitimately eager for
  header-detection's own sake (sync-bypass/tone-race detectors must scan continuously to ever find a
  header), and `AgcSampleAt`'s own forward-fill loop is what ultimately drives
  `_bandpassFilteredProcessedUpTo` forward — so making ONLY `_demodulatedFrequencies` lazy may not be
  sufficient on its own; needs re-verification before treating (b) as viable as stated.
- **(c) Retroactive re-filter.** Keep the current eager architecture, but at `Commit()` time, retroactively
  recompute (using already-cached `FilteredRawSampleAt` inputs — a pure function given the coefficient
  table, no demodulator-state replay needed for the bandpass stage itself) and overwrite
  `_bandpassFilteredSamples` for whatever suffix `[lineStartSample, _bandpassFilteredProcessedUpTo)` was
  already filled with H2 by the time lock happened, THEN also re-run `_demodulator.ProcessSample` over
  that same range to regenerate `_demodulatedFrequencies` (since that's a stateful streaming transform,
  not a pure function of index). Bounded to "whatever's been buffered since lock," which for a bulk push
  could mean re-processing most of the file — not free, but scoped/local rather than a full streaming-
  contract redesign.

**Status: round-1 plan-review found a real blocker (not a nitpick) — paused for a decision on (a)/(b)/(c)
before continuing. Do not implement against the original plan text above; it would produce H1-never-
engages (bulk push) or chunk-dependent decode (chunked push).**

**Real measurement (spike, per the `/adhd` skill's top-scored idea) + round-2 auditor verdict:**

Added a temporary diagnostic (`AnalogFmSstvDecoder.DiagCommitFired`, fires the provisional lock anchor
immediately from `Commit()`) and a throwaway test
(`tests/ScanlineStudio.Core.Sstv.Tests/Diag_BandpassFilterSwitchSpike.cs`) measuring, for a real Martin M1
transmission pushed at several chunk sizes, the gap between the true lock anchor and how far
`BandpassFilteredSampleAt`'s eager cache had already raced ahead by the time that push returned:

| chunk size (samples) | lock anchor (sample @44100Hz) | gap (samples) | gap (ms) |
|---|---|---|---|
| 1 | 40131 | 0 | 0.0 |
| 500 | 40131 | 369 | 8.4 |
| 4096 | 40131 | 829 | 18.8 |
| bulk (whole file, one push) | 40131 | 5,077,525 | ~115,137 (entire rest of the transmission) |

Confirms the lock anchor itself is chunk-invariant (root cause is isolated to filter-selection caching,
not upstream) and that the gap scales linearly with chunk size — small/bounded for realistic streaming
chunk sizes, catastrophic (full no-op) for bulk push.

Fed these numbers plus a `/adhd` divergent-ideation pass (5 frames, scored/clustered, top 3 deepened —
full session not reproduced here) back to the same auditor thread for a second opinion. Verdict:

- **Q1 (does the measured magnitude change the call): no.** Bulk push is a first-class supported caller
  in this codebase, not a test artifact — `TryProcessBuffer`/`ApplyAfcCorrections`/`ApplySlantTracking`
  all have existing shipped fixes specifically for bulk-push correctness. A feature that's a total no-op
  under bulk push is untested-by-construction, worse than not shipping it. But the chunk-invariant anchor
  DOES de-risk a proper fix as a bounded change, not a pipeline redesign.
- **Q2 (is "decouple only the demod feed," found by the `/adhd` deepening pass, sufficient): partially,
  and one premise in it was wrong.** Confirmed a real pre-lock reader of `_demodulatedFrequencies`
  (`AnalogFmSstvDecoder.cs:790-801`, the narrow-vs-normal-VIS discriminator) — not fatal, since that
  window sits inside the VIS header where H2 is legacy-correct anyway. But `AgcSampleAt` is NOT safe to
  "leave untouched" as assumed — it's itself a driver of the same shared bandpass cache, and
  `TryInterleavedHeaderScan` can race it all the way to `TotalSamplesReceived` once
  `_fixedWindowExhausted` fires. The residual gap this leaves is bounded by detection latency, not chunk
  size (chunk-invariant) — unmeasured, flagged as needing verification before trusting the fix, but this
  is the property that actually matters, so the design is directionally sound with a corrected
  justification.
- **Q3 (buildable now within item 4's scope): no — split it.** The lazy-demod change is its own
  behavior-preserving refactor with a clean acceptance criterion (entire existing suite stays
  bit-identical) — `Rel()`'s shared-growth assumption between `_rawSamples`/`_demodulatedFrequencies`
  (`:81`), `TrimBuffers`' `_demodulatedFrequencies.RemoveRange` needing its own watermark (`:536`), and
  `ApplyAfcCorrections`' in-place mutation ordering (`:1996`) all need to survive it. Recommended split:
  **4a** = lazy demod feed (no behavior change, ship when suite is unchanged bit-for-bit; keep the spike
  test but turn it into a permanent regression asserting the anchor-to-cache-head gap is chunk-invariant,
  not just report the raw numbers). **4b** = the actual H1/H2 switch, which becomes the originally-small
  change once 4a lands and is genuinely chunk-invariant. If 4a isn't worth its cost right now, fall back
  to **option (a)**: document the gap (~30ms normal VIS / ~270ms extended VIS) and defer both, rather than
  ship the originally-planned `_mode`-gated version, which the auditor called explicitly indefensible —
  "it would read as done while being inert for the caller shape your own test suite uses."

Off-scope note from this round (not chased): `AverageFrequencyInWindow`'s doc comment (`:2092-2094`)
still says "PLL loop's transient response," stale since the Hilbert-demodulator switch (Piece 14).

**User's call: follow the auditor, split it. 4a DONE.**

`_demodulatedFrequencies` is now its own lazy forward-fill cache (`DemodulatedFrequencyAt`, mirroring
`AgcSampleAt`/`BandpassFilteredSampleAt`'s own established pattern), no longer filled eagerly inside
`PushSamples`' per-sample loop. All three real readers updated (`PixelSampleReader`'s lambda,
`ApplyAfcCorrections`, `AverageFrequencyInWindow`'s pre-lock discriminator).

**Real bug found during implementation (not caught by plan-review), same failure class as the original
S2 bug, different cursor.** First attempt included `_demodulatedFrequenciesProcessedUpTo` as a watermark
term in `TrimBuffers`, matching `_bandpassFilteredProcessedUpTo`'s own existing pattern (matching the
auditor's stated recommendation literally). Two things went wrong depending on how: included
unconditionally → permanently pinned near 0 for a long-idle never-locking stream (its only pre-lock
reader is gated behind `!_fixedWindowExhausted`, so it stops advancing the moment that flips) —
`BufferedSampleCount_StaysBounded_ForLongNeverLockingStream` caught this immediately. Included
conditionally (matching `_consumedSamples`' own `if (!_fixedWindowExhausted)` pattern) → let the
watermark advance PAST this cursor's actual fill position, which **crashed**:
`System.ArgumentException` from `List<T>.RemoveRange` — unlike `_consumedSamples` (a logical cursor,
safe to go stale), this cursor IS the list's own physical length, and `RemoveRange` can never remove
more elements than a list actually holds. Root-caused and fixed properly (not patched around): removed
`_demodulatedFrequenciesProcessedUpTo` from the pre-lock watermark computation entirely (kept it
unconditionally in the locked branch, where real per-line decode consumption already keeps it in step —
safe, matches `_bandpassFilteredProcessedUpTo`'s pattern there); added an explicit catch-up step right
before the `RemoveRange` block (`if (watermark > _demodulatedFrequenciesProcessedUpTo) {
DemodulatedFrequencyAt(watermark - 1); }`) that force-fills the small remaining gap before trimming —
mirrors the existing `AdvanceAgcThroughDeadZone` pattern for the same class of problem, a no-op in the
locked branch, and correct in the pre-lock branch since it only ever fills the SAME bounded range the
watermark computation already proved safe to trim for every other buffer.

**Verified**: full suite 393/393 (392 pre-existing baseline, unchanged bit-for-bit, plus one new
permanent test), golden-vector tests 12/12 unaffected. The throwaway spike (`Diag_BandpassFilterSwitchSpike.cs`)
was converted into a permanent regression test per the auditor's own recommendation
(`BandpassCacheChunkInvarianceTests.cs`) — its first version measured the WRONG quantity (raw pushed-
sample count, which is trivially the whole push size regardless of the fix) rather than the actual
bandpass-cache cursor position; fixed before converting to a permanent assertion. Real numbers, now
chunk-invariant by construction: lock anchor sample 40131 and bandpass-cache cursor 37264 (gap **-2867
samples**, i.e. the cache trails slightly BEHIND the lock point, not ahead) — identical across chunk
sizes {1, 500, 4096, bulk-whole-file}, replacing the pre-fix bulk-push gap of ~5.08 million samples.
The two permanent diagnostics (`AnalogFmSstvDecoder.LockAnchorCommitted`, `.BandpassFilteredProcessedUpTo`)
were kept (not reverted) as the test's own infrastructure, per the auditor's recommendation.

**Final code-level auditor review: EQUIVALENT-WITH-RISKS, no bug found, cleared to start 4b.** Verified
the catch-up-before-trim design directly against source: correct, and load-bearing on an invariant not
originally stated — `watermark <= _bandpassFilteredProcessedUpTo` in BOTH branches (each already
includes it in their own `Min()` chain), which is what stops `DemodulatedFrequencyAt(watermark - 1)`
inside the catch-up from ever re-advancing the bandpass cache itself (its inner
`BandpassFilteredSampleAt` calls become pure cache reads, not new fills) — i.e. what stops the catch-up
from silently reintroducing 4a's own bug from inside `TrimBuffers`. Documented that invariant explicitly
at the catch-up site per the auditor's flag, with an explicit warning not to drop
`_bandpassFilteredProcessedUpTo` from either watermark chain in 4b. Also confirmed: `EndOfImage` does
NOT reset `_demodulatedFrequenciesProcessedUpTo` (correctly — resetting it would desync it from the
list's own physical length); `Rel()` bounds and the length invariant both hold at the degenerate
all-removed edge; existing tests (`EndOfImageResetTests`, `BufferTrimTests`' silence-then-real-transmission
case) already exercise the exact multi-image/repeated-trim scenarios that would have caught a real bug
here, so "bit-identical baseline + the new chunk-invariance test" was judged genuine coverage, not luck.

**One design correction for 4b, the single most valuable thing this measurement bought**: the real
measured gap is NEGATIVE (cache cursor 37264 trails lock anchor 40131 by 2867 samples, ~65ms@44100Hz) —
meaning under the ORIGINAL point-6 design (gate H1/H2 selection on live `_mode` at first-computation
time), those 2867 PRE-anchor samples would get computed AFTER `Commit()` already fired and would be
wrongly assigned H1, when legacy actually used H2 for nearly all of that span (only the final ~30ms
stop-bit window is `m_SyncMode>=3`, already decided out of scope). Fix, same cost: gate 4b on a captured
`index >= lockAnchorSample` field (set in `Commit()`, cleared in `EndOfImage()`), not on live `_mode` —
exactly correct and still chunk-invariant.

Minor nits, both addressed or noted: laziness-vs-ordering distinction now documented at the catch-up
site (4a's real win for a never-locking stream is ordering relative to `Commit()`, not CPU savings — the
demodulator still eventually runs over ~all pre-lock audio either way); `FilteredRawSampleAt`'s
`index > 0` (absolute) vs. `index > _bufferBase` guard is pre-existing (S2, not 4a), currently
unreachable given today's margins, flagged only because 4b will touch these same accessors — watch for
it, not a blocker. Off-scope, not chased: `AverageFrequencyInWindow`'s doc comment still says "PLL
loop's transient response," stale since the Hilbert-demodulator switch (flagged twice now).

**Status: 4a done and committed. Starting 4b (the actual H1/H2 filter switch) — corrected design: gate
on a captured lock-anchor sample index, not live `_mode`.**

### Band-1 item 4b — the actual H1/H2 filter switch, DONE

`SearchBandpassFilter` now carries both coefficient tables (`_h1` 1100-2600Hz, `_h2` 400-2500Hz, both via
the existing `MakeFilter` helper, same tap count) over ONE shared delay line — `ProcessSample(double
input, bool useLocked)` shifts the line unconditionally, dot-products against whichever table
`useLocked` selects, mirroring `CFIR2::Do(d, hp)`'s own single-delay-line-plus-coefficient-choice shape
exactly. No separate H1 warm-up needed, verified by a new unit test
(`ProcessSample_SwitchingToLocked_ReusesExistingDelayLineHistory_NoSeparateWarmUp`) that feeds several
H2-selected samples then switches to H1 for one sample and checks the result against H1's coefficients
convolved against that SAME accumulated history, computed independently by hand from the raw inputs.

`AnalogFmSstvDecoder` gates `BandpassFilteredSampleAt`'s selection on a NEW field,
`_bandpassLockedFromSample` (captured in `Commit()`, reset to `int.MaxValue` in `EndOfImage()`) — NOT
live `_mode` state, per the corrected design from item 4a's own auditor review: `useLocked = _mode is
not null && _mode.NarrowModeCode is null && index >= _bandpassLockedFromSample`, evaluated once, at each
index's own first-computation time. Narrow mode's H3/HBPFN stays out of scope (already-tracked MN/MC
gap family) via the `NarrowModeCode is null` check; the legacy ~30ms(normal)/~270ms(extended) early-
switch window stays out of scope too (already documented in item 4's own entry above).

**Tests**: `SearchBandpassFilterTests.cs` — added an independently-computed (Python, not derived from
the C# implementation, same discipline as the existing H2 fixtures) H1 coefficient fixture (full array
at tap=24@11025Hz, selected values at tap=96@44100Hz), an H1 frequency-response sweep (independently
computed magnitudes at H1's own passband edges 1100/2600Hz plus the same real VIS-bit/sync/leader tones
1200/1900Hz), parametrized the existing causal-impulse-response and zero-padding tests over both H1/H2,
and the shared-delay-line/no-warm-up proof described above. 21 new tests, 42/42 in this file.

**Verified**: full suite 414/414 (413 baseline + 21 new filter tests unchanged, no regressions anywhere),
golden-vector tests unaffected, noise-robustness tests unaffected (both within existing established
tolerance), and — the property this whole item existed to fix — the existing
`DecodedImage_IsPixelIdentical_WhetherSamplesArriveInOneChunkOrMany` round-trip test (chunk sizes
{1, 500, 4096}) still passes at EXACT pixel identity with H1/H2 switching now live, confirming item 4b
did not reopen the Band-1 item 3 chunk-timing race.

**Final code-level auditor review (round 4): EQUIVALENT, ready to commit.** Verified the gating
condition against `sstv.cpp:1826-1832` directly (correctly collapses `(m_Sync||m_SyncMode>=3) &&
!m_fNarrow`), confirmed no sub-`_bufferBase` evaluation risk, confirmed `Commit()`-within-the-same-push
ordering is handled (the round-3 correction's whole point), confirmed mid-reception restart
(`TryVisLockStateMachine`) and AVT post-training lock both correctly get H1, confirmed `EndOfImage`'s
`_bandpassLockedFromSample` reset is genuinely redundant-but-correct (the `_mode is not null` conjunct
already closes that gate) rather than overclaimed, and confirmed nothing from item 4a's own review
reopened (`_bandpassFilteredProcessedUpTo` still in both `TrimBuffers` watermark chains, the catch-up
still can't advance the bandpass cursor). One recommended (non-blocking) gap: the round-3 correction's
own most subtle property — samples strictly before the anchor computed after `Commit()` fires must stay
H2 — was protected only by a doc comment, not a test. Closed before committing: added
`AnalogFmSstvDecoder.FirstLockedBandpassIndex` (diagnostic-only, mirrors `LockAnchorCommitted`'s own
pattern) and `BandpassCacheChunkInvarianceTests.FirstLockedFilterSample_EqualsTheLockAnchor`, which pins
the two equal directly — would fail with a clear, specific mismatch if this ever regressed back to
gating on live `_mode` alone. Two nits also addressed: the third legacy divergence (0.5s post-image dead
zone, `sstv.cpp:1786`/`2243-2252` — legacy stays on H1 there, this port drops to H2 at `EndOfImage`) now
explicitly documented in `SearchBandpassFilter`'s own doc comment and here (see "Also explicitly out of
scope" above); the `BandpassFilteredSampleAt` comment/code phrasing mismatch (loop's "this index" vs the
method's own `index` parameter) fixed with a named local.

**Verified (final)**: full suite 415/415 (414 baseline + 1 new gate-pinning test, no regressions
anywhere), golden-vector and noise-robustness tests unaffected, chunk-invariance confirmed both
end-to-end (`DecodedImage_IsPixelIdentical_WhetherSamplesArriveInOneChunkOrMany`) and directly at the
gate itself (the new pinning test).

**Status: Band-1 item 4 (S1) DONE, committed. With it, all 4 Band-1 (must-fix-before-Phase-2) items are
complete: S4 (`288d5d0`), S2 (`86e3af6`), S3 (`365d57b`/`765ba3c`), S1 4a+4b (this entry). Next:
task #7 (capture new golden-vector fixtures) → task #8 (Phase 3 chain/integration audit).**

## Band 2 — scoping, "Pattern 1" recommendation revisited and withdrawn

Before starting Band 2 (S5, S16, S15, S14, S6 — "should fix during Phase 2 bring-up"), asked the
auditor whether its own original Pattern-1 recommendation ("decide the streaming contract explicitly
before writing individual fixes") still holds now that Band 1 has real outcomes to check it against.

**Verdict: withdrawn. Start S5 directly, no design pass.** The recommendation was written before this
port had a proven streaming pattern; it now has both halves it asked for, already in-tree and
review-hardened: persistent-detector-plus-monotonic-cursor (`_syncBypass1200Detector`/`_visLockStateMachine`
as instance fields, driven by `_syncBypassProcessedUpTo`/`_visLockProcessedUpTo`) and lazy forward-fill
sample caching with a bounded ring buffer (`AgcSampleAt`/`BandpassFilteredSampleAt`/`DemodulatedFrequencyAt`
+ `TrimBuffers`). A design pass now would document code that already exists, not decide anything new.
Empirical case: all 3 Pattern-1 Band-1 items (S1, S2, S3) landed as scoped incremental fixes and are
stable — S1, the worst-shaped one, cost one session and added permanent regression infrastructure, not
throwaway patches. Honest counterweight kept: a unified pass would catch cross-item invariants (like
4a/4b's `watermark <= _bandpassFilteredProcessedUpTo` coupling) by construction instead of via review —
real, but small, and already mitigated by documenting invariants at the site plus a per-item review gate
that's caught one genuine issue each of the last three rounds.

**Important correction to the original audit's own framing**: S5 is NOT the same shape as 4a. 4a was an
*eagerness/ordering* defect (chunk-dependence). S5 is a *cold-start fidelity* defect — `TryDecodeVisDataBits`
already builds fresh detectors as a "pure function of (headerStart, buffered data)" (already chunk-
invariant, confirmed in-code), just missing legacy's continuously-running filter history
(`m_iir11/12/13/19`+`m_lpf*`). Different failure mode, same destination pattern (the persistent-instance
shape at `AnalogFmSstvDecoder.cs:327-333`, not 4a's lazy-cache shape).

**Shape classification for all 5 Band-2 items** (which existing pattern each should reuse):

| Item | Shape | Reuses |
|---|---|---|
| S5 | Cold-start detector rebuild (`TryDecodeVisDataBits`, `:1754-1757`) vs legacy's continuously-running detectors | The persistent-instance pattern (`:327-333`), NOT 4a's |
| S16 | Same as S5 — fresh `PllFmDemodulator` per call (`:1960`) + a clamped 2000-sample warm-up hack | Same conversion as S5 |
| S14 | Half S5 (fresh mark/space detectors, `:1626-1627`), half anchor-precision — split it | Detector half rides with S5 |
| S6 | 4b's shape, not 4a's — a lock-dependent parameter switch on a continuously-running filter | `_bandpassLockedFromSample` directly |
| S15 | ~~Materially different... a semantics change~~ **Correction (see S15's own closing section below): "nothing existing to reuse" was right, but "genuinely new design work" was wrong — it's *previously-attempted-and-reverted* design work (the S2-era re-anchoring draft), a different and more useful status. Closed via documentation, no code needed.** | Nothing existing (the obvious implementation was already tried and reverted during Band-1 S2) |

**Two traps flagged for when Band 2 actually starts** (not yet acted on):
- **S6**: `HilbertFmDemodulator.SetWidth` changes tap count AND phase-diff lag (`HilbertFmDemodulator.cs:46`)
  — unlike H1/H2's constant-tap coefficient swap, legacy explicitly compensates a tap change
  (`SetBPF`'s `m_Skip = (newtap-oldtap)/2`, `sstv.cpp:1602-1613`). 4b's "one shared delay line, no
  warm-up, no delay compensation needed" finding does NOT transfer to S6 — read `CSSTVDEM::SetWidth`
  (`sstv.cpp:1707-1715`) and `Start()` fresh before reusing any 4b reasoning. Exactly the "don't infer
  from a similarly-shaped case" trap CLAUDE.md §3 warns about generally.
- **S15 is coupled to Band 1 in a way the original audit predates**: `_fixedWindowExhausted` is
  load-bearing for `TrimBuffers`' pre-lock watermark (`:523-526` — what makes trimming past a stale
  `_consumedSamples` safe). Changing when the search window closes changes trimming safety. Do S15
  LAST, and re-verify the pre-lock watermark as part of it, not as an afterthought.

**Recommended order: S5 → S16 → S14 → S6 → S15.** S5/S16 are the same mechanical conversion (do them
back to back while the pattern's loaded); S14's detector half rides along; S6 needs its own fresh
`SetWidth` legacy read before anything is written; S15 goes last since it perturbs Band-1's own trimming
invariant.

**Status: Band 2 scoped, no design pass needed. Starting S5.**

### Band-2 item S5 — persistent VIS-bit-decode detectors, implemented

`TryDecodeVisDataBits` used to construct 4 fresh `SyncEnvelopeDetector`s (d11/d12/d13/d19) on every
call, cold-started at `headerStart` on every retry. Verified against legacy directly (auditor
plan-review): `CIIRTANK` (`m_iir11/12/19`) has no `Clear()` method at all and `SetFreq` only ever
writes coefficients, never the resonator's internal state (`z1`/`z2`) — these detectors run with
continuous, never-reset state for the CSSTVDEM object's whole life, even across AFC-triggered retunes.
`m_iir12`/`m_iir19` (d12/d19) are fed unconditionally every sample; `m_iir11` (d11) effectively every
sample too (`m_SyncRestart` hardwired on).

**Real design correction found by plan-review before any code was written**: `m_iir13` (d13) is
NOT the same shape — legacy only feeds it during case 2/9 (`sstv.cpp:1976`), so its value at a given
sample depends on trigger history, not just that sample's own index. An index-keyed forward-fill cache
is structurally wrong for it. **d13 stays method-local, exactly as before** — its cold start costs
nothing measurable anyway (first read is 30ms after it starts being fed; an 80Hz-bandwidth resonator
settles in ~4ms). d11/d12/d19 converted to persistent instance fields behind lazy forward-fill caches
(`D11At`/`D12At`/`D19At`), mirroring `AgcSampleAt`'s own established pattern exactly.

Deliberately NOT unified with the existing `_syncBypass1200Detector`/`_syncBypass1900Detector` (which
already faithfully port legacy's literal SHARING of one d12/d19 pair for the continuous
`TryInterleavedHeaderScan` fallback path) — `_syncBypassProcessedUpTo` being caught up to whatever
`TryDecodeVisDataBits` needs at call time is unverified (the fixed-window path is tried first). Not a
permanent scope cut: converting d12/d19 to index-keyed caches here is exactly the prerequisite that
makes that future unification mechanical instead of a redesign, when/if it's ever done (same deferred-
unification family as `_syncBypass1PrimaryHeld`'s own doc comment already describes).

**`TrimBuffers` trap, correctly anticipated this time (not rediscovered the hard way like item 4a)**:
these 3 new cursors are read ONLY pre-lock, by `TryDecodeVisDataBits` alone — so they're excluded from
BOTH branches' watermark `Min()` chain (not just pre-lock like `_demodulatedFrequenciesProcessedUpTo`,
since post-lock they'd otherwise freeze the watermark at whatever value they held at the moment of
lock, blocking trimming for the entire image). Safety guaranteed purely by an extended catch-up-before-
trim step, same load-bearing invariant as item 4a's own catch-up (`watermark <= _levelAgcProcessedUpTo`
in both branches, so the catch-up's `AgcSampleAt` calls are always pure cache reads).

**Real test gap closed before committing (auditor plan-review flagged it)**: `BufferedSampleCount` only
tracks `_rawSamples`, so these 3 new `List<double>`s silently failing to trim (same bug class Band-1
item 2/4a each hit once already, different cursors) would have passed
`BufferedSampleCount_StaysBounded_ForLongNeverLockingStream` without any warning. Added
`VisDataDetectorBufferedSampleCount` (combined physical length of the 3 new caches) and asserted it in
both existing `BufferTrimTests` tests, not a new test file.

**Verified**: full suite 415/415, no regressions, no new test count (extended existing assertions,
didn't add new test methods). Golden-vector and noise-robustness tests unaffected.

**Final code-level auditor review: EQUIVALENT, ready to commit.** Confirmed d13 genuinely untouched
(same construction/feed/read-timing as before); confirmed the exclude-from-both-branches `TrimBuffers`
reasoning is correct (traced the actual call chain: `TryDecodeVisDataBits` is only ever reachable while
`_mode is null`); confirmed the catch-up safety invariant holds with 3 more consumers (all pure reads
off `AgcSampleAt`, fully order-independent, and trim-timing itself provably can't affect decode output
since the caches are index-keyed with a strictly monotonic feed); confirmed nothing from 4a/4b reopened.
One stale comment found and fixed (the `sample++`-after-reject comment cited "these are stateful
streaming filters, not a cache" as its reason — no longer true for d11/d12/d19, restated against the
real legacy citation, `sstv.cpp:1983`, instead). One useful non-blocking note: the locked-branch
catch-up now runs all three detectors over every sample of every image (previously idle there) — not a
bug, actually a fidelity GAIN (legacy's real `m_iir11/12/19` run every sample too, so the next
transmission's header decode now sees real carried-over state exactly like legacy, not just a
memory-bounded cache) — worth knowing so the added per-sample cost isn't a surprise later. One
recommended (non-blocking) test gap: nothing directly proves the persistence behavior itself (a
regression back to cold-start-per-call would still pass all 415 tests) — deferred, since the 3 new
fields are `private readonly` (structurally can't be silently reassigned to a fresh instance without an
obviously-visible code change), and the auditor itself called this "recommended, not a commit blocker."
If picked up later, pairs naturally with S16 (same detector-persistence shape).

**Status: S5 DONE, committed.**

### Band-2 item S16 — AVT PLL warm-up, corrected scope and implemented

**Plan-review round 1 caught its own earlier misclassification before any code was written**: S16 is
NOT the same shape as S5. Legacy's real `m_pll` (AVT's dedicated PLL) is fed only during `SyncMode`
cases 3-7 (`sstv.cpp:2129/2159/2169/2187/2222`) — intermittent, not a pure function of absolute sample
index, the SAME shape as d13's own case-2/9-only feed that already ruled out an index-keyed cache for
IT in S5. A PLL also has no equivalent of a resonator's fast, data-independent re-settling (its phase
state carries indefinitely) — making "just leave it running forever" (the original plan) an actual
regression, not a neutral simplification: it would make AVT training entry a function of the ENTIRE
preceding stream, which legacy's real per-attempt `m_pll` usage never is.

**The real defect, and the actual fix**: `_avtPllDemodulator` stays fresh-per-training-attempt exactly
as before (no persistence, no new cache, no new cursor, no `TrimBuffers` changes) — but its old
clamped-2000-sample warm-up was far too short relative to legacy's real contiguous feed window. This
port's own `_avtTrainingOriginSample` deliberately skips past all 3 VIS repeats before ever
constructing a training-lock instance at all — but legacy spends that entire skipped span (cases 4-7,
~1820ms) continuously feeding `m_pll` real failed-marker-search audio, plus case 3's own 30ms VIS-
stop-bit window right before that (`sstv.cpp:2127-2129`, verified directly, not assumed — the
plan-review flagged this specific 30ms as a judgment call worth checking rather than guessing).
Widened the warm-up from the arbitrary `AnchorWarmupSamples` (2000) constant to a legacy-derived start
point (`_avtPllWarmupStartSample = headerStart + totalHeaderSampleCount - one VIS bit period`), computed
once in `TryStartAvtTraining` and consumed by `TryResolveAvtTraining`'s existing warm-up loop (now
looping from that point instead of a clamped window). Updated the `TrimBuffers` comment that used to
cite the old `AnchorWarmupSamples`-based coverage to cite the new, correct span instead.

**Verified**: full suite 415/415, no regressions (including AVT's own round-trip tests).

**Final code-level auditor review: EQUIVALENT, ready to commit.** Confirmed the case-3 boundary
(`sstv.cpp:2127-2130`: `if(!m_Sync){ m_pll.Do(ad); }`, unconditional through the whole 30ms countdown)
and the 30ms figure itself (`m_SyncTime = 30*SampFreq/1000`, `sstv.cpp:1986`) match exactly. Flagged one
derivation detail to settle, not guess: whether `totalHeaderSampleCount` includes the VIS stop bit
(if not, the warm-up start would be 30ms early). **Settled directly from `VisHeader.cs`**:
`NormalTailDurationMs = BitDurationMs (parity) + BitDurationMs (stop)`, and `totalHeaderSampleCount` is
computed from `PrefixDurationMs + NormalTailDurationMs` — the stop bit IS included, so the derivation is
exactly right, not off by one bit period. Confirmed numerical safety at the new, much longer warm-up
length (~80k samples through `PllFmDemodulator` before first real read): AGC resets every zero-crossing
(no accumulation), loop drive is hard-clamped, VCO phase accumulates but with `double`-precision error
around 1e-12 rad even at this length — no new failure mode versus the old 2000-sample window, just
smaller in degree. Confirmed the `TrimBuffers` transitive-safety margin actually GREW (from ~45ms to
~580ms) rather than shrank.

**Systemic finding, addressed before committing (not per-item deferred debt)**: the auditor noted this
is the THIRD consecutive item (4b, S5, S16) whose real behavior change was structurally invisible to
the existing suite — decode outcomes stayed correctly identical in all three, but nothing pinned the
underlying MECHANISM, so each would have silently passed if reverted to its old (wrong) behavior. 4b's
own gate is already covered (`BandpassCacheChunkInvarianceTests.FirstLockedFilterSample_EqualsTheLockAnchor`,
added during that item's own final review — the auditor's context was stale on this one, flagged as
still-missing when it wasn't). Closed the other two in one sitting rather than deferring further:
new `tests/ScanlineStudio.Core.Sstv.Tests/LegacyDerivedSpansTests.cs` — `AvtPllWarmupSpan_MatchesLegacyDerivedDuration`
(pins S16's warm-up span as a pure, headerStart-independent constant derived the same way the source
computes it) and `VisDataD11Cursor_NeverResets_AcrossBackToBackTransmissions` (pins S5's persistence
across an image boundary, the property a revert-to-cold-start would actually violate). Two small new
diagnostics added to support them (`AvtPllWarmupStartSample`/`AvtTrainingOriginSample`,
`VisDataD11ProcessedUpTo`), same pattern as every other diagnostic already in this file.

**Verified (final)**: full suite 417/417 (415 baseline + 2 new tests), no regressions.

**Status: S16 DONE, committed.**

### Band-2 item S14 — DONE (commit `7a153a0`)

Implemented per the verified plan below, no changes to the plan itself needed. `FskSpaceAt` added
mirroring `D11At`/`D12At`/`D19At` exactly; `D19At` reuse for mark confirmed correct by two rounds of
auditor code-level review this session (one against current source, one against the actual diff — both
EQUIVALENT). Key legacy confirmation from the code-level review: `InitTone` (`sstv.cpp:1695-1705`)
retunes `m_iir19` and `m_iirfsk` together, inside the same `if( m_AFCFQ != dfq )` block with the same
`dfq` — mark and space always move together in legacy, so sharing `D19At` with the VIS path introduces
no relative divergence for narrow mode specifically (neither retune is modeled by this port at all yet —
pre-existing, S6-adjacent, unchanged by this item).

Non-blocking gap the first review round found (closed before commit, not deferred): the space-cursor
persistence test alone didn't pin the *D19-reuse decision* itself — reverting mark to its own fresh
detector would still pass it. Added `VisDataD19ProcessedUpTo` diagnostic + a narrow-mode-only decode test
(`NarrowHeaderDecode_AdvancesTheSharedD19Cursor`) asserting it advances past 0 — MN/MC never runs
`TryDecodeVisDataBits` at all, so this can only be explained by the narrow-header path itself reading
`D19At`. 419/419 passing (417 before this item, +2 new tests:
`FskSpaceCursor_NeverResets_AcrossBackToBackNarrowTransmissions` and the D19-reuse test above).

<details>
<summary>Original plan-review writeup (pre-implementation)</summary>

#### Band-2 item S14 — plan verified, NOT YET IMPLEMENTED (session paused at 92% budget)

`TryDecodeNarrowModeHeader`'s own `markDetector`/`spaceDetector` (1900Hz/2100Hz `SyncEnvelopeDetector`s,
~line 1774) are constructed fresh every call — same cold-start shape as S5's d11/d12/d19. Legacy's real
equivalents: `m` is literally `d19` (`m_iir19`+`m_lpf19`, unconditional every sample, `sstv.cpp:1851-1853`
— the SAME detector S5 already made persistent as `D19At`), `s` is `dsp` from `m_iirfsk`+`m_lpffsk`
(2100Hz `FSKSPACE`, also unconditional every sample, `sstv.cpp:1855-1857`, not yet covered anywhere).

**Plan-review verdict, confirmed against source, ready to implement:**
1. **Mark detector: REUSE `D19At`, don't add a third 1900Hz instance.** Initial instinct was to keep
   narrow-mode's own separate persistent 1900Hz detector (matching S5's own precedent of NOT reusing
   `_syncBypass1200Detector`/`_syncBypass1900Detector`) — auditor correction: that precedent doesn't
   apply here. S5's non-unification was specifically about `_syncBypass1900Detector`, which is driven by
   a *separate scan cursor* whose caught-up-ness at call time is unverifiable. `D19At` is a plain
   index-keyed cache (ask for index i, it forward-fills and returns) — no cursor-lag question exists.
   Legacy's mark literally *is* d19, same object, same value — reusing `D19At` is MORE faithful, cheaper,
   and stops 3x duplication (`_syncBypass1900Detector`, `_visDataD19Detector`, a hypothetical third)
   becoming 4x. **Required follow-through**: `D19At`'s existing `TrimBuffers` exclusion comment
   (currently justified by "read ONLY by `TryDecodeVisDataBits`") becomes false once
   `TryDecodeNarrowModeHeader` also reads it — update to name both readers (exclusion logic itself stays
   correct, both are pre-lock-only readers).
2. **Space detector: genuinely new.** New persistent field + `FskSpaceAt(int index)` lazy forward-fill
   cache + cursor, exactly mirroring `D11At`/`D12At`/`D19At`'s own shape — plus its own `TrimBuffers`
   catch-up-before-trim call, `RemoveRange`, and a diagnostic-count entry (matching S5's own
   `VisDataDetectorBufferedSampleCount` pattern — fold the new list into that combined count, or add a
   sibling; decide at implementation time).
3. **`NarrowFskHeaderDecoder`'s own bit-accumulation state machine stays fresh-per-call, NOT converted.**
   Real finding: legacy's `DecodeFSK`/`m_fskmode` (`sstv.cpp:2378-2606`) is ALSO called unconditionally
   every sample with no `headerStart` concept at all — a genuinely bigger architectural gap than d13/S16
   ever were. Confirmed correctly OUT of scope for S14 specifically: this port's bounded-fixed-window-
   plus-`_syncBypassNarrowTracker`-fallback architecture is the same, already-accepted shape as the VIS
   path's own `TryDecodeVisHeader`/`TryInterleavedHeaderScan` split — `NarrowFskHeaderDecoder`'s bit
   accumulation is the direct analogue of `TryDecodeVisDataBits`'s own trigger-search logic, which S5
   correctly left fresh-per-call too. Safe specifically BECAUSE once both tone detectors are index-keyed
   caches, the state machine becomes a pure function of `(headerStart, cached values)` — same reasoning
   that already applies to S5's own untouched search loop.
4. **Anchor-precision half: ALREADY CLOSED, no work needed.** The original S14 audit row's own concern
   ("supporting evidence is [only] a round-trip test... drift/jitter unmeasured") is stale — an existing
   doc comment on this method (predating this session's Band-2 audit) already independently verified the
   fixed-nominal-duration commit point against `Main.cpp:7422-7424`: TX writes the mode byte then the
   checksum byte, and the guard-tone write immediately after is commented out in legacy's own TX code —
   image data follows at a fixed offset determined entirely by TX, not by wherever RX's state machine
   happens to lock. `headerStart + VisHeader.NarrowHeaderTotalDurationMs` is confirmed correct, TX-source-
   verified rather than round-trip-self-consistent. Nothing to change here.

**Sizing (auditor's own assessment): small — smaller than S5.** One new detector+cache+cursor (space),
one catch-up, one `RemoveRange`, one diagnostic extension, two comment updates (the `TrimBuffers`
exclusion citation above, plus swapping two local constructions for cache reads in
`TryDecodeNarrowModeHeader` itself). Comfortably a single session once resumed.

</details>

**Status: DONE (commit `7a153a0`).** Implemented per the plan above, no changes needed. 419/419 tests
passing before this item (417 base + 2 new S14 tests).

### Band-2 item S6 — DONE (commit `6da0a65`)

`HilbertFmDemodulator.ProcessSample` gained an `isNarrow` parameter selecting between two precomputed
`(off, out)` tuning pairs — normal (1900Hz/800Hz) and narrow (`NARROW_CENTER`=2172Hz, `NARROW_BW`=256Hz,
`sstv.h:441-444`) — mirroring item 4b's per-call `useLocked` selection shape rather than a stateful
mutator. `AnalogFmSstvDecoder.DemodulatedFrequencyAt` reuses item 4b's own `_bandpassLockedFromSample`
anchor to gate the selection (`_mode.NarrowModeCode is not null && index >= _bandpassLockedFromSample`)
— both fire off the same `Commit()` event, and an auditor plan-review round confirmed the same
negative-gap property item 4a discovered for the bandpass cache recurs here (this cursor also trails
the anchor at `Commit()` time, for a different reason — its only pre-lock driver,
`AverageFrequencyInWindow`, ends at `headerStart+380ms`, well before a narrow anchor at
`headerStart+NarrowHeaderTotalDurationMs` — but the same direction, so the same `index >= anchor` form
absorbs it).

**Roadmap correction, found via a fresh legacy re-read before writing any code**: the original S6 trap
note (below, in the collapsed plan-review section) claimed `CHILL::SetWidth` changes tap count and
cited `CSSTVDEM::SetBPF`'s `m_Skip = (newtap-oldtap)/2` compensation (`sstv.cpp:1602-1613`) as evidence
this couldn't reuse 4b's "no warm-up needed" finding. Verified wrong: `SetBPF` is a different, unrelated
feature entirely — the user-configurable Wide/Narrow/VeryNarrow bandpass QUALITY setting (`m_bpf`
1/2/3), operating on `m_BPF` (`SearchBandpassFilter`, item 4b's own class), not on `m_hill`/CHILL at
all. `CHILL::SetWidth` itself (`sstv.cpp:3022-3051`) only changes two scalars (`m_OFF`/`m_OUT`); tap
count and `m_df` tier on sample rate ONLY, never on `fNarrow`. 4b's finding — a live scalar/coefficient
switch on continuously-running state needs no warm-up — DOES transfer here, confirmed by an auditor
plan-review round working from source independently. Also ruled out during the same fresh read: `m_fqc`
(this port's `ZeroCrossingFrequencyCounter`, which already has a working but never-called `SetWidth`) is
dead code in the live path — AFC now reads directly from `HilbertFmDemodulator`'s own output, post the
Hilbert-demod architecture switch — so no separate CFQC wiring was needed; `HBPFN` (locked-narrow
bandpass) is a separate, already-logged, deliberately-out-of-scope gap (`SearchBandpassFilter.cs`'s own
doc comment), not S6's concern.

**The real finding, confirmed algebraically by two independent derivations (this session and an
auditor code-level review) and worth recording plainly**: at steady state, the `off`/`out` encode and
`ProcessSample`'s final `centerHz - scaled*bandwidthHz/32768` descale are exact algebraic inverses for
ANY consistent `(centerHz, bandwidthHz)` pair — both cancel completely, dynamically too (the smoothing
filter is linear). So `isNarrow` has **no effect on the settled Hz readout** in this port's
representation — unlike legacy, where `m_OFF`/`m_OUT` genuinely matter, because `CHILL::Do` returns the
raw SCALED value directly (`sstv.cpp:3086`) and never converts to Hz at all; this port's Hz conversion
is its own representational choice, and that choice is exactly what makes the selection cancel here.
Sanity-checked against the ALREADY-EXISTING pre-fix test at 2300Hz, which was passing before any S6
code existed. The ONLY observable effect of this item is a brief, bounded output-IIR transient right at
a mid-stream width switch (the smoothing filter's stored state is in the OLD scale for one switch) —
faithfully reproducing a transient legacy has too and does nothing to compensate (no `Clear()`/reset
anywhere in `SetWidth` or its callers). **This is a legacy-fidelity port, not a decode-accuracy fix** —
the original inventory row's "unmeasured, now the live picture-demod path" framing turned out to be a
non-issue for this port's own representation, valuable to know rather than to have assumed.

An earlier draft's regression test (`isNarrow=true` reads back 2044/2172/2300Hz correctly) was
tautological — it would have passed with `isNarrow` silently ignored, for the exact reason above.
Replaced per the auditor's own suggestion with two tests that pin real, executable facts:
`ProcessSample_IsNarrowSelection_IsRepresentationallyInert_AtSteadyState` (wide vs. narrow settle to
the identical value) and `ProcessSample_IsNarrowFlipMidStream_CausesBoundedTransient_ThenResettlesToSameValue`
(a real deviation happens right at the flip, bounded/fast, resettles to the same value). 423/423 tests
passing (419 before this item).

### Band-2 item S15 — CLOSED via documentation, no code needed. Band 2 fully done.

Before drafting a plan, re-read `AnalogFmSstvDecoder.cs`'s own doc comments from Band-1 item 2 (S2,
pre-Band-2) and found this item's core concern had already been investigated once: `TryDecodeHeader`'s
own doc comment describes a "more ambitious earlier draft" that tried periodically re-anchoring
`_consumedSamples` forward to give the fixed-window paths (`TryDecodeVisHeader`/`TryDecodeNarrowModeHeader`)
another shot after each trim, instead of the current one-shot `_fixedWindowExhausted` gate — reverted,
confirmed empirically that it "produces the identical practical outcome" either way, since any header
found after exhaustion is found via `TryInterleavedHeaderScan`'s fallback (which is ALREADY continuous,
never one-shot) at that path's own already-accepted anchor precision. Sent this finding to an auditor
plan-review to settle whether that narrower (trimming-safety-only) result actually closes S15's broader
concern (legacy's real per-sample-forever `m_SyncMode` search vs. this port's one-shot-then-fallback
architecture) or whether a real gap remains.

**Auditor verdict: close S15, no code tonight.** Three points settle the main concern:
1. **The fallback has the same mode-identification power for normal + extended VIS.** `VisLockStateMachine`
   is a full VIS decoder including the extended path (`LockState.DecodeExtendedVis`,
   `EscapeVisByte = 0x23`) — MR/MP/ML stay covered post-exhaustion, not orphaned.
2. **Initial anchor precision is largely washed out downstream.** `TryResolveSyncAnchorCorrection` runs
   after every non-AVT `Commit()`, re-deriving the anchor from a multi-line sync-envelope fold
   regardless of which path committed. The S3 measurement put the two paths ~440 samples (~10ms) apart
   for the affected modes — comfortably inside a line period, so the fold recovers it. This is the
   *mechanism* behind the S2-era empirical result, not just a restatement of it.
3. **Already observed working end-to-end** via `BufferTrimTests.DecodedImage_StillDecodesCorrectly_WhenPrecededByLongSilence_ThatTriggeredTrimming`.

**One real, narrower gap the S2 investigation didn't cover — NOT a new item, it's already tracked as
S8** ("mid-image narrow re-lock", line 136/1137 above: "mid-image narrow-mode FSK-announce re-lock
(`sstv.cpp:2592`, needs a sample-by-sample FSK decoder this port doesn't have — `TryDecodeNarrowModeHeader`
is a fixed-window analytic shortcut with no real-time legacy counterpart)"). Legacy's `DecodeFSK` runs
every sample, forever (`sstv.cpp:1858`, confirmed during S14); this port's only FSK packet decoder lives
inside the fixed-window `TryDecodeNarrowModeHeader` — the same missing capability whether framed as
"initial detection post-exhaustion" (what this S15 investigation found) or "mid-image re-lock" (S8's
original framing). Re-verified S8's own load-bearing assumption while here, since an auditor flagged it
unverified (`SstvModeRegistry.cs:998-999`/`1023-1026`): `GetSyncIntervalCandidates` returns ALL non-AVT
modes including the whole MN/MC family, and `GetSyncIntervalMatchDepth` has an explicit MN73/110/140/
MC110/140/180 row (`isNarrow ? 8-5 : null`) — so `_syncBypassNarrowTracker` genuinely covers MN/MC, and
since its candidate list carries each mode's own distinctive line-duration interval, it can plausibly
identify the SPECIFIC MN/MC submode by timing alone, not just detect "some narrow signal" generically —
a different (not equivalent, but real) mechanism than legacy's FSK-payload-based identification, likely
narrowing S8's gap further than "no coverage at all." Still gated on the MN/MC golden-vector fixture
task #7 already plans to capture, per the roadmap's own "fix when measured, not reasoned" rule for this
item family — not rescheduled here, stays Band 3.

**Why building S15 as originally scoped would be the wrong call for an unattended session, even though
closing it isn't**: its own obvious implementation IS the already-tried-and-reverted re-anchoring draft
— a continuously-retriggering high-precision path structurally needs a rolling anchor, and a bounded
pre-lock buffer (Band-1 S2's own concern) needs one too. **These are the same underlying problem, not
two coincidentally-similar ones** — confirmed by the fact that the one existing attempt at solving
either one attempted to solve both at once, and was reverted because an arbitrary re-anchor point almost
never lands on a real header start — a property of the problem itself, not of that one attempt. Building
this unattended (nothing existing to reuse, unmeasured benefit, touches the load-bearing
`_fixedWindowExhausted`/`TrimBuffers` coupling) would mean relitigating an already-evidenced decision,
not executing a verified plan — exactly what this session's autonomous-continuation authorization
excludes.

**Worth recording for whoever revisits this**: exhaustion is the NORMAL case, not an edge case — pre-lock,
`_consumedSamples` never advances, so `_fixedWindowExhausted` fires ~`MaxSearchCeilingMs` (~1s) into
every epoch regardless of real signal content. In Phase 2's real target deployment (live capture, not a
test fixture starting at sample 0), any transmission not beginning within ~1s of decoder start or ~1s
after the previous `EndOfImage` is found ONLY by the fallback — the fixed-window path is close to
vestigial there, not "the main path with an occasional fallback." Doesn't change the verdict above, but
is the single most useful sentence to leave here.

**Band 2 is now fully done**: S5, S16, S14, S6 shipped as code; S15 closed via this documentation entry
(no code needed — its one real remaining gap turned out to be the already-tracked Band-3 item S8, whose
own load-bearing assumption got independently re-verified along the way). 423/423 tests unchanged (no
code this item).

## Task #7 — six new golden-vector fixtures captured (scottie-s1, robot72, pd90, rm8, mn110, avt)

Source images generated (gradient formula matching the existing `martin-m1`/`robot36` fixtures, sized
to each mode's exact `SstvModeRegistry` canvas), captured by the user against the real legacy Windows
binary, then wired into `GoldenVectorTests.cs`/`GoldenVectorFixtureReaderTests.cs` the same way as the
original two. Full capture/trim/measurement detail lives in
`tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/README.md`'s own new "Task #7" section — this entry
covers only the two real findings worth tracking here.

**Five of six modes decoded cleanly** (scottie-s1, robot-72, pd90, rm8, mn110 — restarts=0, correct mode
detected first-try, deltas in the same healthy range as `martin-m1`/`robot-36`'s own numbers).

**New tracked item, S31 — AVT's real capture never decodes at all (add to Band 3, most urgent of that
band). Root-caused and fixed — see the dedicated "S31" entry further down this file for the real
mechanism (not the working hypothesis below, which turned out to be wrong) and the fix.** Zero
`ModeDetected` events across the entire ~100s real `avt.mmv` capture — not a quality gap
like robot-36's, a total detection failure. Investigated before concluding this is a decoder bug, not a
bad capture: hand-traced the raw audio's frequency content (short-window FFT spot checks) and confirmed
it matches legacy's exact expected header sequence for the first ~2.7s (`OutHEAD`'s 800ms leader
pattern, then a proper VIS leader/break/data-bit sequence), with later content consistent with real
image-body transmission — so the capture is legitimate, the gap is on this port's decode side.
**Working hypothesis, not yet confirmed**: AVT's header is by far the longest of any mode (~8s: 3 VIS
repeats + a ~5.3s training sequence, vs. ~910ms for a normal header) — this port's fixed-window header
detection may not tolerate real-world timing jitter accumulated over that much longer a span, something
no synthetic (self-generated) round-trip test ever exercises. Needs an isolated repro (e.g. feed just
the real header audio, or progressively longer prefixes of it, to the decoder in isolation) before a fix
hypothesis is worth forming — not attempted in this pass, per the user's explicit choice to wire in the
five working modes now and track this separately rather than block on it.

**Smaller finding, folded into S9 (MN/MC narrow retune) rather than a new item**: `mn110`'s real capture
shows no measurable footer trailing-carrier at all (sharp cutoff to noise floor, confirmed via raw
envelope inspection) where the other five new fixtures' footers are all consistent with the existing
428ms-RX-default hypothesis — genuinely unexplained (the real capture session's RX-side state is
unrecoverable after the fact), modeled as an honest zero-footer special case in
`GoldenVectorTests.cs`'s duration check rather than forced to fit the shared formula. Not itself a DSP
bug (this test only checks envelope timing, not decode correctness) — noted here in case it turns out
relevant when S9 is eventually worked.

Test count: 452/452 `ScanlineStudio.Core.Sstv.Tests`, confirmed via a full solution-wide run (not just the
filtered subset used while iterating), 0 failed, 7m9s (423 prior + 29 new: 6 new `LegacyOwnDecode`/
`Fixtures` rows, 5 new `Decoder_DecodesRealLegacyAudio` rows, 5 new `EncoderOutput_DecodesSimilarlyTo`
rows, 6 new `MmvFixture_TxRegionDuration` rows, 6 new `MmvFile_ReadsNewTask7Fixture` rows, 1 new
`BmpFile_ReadsAvtRx...` row — AVT deliberately excluded from the two decoder-dependent theory lists, see
above), solution-wide build clean.

## S31 — AVT's real capture never decoded: root-caused and fixed

The working hypothesis logged above when this item was first tracked ("AVT's header is by far the
longest of any mode... may not tolerate real-world timing jitter") was **wrong** — investigated
properly in a follow-up session, per the user's request to confirm the actual mechanism empirically
before designing a fix, rather than build against the guessed hypothesis.

**Root cause, confirmed empirically** (temporary instrumentation added to both
`AnalogFmSstvDecoder`/`VisLockStateMachine`, run against the real `avt.mmv` fixture, then fully
reverted before any fix code was written — `git status`/`git diff` confirmed clean before proceeding):

- `VisLockStateMachine` — the only mechanism in this port with real-world noise tolerance (finds a VIS
  header anywhere in a stream, not just at a fixed offset relative to `_consumedSamples`) — deliberately
  discarded every AVT match it found, by design (an early scope decision documented on the class itself:
  AVT needed a hand-off to `AvtTrainingLockStateMachine` that only the fixed-window path provided, so
  extending this class to support it was flagged as "a possible future refinement, not attempted").
  The diagnostic hook proved this class already decodes AVT's real VIS byte (`0x44`) correctly from the
  real capture — **three separate times**, once per real VIS repeat, at samples 29495/39526/49559
  (≈2.675s/3.585s/4.495s, spaced exactly `VisHeader.AvtVisBlockDurationMs` apart) — and discarded every
  one.
- The only path allowed to act on an AVT match, `AnalogFmSstvDecoder.TryDecodeVisHeader` (the
  fixed-window path), is a single one-shot attempt anchored at the very start of the current epoch's
  buffer. On a real capture that window lands in `OutHEAD`'s 800ms pre-header leader tones plus ~1s of
  pre-TX room audio (the SAME mechanism already root-caused for martin-m1/robot-36's own anchor-precision
  gap, `VisHeader.MaxSearchCeilingMs` ≈1.3s is exhausted before the real header even starts at ≈1.8s) —
  so it always fails, and (being a one-shot gate per epoch, only reset by `EndOfImage()`, which requires
  a successful decode to ever run) never gets a second chance for the rest of the file.

Net effect: the only mechanism that could find AVT threw the match away; the only mechanism allowed to
act on it never saw real content. Zero `ModeDetected` events, fully explained — not a subtle
timing/jitter issue in the training sequence itself, which the decoder never even got far enough to
reach on real audio.

**Fix** (plan-reviewed by the auditor before implementation, which caught a real blocker before any code
shipped — see below):

1. `VisLockStateMachine.ProcessSample`: removed the premature `mode == SstvModeRegistry.Avt` bail-out in
   `DecodeVis`, letting AVT flow through the same `Verify` state every other mode already uses. Legacy
   justification, confirmed directly against `sstv.cpp:2127-2153`: case 3 runs the identical 1200Hz-hold
   verification for every mode uniformly — the AVT-specific diversion into cases 4-8 (setting the long
   training countdown and `m_SyncAVT`) only happens *after* that shared verification succeeds, never
   instead of it. AVT's own VIS byte (`0x44`) is a normal, non-extended code and AVT is not in the
   Scottie family, so `Verify`'s existing anchor arithmetic already computed exactly
   `headerStart + totalHeaderSampleCount` for it — proven algebraically (and independently re-verified by
   the auditor) via the same identity this class's own doc comment already used to justify its
   non-AVT anchor precision. No new math needed.
2. `AnalogFmSstvDecoder.TryStartAvtTraining`: refactored from `(int headerStart, int totalHeaderSampleCount)`
   to a single `(int visHeaderEndSample)` — every internal use (`_avtTrainingOriginSample`,
   `_avtTrainingFallbackDeadlineSample`, `_avtPllWarmupStartSample`) was already purely a function of
   their sum. Pure refactor, zero behavior change for the existing (fixed-window) caller.
3. `AnalogFmSstvDecoder.TryInterleavedHeaderScan` (the pre-lock noise-tolerant fallback): on an AVT match
   from `VisLockStateMachine`, hands off to `TryStartAvtTraining` instead of `Commit`-ing it as a normal
   line-0 anchor.
4. `AnalogFmSstvDecoder.TryVisLockStateMachine` (piece 6c's mid-reception re-verification, running while
   some OTHER mode is already locked and mid-decode): deliberately still discards an AVT match found
   there, via an explicit guard. Safely restarting into pending AVT training mid-decode would need the
   same in-progress-image teardown `Commit()` does for every other restart, which `TryStartAvtTraining`
   doesn't provide (it's only ever been called while `_mode is null`) — named as a deliberate, narrow,
   deferred non-goal (S31's own reported failure is a first-transmission scenario, not this rarer one),
   not a silent gap.
5. **Auditor plan-review blocker, caught before any code shipped**: item 3 sets
   `_fixedWindowExhausted = true` inside `TryInterleavedHeaderScan`, *before* its own scan loop can find
   an AVT match — invalidating an existing `TrimBuffers` comment's claim that `_avtPllWarmupStartSample`
   was transitively protected by the `_consumedSamples`-based watermark term (that protection only ever
   applied to the fixed-window entry path). Without a fix, a long-running chunked/streaming push could in
   principle trim the buffer past `_avtPllWarmupStartSample` during the `_avtTrainingPending` window and
   crash `Rel()`'s own bounds check — a real, previously undiscovered defect this project's usual bulk
   single-`PushSamples` test shape would never have caught. Fixed with one explicit watermark term
   (`if (_avtTrainingPending) watermark = Math.Min(watermark, _avtPllWarmupStartSample);`), the stale
   comment rewritten to state the new, narrower (~166-sample/~15ms) margin a code-level auditor review
   measured directly, rather than the wide one `_consumedSamples` used to provide.

**Known, accepted residual risks** (both flagged by review rounds, neither chased further): (a) which of
AVT's 3 VIS repeats gets matched is now genuinely signal-dependent (`TryStartAvtTraining` assumes repeat
1) — a match on repeat 2/3 still resolves correctly via `AvtTrainingLockStateMachine`'s own
signal-derived completion point, only the (already-approximate) fallback-deadline path would commit
910ms/1820ms late; (b) `VisLockStateMachine`'s own already-documented false-positive risk (a
sync-heavy image assembling a byte that happens to match a real mode's VIS code) is now, for AVT
specifically, *acted on* rather than silently discarded pre-lock — entering an up-to-~7.1s
`_avtTrainingPending` window during which no other header can be found. Legacy-faithful (`sstv.cpp`'s
own case 3→4-8 behaves identically on a spurious match, timing out via `m_SyncTime`), not a new defect,
but a genuine widening of an already-known risk — noted directly on `VisLockStateMachine`'s own class
doc comment.

**Real fixture, now decoding**: `avt.mmv` — `Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource`
delta 5.80 (restarts=0, correct mode detected first-try, tolerance 15.0), `EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode`
delta 9.92 (tolerance 18.0) — both measured directly, comfortably in the same healthy range as the other
five Task #7 fixtures and well under the ~42.67 corruption floor. `avt` is now included in
`GoldenVectorTests.DecoderFixtures` (previously excluded).

**New tests**: `AvtNoiseTolerantDetectionTests.cs` (4 tests) — end-to-end decode via
`VisLockStateMachine` after leading silence a fixed-window scan would miss; a handoff-equivalence check
proving the sample passed to `TryStartAvtTraining` from the new path matches what the fixed-window path
would have computed (not just coincidentally close); a mid-reception guard regression test (item 4);
a chunked/streaming-push regression test for item 5's `TrimBuffers` fix (documented honestly: a
deliberate sweep across leading-silence lengths and chunk sizes, with that fix temporarily disabled,
never actually reproduced a crash in practice — real coverage for the code path, not a proven repro of
the exact crash; the fix is kept regardless, since the invariant violation itself is real and
structural, independent of how hard it is to trigger).

Test count: 458/458 `ScanlineStudio.Core.Sstv.Tests` (452 prior + 6: 2 new `avt` rows in the existing
`DecoderFixtures`-driven theories, 4 new `AvtNoiseTolerantDetectionTests`), solution-wide build clean.
Two plan-review rounds (auditor) plus one code-level review after implementation — code-level verdict:
EQUIVALENT-WITH-RISKS, no blockers, ready to commit as-is; a handful of stale-comment nits it found were
fixed directly rather than deferred.

## S9, S13 — closed while working the remaining Band-3 items (S9 already done, S13 doc-only)

After S31, the user asked to fix the rest of Band 3. Investigating each item before writing code (per
this project's own "fix when measured, not reasoned" rule) found two of the eight didn't need new code:

- **S9 (MN/MC narrow retune) — already closed.** Band-2 item S6 (`6da0a65`) already ported exactly
  this: `HilbertFmDemodulator.ProcessSample`'s `isNarrow`-selected `(off, out)` pairs
  (`NARROW_CENTER`=2172Hz/`NARROW_BW`=256Hz vs. normal 1900Hz/800Hz, matching `CHILL::SetWidth`'s
  narrow branch exactly), gated in `AnalogFmSstvDecoder.DemodulatedFrequencyAt` on
  `_mode.NarrowModeCode is not null && thisIndex >= _bandpassLockedFromSample`. The Band-3 inventory
  entry was simply never updated/merged when S6 shipped — same gap, two tracking numbers. No code.
- **S13 (m_Type demodulator selector) — doc entry only.** The code toggle itself (PLL/zero-crossing/
  Hilbert RX picture demodulator) is correctly blocked on the not-yet-built Phase-3 settings UI. Added
  the missing `docs/removed-features.md` entry (CLAUDE.md §2 process debt, independent of the code
  blocker) — "Picture-demodulator selector (`m_Type`...)" section.

## S10 — extended-VIS escape byte (and every normal VIS byte) decided from 7 bits, not legacy's real 8

**Widened in scope from the original narrow framing** ("extended-VIS escape byte decided from 7 bits,
not 8") to the real underlying gap, found while investigating it: legacy's real `m_VisData` accumulator
is always 8 bits wide (`m_VisCnt` starts at 8, `sstv.cpp:1966-1967`) — 7 data bits PLUS the parity bit
— before its mode-lookup `switch(m_VisData)` (`sstv.cpp:1993-2074`) ever runs, for EVERY arm: the escape
check (`case 0x23`) and every normal single-byte mode case are arms of the exact same switch, not two
separately-timed decisions. `VisLockStateMachine` (the noise-tolerant fallback path) already did this
correctly — it accumulates 8 bits and matches via `SstvModeRegistry.FindByFullVisByte` (full byte,
parity included). Only `AnalogFmSstvDecoder.TryDecodeVisHeader` (the fixed-window path, tried first on
every header) was wrong: it read only 7 bits, decoded via a parity-stripped helper, and looked up via a
parity-stripped registry lookup — for BOTH the escape check and normal-mode matching.

**Practical effect** (why no existing test caught this): on a REAL, noisy capture, if a VIS byte's 7
data bits happen to match a real mode's low-7-bits but the 8th (parity) bit gets corrupted by noise,
legacy rejects the whole byte outright (`default: m_SyncMode=0`) — this port's fixed-window path would
have accepted it anyway, parity never checked. Invisible to any self-round-trip test since this port's
own encoder (`VisHeader.GenerateSegments`) always transmits a correctly-computed parity bit.

**Fix**: `TryDecodeVisHeader` now reads `VisHeader.FirstByteBitCount` (8, new named constant) bits
before deciding normal-vs-extended, and uses `SstvModeRegistry.FindByFullVisByte` (already existing,
already tested, already used by `VisLockStateMachine`) for both the escape check and normal-mode
lookup — reusing proven-correct code rather than inventing new escape-specific logic. The extended-path
bit-slicing was re-partitioned accordingly (still 16 total bits, `VisHeader.ExtendedDataBitCount`
unchanged) but slices cleanly at the new 8-bit boundary instead of skipping a bit. `NormalSearchCeilingMs`
bumped by one bit-slot (1035→1065ms) to stay in sync with the new 8-bit first decision;
`MaxSearchCeilingMs` itself is unaffected (still dominated by the 1305ms extended ceiling). Removed the
now-dead parity-stripped helpers (`SstvModeRegistry.FindByVisCode`, `VisHeader.DecodeVisCode`) rather
than leaving an unused "match ignoring parity" API around — auditor's own framing: "leaving a public
helper around is how this bug re-enters." Deduped `VisLockStateMachine`'s own local `0x23` escape
constant to reference `VisHeader.ExtendedVisEscapeCode` directly instead of an independently-drifting
copy.

**Auditor plan-review** (round 1, EQUIVALENT-WITH-RISKS, ready to build): independently re-verified
against `sstv.cpp` that this is genuinely one gap, not two (escape and normal-mode ARE the same switch);
recomputed all 24 non-extended legacy VIS bytes from the registry and confirmed all reproduce correctly
post-fix, including RM12's own forced-parity quirk (`Rm12ForcedParityBit`); confirmed the ceiling
analysis (`MaxSearchCeilingMs` genuinely unaffected). Flagged one real risk to watch: the 8th
bit-decision point lands close to the 1200Hz stop-bit boundary on real audio (~14.5ms measured
trigger-settling lag vs. a similarly-sized margin) — if golden vectors regressed, the instruction was to
investigate the decision-point timing, not widen tolerances. **All 7 real golden-vector fixtures passed
unchanged, no tolerance touched** — risk did not materialize.

**Code-level review** (after implementation, EQUIVALENT-WITH-RISKS, no blockers, ready to commit):
independently sanity-checked the stop-bit-boundary risk wasn't just "tests happen to pass" — confirmed
algebraically that even if a decision did drift into the stop-bit region, `VisBitDecision.TryDecide`
can only REJECT there (d11/d13 both non-responsive to 1200Hz), never produce a wrong bit, so the worst
case is degraded anchor precision via fallback, never a wrong mode. Found and fixed two doc-comment
nits (`ExtendedDataBitCount`'s definition reworded for clarity, its stale "already used at" claim
fixed). One pre-existing (not introduced by this fix), narrow, non-blocking risk noted but not chased:
a mid-word bit-rejection during the extended-code's second-byte decode could in principle re-trigger at
a different alignment while the caller still assumes the escape byte was proven — requires a specific
rare failure sequence, degrades to a wrong extended-mode-lookup attempt at worst (not a silent wrong
answer, `FindByExtendedCode` would just fail to match), not fixed here.

Test count: 460/460 (458 prior + 2 new: `WrongParityBit_NormalVisCode_NeverLocksViaFixedWindowPath`,
`WrongParityBit_EscapeByte_NeverLocksAsExtendedViaFixedWindowPath` in `VisToneRaceHeaderTests.cs`),
solution-wide build clean.

## S8 — mid-image narrow-mode (MN/MC) FSK re-lock

**Scope turned out much smaller than the roadmap implied.** Investigating before designing anything
found `NarrowFskHeaderDecoder` — a per-sample port of legacy's real `CSSTVDEM::DecodeFSK`
(`sstv.cpp:2378-2606`) — already existed, already faithful, already tested (two prior rounds of
line-by-line auditor review per its own doc comment). It was only ever used in a one-shot, fixed-window
way (`AnalogFmSstvDecoder.TryDecodeNarrowModeHeader`, fresh instance per call at `_consumedSamples`) —
the same "cold-started every call" architectural gap S5/S16 already fixed elsewhere. S8 is "wire the
already-correct decoder up as a persistent, continuously-fed scanner," not "build a new FSK decoder."

Confirmed directly against source: legacy's real `DecodeFSK(int(d19), int(dsp))` call (`sstv.cpp:1858`)
is unconditional every sample — outside and before the `if(!m_Sync||m_SyncRestart||m_SyncAVT)` gate
(`sstv.cpp:1889`) that restricts `m_sint1`/`m_sint2`/`m_sint3` and the VIS-decode switch. Also confirmed:
`CSSTVDEM::Stop()` (`sstv.cpp:1769-1791`) never touches any `m_fsk*` field — legacy's real narrow-FSK
state is genuinely never externally reset, only self-resets internally on its own failure/success
paths, exactly matching `NarrowFskHeaderDecoder`'s already-existing design.

**Fix**: added a persistent `_narrowFskDecoder`/`_narrowFskProcessedUpTo` pair (constructed once,
decoder-lifetime, never `Reset()`), wired into a new shared `AnalogFmSstvDecoder.TryNarrowFskScan`
method, called from both `TryInterleavedHeaderScan` (pre-lock) and `TryVisLockStateMachine`
(mid-reception, piece 6c) — unlike S31's AVT restriction, a narrow-FSK match needs no special-casing at
the mid-reception call site, since it's immediately actionable via the same `Commit()` every other
restart already uses (confirmed: `Commit()`'s body has no AVT-specific or VIS-specific step).

`NarrowFskHeaderDecoder.ProcessSample` now returns `(int ModeCode, int SamplesSinceBitClockOrigin)?`
instead of bare `int?` — the caller needs the packet's own timing to compute a real anchor. New
`VisHeader.NarrowPostBitClockOriginDurationMs` (539ms) constant for that arithmetic.

**Auditor plan-review** (found 2 real blockers before any code shipped, both resolved per the
auditor's own concrete suggested fixes, adopted directly):
1. The persistent scan cursor cannot share `_syncBypassProcessedUpTo`/`_visLockProcessedUpTo`'s own
   500ms `EndOfImage` jump — doing so would starve the narrow-FSK scanner of exactly the post-image
   window a real mode-change announcement is most likely to arrive in, defeating the whole point of
   this fix. Fixed by giving `TryNarrowFskScan` its own independent inner loop/cursor, outside the
   existing lockstep for-statement and entry-invariant check.
2. The original design assumed `NarrowFskHeaderDecoder`'s own internal sample counter could be treated
   as an absolute index (since the instance is never reset). Auditor: unsafe premise, nothing enforces
   it, a future caller-side skip would silently produce a wrong anchor, not a crash. Fixed: the class
   returns a RELATIVE offset (`SamplesSinceBitClockOrigin`) instead, and the reference point itself was
   corrected from the mode-0 guard-tone trigger (real, unbounded-in-practice jitter — envelope-settling
   lag plus mode 1's own 50ms hold tolerating a trigger up to ~50ms late) to the mode-3→4 transition
   (tightly pinned by construction — a single pass/fail recheck, not a hold).

**Two further regressions found empirically** (full-suite run, not anticipated by either plan-review
round — found the way this project's own methodology requires, by actually running everything before
calling a piece done):
1. An early implementation bound `TryNarrowFskScan`'s call inside `TryInterleavedHeaderScan` by
   `TotalSamplesReceived` (reasoning: "no fixed-window sibling to race against"). Wrong risk addressed
   — `scanBound` isn't only about protecting a fixed-window path, it's what stops ANY pre-lock detector
   reading ahead into a second, not-yet-legitimately-reached transmission on a bulk single-`PushSamples`
   call (the same "bulk vs. streaming ordering" class Band-1 items 2+3 already fixed elsewhere). Caught
   by `LegacyDerivedSpansTests.FskSpaceCursor_NeverResets_AcrossBackToBackNarrowTransmissions`
   (`ModeDetected` fired 4 times instead of 2) and `BandpassCacheChunkInvarianceTests` (unrelated
   fixture, same root cause). Fixed: bound by `scanBound`, matching the other two detectors.
2. Even after that fix, the same test still failed. Root cause: `Commit()` already fast-forwards
   `_visLockProcessedUpTo` to `_consumedSamples` on every commit (regardless of which path found the
   match) specifically so `VisLockStateMachine`'s own mid-reception scan never re-examines a header
   that just committed — `_narrowFskProcessedUpTo` needed the identical treatment and didn't have it.
   Since image 1's real header was found via the FIXED-WINDOW path (which uses its own separate, local,
   fresh decoder instance, not the new persistent one), the persistent decoder had never actually been
   fed samples 0.._consumedSamples — the first mid-reception scan fed it image 1's own real header for
   the first time, correctly decoded it (real, valid content), and fired a spurious second restart.
   Fixed: added the same `Math.Max` fast-forward in `Commit()` and in the sync-anchor-correction delta
   step, right alongside the existing `_visLockProcessedUpTo` ones. Explicitly NOT the same as the
   `EndOfImage` jump item 1 of the plan-review blockers avoided reintroducing — this is the smaller,
   always-necessary "don't re-discover what was just committed" correction, not an artificial lookahead.

**Code-level review** (after implementation, EQUIVALENT-WITH-RISKS, no behavioral bug found, ready to
commit): independently re-derived the anchor arithmetic against `sstv.cpp` and confirmed it exact at
11025Hz (±1 sample at 44100Hz); confirmed both empirical regressions are fully fixed with no third path
needing the same treatment (every `_consumedSamples` mutation site checked); confirmed no interaction
with S6/S9's narrow-mode retune (different signal domain); confirmed the unregistered-mode-code handling
matches legacy's own resume-on-failure behavior. Found a handful of doc/test-wording nits (a misleading
"first/last sample" claim in the new isolated unit test's comment, a watermark-safety argument that
should have cited the existing catch-up mechanism rather than a cursor-ordering claim Commit()'s
fast-forward can violate, a backwards inequality in a comment, a too-loose buffer-bound test threshold,
an O(n²) test helper) — all fixed directly.

Test count: 465/465 (460 prior + 5 new: `SamplesSinceBitClockOrigin_MatchesExactDataBitPhaseLength` in
`NarrowFskHeaderDecoderTests.cs`; `HeaderAfterLeadingSilence_IsRecognizedAndDecodedViaPersistentScan`,
`HeaderAfterLeadingSilence_AnchorMatchesExpectedWithinMeasuredTolerance`,
`MidReception_RealNarrowTransmissionAfterAnotherMode_RestartsAndDecodesCorrectly`,
`BufferStaysBounded_AcrossMultipleImageCycles_WithPersistentNarrowFskCursor` in new
`NarrowFskNoiseTolerantDetectionTests.cs`), solution-wide build clean.

## AVT package: S7 (mid-image re-lock), S11 (PLL signal domain), S17 (training-entry restructure)

Bundled as one plan-review (matching this project's own pattern-3 finding: AVT items are one work
package, not independent). Auditor verdicts per item: S7 ready to build after one on-paper decision
(made below); S11 not ready as originally proposed (a real flaw in the first draft, caught before any
code shipped); S17 close via documentation, no code needed, confirmed empirically rather than assumed.

### S7 — mid-image AVT re-lock, DONE

S31 left `AnalogFmSstvDecoder.TryVisLockStateMachine` (mid-reception re-verification, piece 6c)
deliberately discarding an AVT match found there, since `TryStartAvtTraining` didn't perform the same
in-progress-image teardown `Commit()` already does for every other restart. Fix: extracted
`Commit()`'s own 5-field header (`_mode`/`_lineDecoder`/`_pixels`/`_nextLine`/`_bandpassLockedFromSample`)
into a shared `AbandonInProgressImage()` helper — `Commit()` calls it first then sets its own new-lock
state on top (pure refactor, verified behavior-preserving for every existing caller); the new
mid-reception AVT branch calls it then `TryStartAvtTraining(...)`, leaving `_mode` null (training isn't
resolved yet) and returning `true` unconditionally — the caller (`TryProcessBuffer`'s per-line loop)
already fires `DecodeRestarted` with its own pre-captured mode and correctly re-enters the outer loop's
`_mode is null` routing either way (training resolved same-call or still pending).

Auditor plan-review independently verified all three load-bearing claims (the extraction is
behavior-preserving; the caller machinery is correct; S31's own `_avtPllWarmupStartSample` `TrimBuffers`
protection already covers this new entry path with no changes needed) and flagged one real,
previously-unconsidered cost, accepted rather than engineered around: once `_mode` goes null mid-image,
`_syncBypassProcessedUpTo` (frozen at wherever the FIRST transmission locked, only re-anchored by
`EndOfImage`, which this path deliberately doesn't call) pins the pre-lock watermark there for the whole
up-to-~7.1s pending window — retaining the abandoned image's audio rather than trimming it. Bounded (by
the same AVT-pending window this port already accepts elsewhere), not a repeat of the crash class
`TrimBuffers`' own `_avtTrainingPending` term prevents — the on-paper decision was to accept and assert
the bound in a new test rather than re-anchor the sync-bypass cursor in the AVT branch (which would
touch load-bearing state outside `AbandonInProgressImage()`'s own clean scope for a rare-case
optimization). Also flagged and accepted: false-positive-lock cost is now asymmetric (destroys a good
image instead of being free), matching legacy's own equally-uncorrectable case-3-through-8 shape and
the same accepted-risk category `VisLockStateMachine`'s own class doc comment and S8 already carry.

New tests: `MidReception_RealAvtTransmissionAfterAnotherMode_RestartsAndDecodesCorrectly` (repurposed
from the old S31-era test that pinned the opposite, now-superseded behavior — asserts `DecodeRestarted`
fires with the OLD mode and `ModeDetected` with `Avt`, per the auditor's own requested pin) and
`MidReception_RealAvtTransmissionAfterAnotherMode_ChunkedPush_StaysBounded` (chunked, not bulk — the
only shape that exercises the `_avtPllWarmupStartSample` protection and the accepted retention
tradeoff; measures rather than assumes the "settles well below peak" property instead of predicting an
exact bound).

### S11 — AVT training PLL signal-domain mismatch, DONE

Legacy's real `m_pll.Do(ad)` (`sstv.cpp` cases 3-7) reads `ad` — the AGC output BEFORE the separate
`*32` scale-up and ±16384 clip every OTHER envelope-detector consumer in this file needs (`d`, what
`AgcSampleAt` already returns). The port's AVT PLL feed used `BandpassFilteredSampleAt(w)*32768.0` —
pre-AGC entirely, not even the same family as `ad`.

**First draft rejected by the auditor's own plan-review round, caught before any code shipped**: the
proposed fix (`AgcSampleAt(w)/32.0`, dividing the already-clipped value back down) was based on an
inverted premise — `|ad|` peaks at ~16384 by construction (`m_agc = 16384.0/m_CurMax`), so the clip
triggers whenever `|ad| > 512`, meaning `AgcSampleAt`'s own output is a hard-limited square wave for
roughly 98% of every cycle at normal amplitude, not "rarely." Dividing that back down would have fed
the PLL a ±512 square wave, not a scaled copy of the real waveform.

**Fix actually shipped**: `_agcSamples` now stores the value UNCLIPPED (the `Math.Clamp` moved to
`AgcSampleAt`'s own return statement — zero behavior change for every existing reader of that return
value), and a new `AvtPllSampleAt(index)` reads the same cache directly, dividing back out only the
`*32` term, never the clip. Exact in every case, not an approximation; reuses the existing cache instead
of adding a new one (zero new cursor/list/`TrimBuffers` entries) — the auditor's own recommended
smaller variant of its "new dedicated cache" option, once the clamp-move insight made a full parallel
cache unnecessary.

**Acceptance criterion, corrected before measuring**: auditor traced `PllFmDemodulator`'s own internal
per-half-cycle AGC (normalizes peak-to-peak to a fixed target) against `CPLL::Do`, confirming the class
is scale-invariant to any consistent input multiplier far above its own ~1.0 floor — both the old and
new feeds are the SAME underlying filtered signal, differing only by a slowly-varying scalar the PLL's
own AGC already divides back out. Expected (and correct) result: **no measurable change** in `avt.mmv`'s
own decode delta — this is a fidelity fix (matching legacy's real signal domain exactly), not an
accuracy fix, stated honestly so it isn't later "corrected" back on a false assumption. Measured directly
(not assumed): `Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource` 5.80→5.79,
`EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode` 9.92→9.88 — both unchanged within measurement
noise, exactly as predicted.

### S17 — AVT training-entry restructure, CLOSED via documentation, no code needed

The port's `TryStartAvtTraining` analytically skips all 3 VIS repeats before constructing
`AvtTrainingLockStateMachine`; legacy's real case 3 hands off right after the FIRST repeat, then spends
repeats 2-3 as failed marker-search noise inside cases 4-7's own search loop — a materially different,
search-based entry mechanism the port simplifies away from.

Per the roadmap's own "decide based on measured effect" instruction for this item (not "build the
restructure unconditionally"): auditor plan-review found a source-derived argument that the entry
mechanism can't matter — `AvtTrainingLockStateMachine`'s own case-6-equivalent recalculates the overall
completion timeout ABSOLUTELY from each decoded block's own position in the 32-block sequence (its `h`
byte), not cumulatively from entry, so any error in HOW the state machine entered training cannot
propagate past the first successfully-decoded block. Recommended one cheap empirical check (~20 lines,
temporary instrumentation, added and fully reverted, same methodology as S31's own investigation) before
closing: record `(h, sample)` for every checksum-passing block in `AvtTrainingLockStateMachine` while
decoding the real `avt.mmv` fixture.

**Measured, not assumed**: the port's analytic skip does NOT land exactly on the training's real first
block — the first successfully-decoded block is `h=0x5e` (block 2 of 32), not `h=0x5f` (block 1), a real
~166ms landing imprecision the "ideal" case didn't predict. 31 of 32 blocks decode successfully
(`h=0x5e` down to `h=0x40`). This is a STRONGER confirmation than the auditor's own "ideal-landing"
argument, not a weaker one: it shows the real landing genuinely isn't perfect, yet — because completion
timing recalculates absolutely from whichever block locks first, not cumulatively — this measured
imprecision has zero effect on the training's own final completion accuracy. The restructure would only
recover that one missed block, which recovery doesn't change the answer at all. No code change; closed
via documentation, matching S9/S15's own precedent for this project's "fix when measured, not reasoned"
rule working in the OTHER direction (measurement showing a fix isn't needed, not confirming one is).

### Code-level review, all three items combined

Verdict: **EQUIVALENT-WITH-RISKS, no blockers.** S11's signal domain confirmed bit-exact against
`sstv.cpp:1834-1839`/every AVT case's own `m_pll.Do(ad)` call site; every existing `_agcSamples` reader
confirmed to still go through the clamped return, not the raw cache. S7's `AbandonInProgressImage()`
extraction confirmed behavior-preserving field-for-field; the deliberately-NOT-reset
`_visLockStateMachine`/`_visLockOriginSample` pair confirmed correct (resetting either alone would have
been the actual bug); trim safety traced explicitly through both the locked-branch and pending-window
watermark paths, no cursor can outrun retained data. S17 confirmed zero residual diagnostic
instrumentation in either touched file. Three documentation-only findings closed directly (no behavior
change, so no re-test needed): `AvtPllSampleAt`'s doc comment now states its fix is AGC-stage-only, not
full-chain (the upstream bandpass stage still runs H2/search instead of legacy's real H1 throughout AVT
training — a real, separate, ALREADY-tracked gap per `SearchBandpassFilter.cs`'s own doc comment, not
opened or closed by S11); `_avtPllWarmupStartSample`'s computation now documents the one real behavioral
divergence S7 makes newly reachable (legacy's case-3 PLL feed is gated `!m_Sync`, so it skips its own
30ms warm-up span when a mode is already locked — this port always includes it; immaterial to the
~1850ms warm-up window, confirmed by S7's own passing mid-reception test, but was previously unflagged);
two stale comments at the `TryVisLockStateMachine` call site corrected (claimed `Commit()` always runs
before `DecodeRestarted` fires — true for non-AVT restarts, not for the AVT branch, where training is
still pending). Two remaining nits accepted as genuinely cosmetic, not fixed: a single duplicate-fed
sample on the AVT restart path (state machine can't re-match on it) and a per-call vs. per-fill
`Math.Clamp` move in `AgcSampleAt` (free in practice). One off-scope finding logged in one line per this
project's own ADHD-scope rule, not chased: legacy sets `m_ReqSave` to preserve a substantially-complete
partial image on ANY mid-reception restart (`sstv.cpp:2135-2137`); this port drops the in-progress image
unconditionally on every restart path, not just the new AVT one — pre-existing, no
`docs/removed-features.md` entry yet.

Test count: 466/466 (465 prior + 1 net new: `MidReception_RealAvtTransmissionAfterAnotherMode_
ChunkedPush_StaysBounded` in `AvtNoiseTolerantDetectionTests.cs`; the existing
`MidReception_RealAvtTransmissionAfterAnotherMode_RestartsAndDecodesCorrectly` was rewritten in place to
pin the new restart behavior rather than added as a new test; no new tests for S11 (verified via the
existing golden-vector re-measurement) or S17 (closed via documentation, temporary instrumentation
fully reverted)), solution-wide build clean.

## S12 — m_sint2/m_sint3 freeze-while-decoding-VIS gating, DONE

`TrySyncIntervalDetectionStep`'s m_sint2/m_sint3 blocks (`AnalogFmSstvDecoder.cs`) evaluated
unconditionally every sample, with no equivalent of legacy's real `switch(m_SyncMode)` gating —
`m_sint1` had already been fixed (an earlier holistic-review pass), but `m_sint2`/`m_sint3` had not.

**Legacy source read directly** (`sstv.cpp:1889-1973`), not inferred: case 0 (`if(!m_Sync && m_MSync)`)
runs `m_sint1.SyncStart()`, then (if that didn't match) `m_sint2`'s full SyncMax-or-SyncStart if/else,
then the entire `m_sint3` phase-latch block. Case 1 keeps calling `m_sint2.SyncMax` (condition-true only,
no else-branch, no SyncStart) but has **zero** `m_sint3` references at all. Cases 2/9/3 (real VIS-bit
decode/verify) have **zero** references to any of the three trackers, confirmed by grepping every
`m_sint1`/`m_sint2`/`m_sint3` occurrence in the file.

**First plan-review round caught a real design gap before any code was written**: my original proposed
fix reused the pre-existing `_syncBypass1PrimaryHeld` field (already gating `m_sint1`) to gate
`m_sint2`/`m_sint3` too — auditor traced the actual case boundaries and found `_syncBypass1PrimaryHeld`
only tracks legacy's case-0↔1 boundary (m_SyncMode 0 vs 1), not the case-2/9/3 freeze the item is
literally named for: it goes false again the instant d12 dips below SLvl, which VIS data-bit tones
(1100/1300Hz, close to d12's 1200Hz passband) can readily cause mid-decode — exactly the failure mode
`m_sint1`'s own fix comment already named as the reason it needed gating in the first place. The narrow
fix would have left the real freeze unimplemented while closing the checkbox on a false claim.

**Fix actually shipped**: exposed `VisLockStateMachine`'s own internal state (already modeling
Search/ConfirmLock/DecodeVis/DecodeExtendedVis/Verify = legacy's case 0/1/2/9/3 exactly) via two new
properties, `IsSearching` (case 0 only — gates `m_sint3`'s whole block and `m_sint2`'s SyncStart) and
`IsAtOrBeforeConfirmLock` (cases 0-1 — gates `m_sint2`'s SyncMax continuation). Read at the top of
`TrySyncIntervalDetectionStep`, which already runs BEFORE `_visLockStateMachine.ProcessSample` for the
same sample index (`TryInterleavedHeaderScan`'s own loop order) — giving the state as of the END of the
previous sample, exactly matching legacy's `switch(m_SyncMode)` using its pre-transition value. No new
cursor/list/`TrimBuffers` entry needed; reuses the state machine this file already runs every sample.
A stale comment claiming "`m_sint2` already has an equivalent effect for free" (never true — leftover
justification from when this gap was first identified and deliberately not fixed) was corrected in the
same commit.

**Code-level review, verdict EQUIVALENT, no blockers.** Verified all three legacy case boundaries
against source directly (case 0's SyncMax-or-SyncStart split, case 1's SyncMax-only/no-else, cases
2/9/3's total absence), the ordering claim (checked all 3 real transition edges: trigger sample,
ConfirmLock-fail sample, per-bit-reject sample — no off-by-one), the `EndOfImage`/`Reset()` interaction
(clean — matches legacy's own `Stop()` clearing `m_SyncPhase` in the same place), and the pairing with
`m_sint1`'s unchanged gate (no new double-fire risk). Two doc-only findings closed directly: the existing
`_syncBypass1PrimaryHeld`/`VisLockStateMachine` "two copies can disagree during resettle" comment (already
documenting a pre-existing divergence) now also notes S12's own new consequence — a transient false
Search→ConfirmLock→DecodeVis in the state-machine copy during that same resettle window can freeze
m_sint2/m_sint3 for up to ~270-540ms where legacy would not (bounded, low-probability, not fixed); a
second comment records that legacy freezes `m_sint2`/`m_sint3` permanently after a successful lock
(`m_SyncMode=256` until `Stop()`) while `VisLockStateMachine` resets straight back to `Search`, argued
(and confirmed unreachable) since every lock-returning call exits the scan loop immediately either way.
One test-coverage nit closed: a load-bearing comment was added at the `TryInterleavedHeaderScan` call
site itself, flagging that swapping `TrySyncIntervalDetectionStep`/`ProcessSample`'s call order would
silently invert this gate with no existing test catching it.

Two new isolated unit tests in `VisLockStateMachineTests.cs` (matching this project's own "verify each
sub-piece before wiring" methodology, no `AnalogFmSstvDecoder` involved): `IsSearching_
FalseOnceTooShortBlipEntersConfirmLock_TrueAgainAfterItResets` (reuses the existing
`TooShortBlip_NeverAdvancesPastConfirmLock` blip shape to pin the case-0↔1 round trip) and
`IsAtOrBeforeConfirmLock_FalseWhileRealVisHeaderIsDecodingVisBits` (a full real header, asserting both
phases are observed and in the right order). Full suite green, no existing tolerance needed widening —
consistent with the auditor's own prediction that a real, clean fixture's own tone content rarely
produces the momentary dip this gate specifically guards against.

Test count: 468/468 (466 prior + 2 new, both in `VisLockStateMachineTests.cs`), solution-wide build
clean.

## Band 4 — documentation/test-only items (S27, S29, S30, S21), DONE

All four Band-4 items are pure documentation/test additions — no DSP behavior change anywhere, verified
by an unchanged full-suite result before and after. (S13's doc half was already closed earlier, alongside
S9/S10.) User asked for "a good deep documentation and comment update and audit," so each item below was
re-verified against legacy source directly rather than trusted from the original inventory table's own
summary, and the whole batch got a dedicated code-level auditor review before commit.

### S27 — CQ100 mode: missing `removed-features.md` entry + a stale code comment

Investigated fresh rather than trusting the inventory table's narrower "FIR tap-tripling" framing: an
exhaustive grep of every `g_dblToneOffset` reference in `sstv.cpp` found 44 distinct referencing lines
(46 occurrences) across the whole DSP core (VIS-decode envelope detectors, sync-interval/AFC
frequencies, bandpass filter cutoffs, the AVT training PLL center, `CHILL`'s own `m_OFF`) — not just
`HilbertFmDemodulator`'s tap-tripling, which is a second, independent CQ100-gated effect
(`sstv.cpp:3048-3050`, `m_tap *= 3`). **Code-level audit caught the first draft's own doc entry was
still incomplete** despite that grep: two more real `sys.m_bCQ100`-gated DSP effects existed
independent of `g_dblToneOffset` itself and were missing from `removed-features.md` — an `m_OFP`
sync-timing shift (`sstv.cpp:1181-1184`, a division by `g_dblToneOffset`, not the additive shift every
other site uses, roughly -1.1ms) and a narrowed AFC capture window (`sstv.cpp:1678-1681`/`1688-1691`,
sync±50Hz replacing the wider default range in both narrow and normal branches). Both added to the
entry; the "~30 call sites" estimate corrected to the exact 44/46 figure in all three places it
appeared (`removed-features.md`, this entry, `HilbertFmDemodulator.cs`'s own comment). All four effects
are set from exactly one place, a `-i` command-line flag at startup (`Main.cpp:1065-1077`) — never
reachable via the GUI, an `.ini` key, or any other path (confirmed by a whole-tree grep finding no other
assignment site for either `g_dblToneOffset` or `sys.m_bCQ100`), and this port has no command-line-flag
entry point that could set an equivalent. Fixed the stale comment in `HilbertFmDemodulator.cs` (previously
claimed `g_dblToneOffset` is "confirmed always 0.0," which overstated it — corrected to "confirmed 0.0
on every path this port's architecture can reach," matching `SearchBandpassFilter.cs`'s own already-
correct wording for the same fact) and cross-referenced the new `removed-features.md` entry.

### S29 — `MakeFilter` odd-tap trailing-zero divergence: add an executable guard

The existing `MakeFilter_IsSymmetric_ForEvenTap` test already deliberately excludes odd tap (both of
this port's currently-reachable tap counts, 24@11025Hz and 96@44100Hz, are even), and the class's own
doc comment already documented WHY (legacy's real mirroring loops, `fir.cpp:421-426`, write exactly
`2*(tap/2)+1` entries — for an odd tap that's one short of the full `tap+1` array, leaving the last
slot at its zero-init default) — but nothing pinned this with an executable assertion. Added
`MakeFilter_OddTap_TrailingSlotStaysZero_NotSymmetric` (`SearchBandpassFilterTests.cs`), parametrized
over two odd tap counts (23, 25) at the port's real filter parameters: asserts the trailing slot is
exactly 0.0, the slot just before it is a real nonzero coefficient, and the array is provably not
symmetric the way the even-tap case is. Guards against a future refactor that "helpfully" fully
populates the trailing slot (looking like an off-by-one fix) silently diverging from legacy's real
(latent, currently unreachable) behavior.

### S30 — `CHILL` middle decimation tier (16-40kHz): add instance-level coverage

The raw filter-coefficient generator (`MakeHilbert`) was already exercised at the middle tier's tap
count via existing `[InlineData(24, 22050.0)]` cases, but no test ever constructed an actual
`HilbertFmDemodulator` INSTANCE at a sample rate in the 16-40kHz range — meaning the tier-selection
logic itself (`sampleRate >= 16000` branch) and its `tierMultiplier=2.0` wiring
(`_offWide`/`_outWide`/`_offNarrow`/`_outNarrow`) had no end-to-end proof, only the isolated kernel.
Added `Constructor_MiddleDecimationTier_16To40kHz_SelectsTap24Df1_AndDecodesCorrectly`
(`HilbertFmDemodulatorTests.cs`) at 22050Hz: asserts `HalfTap == 12` (directly pins tier selection, not
just its downstream effect) and that settled tone readback at 1500/1900/2300Hz is correct, mirroring
the existing `SettledOutput_SteadyTone_ReadsBackCorrectFrequency` pattern already used for the other
two tiers. Updated the class's own doc comment to note this tier is no longer untested, just unused by
any currently-supported sample rate.

### S21 — RM8/RM12 int-truncation divergence: record the exact measured bound

The existing doc comment (`MonoAveragedPairedScanlineDecoder.cs`) already correctly identified the
divergence (legacy truncates to `int` twice — once inside `GetPixelLevel`'s own `d *=
m_DemWhite`/`m_DemBlack`, once at the RM8/RM12 branch's own `d *= gain`; `GetPictureLevel`, the function
the RM8/RM12 branch actually calls, itself calls `GetPixelLevel` exactly once, so this collapses one
hop without changing the truncation count) but only estimated its size as "a couple of levels." Read
`Main.cpp:4038-4073`/`4437-4449` and `ComLib.h:242-243` directly to pin the exact chain
(`m_DemWhite`/`m_DemBlack` both default to `128.0/16384.0`, confirmed via `Main.cpp:875-877`, and
`m_DemCalibration` defaults to 0/`Main.cpp:878`, so `GetPixelLevel`'s calibration branch is never live
on the default path this port models — the single default pair covers this port's whole reachable
domain, not an incomplete scope), then wrote an exhaustive test
(`MonoAveragedPairedScanlineDecoderTests.IntTruncationDivergence_MatchesLegacysExactTwoTruncationChain_WithinMeasuredBound`)
that independently replicates legacy's real two-truncation chain and sweeps every achievable input in
this port's own AGC'd ±16384 sample domain against the port's actual formula. **Exact measured result**:
maximum divergence of precisely 2 levels, never more, and asymmetric as the original comment predicted
but never quantified — legacy-minus-port ranges `-1..+2`, not a symmetric `±2`. `RmGainFactor` (was
`private`) made `internal` so the test references the SUT's real constant rather than a
separately-drifting literal copy. Both the code comment and the roadmap's own "Piece 12" note (search
"No truncation replication") updated with the exact figure. Well inside the existing
10.0 round-trip / 15.0-25.0 golden-vector tolerances, now confirmed by measurement rather than estimate.

**Code-level audit** independently re-derived the exact bound in closed form (not just re-running the
test) and confirmed `-1..+2`/max-abs-2 correct, confirmed the sweep domain is robust beyond its own
stated ±16384 bound (both chains saturate identically outside roughly `[-14336,+14224]`, so the
"every achievable" phrasing is harmless rather than an under-sweep), and confirmed no overflow/sign
risk anywhere in the chain (both sides stay in `double` throughout, matching legacy's own `double`
multipliers on `int` accumulators). Two comment-accuracy nits found and fixed: the test's own doc
comment named `GetPixelLevel` where legacy's real call site is `GetPictureLevel` (which itself calls
`GetPixelLevel` once — the numeric claim was never affected, just the citation); and a self-contradictory
sentence about `Math.Truncate` vs `Math.Floor` was corrected to describe what the code actually needs
(C#'s `(int)` cast's own toward-zero semantics).

Test count: 472/472 (468 prior + 4 new: `MakeFilter_OddTap_TrailingSlotStaysZero_NotSymmetric` in
`SearchBandpassFilterTests.cs` (2 theory cases), `Constructor_MiddleDecimationTier_16To40kHz_SelectsTap24Df1_AndDecodesCorrectly`
in `HilbertFmDemodulatorTests.cs`, `IntTruncationDivergence_MatchesLegacysExactTwoTruncationChain_WithinMeasuredBound`
in new `MonoAveragedPairedScanlineDecoderTests.cs`), solution-wide build clean, no DSP behavior change.

## Milestone audit, Phase 1+2 (docs/audit-playbook.md) — 2026-08-04

After Band 1-4 closed and Task #7's 8 golden-vector fixtures landed, ran the milestone-audit playbook,
scoped down from its generic template to this repo's actual current state (radio/CAT, DI wiring,
localization, and UI aren't built yet — skipped as units; RX had heavy per-item auditor coverage this
session already, so Phase 2 effort was weighted toward TX/encode and the cross-cutting cursor mechanism,
both comparatively under-scrutinized).

**Status: Phase 1 (unit map, done by the orchestrating session directly, no auditor spend) and Phase 2
(4 batches, fully fanned out) are done. Phase 3 (chain/integration audit) has NOT yet run.** This is
not a complete audit — Phase 3 is explicitly the part per-function/per-unit checks can't cover, and
batch D itself found real golden-vector coverage gaps (Scottie DX, MR73, R24 each exercise a code path
no other fixture does); batch A confirmed TX has zero legacy-decode-verified golden vectors at all
(round-trip only), so TX chain-verification is fundamentally data-limited without a legacy-binary
automation path this project doesn't have.

### Phase 1 unit map

| Unit | Files | Legacy ref | Golden vector |
|---|---|---|---|
| AGC/level detection | `LevelAgc.cs`, `SyncEnvelopeDetector.cs`, `AfcTracker.cs` | `CLVL`, `sstv.cpp` | shared, all 8 fixtures |
| Sync/VIS header detection | `VisLockStateMachine.cs`, `SyncIntervalTracker.cs`, `VisHeader.cs` | `sstv.cpp:1889-1973`+ | all 8 |
| AVT training | `AvtTrainingLockStateMachine.cs`, `PllFmDemodulator.cs` | `sstv.cpp` cases 3-8 | `avt.mmv` |
| Narrow FSK header | `NarrowFskHeaderDecoder.cs` | `DecodeFSK` | `mn110.mmv` |
| Picture demodulator | `HilbertFmDemodulator.cs`, `SearchBandpassFilter.cs` | `CHILL`, bandpass | all 8 |
| RX: RgbSequential (Martin/Scottie/SC2) | `RgbSequentialScanlineDecoder.cs` | `Main.cpp` decode switch | `martin-m1`, `scottie-s1` |
| RX: YCbCrRobot (Robot 36) | `RobotScanlineDecoder.cs` | same | `robot36` |
| RX: YCbCrSequential (Robot72/R24) | `YCbCrSequentialScanlineDecoder.cs` | same | `robot72` |
| RX: YCbCrLinePaired (PD/MP) | `YCbCrLinePairedScanlineDecoder.cs` | same | `pd90` |
| RX: MonoAveragedPaired (RM8/RM12) | `MonoAveragedPairedScanlineDecoder.cs` | same | `rm8` |
| TX: all 5 encoder families + orchestrator | `AnalogFmSstvEncoder.cs` + 5 `*ScanlineEncoder.cs` | `Main.cpp`'s `Line*`, `CSSTVMOD` | **none** — round-trip only |
| Cross-cutting buffer/cursor/restart | `AnalogFmSstvDecoder.cs` (`TrimBuffers`/`Commit`/`EndOfImage`/`AbandonInProgressImage`) | `sstv.cpp` state machine | indirectly, via the above |

### Phase 2 batches (all 4 run, `auditor` subagent, isolated context each)

**Batch A — TX encoders vs legacy.** Verdict: EQUIVALENT-WITH-RISKS. Per-line channel order,
sync/separator placement, per-channel durations, and line counts CONFIRMED correct for all 6 families /
all 43 modes against the real `Line*` functions — the historical Scottie failure class (right duration,
wrong order/sync placement) does not recur anywhere. VIS/header/AVT-preamble/narrow-FSK-packet TX all
CONFIRMED byte/bit-exact.

**Batch B — buffer/cursor/restart mechanism.** Verdict: EQUIVALENT-WITH-RISKS. `Stop()`/`EndOfImage()`
parity with legacy CONFIRMED exact (resets precisely the same state legacy's own `Stop()` does, no more
no less). Cursor enumeration CONFIRMED complete (found several the task's own framing didn't name, all
verified clean). Every `RemoveRange` physical-length invariant CONFIRMED to hold.

**Batch C — AVT + narrow-FSK interaction.** Verdict: EQUIVALENT-WITH-RISKS. S17's own closing claim
(training-entry imprecision doesn't affect final accuracy) independently RE-DERIVED from source and
CONFIRMED, not just re-trusted. Narrow-FSK's lack of a PLL-style warm-up gap CONFIRMED (no filter/phase
state to warm up, unlike AVT's PLL).

**Batch D — RX decoder family cross-check.** Verdict: **NOT EQUIVALENT**. Mode→`ColorEncoding` mapping
CONFIRMED exact 1:1 against legacy's real RX switch partition for every one of legacy's six branches.
Peak-pick-vs-bare read mode CONFIRMED correct per family, not by analogy, at every legacy call site.
Channel order for the two default-branch (untested) mode families (Pasokon, MC) CONFIRMED from their
real TX generators, not inferred.

### Findings, prioritized (MUST/SHOULD/COULD/NICE-TO-HAVE)

**MUST — confirmed, live-reachable bugs, fix soonest:**

1. **[D] Pixel-pitch trim accumulator drift.** Independently confirmed by the orchestrating session
   directly against `Main.cpp:4454-4503`, not just trusted from the auditor. Legacy's segment
   *transitions* (`m_CG`/`m_CB` etc.) use the full, untrimmed nominal channel span; only the
   pixel-index-*within*-a-segment mapping (`x = ps*Width/m_KSS`) uses the trimmed divisor, and any
   leftover time at a segment's tail is simply discarded in legacy, never folded forward. This port's
   decoders instead accumulate `idealSamplesSoFar` by the *trimmed* total across each scan segment
   (`RgbSequentialScanlineDecoder.cs:25-30` and the identical pattern in the other 4 decoders), so every
   scan segment after the first starts early — compounding across channels. Measured magnitude (per
   batch D): PD90 Y2 shifts by up to 4.0px, Martin M1/Scottie S1 B/R channels by 1.33/2.67px, Pasokon
   ~1.33px/segment. Unaffected: RM8/RM12 (single scan segment) and "group C" modes (trim factor exactly
   1.0). Fixtures pass today because their 15.0-25.0 average-delta tolerance absorbs a few pixels of
   channel misregistration — the exact "round-trip + golden vector both agree while wrong" shape
   CLAUDE.md §4 warns about, since the trim was validated on the RX side alone (Piece 11) with nothing
   pinning absolute segment-start positions against legacy's real untrimmed boundaries.
2. **[C] `TryNarrowFskScan` is a whole-buffer pre-pass, not a per-sample interleave.** Independently
   confirmed by the orchestrating session directly (`AnalogFmSstvDecoder.cs:1352-1365`: a complete
   internal `for` loop from `_narrowFskProcessedUpTo` to `bound`, called and fully resolved BEFORE the
   sync-bypass/VIS-lock lockstep loop takes even one step, `:1804-1809`). On a bulk `PushSamples`
   containing an earlier other-mode transmission followed later by a narrow (MN/MC) one, the narrow scan
   sweeps the whole buffer and can commit the LATER transmission before the sync-bypass/VIS-lock loop
   ever examines the EARLIER one's samples — re-introducing, for the narrow-FSK path specifically, the
   exact bug class the `m_sint1` decoder-ordering fix (piece 7d) was written to eliminate. The mid-image
   caller (bounded to ~one line via `TryVisLockStateMachine`) has the same shape but is low-impact there;
   the pre-lock bulk-push caller is the real exposure.
3. **[B] AVT locked images never trim at all.** `_afcProcessedUpTo`/`_slantProcessedUpTo` are the
   locked-branch watermark's two terms (`AnalogFmSstvDecoder.cs:883`), but AVT is the one mode whose AFC
   and Slant trackers are permanently null (`InitializeAfc`/`InitializeSlant` return early for AVT,
   `:2698-2704`/`:2796-2807`), so neither cursor ever advances once an AVT image locks. AVT is ~90s/image
   (240 lines × 375ms) — `_bufferBase` stays pinned at the lock anchor for the whole image, retaining
   ~56MB@11025Hz/~225MB@44100Hz per AVT image. Same failure class Band-1 item S2 fixed pre-lock,
   re-opened here on the locked side for AVT specifically. Not caught by any existing test — the only
   AVT buffer-bound test measures peak-vs-final AFTER the fixture's own final `EndOfImage` already
   trimmed everything back down.

**SHOULD — real, worth doing soon, bounded or conditional impact:**

4. **[A] TX frequency-mapping drops legacy's two integer truncations — DONE.** Legacy's `ColorToFreq`/`GetRY`
   chain truncates twice (int arithmetic); every port TX encoder maps in unrounded doubles. Net: a
   systematic (not random) ~0-4Hz, mean ~2Hz, one-sided-high bias on every transmitted pixel — visually
   nil, but it means true bit-exact TX parity against a real legacy decode is unattainable until this is
   modeled (two `Math.Floor`s), and it's the reason TX golden-vector capture (if ever done) wouldn't
   match byte-for-byte even with a perfectly-timed encoder.
5. **[A] `OutHEAD` pre-VIS tone burst never emitted — DONE** (800ms normal / 400ms narrow, `Main.cpp:7270-7292`,
   called unconditionally before VIS at legacy's shipped `m_VOX=0` default). Not a decode blocker (a real
   legacy RX still locks on the VIS leader), but a real unported TX segment with no
   `docs/removed-features.md` entry and no code comment — a CLAUDE.md §2 process-rule gap, same class as
   S27's CQ100 gap before it was fixed.
6. **[C] Mid-image narrow restart leaves ~1 line with a stale demod-cache config — ASSESSED, DEFERRED**
   (see "Items 6, 8, 10" below). When a non-narrow
   locked image is abandoned mid-image for a narrow-mode commit (S8), `_bandpassFilteredSamples`
   (`useLocked`) and `_demodulatedFrequencies` (`isNarrow`) are both per-index-frozen caches already
   driven ahead by the abandoned line's own decode — so roughly the first line of the NEW narrow image
   is stuck with the OLD mode's `useLocked=true`/`isNarrow=false` config, meaning S9's own narrow Hilbert
   retune doesn't apply to it. Bounded (~1 line), but confirmed, not just risked — neither S8's nor S9's
   own individual review was positioned to see this, since it only exists at their intersection.
7. **[C] Narrow-FSK detection is fully suspended for the whole ~7.1s AVT-training window — DONE.** Legacy calls
   `DecodeFSK` unconditionally throughout AVT training and would actually abort training on a valid
   MN/MC packet found during it; this port's `TryDecodeHeader` short-circuits to AVT resolution while
   `_avtTrainingPending`, so a narrow packet overlapping an AVT header is silently missed. Not documented
   anywhere in the S7/S8 comments.
8. **[B] Pre-lock watermark's `<=` should be strict `<` — DONE** (see "Pre-lock watermark strict
   inequality" below). `FilteredRawSampleAt` reads
   `_rawSamples[Rel(index-1)]`; if a trim ever left `_bufferBase == _bandpassFilteredProcessedUpTo`, the
   next fill would throw. Currently unreachable — protected only by an undocumented numeric coupling
   (`preLockRetentionSamples` ≈1.3s exceeds the 380ms narrow-discriminator window that's the actual
   driver) that nothing enforces or comments on.
9. **[B] `TryInterleavedHeaderScan`'s entry invariant holds by reachability, not construction — DONE**
   (documented, no behavior change — see "TryInterleavedHeaderScan's entry invariant" below). S7's
   `AbandonInProgressImage()` created a second way for `_mode` to become null without going through
   `EndOfImage()` (which is what normally re-syncs `_syncBypassProcessedUpTo`/`_visLockProcessedUpTo`).
   Currently safe only because that call site always leaves `_avtTrainingPending` true, short-circuiting
   the scan entirely until a guaranteed `Commit()`. A future "abandon without committing" path would
   throw.
10. **[B] `PixelSampleReader`'s `Math.Clamp` silently substitutes the boundary sample — ASSESSED,
    DEFERRED** (see "Items 6, 8, 10" below) for an
    already-trimmed index, at the one call site that actually writes pixels — converting what `Rel()`'s
    throw elsewhere in the file treats as a loud bug into a silent one, at the highest-consequence reader.
    Safe today (locked watermark stays 2000 samples of margin back), but the inconsistency itself is a
    risk.
11. **[D] Luma `Limit256` clamp missing in 3 of 5 RX decoders — DONE** (Robot36, YCbCrSequential,
    YCbCrLinePaired don't clamp pre-`YCtoRGB`; RgbSequential and MonoAveragedPaired do, matching their
    own legacy sites) — a real cross-family inconsistency, not a uniform policy choice. Only bites on
    out-of-band/overdriven input, which no existing fixture exercises. See "Working the SHOULD backlog"
    below.
12. **[D] Robot 36's tone-selector reads ~1ms later than legacy's own decisive window — DONE**, inside the
    demodulator's settling region toward the following porch — correct on the clean synthetic/fixture
    signal, fragile (biased toward the ambiguous-band toggle fallback) on a real noisy one. **MUST
    fix 3's code-level review found this got MORE relevant, not less**: correcting the luma segment's
    own boundary (MUST fix 3) moved the tone-selector's read point 0.37ms later than before, so it now
    lands on the exact LAST sample of the selector segment — right at the boundary with the 1900Hz
    porch that follows (1900Hz being exactly the ambiguity midpoint, worst case for contamination).
    Correct today (robot-36 still decodes with restarts=0), safe if the demodulator's own group delay
    is non-negative (not traced), but this is now the one read MUST fix 3 pushed right up against a
    segment boundary. Cheap hardening suggested but not applied: read at `endSample - 1` minus a small
    margin, or bound it by legacy's own `m_CG` decisive-window end rather than the segment's full end.
13. **[D] Golden-vector coverage gaps: Scottie DX, MR73, R24 — SUBSTANTIALLY ADDRESSED.** Each is the
    ONLY mode exercising a specific code path no other fixture reaches (Scottie DX: the sole
    `NeverPeakPicks` mode; MR73: the sole mode where luma/chroma trim by different divisors; R24: the
    sole mode using legacy's row-doubling substitution). The TX-side golden-vector work (this file's
    own "TX-side golden-vector tests wired in" entry) added real-legacy-decode coverage for all three
    via `LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage` — a real legacy install decoding
    this port's own TX output for each mode, exercising each one's distinct code path end-to-end.
    Not FULLY closed: that only covers the TX-encode direction; there's still no real legacy-CAPTURED
    (RX-direction) fixture for any of these three, so a bug specific to how this port's RX chain
    handles one of these three paths against REAL analog-captured audio (as opposed to this port's own
    clean encoder output) would still pass every existing test. Downgraded from a live gap to a smaller,
    honestly-scoped residual one.

**COULD — worth doing, low urgency:**

14. **[D] Line-paired chroma trim is generalized from the sequential family's own rule** (`IsChromaChannel`
    routes ALL families' RY/BY through `Ks2sTrimFactor`) rather than read from `LinePaired`'s own legacy
    branch, which actually uses `m_KSS`. Numerically harmless today (the two factors are equal in every
    currently-reachable group), but would silently break if a line-paired mode ever landed in the one
    group where they differ. Likely gets touched incidentally while fixing MUST item 1 above.
15. **[C] Three doc comments disagree about whether `_narrowFskProcessedUpTo` is ever re-anchored** —
    `AnalogFmSstvDecoder.cs:333-334` and `:1346` both still claim it's never jumped; `:1984`/`:1999`/
    `:2179` correctly show it being fast-forwarded. Doc-only fix, zero behavior implication.
16. **[B] `InitializeSlant` uses a bare assignment where `InitializeAfc` deliberately uses `Math.Max`** —
    an undocumented asymmetry. Harmless today (fresh tracker + fresh detector on every re-init, no
    in-place buffer mutation the way AFC's correction has), but worth a comment or a matching guard.

**NICE-TO-HAVE — cosmetic / near-zero impact:**

17. [A] MR/ML "hold" segments emit 1900Hz instead of holding the last pixel's real frequency — ~1
    sample, duration preserved, already documented as an approximation.
18. [A] AVT's trailing blip is a literal tone (holds phase) where legacy writes 3 samples of true zero.
19. [A] TX segment-boundary rounding uses `Math.Round`; legacy's real running accumulator truncates —
    both are drift-free running accumulators, differing by ≤1 sample at any single boundary.
20. [A] TX output isn't filtered/scaled the way legacy's real BPF+`m_outgain` pipeline is — only matters
    if TX golden vectors are ever captured from real legacy audio.
21. [A] RM8/RM12's TX luma average is kept as `double`; legacy averages already-truncated `int`s — same
    class as MUST-adjacent finding 4, same fix if ever addressed.
22. [A] TX footer trailing-carrier length uses the port's own (arguably saner) TX-mode duration where
    legacy quirkily uses the RX-mode's; worth one documentation line, not a behavior change.
23. [B] `FirstLockedBandpassIndex` never resets across images on a multi-image stream — diagnostic-only
    field, no functional consumer.
24. [B] `_afcBoundSample` caps the locked watermark at the image's nominal extent, pinning ~0.1% of a
    slow-clock image's tail — bounded, near-zero.
25. [C] AVT's never-locks fallback timeout fires ~9ms early relative to `AvtTrainingLockStateMachine`'s
    own internal timeout, dropping a small margin term legacy's own formula includes.
26. [C] `AvtTrainingLockStateMachine.ProcessSample` returns on the exact sample its counter hits zero;
    legacy's own `Start()` call happens one sample later. ~1 sample.

**Verified, no action needed** (recorded so nobody re-investigates): batch A's `YCbCr.FromRgb` missing
`LimitRGB`-equivalent clamp (proven unreachable — Y/RY/BY extremes for any real 8-bit RGB input never
exceed [0,255]); batch C's confirmation that narrow-FSK has no AVT-PLL-style warm-up-gap concern of its
own (no filter/phase state); batch C's independent re-derivation confirming S17's original closing
argument holds exactly (budget arithmetic matches legacy's real case-3-through-8 timing to sub-ms
precision).

### MUST fix 1 — AVT buffer-trim, DONE

`TrimBuffers()`'s locked branch included `_afcProcessedUpTo`/`_slantProcessedUpTo` unconditionally in
its watermark `Math.Min` chain. AVT is the one mode where `InitializeAfc`/`InitializeSlant` both leave
`_afcTracker`/`_slantTracker` null — two SEPARATE legacy guards, not one (AFC: `sstv.cpp:2258/2263/2267`'s
`m_afc && m_CurMax>16 && mode!=smAVT`, inside all three `m_Type` branches; Slant/AutoStop:
`Main.cpp:3886`'s `(m_AutoStop||m_AutoSync||KRSA->Checked) && mode!=smAVT` — a code-level review
correction caught an earlier draft of this fix's own comment mis-citing both to the same guard). With
both trackers null, `ApplyAfcCorrections`/`ApplySlantTracking` both return immediately every call
without ever advancing their own cursor again once frozen at commit time — permanently pinning the
whole AVT image's buffer for its entire ~90s duration (240 lines × 375ms), retaining tens to hundreds of
MB depending on sample rate. Same failure class Band-1 item S2 already fixed pre-lock, silently reopened
here on the locked side for AVT specifically.

**Fix**: mirrors the pre-lock branch's own already-established pattern for these exact two cursors (its
own comment: "AFC/Slant don't exist yet ... excluded here ... because there's nothing to include") —
`_afcProcessedUpTo`/`_slantProcessedUpTo` are now only folded into the locked watermark `Math.Min` chain
when their tracker is actually non-null. For every other mode (where both trackers are real), the
existing defensive stall-protection property (documented in the same comment block: "an idle-forever
previous lock, e.g. AFC/Slant stalled, shouldn't be able to grow unboundedly either") is preserved
exactly unchanged.

**New regression test** (`AvtNoiseTolerantDetectionTests.LockedAvtImage_BuffersActuallyTrimMidDecode_NotJustAtTheVeryEnd`):
pushes a full AVT image in 20000-sample chunks, samples `BufferedSampleCount` after every chunk once
locked, and checks the buffered count plateaus (not just "ends small," since the existing
`ChunkedPush_StaysBounded` test only samples at the very end, after the image's own final `EndOfImage`
already resets everything — this test samples DURING the locked decode itself). Two checks: a relative
plateau ratio (second-half peak < 1.5× first-half peak) AND an absolute ceiling (a generous multiple of
one line's own sample count), the second added per code-level review — a relative ratio alone would also
pass a slower-but-still-unbounded leak. Also asserts the decoded image still matches the source within
the existing 20.0 AVT tolerance, since `PixelSampleReader`'s index lambda clamps rather than throwing on
an out-of-range read — an over-aggressive watermark would otherwise silently corrupt pixels rather than
crash, and a buffer-only check wouldn't catch that. **Confirmed to actually discriminate the bug**, not
just pass coincidentally: temporarily reverted the fix and re-ran — pre-fix measured 493334 samples
(first-half peak) growing to 993334 (second-half peak, exactly 2.01×, both failing assertions); restored
the fix, re-confirmed passing.

**Code-level review**: EQUIVALENT-WITH-RISKS, ready to commit. Verified every reader that could read
"behind" the new AVT watermark stays safely bounded by the remaining chain terms (pixel decode's own
small margin needs, `ApplyAfcCorrections`/`ApplySlantTracking`'s own early-returns, the sync-anchor-
correction warm-up being unreachable for AVT, `_avtPllWarmupStartSample`'s own pre-lock-only term, the
unconditional D11/D12/D19/FskSpace catch-ups) — no path found where the fix releases data a live reader
still indexes. Verified the stall-protection property can only ever be skipped for AVT specifically (the
one non-AVT window where trackers are momentarily uninitialized, `_pendingAnchorCorrectionMode is not
null`, is already excluded earlier in the same method). Two citation nits fixed (the AFC/Slant guard
mix-up above); two test-strengthening suggestions folded in before commit (the pixel-correctness
assertion and the absolute-ceiling check, both described above).

Test count: 473/473 (472 prior + 1 new: `LockedAvtImage_BuffersActuallyTrimMidDecode_NotJustAtTheVeryEnd`
in `AvtNoiseTolerantDetectionTests.cs`, strengthened with a pixel-correctness assertion and an absolute
buffer ceiling per code-level review, both folded into the same test rather than split out separately),
solution-wide build clean.

### MUST fix 2 — `TryNarrowFskScan` whole-buffer ordering bug, DONE

`TryInterleavedHeaderScan` (pre-lock) used to call `TryNarrowFskScan(scanBound)` ONCE, letting it run
to completion (match or exhaust `scanBound`) before the interleaved sync-bypass/VIS-lock loop below it
ever took a single step. Legacy's real `DecodeFSK` call is unconditional and runs BEFORE the sync/VIS
switch, but for the SAME sample every time (`sstv.cpp:1858` vs `:1889`, confirmed directly, not
inferred) — never a whole-buffer sweep ahead of the other detectors. On a bulk push spanning an earlier
non-narrow transmission followed by a later narrow one, once `_fixedWindowExhausted` (quick, ~1.1s)
made `scanBound` span the whole buffer, the old code let the narrow scan find and `Commit()` the LATER
transmission before the loop ever examined the EARLIER one's own header — silently skipping it. Same
bug class the `m_sint1` decoder-ordering fix (piece 7d) eliminated, reopened here for narrow-FSK.

**Fix**: `TryNarrowFskScan` itself is unchanged — only how it's called from `TryInterleavedHeaderScan`
changed. One catch-up call before the loop, bounded by `Math.Min(scanBound, _syncBypassProcessedUpTo)`
(never further than wherever sync-bypass already sits, since `_narrowFskProcessedUpTo` can legitimately
lag behind across image boundaries — it's never reset/jumped by `EndOfImage`, unlike
`_syncBypassProcessedUpTo`/`_visLockProcessedUpTo`), then one call per loop iteration bounded by
`_syncBypassProcessedUpTo + 1` — in the steady state this processes exactly one narrow-FSK sample per
interleaved-loop sample, reproducing legacy's real per-sample order for every sample in range, not just
the first one.

**New regression test** (`NarrowFskNoiseTolerantDetectionTests.BulkPush_EarlierNonNarrowTransmissionBeforeLaterNarrowOne_DetectsEarlierFirst`):
3s leading silence (forces detection through the noise-tolerant scan, not the one-shot fixed-window
path), a full Martin M1 transmission, then a full MN110 transmission, all pushed in ONE bulk call so
`scanBound` spans everything from the first call — exactly the shape that exercised the bug. Asserts
`ModeDetected` fires twice, Martin M1 first then MN110, both images decode within tolerance, and (per
code-level review) zero `DecodeRestarted` events. **Confirmed to discriminate the bug**: reverting the
fix made `ModeDetected` fire once (MN110 only — Martin M1 silently skipped entirely); restored, fires
twice in the right order.

**Code-level review**: EQUIVALENT-WITH-RISKS, ready to commit. Verified the ordering guarantee holds for
every case, not just the tested scenario (reversed order, back-to-back narrows, multiple images in one
push — all sound, since the per-sample interleave always evaluates narrow-FSK before sync-bypass/VIS-
lock at the SAME index, and the catch-up call only ever covers `_narrowFskProcessedUpTo`'s own past
relative to sync-bypass, never its future). Confirmed the `Math.Min` in the catch-up call is load-
bearing (removing it reintroduces the exact original bug). Two stale-comment nits fixed (claims that the
scan "runs once per call" and "is called with scanBound" — now literally called more often, with tighter
bounds, though the underlying conclusions both still hold, now stated accurately).

**Real risk found and deliberately deferred, not silently absorbed**: the fix increases exposure at a
SECOND call site (`TryVisLockStateMachine`, mid-reception, NOT touched by this fix — its own bound is
`_consumedSamples`, growing by one decoded line per call, so its own theoretical inversion window was
already up to ~146-428ms, not one sample, even before this fix). Before this fix, on a bulk push, the
pre-lock whole-buffer pass had already exhausted `_narrowFskProcessedUpTo` to `TotalSamplesReceived` by
the time locked decode began, making the mid-reception call site's own scan an accidental no-op. After
this fix, `_narrowFskProcessedUpTo` sits much further behind once locked, so that scan now genuinely
runs over the locked image's own picture content — a legacy-faithful exposure (real `DecodeFSK` is
unconditional while locked too, so legacy carries the identical theoretical risk), but newly reachable
in this port where it was previously accidentally muted. The original milestone-audit finding's own
"minor there" assessment undersold this by roughly 4 orders of magnitude in sample count once this fix
landed. **Deliberately deferred, not fixed in this same pass** — same fix shape could be applied to
`TryVisLockStateMachine`'s own call site later if this ever proves reachable in practice; the new test's
own `Assert.Equal(0, restartCount)` stands as a live guard against it regressing silently in the
meantime.

Test count: 474/474 (473 prior + 1 new), solution-wide build clean.

### MUST fix 3 — pixel-pitch trim accumulator drift, DONE

The biggest and most invasive of the three MUST fixes, touching 4 decoder files identically. Legacy's
real per-channel scan-segment BOUNDARIES (`Main.cpp:4454-4503`'s `ps < m_KS`/`ps < m_CG`/`ps < m_CB`
checks) are defined using the mode's full, UNTRIMMED nominal channel span. Only the pixel-index-
WITHIN-a-segment mapping (`x = ps*Width/m_KSS`, `ps` already relative to the segment's own start)
uses the TRIMMED divisor. Any leftover time at a segment's tail (once `x` would reach `Width`) is
simply never assigned a pixel in legacy — discarded, not folded into where the next segment starts.
`RgbSequentialScanlineDecoder`/`RobotScanlineDecoder`/`YCbCrSequentialScanlineDecoder`/
`YCbCrLinePairedScanlineDecoder` instead accumulated their running position by the TRIMMED total
across each whole scan segment, so every segment after the first started early — compounding across
channels.

**Fix**: in each of the 4 files, capture `segmentStartSample = idealSamplesSoFar` BEFORE the per-pixel
loop; walk pixels using a separate `pixelWalk` accumulator (the trimmed per-pixel duration) relative
to that start; after the loop, set `idealSamplesSoFar = segmentStartSample + scan.DurationMs / 1000.0
* sampleRate` (the segment's FULL untrimmed duration), discarding the trimmed pixel walk's own
leftover exactly as legacy's own `x >= Width` boundary does. `RobotScanlineDecoder.cs`'s fix lives in
its shared `DecodePixels` helper (`ref double idealSamplesSoFar`), used for both the luma and chroma
scan segments. `MonoAveragedPairedScanlineDecoder.cs` (RM8/RM12) deliberately NOT touched — single
scan segment per line, structurally immune (confirmed by measurement: rm8's own golden-vector delta
was numerically unchanged) — a one-line comment added there instead, flagging that the bug would
return if a trailing segment were ever added to that family.

**Golden-vector re-measurement** (temporary zero-tolerance technique, together with MUST fix 2's own
narrow-anchor-timing change): martin-m1 1.263→0.44 (big improvement), robot-36 14.476→16.19 (worsened
slightly, same accepted-tradeoff category as an earlier Piece A finding — a real, honestly recorded
consequence of a genuine correctness fix, not chased to zero), scottie-s1 2.74→1.92 (improved),
robot-72 13.46→14.57 (worsened slightly, same category), pd90 1.99→0.96 (improved), rm8 13.76→13.76
(UNCHANGED, confirming `MonoAveragedPaired`'s immunity directly), mn110 12.79→2.39 (improved — NOT
attributed to this fix, since MN110 is a "group C" mode with trim factor exactly 1.0, a mathematical
no-op for it; attributed to MUST fix 2 instead), avt 5.78 (was 5.79, unchanged). No tolerances needed
changing.

**New dedicated test file** `PixelPitchSegmentBoundaryTests.cs`: two isolated tests using synthetic
hard step-edge images (unlike the real `.mmv` fixtures' smooth gradients, a sharp edge makes pixel-
column drift directly measurable, isolated from AGC/noise). Martin M1's R channel (third of three scan
segments, compounding drift from both G and B before it) and PD90's Y2 channel (fourth of four
segments, the single worst-case drift magnitude among all affected decoders). **Confirmed to
discriminate the bug directly**: reverting the fix moved the detected edge column from the true 160 to
165 for both (a measured 5px systematic shift); restored, Martin M1 lands at 162 (2px off) and PD90 at
161 (1px off), both within the ordinary-noise tolerance.

**Attempted, not committed**: dedicated step-edge tests for `RobotScanlineDecoder`/
`YCbCrSequentialScanlineDecoder` too, to close a code-level-review-flagged coverage gap. Investigation
finding worth recording: Robot 72's chroma segments are only 69ms wide across the full 320-pixel width
(~2.4 samples/pixel at 11025Hz) vs. Martin M1's/PD90's own ~5-6 samples/pixel — a sharp step edge on a
channel this narrow is dominated by ordinary envelope-detector settling smear (the same real-time delay
spans far more pixels when each pixel represents so little time), not by this fix's own segment-start
placement. A correct test for these two families needs a differential (pre-fix-vs-post-fix column
shift) design, not an absolute-position check — out of scope for this pass, tracked as a SHOULD-level
follow-up rather than silently dropped.

**Code-level review**: EQUIVALENT, ready to commit. Independently cross-checked the fix's whole premise
— that `scan.DurationMs` really is legacy's real untrimmed nominal span — against `CSSTVSET::SetSampFreq`
formulas for 7 different modes (MRT1, SCT1, R36, R72, PD90, AVT, MR73), all exact matches. Verified the
Robot `ref`-threading composes correctly end-to-end (chroma now starts at exactly legacy's `m_SB`, was
2.7px early). Verified TX independently confirms the trimmed-pitch/untrimmed-boundary asymmetry is
real and RX-only (TX places segments at flat untrimmed pitch, no trim at all). Two real risks found and
addressed: (1) the Robot 36 tone-selector read-timing risk, now folded into the existing SHOULD item 12
above with the fix's own specific consequence documented; (2) the test coverage gap for Robot 36/72,
investigated (see "attempted, not committed" above) and tracked rather than silently left. Several nits
fixed directly: `MonoAveragedPairedScanlineDecoder.cs`'s own immunity now has a guarding comment; the
two committed tests' tolerances tightened and their real measured values recorded in-comment instead of
left implicit.

Test count: 476/476 (474 prior + 2 new, both in `PixelPitchSegmentBoundaryTests.cs`), solution-wide
build clean.

### Next steps

**All 3 MUST fixes are now DONE.** Also tracked, not yet actioned: the deferred `TryVisLockStateMachine`
narrow-FSK exposure noted in MUST fix 2's own entry; the Robot 36 tone-selector read-timing risk (SHOULD
item 12); the Robot 36/72 step-edge test coverage gap noted in MUST fix 3's own entry above. None of
these block anything — all are real, honestly documented, deliberately deferred. Remaining SHOULD/COULD/
NICE-TO-HAVE items from the original Phase 2 findings list are still open. Phase 3 (chain/integration
audit)'s own TX-side-tests-first prerequisite (user's own explicit directive, see above) is now DONE —
see "TX-side golden-vector tests wired in" below.

**Explicit prerequisite before Phase 3 (chain/integration audit): build TX-side verification tests and
confirm them first.** Phase 3's own mandate is to verify real input through the composed chain against
LEGACY-captured output, NOT an internal round-trip — but batch A's finding stands: TX currently has
**zero** legacy-decode-verified coverage, only internal round-trip (this port's own encoder → this
port's own decoder agreeing with itself), which is exactly the failure shape CLAUDE.md's own Scottie
incident warns about. Running Phase 3's TX-side chain audit on top of that gap would mean auditing
against a reference this port doesn't actually have yet. User's explicit direction: get TX tests built
and verified BEFORE running Phase 3, not after — do not skip straight to the chain audit with this gap
still open. This likely means capturing TX-side golden vectors (this port's own encoder output run
through a real legacy decode, the reverse direction of the existing RX fixtures) — bottlenecked on the
user's own time with the real legacy binary, same category as Task #7 and finding 13's Scottie-DX/MR73/
R24 coverage gaps, not something this session can do unaided.

### TX-side capture prep — DONE (waiting on the user's own real-legacy-install time)

User is setting up the real legacy binary under Wine locally and asked whether this session could
prepare the TX-side `.mmv` files itself. Investigated the legacy capture mechanism directly
(`Sound.cpp`) before assuming anything: no CLI/headless RX mode exists (only `-r`/`-i` flags, unrelated),
but the SAME `.mmv` format already used for the existing RX fixtures can be fed into legacy via its own
`File → Play` menu action and replayed through its real-time RX pipeline — no live audio hardware/
routing needed. This session has no screenshot/visual-feedback tooling to drive that GUI reliably, so
the actual `File → Play` / save-image steps stay the user's own — but everything else (generating
correctly-formatted TX audio, and later processing the results into real fixtures/tests) is fully
automatable and was done now.

**New file-format writers, verified before any fixture generation (user's own explicit request: "be
sure to have opus verify the correctness of these two tools before assuming they work")**:
`MmvFile.Write` and `BmpFile.Write` (inverses of the existing `Read` methods, `tests/ScanlineStudio.Core.Sstv.Tests/`).
Opus code-level review (read-only, against `Sound.cpp`/the standard BMP/DIB spec) found `BmpFile.Write`
clean but caught a REAL blocker in `MmvFile.Write`'s first draft: `(short)Math.Round(clamped *
32768.0f)` could wrap a full-scale +1.0 sample to -32768 instead of saturating at +32767 (`Math.Round`
has no `float` overload, so the multiply widens to `double`; .NET's saturating float→int32 conversion
catches the intermediate 32768.0 fine, but the SUBSEQUENT int32→short narrowing is plain truncation,
not saturation — 32768 = `0x8000` truncates to a `short` as -32768). Genuinely reachable, not
hypothetical: `AnalogFmSstvEncoder` yields raw full-scale `Math.Sin(phase)`, so ~1.3% of every cycle's
samples land in the wrap window — every generated file would have been silently corrupted with a
~2x-full-scale impulse roughly every 75 samples throughout the whole transmission. Fixed by clamping in
the integer domain (`Math.Clamp(Math.Round(...), short.MinValue, short.MaxValue)`) instead of relying
on the cast to saturate. New round-trip tests (`FixtureFileFormatTests.cs`, 14 tests, including a
10,000-sample full-cycle sine sweep) confirmed to discriminate the bug directly (reverting the fix
reproduces the exact `+1.0 → -1.0` wrap); `BmpFile.Write`'s own review found zero defects, confirmed by
a hand-traced 2x2/3x3 round-trip plus 5 parametrized round-trip tests across odd/even widths.

**11 TX `.mmv` files generated** (`tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/TxCapture/`,
11025Hz, matching every other fixture): the 8 modes with existing RX fixtures (reusing their exact
source `.bmp`), plus 3 modes the milestone audit flagged as having zero coverage anywhere (Scottie DX,
MR73, R24 — each the sole mode exercising a specific code path), with newly-generated source `.bmp`s
using the same established gradient formula. Generated via a temporary test-project generator, run
once, then fully reverted (git diff clean) per this project's own established methodology.

**Self-decode verification, not just file-format correctness** — user's own explicit follow-up
question caught a real gap: the file-format round-trip tests alone don't prove the ACTUAL generated
files are valid, decodable transmissions, only that arbitrary float values survive the byte format.
New `TxCaptureFixturesTests.cs` (11 tests, kept permanently, not reverted) reads each real generated
file back exactly as a human would hand it to legacy, decodes it with this port's own decoder, and
confirms zero restarts, the correct mode detected, and a correct decode within the same tolerances the
existing RX-direction golden-vector tests use. All 11 pass.

**Next**: `TxCapture/README.md` documents the exact steps and file-naming convention for the user's own
side (set legacy's sample rate to 11025Hz first — mismatched rates trigger a silent lowpass-resample
prompt on `File → Play`, defeating the whole point). No rush — fixtures can be wired in individually as
results come back, without waiting for all 11.

### TX-side golden-vector tests wired in — DONE (all 11 modes) — 2026-08-04

All 11 `<mode-id>_TX_RX.bmp` results came back from the user's real legacy install (Wine). New theory
`LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage` in `GoldenVectorTests.cs` (11 cases) compares
each source `.bmp` against legacy's own real decode of this port's own encoder output — this is the
actual test spec/14-roadmap.md's own "Explicit prerequisite before Phase 3" note required: TX output
verified against a REAL legacy decode, not just this port's own decoder agreeing with itself.

R24 needed special handling, not a plain top-crop: its source `.bmp` is 120 rows (its real transmitted
row count, `SstvModeRegistry.R24`'s own doc comment) but legacy's saved `_TX_RX.bmp` is the usual 256-row
canvas with each real row duplicated into 2 consecutive display rows (`Main.cpp:4160-4168`, `R=y*2`). A
plain `CropToTop` would compare doubled rows against undoubled source rows and misalign 2x — added
`CropToTopEvenRows` (takes rows 0, 2, 4, ..., 238 of the top 240) to undo the doubling before comparing.

Deltas measured directly via the same temporary-zero-tolerance technique used throughout this session:
robot-36 3.30, martin-m1 1.53, scottie-s1 1.05, robot-72 3.21, pd90 2.40, rm8 5.15, mn110 2.60, avt 0.86,
scottie-dx 1.03, mr73 3.22, r24 3.70. All 11 restart-free, correct mode detected first-try. Every one of
these is BELOW the RX-direction `LegacyOwnDecode_MatchesSourceImage_EstablishesBaselineDelta` numbers for
the same modes (robot-36 6.99, martin-m1 1.57) despite going through this port's own encoder first — real
evidence this port's TX output is a valid, accurately decodable transmission to a real legacy receiver.
Tolerances set to ~2x each measured value (same margin style as the RX baseline's 15.0), still
comfortably under the ~42.67 corruption floor this gradient-image metric measures elsewhere in this file.

**The "TX-side verification tests, built and confirmed" prerequisite is now fully satisfied.** Phase 3
(chain/integration audit) can run.

Test count: 512/512 (501 prior + 11 in `GoldenVectorTests.cs`), solution-wide build clean.

### Phase 3 — chain/integration audit (docs/audit-playbook.md) — 2026-08-04

Two `auditor` calls, isolated context each, per the playbook's Phase 3 instructions: audit the SEAMS
between already-reviewed units (Phase 2), not re-litigate per-unit correctness. RX chain (AGC → sync/VIS
→ AVT/narrow-FSK → demodulator → 5 decoder families → buffer/cursor) and TX chain (encoder orchestrator →
5 encoder families → VIS header handoff) run separately, since TX/RX don't share runtime state.

**RX chain verdict: NOT EQUIVALENT.** One confirmed MUST bug, two risks, two nits.

**TX chain verdict: EQUIVALENT-WITH-RISKS.** No analog of the MUST-bug-class found — structurally
impossible (see below). One new SHOULD-level finding, three already-tracked nits re-confirmed at the seam.

#### MUST 4 — RX per-line cursor rounds every line; legacy never rounds within a transmission

Independently re-verified against both sides directly (not just trusted from the auditor):
`AnalogFmSstvDecoder.cs:1094/1125` computes `lineSampleCount = (int)Math.Round(_effectiveSamplesPerLine)`
and advances `_consumedSamples += lineSampleCount` — i.e. line *k* starts at `anchor + k*round(E)`.
Legacy (`Main.cpp:4133-4148`, `DrawSSTVNormal`) uses ONE continuous integer sample counter `n` for the
whole transmission and derives each line's boundary as `y = int(double(n)/SSTVSET.m_TW)` — a `double`
division against the running counter, never rounded per line. Line *k* starts at exactly `anchor + k*E`
in legacy; this port's line starts drift from that by up to ~0.5 samples/line, compounding across the
image (same bug shape as MUST fix 3, promoted one level up: MUST fix 3 fixed rounded-vs-unrounded
*within* a line/segment; this is the same mismatch *between* lines).

**Structurally invisible to Auto Slant**, which could otherwise mask/correct it: `ApplySlantTracking`
(`:2898-2954`) tracks its own separate, exact fractional grid (`_slantIdealSamplesSoFarInLine`, rolled
over with carried remainder, never reset to 0 — `:2951`), so the measured sync-peak position stays
constant regardless of the line-cursor's own rounding error, and `SlantTracker` never sees a drift to
correct.

Predicted per-mode magnitude (arithmetic from each mode's own registry timing at 11025Hz, not yet
measured against a dedicated test): Robot 72 worst at ~0.50 samples/line × 240 lines ≈ 120 samples drift
by the last line (~25-50px depending on channel); RM8 ~0.47/line × 120 ≈ 56 samples; Robot 36 ~0.25/line
× 120 ≈ 30 samples; Scottie S1/Martin M1/PD90 much smaller (their line pitch lands closer to an integer
at 11025Hz). Correlates with — not proof of, stated as a hypothesis — the existing decoder-vs-source
delta ranking in this file (~line 3153): robot-36/robot-72/rm8 are the three worst-scoring fixtures,
pd90/martin-m1 the two best, matching the `|round(E)-E|` magnitude ranking above.

Not caught by any existing test: golden-vector tolerances (15.0-25.0) absorb a few pixels of horizontal
shear; TX is not making the equivalent mistake (Batch A/Phase-3-TX both confirm TX's accumulator is
drift-free), so no test compares this port's own RX decode against a bit-exact-timed reference precise
enough to expose sub-pixel-per-line drift.

**Fix shape (not yet applied):** keep a `double` line-start accumulator alongside `_consumedSamples`
(mirrors MUST fix 3's own `segmentStartSample`/`pixelWalk` split), advance it by the unrounded
`_effectiveSamplesPerLine` every line, and pass its rounded value as `lineStartSample` — `_consumedSamples`
itself is load-bearing for at least six other cursors/watermarks and should keep tracking the rounded
accumulator's value, not be replaced by a double.

#### Other RX chain findings

- **[risk] No stated scheduler/re-entrancy contract for `LineDecoded`/`DecodeRestarted`/`ModeDetected`.**
  All three are invoked synchronously from inside the cursor-advance loop (`:1127`/`:1171`/`:2112`). A
  subscriber that re-enters `PushSamples` (directly or via a scheduler) would resume the outer loop with
  stale `mode`/`pixels`/`lineDecoder` locals against a different `_consumedSamples` epoch. Unreachable
  today (no production subscriber exists yet), but CLAUDE.md §4's concurrency rule requires every
  cross-thread stream to state its scheduler and slow-subscriber behavior — none of these three do yet.
  Real landmine for the eventual `ScanlineStudio.Application`/UI wiring.
- **[risk] `LineDecoded` hands a live alias of the mutable `_pixels` array**, not a copy (`:1127`,
  `MutableImageSource` wraps the same array `Commit`/`AbandonInProgressImage` later replace or mutate).
  Same landmine category as above — the eventual UI subscriber needs to know this before it queues an
  `IImageSource` for later rendering.
- **[nit] Last pixel of a line can read an AFC-uncorrected sample**, ≤1px, one `m_AFCDiff` magnitude —
  `ApplyAfcCorrections`'s bound (`:1110`) and the decoders' own line extent can differ by up to ~0.85
  samples (two independent roundings).
- **[nit] `_afcBoundSample` computed from the nominal sample rate, not the effective (slant-corrected)
  one** — already-recorded NICE-TO-HAVE 24, now confirmed larger in practice than that entry's original
  "~0.1%" estimate once MUST-4's own drift is accounted for (robot-72 overruns by ~120 samples with the
  drift present).

Checked and clean (explicitly, not skipped): cross-module unit/scaling handoffs (AGC → bandpass →
Hilbert demod, all traced against `sstv.cpp`'s equivalent domain, no unconverted handoff); filter-config
flips at the `useLocked`/`isNarrow` boundary (one delay line, coefficient-table swap only, no stale
parallel state); second-image reset completeness (every field not reset by `EndOfImage` is either
unconditionally reassigned on the next `Commit`, deliberately persistent to match legacy, or already
tracked — no fourth un-reset item found beyond MUST fixes 1-3).

#### TX chain: why the MUST-bug-class is structurally impossible here

Legacy's TX accumulator (`CSSTVMOD::Write`, `sstv.cpp:2842-2846`) is `m_dPos += tim*m_TxSampFreq/1000`,
reset only once per transmission (`InitTXBuf`) — never per line or segment. `AnalogFmSstvEncoder.cs:36-46`
mirrors this exactly (one accumulator spanning header + every pixel + footer), and all five
`*ScanlineEncoder.cs` files feed it the same per-pixel duration they yield (`scan.DurationMs /
mode.ImageWidth`, matching legacy's own `tw /= width`) — there is no second, trimmed derivation of a
segment's length anywhere on the TX side for a MUST-fix-3/MUST-4-shaped mismatch to hide in. Cross-line
counts (all 43 modes, including R24's internal double-increment) and phase continuity (`CVCO::Do`, never
reset per segment) were independently re-derived from source and confirmed to match.

**New SHOULD-level finding: no image/mode dimension contract validation in the TX orchestrator — DONE
(see "Working the SHOULD backlog" below).**
`AnalogFmSstvEncoder.EncodeAsync` (`:23-39`) never checks `IImageSource.Width`/`.Height` against
`mode.ImageWidth`/`.ImageHeight` before encoding; every scanline encoder then indexes the image blindly
(e.g. `RgbSequentialScanlineEncoder.cs:26`, `image.GetScanline(lineIndex)[x]` for `x` up to
`mode.ImageWidth-1`) — independently confirmed directly. A too-small image throws `IndexOutOfRangeException`
mid-stream (after header + partial line already yielded, no clean error state); a too-large one silently
crops with no signal. Legacy is structurally immune (its `Line*` functions read width from the bitmap
itself). Not reachable today (every caller — tests and the TX fixture generator — already passes a
correctly-sized image), but a real landmine once real image input (crop/resize UI, arbitrary file load)
lands in a later phase. Fix is one guard at `EncodeAsync`'s entry, not a math change.

Already-tracked nits re-confirmed at the seam, no new severity: AVT's 3-sample DC-hold vs legacy's true
zero on its training tail (NICE-TO-HAVE 18 — one untested hypothesis noted: this sits at exactly the
preamble→first-body-line boundary, the same region S31, already fixed, used to fail at; not investigated
further, S31 itself is closed); no TX BPF/`m_outgain` stage (NICE-TO-HAVE 20); footer trailing-carrier
unit mix assuming `m_TxSampOff==0` (NICE-TO-HAVE 22).

#### Phase 3 summary

**Findings, prioritized:**
- **MUST**: MUST 4 (RX line-cursor rounding) — the only chain-level bug found; everything else Phase 1-2
  already covers is fixed.
- **SHOULD** (new, added to the existing Phase 1-2 SHOULD list): TX dimension-contract guard; RX
  event-scheduler contract (`LineDecoded`/`DecodeRestarted`/`ModeDetected`); RX `LineDecoded` live-alias
  hazard.
- **NICE-TO-HAVE** (new): the two AFC-boundary nits above.

Not yet fixed — reported for prioritization, per the user's own standing "document everything so nothing
is lost" preference. 512/512 tests still pass (nothing here is caught by any existing test, per each
finding's own "why the tests don't catch it" note above).

### MUST 4 — RX per-line cursor rounding, DONE

Same discipline as MUST fixes 1-3: dedicated regression test written first, confirmed to FAIL on the
pre-fix code with the predicted magnitude, then the fix implemented, re-confirmed, code-reviewed.

**Test** (`LineCursorRoundingTests.cs`, new file): encodes a Robot 72 image (largest predicted per-line
rounding error among the fixture modes, ~0.50 samples/line at 11025Hz) with a hard vertical step edge on
the Y channel at the same column in every row, decodes it, and compares the detected step column at an
early line (2) against the last line (239). Robot 72's Y channel was chosen specifically because it's
the FIRST scan segment in its line shape, isolating this bug from MUST fix 3's already-fixed within-line
effect. Pre-fix (confirmed by actually reverting the fix and re-running): early=161, last=136 — a 25px
drift, matching the ~25px prediction (0.50 samples/line × 239 lines / 4.756 samples/px) almost exactly.
Post-fix: early=161, last=161 — drift eliminated, both within the same ~1px settling noise as
`PixelPitchSegmentBoundaryTests`' own established floor.

**Fix**: new `_idealLineStartSample` double field in `AnalogFmSstvDecoder.cs`, advanced by the unrounded
`_effectiveSamplesPerLine` every line (mirrors MUST fix 3's own `segmentStartSample`/`pixelWalk` split,
one level up). Each line's `nextLineStartSample = round(_idealLineStartSample + _effectiveSamplesPerLine)`
is computed fresh from the running double total — never by re-rounding and accumulating a fixed per-line
step — and `_consumedSamples` (still an `int`, still load-bearing for every other cursor/watermark in the
file) is set to that rounded value rather than incremented by a fixed `lineSampleCount`.
`_idealLineStartSample` is resynced to `_consumedSamples` at every other site that assigns it directly
(`Commit`, `TryResolveSyncAnchorCorrection`, `EndOfImage`'s dead-time skip) so it never drifts across an
image boundary — only during the per-line loop's own fractional accumulation.

**Golden-vector re-measurement** (`GoldenVectorTests.cs`, zero-tolerance technique): RX decode-vs-source
deltas improved most on exactly the modes predicted to have the largest per-line rounding error — robot-36
16.19→5.04, robot-72 14.57→4.41, rm8 13.76→4.17 (rm8's improvement is itself confirmatory: MUST fix 3
couldn't touch it, since `MonoAveragedPairedScanlineDecoder` has only one scan segment per line, but MUST
4 lives in the shared per-line loop, so it improves rm8 just as much — proving these are two genuinely
different bugs, not one being re-measured). Smaller mixed changes on modes with tiny predicted error, same
accepted-tradeoff category as prior fixes: martin-m1 0.44→0.77 (worsened slightly), scottie-s1 1.92→0.45,
pd90 0.96→0.93, mn110 2.39→1.97 (all improved), avt 5.78→6.74 (worsened slightly, code-review confirmed
AVT uses the identical legacy boundary rule so this is expected variance, not a missed case). TX-direction
deltas (`LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage`) are UNCHANGED, exactly as predicted —
this fix only touches the RX decoder's cursor, not the encoder.

**Code-level review**: verdict EQUIVALENT. Confirmed against the real current file (not just the
snippet): all 5 `_consumedSamples`-writing sites covered (the narrow-mode-header site resyncs
transitively via its own immediately-following `Commit()` call); same-line `_effectiveSamplesPerLine`
correctly used for both the boundary computation and the accumulator advance (Auto Slant's own mutation
runs strictly after); `lineSampleCount`'s new varying-by-±1 definition has no consumer that assumed a
fixed per-line constant; double-accumulation drift over a full image is ~1e-7 samples, six orders of
magnitude below the bug being fixed. Two cheap nits fixed in this pass (both doc-only): the field's own
comment now notes the ROUNDED cursor is `Math.Round` (round-half-to-even) vs legacy's effective ceiling
— a small, uniform, non-compounding ~0.1px bias absorbed by `SyncAnchorCorrector`, unlike the compounding
drift this fix removes — and now explicitly notes the narrow-mode-header site's transitive coverage via
Commit(). Not fixed (both truly zero-impact, left as documented, not code changes): `Math.Round`'s
banker's-rounding vs legacy's consistent ceiling (bounded to 1 sample, non-compounding either way,
consistent with `Math.Round` usage elsewhere in the file).

Test count: 513/513 (512 prior + 1 in `LineCursorRoundingTests.cs`), solution-wide build clean.

**All 4 confirmed MUST bugs from the milestone audit (3 from Phase 1-2, 1 from Phase 3) are now fixed.**
Remaining open: the 3 new SHOULD-level landmines from Phase 3 (TX dimension-contract guard, RX
event-scheduler contract, RX `LineDecoded` live-alias) plus the pre-existing SHOULD/COULD/NICE-TO-HAVE
backlog from Phase 1-2 — none reachable without a live caller/UI, none urgent.

## Working the SHOULD backlog — 2026-08-04

User: "take the shoulds." Working through all 13 open SHOULD items (10 from Phase 1-2, 3 from Phase 3),
triaged by effort. Doc-only batch (event-scheduler contract, `LineDecoded` live-alias, finding 13 status)
already done above. This section covers the real code fixes.

### TX image/mode dimension-contract guard (Phase 3 SHOULD) — DONE

`AnalogFmSstvEncoder.EncodeAsync` never validated an image's dimensions against the mode before
encoding. Split into a public non-iterator `EncodeAsync` (validates `image.Width/Height` against
`mode.ImageWidth/Height`, throws `ArgumentException` if mismatched) delegating to a private
`EncodeAsyncCore` iterator (the original body, unchanged) — a plain iterator method's body doesn't run
until the first `MoveNextAsync`, so the guard needed to move outside the iterator to throw synchronously
at the `EncodeAsync()` call site, not merely on first enumeration. `ISstvEncoder`'s own XML doc now states
the throw-on-mismatch contract for any future implementer.

New `AnalogFmSstvEncoderInputValidationTests.cs` (4 tests): too-small image throws immediately (checked
via `Assert.Throws` on the un-enumerated call, not `ThrowsAsync`), too-large image throws immediately,
a width-only mismatch throws (code-review finding: the first two tests varied both axes together),
correctly-sized image doesn't throw and produces samples.

Code-level review: verdict PASS-WITH-RISKS. Confirmed no interface break (only implementer), exact-match
is the right rule (no `*ScanlineEncoder.cs` ever legitimately reads outside the mode canvas), exception
type/param convention matches the codebase's own sibling usage. One real risk flagged and independently
resolved: whether any existing golden-vector fixture `.bmp` might not exactly match its mode's canvas
size (which would make this guard newly throw where the old code silently cropped) — checked directly
(`file` on all 8 fixture bmps): every one is byte-for-byte exactly its mode's `ImageWidth x ImageHeight`,
confirmed safe. Two cheap nits fixed (doc comment on the interface, the width-only test case); one nit
left undone (paired-encoder height-not-divisible-by-2 still unguarded — a future-mode-only gap, not
reachable by any mode this port currently defines).

### Robot 36 tone-selector read-point hardening (SHOULD item 12) — DONE

**Round-1 finding caught a real mistake before it shipped**: the first version of this fix backed the
tone-selector's read point off from the segment's exact last sample using an INVENTED
`SettlingMarginSamples = 6` constant, on the assumption that legacy has no narrower "decisive window" to
port instead. Independently re-verified directly against source and found this assumption wrong: legacy
(`sstv.cpp:664-665`, case smR36) sets real `m_SG`/`m_CG` constants — `m_CG` sits exactly 1.0ms before the
tone segment's own nominal end — and `Main.cpp:4286-4297`'s RX switch only re-decides `m_DSEL` for
`ps ∈ [m_SG, m_CG)`, freezing at whatever it was once `ps` reaches `m_CG`. This port's pre-fix read
(`endSample - 1`) was reading a full ~1.0ms (~11 samples at 11025Hz) LATER than legacy's own real last
decision — a genuine fidelity gap, not a theoretical contamination worry, and squarely inside the
following 1900Hz porch's own settling zone (1900Hz being exactly the ambiguity midpoint).

Rewrote using the real legacy constant: new `DecisiveWindowTailMarginMs = 1.0` (traced directly to
`sstv.cpp:664-665`'s derivation, replacing the invented margin), read point now
`endSample - 1 - round(1.0ms in samples)`. Removed the old version's lower clamp against the segment
start (round-1 review's own nit: unreachable, and its fallback would have been wrong if ever reached) —
confirmed unreachable at every sample rate this port supports (the margin would need to be within ~1
sample of the segment's full 4.5ms duration).

Round-2 code-level review: verdict PASS, both round-1 risks confirmed resolved. Two cheap nits fixed:
a doc-comment precision correction (the read lands ~0.1 sample before `m_CG`'s exact boundary, not
exactly on it — deliberate, trades a hair of precision for robustness against `Math.Round`'s worst-case
direction, not an off-by-one) and a note on a latent legacy inconsistency (`sstv.cpp:665` computes `m_CG`
from the bare/nominal `SampFreq`, not the slant-corrected `m_SampFreq` every neighboring line in the same
switch uses — looks like a legacy typo, deliberately not reproduced, flagged so a future reader doesn't
"fix" this port toward it).

Golden-vector re-measurement (zero-tolerance technique): robot-36 RX decode-vs-source and
self-encode-vs-real-audio-decode deltas both UNCHANGED (5.04, 4.79) — expected, not a null result: on
this clean synthetic fixture the tone is fully decisive across its whole span, so old and new read points
land on the same side of the ±200Hz threshold either way. This fix only changes behavior when the OLD
read point was contaminated toward the porch — noisy/real signals or active slant correction, which this
particular fixture doesn't exercise. Confirmed via code review, not assumed.

Test count: 517/517 (513 prior + 4 in `AnalogFmSstvEncoderInputValidationTests.cs`), solution-wide build
clean.

### Luma `Limit256` clamp added to 3 RX decoders (SHOULD item 11) — DONE

Traced legacy's real per-CHANNEL clamp pattern directly (`Main.cpp:4275-4430`) rather than assuming a
uniform "clamp every luma channel" rule -- the actual rule is per-channel, not per-family, and includes
one genuinely surprising asymmetry:

- Robot36 (`Main.cpp:4275-4297`): Y clamped (`Limit256`, line 4282); chroma (R-Y/B-Y via tone-select)
  NOT clamped (line 4304, raw `short(d)`).
- Robot72/MR/ML family (`Main.cpp:4316-4366`): Y clamped (line 4332); R-Y/B-Y NOT clamped
  (lines 4342/4351).
- PD/MP/MN family (`Main.cpp:4381-4430`): Y1 clamped (line 4387); R-Y/B-Y NOT clamped
  (lines 4396-4397/4405-4406); **Y2 -- a SECOND luma read via the identical `GetPictureLevel` peak-pick
  path Y1 uses -- is NOT clamped** (`Main.cpp:4420-4422` feeds straight into `YCtoRGB` with no
  `Limit256` call at all). Confirmed by code-level review as a real legacy asymmetry, not a misread --
  worth noting since it's the single easiest part of this fix to get wrong (the intuitive assumption is
  "both luma segments alike," which peak-picking IS but clamping is NOT).

Threaded a `clamp: bool` through each decoder's existing per-channel dispatch (the channel switch in
`YCbCrSequentialScanlineDecoder.cs`/`YCbCrLinePairedScanlineDecoder.cs`, a new parameter on
`RobotScanlineDecoder.cs`'s shared `DecodePixels` helper), applying `Math.Clamp(value, 0, 255)` only
where legacy does.

New `Limit256ClampTests.cs` (3 tests, one per decoder): drives each decoder directly with a constant
out-of-band frequency (2700Hz, 400Hz past `LuminanceMaxHz`, raw pre-clamp value 384) via a scripted
`PixelSampleReader` stub, and asserts the clamped channel(s) land at 255 while unclamped channel(s) keep
the raw 384 -- including a dedicated Y1-vs-Y2 assertion. Confirmed to discriminate the bug: temporarily
reverted the clamp in all three source files, re-ran, all 3 failed with the exact predicted unclamped
values (74 vs 255, 120 vs 0, etc.), restored, re-confirmed all 3 pass.

Code-level review: verdict PASS. Y1-vs-Y2 asymmetry independently re-confirmed against source. One real
caution flagged, addressed directly rather than dismissed: the port's clamp boundary (`value` reaches
256 exactly at `LuminanceMaxHz`) is not strictly a no-op for all in-band input -- `ReadPeakPicked`'s
own "larger of two samples" bias could in principle read slightly over the nominal band on near-white
content and clip a couple of levels. This is legacy-faithful (legacy's own `Limit256` clips the exact
same peak-picked overshoot), not a port-introduced divergence, and the full suite (520/520, including
the robot-36/robot-72/pd90/mn110 real-legacy-capture golden vectors, all unchanged tolerances) confirms
no real fixture actually triggers it.

Test count: 520/520 (517 prior + 3 in `Limit256ClampTests.cs`), solution-wide build clean.

### Narrow-FSK suspended during AVT training window (SHOULD item 7) — DONE

Confirmed directly against source: legacy's `DecodeFSK` (`sstv.cpp:1858`) runs UNCONDITIONALLY every
sample, including throughout AVT training -- called before the `if(!m_Sync||...)` block AVT's own
case-3-8 state machine lives inside (`:1889`). Its narrow-packet-completion handler (`:2589-2593`)
commits to the narrow mode whenever `(m_SyncRestart || !m_Sync) && m_NextMode && (m_SyncMode >= 0)`.
Independently traced every real `m_SyncMode` assignment during AVT training (round-2-review correction
of an earlier, imprecise citation): the real values are 4/5/6/7/8 (`:2161/2173/2180/2202/2208/2212/
2217/2224/2230/2235`) plus the 256 timeout sentinel (`:2157/2167/2185`) -- all `>= 0`. `m_Sync` stays
false throughout training (the only `m_Sync = 1` assignment anywhere in legacy is `Start()`, `:1743`).
So a valid narrow packet found DURING AVT training genuinely aborts it in legacy. This port's
`TryDecodeHeader` used to short-circuit straight into `TryResolveAvtTraining` while
`_avtTrainingPending`, silently missing any such packet.

**Real design correction found during development, not just a code-review nit**: the first working
version checked `TryNarrowFskScan` once in `TryDecodeHeader` itself (bounded to
`_avtTrainingFallbackDeadlineSample`) before ever calling `TryResolveAvtTraining`. This failed the new
end-to-end test: a bulk single `PushSamples` call lets `TryResolveAvtTraining`'s own while loop consume
the ENTIRE buffer in one shot on the FIRST call (`TotalSamplesReceived` is already the whole buffer), so
`TryDecodeHeader` never got a "second chance" call while `_avtTrainingPending` was already true to check
data that arrived in the SAME push -- the exact "bulk vs. streaming ordering" bug class MUST fix 2 fixed
elsewhere. Fixed by moving the check INSIDE `TryResolveAvtTraining`'s own per-sample loop, interleaved
exactly like `TryInterleavedHeaderScan`'s own already-established lockstep pattern: an initial catch-up
call (`TryNarrowFskScan(Math.Min(TotalSamplesReceived, _avtTrainingProcessedUpTo))`) before the loop,
then `TryNarrowFskScan(_avtTrainingProcessedUpTo + 1)` at the top of every iteration, checked BEFORE
that iteration's own AVT training step (matching legacy's real per-sample order, DecodeFSK before the
sync-mode switch). On a match, explicitly clears `_avtTrainingPending`/`_avtTrainingLock`/
`_avtPllDemodulator` (matching `EndOfImage`'s own reset list for these same 3 fields), since `Commit()`'s
own teardown (`AbandonInProgressImage`) doesn't touch AVT-training-specific state.

New `NarrowFskDuringAvtTrainingTests.cs`: encodes a real AVT image, takes a 4.5s prefix (inside AVT's
own ~2.73s-8.04s training window), appends a FULL real MN110 transmission immediately after, pushes the
combined buffer, asserts the decoder locks onto MN110 (not AVT) with zero restarts (a first-ever lock,
not a restart -- AVT training never committed to `_mode`). Confirmed to discriminate: reverted just this
fix (`git stash` on the one changed file), re-ran, decoder locked onto "avt" instead of "mn110" as
predicted, restored, re-confirmed passing. A header-only splice was tried first and found insufficient
-- `Commit()` defers `ModeDetected` for non-AVT modes until `TryResolveSyncAnchorCorrection` succeeds,
which needs several lines' worth of buffered samples PAST the anchor; the full image provides that.

Code-level review: verdict PASS. Independently re-verified the entire `m_SyncMode`/`m_Sync` legacy
premise line-by-line against source (confirmed correct) and every interleaving/field-reset/trim-safety
detail (all clean). Two cheap doc nits fixed: the `m_SyncMode` value list corrected (6/7 were missing,
512 was wrongly included -- it's `Stop()`'s value, not a training one) in both the fix's own comment and
the test's XML doc; a stale comment claiming `_narrowFskProcessedUpTo` pauses for the whole training
window was corrected (no longer true -- it now advances throughout training via the interleaving this
fix adds). One low risk noted and accepted: the persistent `_narrowFskDecoder` now sees AVT audio it
never did before (more legacy-faithful, since `DecodeFSK` is unconditional in legacy too) -- covered by
the existing `AvtTrainingLockDecoderTests`/`AvtNoiseTolerantDetectionTests` (both bulk-push full AVT
transmissions and assert `detectedMode.Id == "avt"`, which a false narrow-positive during training would
flip outright), all still passing.

Test count: 521/521 (520 prior + 1 in `NarrowFskDuringAvtTrainingTests.cs`), solution-wide build clean.

### TryInterleavedHeaderScan's entry invariant (SHOULD item 9) — documented, no behavior change

`AbandonInProgressImage()` is a second way `_mode` becomes null without going through `EndOfImage()`
(which normally re-syncs `_syncBypassProcessedUpTo`/`_visLockProcessedUpTo` before
`TryInterleavedHeaderScan` -- the pre-lock scanner gated on `_mode is null` -- would run again). This
method doesn't do that resync. Currently safe by REACHABILITY, not by construction: both existing call
sites (`Commit()`, which immediately re-assigns `_mode`; and S7's AVT-training-abandon path, which
leaves `_avtTrainingPending` true, short-circuiting `TryDecodeHeader` past `TryInterleavedHeaderScan`
until a guaranteed later `Commit()`) never actually let `TryInterleavedHeaderScan` observe the unsynced
cursors this method leaves behind. Added a precise doc comment on `AbandonInProgressImage()` itself
explaining this coupling explicitly, so a FUTURE call site that abandons an image without either
committing a new one or entering AVT training doesn't silently break it. No code/behavior change --
both existing call sites are exhaustively safe today, and this method doesn't need a resync neither of
its current callers requires.

### Pre-lock watermark strict inequality (SHOULD item 8) — DONE

**Self-correction, worth recording**: this item was first assessed (like items 6/10) as needing "an
explicit floor clamp threaded through consistently, not a one-line change" and deferred. Re-examined the
same day after the user asked "are 6/8/10 not fixes, or just need more research?" -- reconsidering
`FilteredRawSampleAt`'s own ternary (`index > 0 ? (...reads index-1...) : _rawSamples[Rel(index)] * 0.5`)
showed the earlier conclusion was too conservative: the problematic `index-1` read only happens for
`index >= 1`, so wrapping the subtraction in `Math.Max(0, ...)` rather than using a bare `- 1` gives
EXACTLY today's own value (0) at the one boundary case that worried the original assessment
(`_bandpassFilteredProcessedUpTo == 0`), and exactly one less everywhere else -- a genuinely safe,
minimal, one-line-per-site change after all.

Both `TrimBuffers` sites (pre-lock and locked branches) changed from
`Math.Min(watermark, _bandpassFilteredProcessedUpTo)` to
`Math.Min(watermark, Math.Max(0, _bandpassFilteredProcessedUpTo - 1))`. Updated the adjacent
load-bearing-invariant comment (the one `DemodulatedFrequencyAt(watermark - 1)`'s own catch-up call
relies on, "watermark is ALWAYS <= _bandpassFilteredProcessedUpTo") to note the tightened bound only
strengthens that invariant, never weakens it.

No dedicated test added: the condition is currently unreachable (other retention margins already keep
this term from ever being the chain's own minimum), so there's no observable behavior to write a
discriminating test against -- relied on the mathematical proof (documented in the fix's own comment)
plus the full suite staying green (521/521, unchanged count and unchanged pass) as the correctness
signal instead.

Code-level review: verdict PASS. Independently re-derived the arithmetic identity (`Math.Max(0, X-1)`
equals today's `X` at `X==0`, equals `X-1` for `X>=1`), confirmed the crash-prevention claim holds at
both the `X==0` boundary and every `X>=1` case, confirmed the load-bearing invariant is strengthened not
weakened (tightening one term in a `Math.Min` chain can only make the chain's own result smaller-or-equal,
never larger), confirmed no other reader of `_bandpassFilteredProcessedUpTo` assumed the old
non-strict relationship, and confirmed strict monotonicity (`_bufferBase` can only ever retain
equal-or-more data than before, never less -- the safe direction). One doc-accuracy nit fixed: the
fix's own comment overstated which specific downstream call would have broken under a bare `-1` at the
`X==0` boundary (an earlier clamp+early-return already absorbs it) -- `Math.Max(0, ...)` is still the
right choice for being locally self-evident, just the originally-stated failure mode wasn't the real one.

Test count: unchanged at 521/521 (no new tests -- see "No dedicated test added" above), solution-wide
build clean.

### Items 6, 10 — assessed, deferred (not fixed)

Both looked cheap on first read; each turned out to need either a real architectural change (item 6) or
carried a genuine regression risk once traced through (item 10) -- disproportionate to their own
[C]/[B] severity and currently-unreachable/bounded status. Documented precisely instead of fixed, so the
investigation isn't lost and isn't silently re-discovered later.

**Item 6 (mid-image narrow restart stale demod-cache config)**: `BandpassFilteredSampleAt`/
`DemodulatedFrequencyAt` are forward-fill caches that freeze each index's `useLocked`/`isNarrow` gate
decision AT COMPUTE TIME, never revisited. When a non-narrow locked image is abandoned mid-image for a
narrow-mode commit (S8), a mid-image narrow-FSK interrupt's own anchor can land BEHIND where these
caches already advanced to (the just-decoded line's own pixel reads already drove them forward,
~1 line's worth) -- so the new narrow image's own first few samples read values computed under the OLD
mode's filter config. Investigated whether this is even a real port-vs-legacy divergence (legacy's own
real-time single-pass filters can't retroactively reprocess a sample either, once fed) -- concluded it
genuinely is: legacy is NEVER "ahead" of real time, so it never creates this situation in the first
place, while this port's lazy forward-fill caching (needed to support bulk-push callers) can race ahead
of the logical decode position, creating a staleness window with no legacy equivalent. A correct fix
needs retroactive cache invalidation AND a filter-state rewind/checkpoint for `SearchBandpassFilter`/
`HilbertFmDemodulator` (both single-delay-line, coefficient-swap-only filters with no snapshot/restore
mechanism today, confirmed by reading both classes) -- a real architectural addition, disproportionate
to a bounded ~1-line, [C]-severity finding. Deferred, not chased further this pass.

**Item 10 (`PixelSampleReader`'s `Math.Clamp` vs `Rel()`'s throw)**: real inconsistency (one component
silently substitutes a boundary sample, the other throws loudly, for what's structurally the same
"read behind the trim watermark" condition) but changing the clamp to a throw is a genuine behavior
change with an unclear benefit -- "safe today" per the existing comment (locked watermark stays 2000
samples of margin back), and a new throw path risks surfacing in production differently than the silent
substitution would, for a component (`PixelSampleReader`) that's the single highest-consequence reader
in the file (the one that actually writes pixels). Left as documented, not changed -- flagged as a real
inconsistency worth a future look if this margin's own assumptions ever change, not fixed reactively
without a concrete failure to design against.

## TX-side SHOULD cluster (items 4, 5) — DONE, in an isolated fork worktree

User approved running two pipelines in parallel: a `fork` (isolated git worktree) handling the
TX-side SHOULD items (4: frequency-mapping truncation, 5: OutHEAD leader-tone port), while the main
session continued the RX-orchestrator cluster (6, 7, 9, boundary-hardening 8/10) directly in the main
working tree. This section covers the fork's own work; the RX-side cluster's own entry lives
separately (main branch). Same test+review discipline as every other fix this session, run
independently in this worktree.

### SHOULD item 4 — TX frequency-mapping integer truncation — DONE

Legacy's real pixel-to-frequency TX chain truncates TWICE via integer arithmetic: `GetRY`
(`ComLib.cpp:3653-3668`) assigns a `double` RHS into `int&` out-parameters (truncates toward zero,
confirmed always non-negative for real 8-bit RGB input so `Math.Floor` is the correct C# equivalent),
then `ColorToFreq`/`ColorToFreqNarrow` (`ComLib.cpp:3491-3501`) does `d*(max-min)/256` using INTEGER
division. A THIRD, family-specific truncation was found by reading `TMmsstv::LineRM` directly
(`Main.cpp:6796-6799`): RM8/RM12's luma averaging (`YY = (YY + Y[x]) / 2`) is also integer division.
This port mapped in unrounded doubles end to end -- a systematic ~0-4Hz one-sided bias on every
transmitted pixel.

Added `YCbCr.FromRgb`'s own `Math.Floor`+`Math.Clamp` (matching `GetRY`+`LimitRGB`'s exact order) and
a new shared `YCbCr.ColorToFreq(colorValue, luminanceMinHz, luminanceMaxHz)` (`Math.Floor` after the
multiply-divide, proven bit-exact to C++ integer division: the multiply is an exact integer product
under 2^53, the divide is by a power of two). Wired into all 5 `*ScanlineEncoder.cs` files, replacing
each one's own inline unrounded formula; `MonoAveragedPairedScanlineEncoder.cs` also got the RM8/RM12-
specific integer-division averaging fix.

New `YCbCrColorToFreqTruncationTests.cs`: exhaustive sweep (every integer 0-255, both bands this port
defines) confirming bit-exact match against an independent from-scratch reimplementation of legacy's
real integer division; confirmed to discriminate (temporarily reverted to floating-point division,
re-ran, confirmed failure at 1503 vs 1503.125, restored). `YCbCrTests.cs`'s existing round-trip
tolerance widened from +/-1 to +/-4 -- measured via a 2,000,000-sample random sweep, not guessed
(expected and legacy-faithful: legacy's own real TX/RX round-trip is lossy by this same chain).

Code-level review (round 1): EQUIVALENT-WITH-RISKS. Two real risks, both fixed: (1) `YCbCr.FromRgb`'s
C# operation grouping (`16 + a*r + b*g + c*b`, left-to-right) differed from legacy's exact grouping
(`16.0 + (a*R + b*G + c*B)`, weighted terms summed first) -- floating-point addition isn't
associative, and every R=G=B gray level's exact chroma value is precisely 128 (the weight
coefficients sum to exactly zero), i.e. exactly on the truncation boundary, so the two groupings could
land different gray levels on different sides of it. Reparenthesized to match legacy exactly. (2) A
misleading golden-vector comment claiming the TX-direction real-legacy-decode test was re-measured and
found unchanged "because a sub-3Hz shift is below one quantization level" -- WRONG: that test reads
checked-in files from disk and never invokes the encoder at all, so "unchanged" was a tautology, not
evidence. Corrected to disclose the real gap: TX-vs-real-legacy validation of this fix doesn't exist
yet, needs a fresh `TxCapture/` re-capture (out of scope, needs the user's own real legacy install).
Two doc-comment nits also fixed (multiply-exactness under-specified; "+/-4 gives margin" corrected to
"+/-4 equals the exact measured max, not a margin").

Golden-vector re-measurement (the one test in `GoldenVectorTests.cs` that live-encodes): all 8 modes
moved (martin-m1 1.29->1.34, robot-36 4.79->4.16, scottie-s1 0.48->0.37, robot-72 4.64->4.13, pd90
1.58->0.18, rm8 3.43->3.80, mn110 1.10->0.14, avt 9.65->9.87), mostly improved, a few worsened
slightly (accepted-tradeoff category, same as every other fix this session), all comfortably inside
existing tolerances.

### SHOULD item 5 — OutHEAD pre-VIS leader-tone burst — DONE

Legacy (`Main.cpp:7270-7292`, `TMmsstv::OutHEAD`) emits a leader-tone burst UNCONDITIONALLY at the
very start of every real transmission (`Main.cpp:7393`, called before the VIS/narrow-FSK header block)
at the shipped `sys.m_VOX==0` default -- narrow: 1900,2300,1900,2300 (400ms); normal:
1900,1500,1900,1500,2300,1500,2300,1500 (800ms), all 100ms/tone. This port's TX encoder never emitted
it at all -- a real missing TX segment, no `docs/removed-features.md` entry, same class of gap S27's
CQ100 omission was before it got fixed. AVT gets the SAME 800ms non-narrow burst as every other
non-narrow mode, not a special AVT-only header -- confirmed directly against source (AVT's own
3x-VIS-repeat logic, `Main.cpp:7429`, lives INSIDE the later non-narrow branch OutHEAD precedes).

Added `VisHeader.GenerateOutHeadSegments(bool narrow)` plus 3 new named constants
(`OutHeadToneDurationMs`/`OutHeadNarrowDurationMs`/`OutHeadNormalDurationMs`), wired into
`AnalogFmSstvEncoder.GenerateFrequencySegments` as the very first segments emitted, before the
existing AVT/narrow/extended/normal branch.

New tests (`VisHeaderTests.cs`): two pure unit tests pin the exact tone sequences; a third
(theory, 3 cases: robot-36/avt/mn110) drives the REAL encoder end to end and measures the actual
generated audio's frequency at t=150ms via a zero-crossing-rate estimator -- this should land on
OutHEAD's own second tone (1500Hz normal, 2300Hz narrow), which is NOT what a no-OutHEAD encode would
produce at that timestamp (VIS's own leader is an unbroken 300ms of 1900Hz). Confirmed to discriminate
(temporarily removed the segment-emission wiring, all 3 cases failed measuring ~1894Hz, matching the
no-OutHEAD prediction almost exactly, restored).

Full-suite run surfaced 2 real consequences of the new 400-800ms of leading audio, both fixed:
`NarrowFskNoiseTolerantDetectionTests`' anchor-position test needed its `expectedAnchor` formula
updated to add the new leading burst; `SstvRoundTripTests`' AVT-specific tolerance needed raising
(10.0 -> 16.0, measured 11.21) since AVT's own already-documented-fragile training lock absorbs a
modest quality cost from the longer preamble (mode detection unaffected).

**Code-level review (round 1): EQUIVALENT-WITH-RISKS, one real finding fixed properly (not just
patched around) rather than dismissed.** 5 test files (`SyncBypassDetectionTests.cs`,
`SyncBypass1DetectionTests.cs`, `SyncScanInterleaveTests.cs`, `PiecesSixCReachabilityTests.cs`,
`SyncBypassNarrowDetectionTests.cs`) strip a fixed header-duration offset from live-encoded audio to
reach a "headerless" body, specifically to exercise the sync-interval-bypass path (`m_sint1`/
`m_sint2`/`m_sint3`). None of their skip formulas included the new OutHEAD term -- post-fix, every one
was 400-800ms short, meaning they were silently locking via the REAL VIS header path instead of the
bypass path they exist to test, while every assertion still passed (a VIS lock is more accurate than a
bypass lock, so nothing looked wrong from outside -- the exact "round-trip passes while both halves
agree on something wrong" shape CLAUDE.md's own Scottie incident warns about, just for a test fixture
instead of production code). Fixed all 5 by adding the missing `OutHeadNormalDurationMs`/
`OutHeadNarrowDurationMs` term. Re-measuring afterward surfaced something genuinely interesting: the
OLD documented deltas for these files (SyncBypassDetectionTests: 19.01/12.65/25.13/13.85;
SyncBypass1DetectionTests: 39.03; SyncBypassNarrowDetectionTests: 24.27/21.17/19.62/16.38/14.29/11.42)
were themselves measuring VIS-lock accuracy, not real sync-bypass accuracy -- with the strip offset
now correct and the bypass path genuinely engaged, freshly measured values are all SMALLER, not
larger (SyncBypassDetectionTests: 3.48/4.93/6.91/4.86, tolerance 29.0->14.0;
SyncBypass1DetectionTests: 6.63, tolerance 43.0->10.0; SyncBypassNarrowDetectionTests:
13.76/13.20/13.04/9.19/8.84/8.29, tolerance 25.0->18.0) -- a genuine sync-bypass anchor turns out to
be MORE precise than what the old (contaminated) numbers were ever actually measuring.

A second finding was a weakly-supported explanation, not a wrong assertion: an early comment
attributed AVT's own tolerance increase to "AGC/level-detection settling," which round-1 review
found implausible (AVT already carries ~10s of its own preamble before line 0, so its AGC is long
converged either way; its training PLL is documented elsewhere as amplitude-scale-invariant) and
proposed an alternative (unverified) mechanism instead. Corrected to present both as open hypotheses,
explicitly noting no instrumented measurement was taken to settle it -- round-2 review additionally
caught that the alternative "whole-VIS-repeat-block-shift" hypothesis doesn't even fully fit either,
since AVT's numbers moved in OPPOSITE directions across two different tests for this same fix
(SstvRoundTripTests' self-round-trip worsened, GoldenVectorTests' self-vs-real-legacy-decode
improved) -- flagged as genuinely unresolved rather than smoothed over with a plausible-sounding story.

Round-2 review: PASS-WITH-RISKS, both round-1 findings confirmed resolved at the code level; residual
findings were all documentation staleness introduced BY the correction itself (two of the five fixed
test files' own comments still cited their old, pre-fix-contaminated numbers) -- all fixed the same
way, by actually re-measuring rather than just updating prose.

Test count: 527/527 (520 prior + 3 in `VisHeaderTests.cs`'s new theory + 2 in its two unit tests),
solution-wide build clean.

**Merged into master.** Before merging, two independent, isolated-context Opus auditor reviews (no
visibility into either side's own prior review reasoning) re-checked the full accumulated diff on each
side end to end -- RX-side (items 4/7/8/9/10/11/MUST 4) and TX-side (items 4/5) -- specifically
re-verifying code AND every comment/legacy-citation, not just re-running the per-item checklist. Both
reviews found only comment/citation staleness (stale "shared by BOTH" caller counts, a misleading
past-tense claim, an off-by-one `Main.cpp` line citation propagated across 4 sites, a dangling doc-
comment cross-reference, imprecise citation ranges, a residual-bias figure that was rate-dependent, an
unacknowledged Auto Slant interaction) -- no functional/behavioral findings on either side. All fixed;
both suites re-confirmed green before merge (RX 521/521, TX 527/527).

**Phase 1 status (2026-08-04): closed out, moving to Phase 2.** All 4 MUST bugs from the full 3-phase
milestone audit fixed; 11 of 13 SHOULD findings fixed or closed via documentation (items 6, 10
deliberately deferred, real reasoning recorded above); remaining COULD (14-16) and NICE-TO-HAVE (17-26)
items are open but low-urgency/cosmetic by their own original triage, same tier as the already-accepted
Band 5 precedent — none block moving on. Two independent, code-level comprehensive audits (RX diff, TX
diff, fresh Opus context each) ran on the full accumulated SHOULD-fix work immediately before this
close-out and found no functional issues.

**Architecture question resolved before Phase 2 code started**: whether `IRadioController`'s scope
should stay rigctld-client-only or also build in Hamlib directly (WSJT-X style). Resolved further than
originally framed — not just "add Hamlib alongside hand-written protocols," but **no hand-written
per-rig CAT protocols at all**. Scanline Studio is a pure client of external CAT backends (Hamlib linked
in-process, `rigctld`, flrig, OmniRig-as-client), full reasoning and backend list in
[[03-cat-layer]] (renamed from "CAT Layer" to "External CAT Backends"), removal accounting in
[docs/removed-features.md](../docs/removed-features.md)'s "Native per-rig CAT protocol implementations"
entry. `spec/02-radio-layer.md` and `spec/04-rigctld.md` updated to match (no `IRigRegistry`/
`RigDefinition`, `RadioConnectionSpec` subtypes now per-backend).

## Phase 2 — Radio layer (no CAT rigs yet) — DONE

Both roadmap items landed together: `ScanlineStudio.Abstractions.Radio` interfaces, `RadioController` (reference
`IRadioController`), and `RigctldClientProtocol`/`RigctldProtocolFactory` (client mode). 639/639 tests
pass solution-wide (528 pre-existing DSP + 111 new/other, none regressed — confirmed `git status` shows
nothing SSTV-related touched). Full detail below; this entry is the summary.

**Design settled via an auditor plan-review pass before any code was written** (not a mechanical build —
this is new architecture, not a port): `IRadioProtocol` dropped its `IRadioTransport` parameter (each
protocol owns its own transport internally, the only shape that also fits future call-based backends
like linked Hamlib/OmniRig — a real `spec/02-radio-layer.md` amendment, not just a code detail);
backend resolution via `IRadioProtocolFactory` with exactly-one-match required (never silent
first-match-wins); poll-loop error taxonomy splitting rigctld protocol errors (`RPRT -n`, no backoff,
keep polling) from transport failures (backoff + dispose/recreate the protocol via the factory, with an
overflow-safe clamp on the exponential formula); `\dump_caps` parsing rejected in favor of probing
`f`/`m`/`t` directly at connect (a `spec/04-rigctld.md` amendment) since `\dump_caps`'s grammar drifts
across Hamlib versions and couldn't be verified without a real instance at plan-review time.

**Hamlib cloned locally for reference** (`hamlib/`, gitignored, same convention as
`yoniq-old/YONIQ-main/`/`QSSTV-main/`) — resolved the plan-review's flagged highest-risk unknown
(whether rigctld's `f`/`m`/`t` get-commands emit a trailing `RPRT` line in backward-compatible mode) by
reading `tests/rigctl_parse.c` directly rather than guessing: they don't (only `set` commands and
errors get an `RPRT` line; `get` commands succeed with just their raw value line(s)). Also confirmed
Hamlib ships a hardware-free "Dummy" rig backend (`RIG_MODEL_DUMMY`, `port_type = RIG_PORT_NONE`) and
the exact real-Hamlib mode-token vocabulary (`src/misc.c`'s `mode_str[]`) used for `RadioMode` mapping.

**Real interop, not just fixtures** — the user's own suggestion mid-session: since a real `rigctld` +
Hamlib's Dummy rig backend exists, spin one up as a subprocess and drive this port's own
`TcpTransport`/`RigctldClientProtocol` against it over a real loopback socket, rather than trusting
fixture-replay tests alone (which only prove the parser matches bytes someone wrote down). Landed as
`RigctldDummyRigIntegrationTests` (4 tests) — best-effort, skips cleanly if `rigctld` isn't on PATH,
confirmed manually first (`rigctld -m 1` by hand) before writing the C# test. Real output matched the
source-derived prediction exactly on the first try, including the Dummy backend's genuinely-unsupported
`get_ptt` (`RPRT -11`), exercising the same capability-absence path the fixture tests cover separately.

**Two real implementation bugs caught by the test suite itself, not review**: (1) `ConnectAsync`'s
`Task.Run` lambda read the `_pollLoopCts` field at execution time instead of capturing its token before
scheduling — a fast concurrent `DisconnectAsync` (exactly what
`ConnectAsync_PublishesConnectingThenConnected...` does) could null the field before the lambda ran,
`NullReferenceException`. Fixed by capturing the token into a local before `Task.Run`. (2) A test
asserted `LastKnownState` *after* `DisconnectAsync`, which deliberately clears it back to null — a test
bug, not an implementation bug, fixed by asserting before disconnecting.

**Deferred, not forgotten**: `\chk_vfo`/VFO support (not needed by `RadioState`'s current domain model).
Server mode, flrig/OmniRig-as-client, `TemplateCatProtocol`, and all `ScanlineStudio.Application`/UI/settings-
persistence wiring are Phase 3/4 per the plan below, unaffected. (Linked Hamlib itself was originally
slated for Phase 4 too — done early instead, see the section immediately below.)

**Demo** (not yet built — Phase 3's job, wiring this into `ScanlineStudio.Application`/UI): `IRadioController`
connects to a real `rigctld` instance and reports live frequency/mode changes in a log/console.

## Linked Hamlib backend ("bring-your-own-libhamlib") — DONE, ahead of its original Phase 4 slot

Built directly after Phase 2 rather than waiting for Phase 4, since the packaging design question
(below) was the actual blocker on starting it, not calendar ordering.

**Packaging decision**: the user asked how to compile Hamlib into the app in-process, WSJT-X-style.
Investigated for real rather than assuming — WSJT-X's actual approach turned out to be a private Hamlib
fork, statically linked via a "superbuild" CMake step (one static binary, no runtime swap), which the
user then explicitly asked NOT to be used as the framing for the exploration. Ran a full `/adhd`
divergent-ideation pass (5 cognitive frames — regulator, logistics, remove-the-load-bearing-assumption,
3am-on-call, ant-colony — 30 raw ideas, clustered, top 3 deepened) followed by an Opus `auditor` review
of the 4 resulting candidates plus the researched WSJT-X precedent. Verdict: **"bring-your-own-
libhamlib"** — Scanline Studio never builds, forks, or vendors Hamlib at all; `ScanlineStudio.Core.Radio.Hamlib` P/Invokes
whatever `libhamlib` the user's OS/package manager already has installed, discovered at runtime,
version-gated to major-4, falling back to the already-working `rigctld` client on failure. Rejected
WSJT-X's static-fork approach specifically on cost/maintenance grounds for a solo hobby project with a
constrained CI-minutes budget and no unmerged Hamlib patches to justify carrying a fork — not on license
grounds (both static and dynamic linking satisfy LGPL here, since Scanline Studio's own source is fully public).
The auditor also found the C-struct-free surface meant no shim project was needed at all (unlike
[[05-audio-engine]]'s MiniAudio integration, which does need one).

**Two rounds of `auditor` plan-review before any code was written** (new architecture, not a port — same
discipline as Phase 2's own plan-review pass), each explicitly re-verifying the prior round's fixes
against real source rather than trusting them:
- **Round 1** found 4 real blockers: `hamlib_version2` is a `const char *` **data export**, not a
  function — P/Invoking it as a function delegate would have crashed the version probe itself (fixed:
  use `rig_version()` instead, confirmed present in the released-4.x export list); Hamlib error codes
  are negative and split into soft/hard via `RIG_IS_SOFT_ERRCODE` — a naive "nonzero = command-level"
  classification would have made a dead/unplugged rig spin `CommandFailed` forever instead of ever
  triggering `RadioController`'s reconnect (fixed: pinned to the macro's exact 11-member soft list);
  no thread was specified for the blocking native calls, which would have frozen the UI thread on a PTT
  keystroke (fixed: `SemaphoreSlim` for mutual exclusion + `Task.Run` for offload, documented as two
  separate concerns); and discovery/the version gate was being re-run on every `RadioController` backoff
  reconnect instead of cached once (fixed: `IHamlibRuntime` computes both eagerly in its constructor).
- **Round 2** re-verified all 4 fixes against source (all confirmed genuinely correct, not hand-waved)
  and found residue from the fixes themselves: a missing UTF-8 null terminator on the switched-off-
  default-marshaling string params, the semaphore-release-vs-uncancellable-native-call contract needed
  stating explicitly, the `RIG_MODE_*` table listed bit *positions* where "exact-value equality" needed
  bit *values* (`1UL << n`), and the connect sequence (`rig_init` → `rig_set_conf`×N → `rig_open`)
  needed pinning since two parts of the plan implied different orderings. Verdict: "Go," all four pinned
  on paper, no third round needed.
- **One more real design bug found later, writing tests** (not caught by either review round): the
  discovery-order locator's override-path handling contradicted its own spec text — implemented as a
  last-resort fallback (tried only after auto-detection failed), when the spec said an explicit user
  override should *win* over auto-detection. Fixed in both the spec and the locator before any test was
  written against the wrong behavior.

**Built**: `ScanlineStudio.Core.Radio.Hamlib` — `INativeLibraryLoader`/`NativeLibraryLoader`,
`HamlibLibraryLocator` (3-tier discovery, override tried exclusively when set),
`IHamlibNative`/`HamlibNative` (the frozen P/Invoke surface, `CLong` for `pbwidth_t`/`hamlib_token_t`,
explicit-UTF-8-plus-null-terminator string marshaling, `Cdecl` throughout), `HamlibVersionGate`,
`IHamlibRuntime`/`HamlibRuntime`/`IHamlibNativeFactory`, `HamlibRadioProtocol`, `HamlibProtocolFactory`.
`HamlibConnectionSpec` added to `ScanlineStudio.Abstractions`. Full design: `spec/03-cat-layer.md`'s "Linked
Hamlib: bring-your-own-libhamlib" section. Full plan with both review rounds' findings:
`/home/artien/.claude/plans/temporal-launching-valiant.md`.

**Tested**: 40 new tests in `ScanlineStudio.Core.Radio.Tests` — fixture/fake-driven unit tests covering
connect-sequence ordering, soft-vs-hard error classification (both branches), capability probing,
dispose safety, and a concurrency test proving the semaphore actually serializes overlapping native
calls, plus **4 real-interop tests against this dev machine's actual installed `libhamlib.so.4` (4.5.5)**
driving Hamlib's own hardware-free Dummy rig backend — confirmed genuinely executing (not skip-via-early-
return) via real ~120-165ms durations, verifying the whole discovery→version-gate→P/Invoke→marshaling
pipeline against genuine native code, not just fakes. Full solution: 678/678 passing (`ScanlineStudio.Core.Sstv.Tests`
528/528 unchanged, confirming no DSP regression). One pre-existing flake noted, not chased (ADHD-scope
one-liner): `RigctldDummyRigIntegrationTests.Capabilities_PttUnsupportedOnTheDummyRig_IsProbedCorrectly`
intermittently fails only under the full parallel test run — a subprocess-connection-wait timing race,
confirmed by two clean 100%-green runs with `xunit.parallelizeTestCollections=false`; pre-existing test
infrastructure fragility exposed by adding more concurrent real-process/real-native tests, not a defect
in the new Hamlib code.

**Explicitly deferred, not built this pass**: cross-backend auto-demotion to `rigctld` (no home for that
policy yet — `IRadioController` has no "try the next backend" concept, needs the
`ScanlineStudio.Application`/settings layer, which doesn't exist) and the Settings UI for the manual
library-override path (hard-coded as a constructor parameter for now).

## Phase 3 — Minimal UI, first end-to-end path — DONE

- [[09-ui]]: `MainWindow` walking skeleton — waterfall, RX image panel, basic TX button — wired to Phase 1/2 services through `ScanlineStudio.Application`. Shipped as 3 real Dock panes (Waterfall/RX Image/TX Controls) plus a fixed radio/frequency status strip in `MainWindow`'s own chrome (a deliberate scope trim, not a 4th dockable pane — nothing in this roadmap or [[09-ui]]'s dialog inventory calls for that).
- [[10-localization]]: `ILocalizationService` + `Translate` extension in place from the start (retrofitting localization onto an already-built UI is far more expensive than building it in from the first window). Done; `ja.json` and `.dfm`-mining are still open (no legacy dialog counterpart exists for these 3 new panes to mine strings from).
- [[07-image-pipeline]]: minimal `IReceivedImageBuffer`/basic TX image selection (crop/resize can follow in Phase 4) — shipped as `IImageFileLoader` (load + fit-to-mode resize only, no user-facing crop/resize tooling).

**Demo — actually run, not just claimed:** real `rigctld` + Hamlib Dummy rig for the radio half (the running app genuinely showed a live frequency in the status strip). For RX/TX audio, no GUI-automation tool was available in the sandbox this ran in, so the round trip was proven via two independent `ISstvSessionService` instances (mirroring two real app processes) over a real PipeWire virtual audio cable — real `MiniAudioEngine`, real `AnalogFmSstvEncoder`/`Decoder`: mode auto-detected, all lines received, PTT keyed correctly, received image pixel-identical to the source. Exercises the same `TransmitAsync` call path the real TX button invokes.

**Aesthetic note**: partway through, the visual direction was corrected to "raw, functional instrumentation" (cuSDR64/Perseus/SDR++ — no rounded corners, no gradients/shadows, 2-4px padding max, monospace numeric readouts, bordered module groups) — recorded as the durable directive in [[09-ui]]'s "Visual design direction" section, applied to everything already built. Waterfall visual richness was explicitly deprioritized relative to RX/TX image handling and templating — kept deliberately simple, revisit later.

Full build log, real bugs caught along the way (a settings-schema layering bug, an RX-image row-copy bug, an e2e-demo design contradiction), and file-level detail: see the session's own `PROJECT_BRIEF.md` history and `/home/artien/.claude/plans/hidden-conjuring-curry.md`.

## Phase 4 — Making the program usable: settings, options, dialogs, logbook

**Re-scoped 2026-08-05 (direct user decision)**: Phase 4's organizing theme is now "the program is
actually usable day to day," not just "CAT protocols + image tooling." Two changes from the original
plan: rigctld **server mode** is dropped outright (see [[04-rigctld]]'s "Purpose"/"Server mode" sections
— built-in linked Hamlib plus rigctld client-mode coverage is enough CAT surface; a server role wasn't
worth the added scope), and the settings/dialogs/localization-completion work originally bucketed under
Phase 5 ("Extensibility and polish") moves up into Phase 4, since a usable program needs its settings
UI and remaining dialogs before it needs a plugin system. Phase 5 is now just extensibility (see below).

- [[03-cat-layer]]: linked Hamlib backend — **done early** (see "Linked Hamlib backend" section above,
  landed right after Phase 2 instead of waiting for Phase 4). `TemplateCatProtocol` fallback still here.
- **Maybe later** (not committed, no code/design yet): flrig client backend — flrig has a real, still-actively-used user base distinct from plain Hamlib/rigctld users, worth adding if that demand shows up post-launch. OmniRig-as-client similarly deferred. Revisit once Hamlib/rigctld coverage is in and actual user requests make the priority call for real, rather than guessing now.
- [[07-image-pipeline]]: full crop/resize/overlay, stock library, RX history — **done** (`ITransmitImagePreparer`, `TxImageEditorPaneViewModel`/`TxImageEditorPaneView`, `IStockImageLibrary`, `IReceiveHistoryStore`; filter/preset support deferred to [[11-plugin-system]], Phase 5, not part of this interface).
- [[08-logging]]: logbook, ADIF import/export, offline callsign lookup; QRZ.com opt-in lookup can trail slightly if needed. Not started — the callsign/country lookup piece is additionally blocked on a human emailing Clublog for a `cty.dat` API key (see [LICENSES.md](../LICENSES.md)'s "Candidate future asset" note); the logbook/ADIF core doesn't depend on that and can proceed first.
- [[09-ui]]: remaining dialogs from the inventory table — `OptionsDialog` (tabbed general/TX/RX/audio settings), `RadioSettingsDialog`, `MacroKeyEditor`, `ColorSettingsDialog`, `LanguageSettingsDialog`. (Moved from Phase 5 — `PluginManagerDialog` stays in Phase 5, it has no purpose without the plugin host it's Phase 5's own primary deliverable.)
- [[12-settings]]: legacy `.ini` importer, migration chain exercised by a real version bump. (Moved from Phase 5.)
- [[10-localization]]: remaining views localized, community-translation-friendly locale-file workflow documented. (Moved from Phase 5.)
- ~~[[04-rigctld]]: server mode~~ — **dropped**, not deferred. See the re-scoping note above.

**Demo:** the program is fully usable day to day — configure settings/rig/macros/colors/language through
real dialogs, operate SSTV with a directly-connected rig, and keep a proper log. This is the point at
which YONIQ v2 first matches legacy MMSSTV/YONIQ's core day-to-day workflow, plus a real settings/options
experience legacy users would recognize.

## Phase 5 — Extensibility

**Trimmed 2026-08-05** to just the plugin system — everything else previously bucketed here (remaining
dialogs, settings migration, localization completion) moved into Phase 4 above.

- [[11-plugin-system]]: plugin host, at least one built-in extension point (`IImageFilter`) proven to load through the plugin path, plus `PluginManagerDialog` ([[09-ui]]'s inventory table). If [[15-template-designer]] work has started by this point, its `ITemplateItem` extension point (the documented successor to the legacy CItems DLL ABI) is the higher-value target to prove the plugin path against, since it's the one with real legacy prior art and potential third-party demand.

**Demo:** a user can extend the app with a plugin (at minimum, an image filter loading through the same
path a future `ITemplateItem` extension would use).

## Phase 4+ backlog — legacy YONIQ/QSSTV feature inventory (2026-08-05)

Four parallel research passes (logbook/QSO, TX macros + CW-ID, waterfall/color, RX/TX
quality-of-life), each verifying claims directly against `yoniq-old/YONIQ-main/` source
(QSSTV-main/` cross-checked only as secondary inspiration, never a port target) after the
Settings/Options/radio-telemetry system (Pieces 1-6 above) shipped. Not yet scheduled into a
phase or scoped into a build plan — a UI-design pass is happening first; this is a tracked
candidate list to pull from once that's further along, not a commitment.

**Logbook/QSO tracking** ([[08-logging]]'s plan already matches legacy reality on format/ADIF/
QRZ.com/cty.dat scope — these are the deltas found):
- QSL sent/received flags — legacy has them, `QsoRecord` doesn't. Trivial.
- Maidenhead grid locator — missing from legacy AND the current plan, but standard in modern
  ADIF and present in QSSTV's own field set. Trivial; matters for real ADIF interop with
  third-party loggers.
- Duplicate-QSO detection (by callsign, or callsign+band) — real legacy feature
  (`LogSet.cpp`/`LogFile.h`'s `m_CheckBand`), not in the current plan. Small.

**TX macros / CW-ID** (legacy's "macro" is a token-picker popup, not a saved template — shared
across the overlay editor, CW-ID text, and repeater auto-answer via one substitution function,
`MacroText`):
- Token-picker UI + substitution service — low-medium complexity, but the real blocker is that
  **no operator-callsign/profile setting exists yet** to substitute from (same root gap
  [[07-image-pipeline]] already flags for why overlay text is plain free-typed today).
- CW-ID (real, working legacy feature — not dead like Vari SSTV) — low-medium complexity, and
  can reuse `SstvSessionService`'s existing `TuneAsync`/`GenerateTone`/`PlayWithPttAsync` (built
  for the Tune button, Piece 5/6 above) rather than needing a new audio subsystem.
- The two only intersect at token substitution; buildable independently.

**Waterfall/color** (still plain grayscale — a prior, standing decision already deprioritized
this; the items below are a complete inventory, not a priority push):
- No color rendering at all vs. legacy's real 7-color palette (gradient low/high, FFT
  background/trace/peak-hold, sync marker, freq marker — confirmed the exact list from
  `Option.cpp`, NOT `ColorSet.cpp`/`ColorBar.cpp`, which are a different generic picker used by
  the TX title bar/overlay tool/CItems plugins, not the waterfall). Low.
- No separate FFT/scope trace view (legacy has one distinct from the waterfall image). Medium.
- No peak-hold/persistence overlay, no sync/frequency marker lines. Low-medium each.
- No zoom/bandwidth-range control (legacy: 3 discrete presets, no continuous zoom). Low-medium.
- No interactive notch-filter marker (click/drag to set, right-click to toggle) — legacy's
  version configures an audio notch filter, not RX retuning. Medium-high; depends on a notch
  filter DSP block that doesn't exist yet in `ScanlineStudio.Core.Sstv` (unverified, flagged only).
- No dedicated signal-strength meter, no legacy-style debug "digital scope" tool. Low-medium /
  medium-high respectively — the debug scope is probably the lowest-value item in this list.
- QSSTV does a nicer multi-hue heatmap gradient (vs. legacy's flat 2-color linear interpolation)
  and an adjustable dB range — worth a look if/when color rendering is built, not a legacy port
  requirement.

**RX/TX quality-of-life** (excludes the already-deliberately-dropped DSP tunables — PLL VCO
gain, zero-crossing params, RxBPF width, squelch level, calibration wizard, differentiator, LMS
filter — those are a known exclusion, not rediscovered here):
- ~~Manual "ReSync" button~~ — **done** (2026-08-07): the roadmap's own original framing ("applies
  an already-computed sync-skip correction") pointed at the wrong legacy feature — traced the real
  click handler (`TMmsstv::KRFSClick`, `Main.cpp:14004-14020`) and found `ReSyncSSTV` (the 32-line
  envelope fold this entry originally meant) is only ever called by two "high-precision sync" MENU
  items, never the button. The real button is much simpler: live per-line sync-peak tracking
  (`m_SyncPos`/`m_SyncRPos`) plus a forward-only sample skip (`m_Skip`), no fold/cache. New
  `AnalogFmSstvDecoder.RequestReSync()` → `PerformReSync()`/`DrainPendingSkip()` (the skip is applied
  incrementally, spanning `PushSamples` calls, since an atomic apply can read past the end of the
  received stream on the common path), plus a two-scope suppression in `ApplySlantTracking` matching
  legacy's own `m_SyncPos != -1` (one line, no history push) vs `m_AutoSyncCount` (rest of the image,
  history keeps flowing via new `SlantTracker.ProcessLineHistoryOnly`, only the correction is
  skipped) gates. Full `ISstvDecoder`/`ISstvSessionService` plumbing, backend-only — no UI button
  wired yet. 3 design-review rounds + a final code-level auditor review (EQUIVALENT-WITH-RISKS, no
  blockers) before/after implementation. See `PROJECT_BRIEF.md` for the full account.
- ~~AFC on/off toggle~~ — **done** (2026-08-07): new `SstvDecoderSettings.AfcEnabled` (nullable,
  STJ-default-loss-safe, same pattern as `ReceiveHistorySettings.MaxEntries`), threaded into
  `AnalogFmSstvDecoder`'s ctor and `InitializeAfc`'s existing AVT-exclusion guard (AFC-off now
  lands on the identical "`_afcTracker` stays null" path every consumer already null-checks, no
  other code changed). `Program.cs`'s `ISstvDecoder` registration switched from an eager instance
  to a settings-reading factory. Real, not a legacy port (legacy's own AFC, `sstv.cpp:1471`, is
  unconditionally always-on with no user-facing switch) — documented as such. Restart-only
  (singleton, `readonly` field). Auditor-reviewed (`Core.Sstv` touch, CLAUDE.md §7) — verdict
  EQUIVALENT-WITH-RISKS, no blockers; the two cheap risk fixes (settings-default test coverage,
  restart-only doc note) applied.
- ~~RX history retention limit (legacy default 32)~~ — **done** (2026-08-06): verified against
  actual legacy source (`Main.cpp:898`'s `sys.m_HistMax = 32`, applied unconditionally to
  `CBitmapHist::m_Head.m_Max` on every `Open()`, `ComLib.cpp:2658-2686` — the class's own
  constructor default of 64 never survives to be the effective default). `ReceiveHistorySettings`
  gained a nullable `MaxEntries` (STJ-property-default-loss-safe, same pattern as
  `AudioDeviceSettings.TxVolumePercent`) and `SqliteReceiveHistoryStore.RecordAsync` now trims the
  queryable index to the newest N after every insert. Deliberately does **not** delete the
  underlying image files on disk (legacy's single fixed-size ring buffer has no equivalent to this
  port's separate real files; unsupervised automatic file deletion is a materially different risk
  than trimming a DB index) — orphaned files beyond the retention window are a real, tracked
  follow-up, not a silent gap. Window position/size memory across restarts and the "jump to
  latest" history-browser nav button remain open, trivial.
- RX buffer mode + "high-precision" slant/sync replay actions — medium, DSP-adjacent (a rolling
  raw-audio buffer replayed through sync/slant correction); flag carefully, don't treat as a
  plain UI toggle.
- Auto-stop-at-end-of-signal / auto-resync toggles — group with the already-tracked
  `m_SyncRestart`/abandoned-image-save gap (below, under "explicitly deferred") rather than file
  as new items; all three are one legacy "Lock" toolbar button.
- **Confirmed NOT a real feature — don't port**: "always on top" (`m_StayOnTop` is write-only in
  legacy's own ini handling, never read back or wired to anything; vestigial/dead code there too).
- **Confirmed NOT a gap**: auto-save-on-receive — legacy always auto-saves unconditionally too,
  same as this port's current `ReceiveHistoryRecorder`.
- VOX (a TX tone-burst preamble to trigger a rig's own VOX circuit, not audio-input-detected PTT,
  and doesn't touch the CAT/PTT layer at all) — real but niche given this port already has real
  CAT PTT; low priority.

**New UI shell mock2 elements omitted for lack of real backing data** (found while building the
fixed Menu/header/3-tab shell, see `/home/artien/.claude/plans/wondrous-crafting-ladybug.md` —
wired everything real, these had no real data behind them today):
- ~~Manual "lock to a specific mode" RX decode override + the mock2 Mode card's quick-mode-button
  grid~~ — **backend done** (2026-08-08): the roadmap's own framing was deliberately vague pending
  research. Traced the real legacy click handler (`TMmsstv::SBMClick`, `Main.cpp:6096-6122`, calling
  `CSSTVDEM::Start(mode, TRUE)`, `sstv.cpp:1749-1767`) and found (via `UpdateModeBtn`,
  `Main.cpp:5988`) it's a ONE-SHOT "start decoding as mode X right now" kick, not a persistent lock
  — once the forced image ends, ordinary VIS auto-detect resumes automatically for the next
  transmission. New `ISstvDecoder.ForceMode(SstvModeDefinition)`, fire-and-forget like
  `RequestReSync`, reusing the exact same `Commit()`/anchor-correction pipeline VIS auto-detect
  itself uses. Went through 2 rounds of auditor plan-readiness review (round 1 caught a real
  audio-thread-crash blocker in the anchor choice; round 2 caught a stale-pending-anchor edge case
  forcing AVT mid-another-mode's-resolution). Backend-only — no UI wired yet (mock2's Auto/Locked
  segment + quick-mode grid is the eventual consumer). See `PROJECT_BRIEF.md` for the full account.
- ~~Live decode "Remaining time" / per-line progress readout~~ — **backing data done** (2026-08-07):
  new `IReceivedImageBuffer.Progress` (`double?`, `null` when idle, `[0,1]` fraction while decoding,
  snapped to exactly `1.0` on the completing scanline group), computed in `ReceivedImageBuffer.cs`
  by reusing `ReceiveHistoryRecorder`'s own step-learning technique for the same
  paired-line/RowsPerTransmissionLine problem. Backend-only — no ETA/remaining-time computation or
  UI binding yet; a future ViewModel can derive "remaining time" from `Progress` + its own elapsed-
  time tracking with no further backend change needed.
- RX frame actions (Abort/Re-decode/Copy-to-TX/Log QSO) and a completion progress bar — no
  abandon-current-frame, re-decode, or QSO-log-linking primitive exists yet. Medium-large,
  several independent features.
- Sync/slant correction readouts and controls (ppm, offset px, ReSync/Reset, advanced timing) —
  cross-reference the "Manual ReSync button"/"AFC toggle" items above; not re-filed as new.
- RX input-chain telemetry (squelch, BPF, notch/AGC, buffer, clipping %, noise floor, L/R level
  meters) — none of this is measured anywhere in `ScanlineStudio.Core.Audio`/`Sstv` today.
  Medium-high, several DSP measurements needed.
- Per-line SNR / luminance histogram / calibration-tone-offset readouts ("Signal quality" card)
  — no per-line SNR or histogram computation exists in the decode pipeline. Medium.
- Structured "Decode activity" log (freq/mode/callsign-OCR/grid/SNR/slant/lines/state per decode)
  and its decoder-trace pane — no such structured event log exists; would need a new decode-
  history recorder distinct from `ReceiveHistoryStore`. Medium-large.
- Frame metadata card (callsign OCR, grid/QRZ, VIS/frequency stamp, OCR confidence, dropped
  lines, file size, note, flag) — none of these fields exist on `ReceiveHistoryEntry` or
  anywhere else; OCR/QRZ lookup is a wholly new feature. Large.
- Unattended RX (scan/watch list, dwell time, alert-on-decode) — no scanning/watch feature
  exists in `IRadioSessionService`/`ISstvSessionService`. Large.
- "Decode rows colored by state" (yellow=decoding/green=saved+logged/red=partial) — the flat
  WSJT-X-style chrome pass added the color classes and LED-indicator convention this would use,
  but there's no decode-state field on any list row to drive it (same gap as the Decode-activity
  log above). Flagged directly to the user mid-session; not yet resolved either way.
- VOX tone-burst preamble row in the Transmit tab's TX-mode card — cross-reference the VOX
  bullet above; no new note.
- Whole Identification card (FSK ID/CW ID/Tail) — blocked on the already-tracked missing
  operator-callsign/profile setting (cross-reference "TX macros / CW-ID" above); no new note.
- TX output device name / TX sample-clock / occupied-bandwidth / monitor-audio-while-
  transmitting readouts — none of these are exposed anywhere in `ScanlineStudio.Core.Audio`
  today. Small-medium each.
- ~~Historical power/ALC-over-time TX meter plot~~ — **backing data done** (2026-08-07):
  `TxControlsPaneViewModel.TelemetryHistory` (`ObservableCollection<TxTelemetrySample>`, 120-sample
  cap, oldest-evicted-first), appended in `OnRadioStateChanged` under the identical
  "actually transmitting" gate as the existing `LiveSwrRatio`/etc. readouts. Backend-only —
  nothing renders it yet; a future chart binds directly, no translation step needed.
- Whole Outgoing-metadata card (VIS code/FSK ID/CW ID/callsign/to-station/grid-beam/report/
  freq-mode/date burned into the picture) — blocked on the same missing operator-profile
  setting as Identification, plus a separate "to station"/report/QSO-context concept that
  doesn't exist yet. Medium-large.
- TX image editor: Move/Scale/Rotate/Box/Line/Mask/Pick tools, Undo/Redo, zoom/snap-grid,
  brightness/contrast/saturation/gamma/sharpen/denoise adjustments — `ITransmitImagePreparer`
  only implements Crop/Resize/ApplyOverlay; none of these operations exist in the pipeline.
  Large, several independent features.
- Insert-field token picker / saved templates in the overlay editor — blocked on the same
  missing operator-profile setting as CW-ID, plus the separately-deferred template designer;
  no new note.
- TX queue (batch multiple images), persisted TX log, and "recently sent" reuse strip — no
  queueing, no TX-history store distinct from RxHistory exists. Medium-large, three separate
  features.
- Gallery free-text search across callsign/grid/note and band filter — `ReceiveHistoryEntry`
  has no callsign/grid/frequency fields at all; would need the RX logbook fields already
  tracked under "Logbook/QSO tracking" above. Medium, blocked on that work.
- Gallery Unlogged/Flagged filters and "Log entry"/"Open in log" actions — `LinkedQsoId` exists
  on `ReceiveHistoryEntry` but nothing ever sets it to non-null; there is no logbook-linking
  feature behind it yet, just an inert field. Medium, blocked on real QSO-log integration
  ([[08-logging]]).
- Gallery sort by callsign/SNR, and per-frame Export/Re-decode actions — no callsign/SNR data
  exists on entries, and `IReceiveHistoryStore` has no export/re-decode operation. Small-medium.
- Gallery Storage card's Sidecar-format and disk-free-space readouts — no JSON/EXIF sidecar is
  written today, and no free-space query exists anywhere. Small each.

**Options window: legacy YONIQ Option-dialog items added as disabled+tooltip placeholders
(2026-08-06)** — direct user request: port every option legacy's own Options dialog has, skip
what's already covered elsewhere, grey out + explain whatever has no real backing feature yet.
Most of the ~50 items added map onto gaps
**already tracked above** (PLL VCO gain/loop order/cutoff, zero-crossing type/order/cutoff/
smoothing, RxBPF width, sense/squelch level, differentiator, calibration wizard, TX BPF/LPF, TX
sample-clock offset, loopback test mode — all under "already-deliberately-dropped DSP tunables"
just above; demod-type selector and auto-start-on-sync-detect — cross-reference "Manual ReSync"/
"AFC toggle"/"Auto-stop-at-end-of-signal" above; window-position/size memory — cross-reference
"RX history retention limit" above; 7 waterfall/spectrum colors — cross-reference "Waterfall/
color" above; CW ID text/frequency/speed + FSK encode/decode — cross-reference "TX macros /
CW-ID" above, same operator-profile blocker; OmniRig 4th CAT backend — already tracked in
`spec/03-cat-layer.md` as speculative/undesigned) — no new notes for any of those. Genuinely new
gaps found while doing this pass, not previously tracked anywhere:
- ~~Sound FIFO buffer size (RX/TX)~~, ~~sound-card thread (capture-drain) priority~~,
  ~~app process priority~~ — **done** (2026-08-07). Buffer size: new
  `yoniq_audio_open_options.period_size_in_frames`/`periods` (native, `0` = miniaudio's own
  default, unchanged), threaded through `NativeAudio.OpenOptions` →
  `MiniAudioCaptureSession`/`PlaybackSession` → `MiniAudioEngine.Start*Async` →
  `AudioDeviceSettings.PeriodSizeInFrames`/`Periods` (plain non-nullable, CLR-default-safe).
  Verified via a real virtual-device round-trip test with non-default values. Capture-drain
  thread priority: `AudioDeviceSettings.CaptureThreadPriority` (nullable, STJ-safe) →
  `MiniAudioCaptureSession`'s drain `Thread.Priority`; the real-time native callback thread itself
  has no managed-settable priority, out of scope by construction. App process priority: new
  `ScanlineStudio.Application.AppPerformanceSettings.ProcessPriority` (nullable, STJ-safe), applied
  in `Program.cs` via `Process.PriorityClass`, guarded (a failure must never block startup).
- ~~Stereo capture source (Mono/Left/Right)~~ + ~~separate stereo-TX toggle~~ — **done**
  (2026-08-07), explicitly **not a confirmed legacy port** (documented as such at every layer, not
  traced against actual legacy source). New `AudioChannelSource` enum
  (`ScanlineStudio.Abstractions.Audio`) + `AudioDeviceSettings.CaptureChannelSource`/
  `StereoTxEnabled` (plain non-nullable, CLR-default-safe). Native: `capture_session_data_callback`
  extracts the selected channel from a real 2-channel device open into the shim's own
  ring (still always mono past that point — `IAudioEngine`'s "samples are always mono" contract is
  unchanged); `playback_session_data_callback` duplicates mono to interleaved L/R when stereo TX is
  on. Verified via real virtual-device tests: L/R content genuinely separates on capture, TX
  signal genuinely duplicates to both output channels. **Real underrun-padding bug found and fixed
  as part of this** (not pre-existing before this feature): a naive single-channel-width `memset`
  would have only ever zeroed the Left channel's bytes on underrun with stereo TX on, leaving Right
  with stale/garbage backend memory — fixed to zero both channels' worth, confirmed via a
  revert-fix-confirm-fail regression test (reintroducing the naive version made the new
  both-channels-silent test fail exactly as predicted).
- ~~PTT lock (hold PTT continuously, a manual-keying diagnostic aid)~~ — **done** (2026-08-07,
  safety-critical, auditor-reviewed). `ISstvSessionService.SetPttLockAsync`/`IsPttLocked`. An
  initial implementation was audited and found to have real defects before shipping — all fixed:
  an engaged lock could not be overridden by the SWR auto-cutoff/manual Stop TX (a lock must never
  defeat a safety cutoff — fixed so any abnormal termination, not just a normal completion, always
  force-unkeys and force-clears the lock); app shutdown while locked left the rig keyed
  (`DisposeAsync` now force-unkeys); unlock could silently no-op on a still-keyed rig in exactly
  the cases that mattered (`TuneAsync`'s `leaveKeyedAfterTune` leaves PTT keyed without setting the
  lock flag) — fixed by removing the short-circuit entirely (every call now always issues the
  command; confirmed idempotent-safe on every real protocol backend) and serializing via a
  `SemaphoreSlim` (closes a real TOCTOU race the short-circuit version had); RX paused by a
  lock-covered Transmit/Tune call now correctly resumes on unlock (`_rxPendingResumeAfterUnlock`
  handoff). One documented, accepted residual race (unlock racing a Transmit/Tune's own entry) —
  latent, no production caller wired yet, fixing it fully would make emergency-unlock less
  responsive, a worse trade. RTS-on-RX (the other half of this original bullet) intentionally
  dropped from this pass — no serial-control surface exists, may conflict with Hamlib's own
  RTS-PTT-type ownership, needs a legacy re-check before deciding if/how it fits this port's CAT
  architecture at all; still open.
- Sound-file ID (a recorded `.mmv`-style audio clip played instead of a CW-keyed tone) — distinct
  from CW-ID above (which is real and already tracked); this is a second, separate ID method with
  its own file-path field. Small-medium once CW-ID's operator-profile blocker is resolved, since
  they'd likely share the same "ID method" selector.
- ~~Tune-satellite-trigger toggle~~ — **done** (2026-08-07), explicitly **not a confirmed legacy
  port** (a citation attempt against `CtrBtn.cpp` only found a UI-enablement guard, not the actual
  post-tune state-transition logic — documented as an assumption, not verified). New
  `ISstvSessionService.TuneAsync(..., bool leaveKeyedAfterTune = false)`: when true, skips the
  normal un-key/resume-RX step for that one call, implemented as a separate, local flag inside
  `PlayWithPttAsync` — deliberately not reusing the PTT-lock's own field, so `IsPttLocked` never
  lies about what's actually holding PTT keyed.
- QRZ.com lookup enable — narrower than the already-tracked "OCR/QRZ lookup" gap under Frame
  metadata above (that one covers OCR too); legacy's own QRZ integration also hardcoded a personal
  account password, which is not being resurrected in any form — a real implementation needs its
  own API-key configuration, not a straight port.
- JPEG save quality (0-100) — received images are saved as PNG (lossless) today; this setting has
  no format to apply to unless/until a JPEG save path is added. Trivial once/if that happens.
- Legacy's `WinFont`/Japanese-English font-switch buttons and the Windows-only "always use DIB"
  rendering toggle are **not tracked here at all** — see `docs/removed-features.md`'s new
  "Legacy UI font switching" entry (superseded by the design system, not a gap) and the
  Win32/GDI-only DIB toggle isn't a real user-facing capability, no entry needed either.
- Legacy's YONIQ-fork-specific external "log connection" (IP:port socket to an unnamed companion
  app) is also **not tracked here** — see `docs/removed-features.md`'s new entry; too
  underspecified to even represent as a disabled placeholder.

## Explicitly deferred beyond v1

- Perspective correction / webcam capture ([[07-image-pipeline]]).
- Full Hamlib extended command-set coverage beyond frequency/mode/PTT ([[04-rigctld]]).
- Plugin sandboxing beyond same-process isolation ([[11-plugin-system]]).
- Legacy proprietary `.MDT` log format import (ADIF is the supported migration path instead, [[08-logging]]).
- The full QSL/template designer and `.mtm` import ([[15-template-designer]]) — specified but deferred; [[07-image-pipeline]]'s minimal `ImageOverlay` covers text-only TX overlay in the meantime.
- SSTV repeater/beacon mode ([[06-sstv-dsp]], legacy `RepSet.cpp`).
- Contest logging (JASTA application, `MMCG.DEF` JARL area database) — out of scope entirely, not just deferred; see [docs/removed-features.md](../docs/removed-features.md).

## Release gates

Before any tagged release: full [[13-testing]] manual hardware checklist (real rig CAT session, real audio device round-trip, real third-party `rigctld` interop) passes on at least one Windows, one Linux, and one macOS machine, in addition to the automated CI matrix being green.

## Open items requiring a decision before the relevant phase starts

- ~~Project license~~ — **decided**: LGPL-3.0-or-later, matching upstream. See [LICENSES.md](../LICENSES.md). The remaining open sub-item is confirming the `Terms.txt` freeware-clause interpretation with the upstream author (JE3HHT) if the project ever moves toward commercial distribution — not a blocker for development.
- [[08-logging]]: source and license-audit the callsign-prefix/country dataset before bundling (Phase 4) — `ARRL.DX` is already ruled out, see [LICENSES.md](../LICENSES.md). **Pre-audited 2026-08-02**: Clublog's `cty.dat` has no fee, but redistribution requires a human to email Clublog's helpdesk describing the proposed use and obtain an individual API key before the data can be downloaded/bundled — not a simple open-license drop-in. See [LICENSES.md](../LICENSES.md)'s "Candidate future asset" note. Remaining before Phase 4: someone actually emails Clublog and gets the key (not agent-doable), then the real bundled-asset row gets added to LICENSES.md.
- ~~[[05-audio-engine]]: confirm PortAudio latency is acceptable on Windows before committing to it as the sole backend, vs. adding a native WASAPI backend later~~ — **resolved, PortAudio rejected outright.** Two independent Opus consultations plus direct verification in this repo's own dev sandbox found PortAudio fails this spec's own requirements, not just a latency concern: no real device-change API in any released version, no PulseAudio/PipeWire host API on Linux (confirmed by creating a real virtual sink and showing a live PortAudio device probe couldn't see it at all — the exact virtual-cable workflow this spec requires, failing in practice), and no sample-rate conversion. Switched to `miniaudio`, whose WASAPI backend (`IAudioClient3` low-latency mode) *is* the native-WASAPI escape hatch this item used to hold open, without needing COM interop in `ScanlineStudio.Core.*`. See [[05-audio-engine]]'s Backend choice section for the full reasoning.
- [[15-template-designer]]: the `.mtm`/`PARALIST.BIN` binary format needs a proper reverse-engineering pass (cross-checked against `Draw.cpp`'s own `Load`/`Save` methods) before any implementation work on that subsystem can start — flagged as a prerequisite, not yet done.
- [LICENSES.md](../LICENSES.md): confirm whether Chilkat or FastReport actually back a real feature by building and running the legacy binary directly (not verifiable from source alone) — currently assumed unused/orphaned based on a source-only search.
