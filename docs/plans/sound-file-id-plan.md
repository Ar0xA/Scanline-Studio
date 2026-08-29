# Plan: Sound-file station ID (`CwIdMode.SoundFile`, legacy `OutputMMV`)

Status: **APPROVED to build (round 2b confirmation: "yes, start building now").** All 4 round-2
fixes verified against real code (not just the doc's own claims). One small follow-on decision from
the confirmation pass and 3 mechanical nits are folded in below, marked **[round 2b]**.

**Round-1 verdict summary** (full text kept in session history, not duplicated here): confirmed
Option A over B, confirmed the file-I/O/DSP layering, confirmed the footer-interaction question is
resolved (purely additive, no `sys.m_CWID` read in the footer branch) — but found the Option A
sentinel design has a real ordering hazard, one factual error in the plan's gain claim, and four
undecided-but-load-bearing details (amplitude normalization, sample-rate ladder, out-of-range byte
handling, PCM format contract). All addressed below; changed sections marked **[round 2]**.

**Round-2 verdict summary**: independently re-verified every round-1 fix line-by-line against
`Main.cpp`/`sstv.cpp`/`ComLib.cpp`/`fir.h` — all confirmed correct, including the amplitude
normalization (confirmed `/32768.0f` is right, with a cleaner precedent than the plan cited:
`WavFile.cs:169`'s existing `ReadInt16() / 32768f`, not `LevelAgc.cs`'s reverse-direction bridge) and
the sample-rate-ladder divergence (confirmed necessary and MORE correct than porting legacy's own
`SampType`/`SampBase` ladder, which pitch-shifts at rates like 10500 Hz that this port's rate list
allows and legacy's never did). Found 3 new blockers (below) and 1 design call. All addressed;
changes marked **[round 2b]**.

## Scope

Implement the third `CwIdMode` state (`SoundFile`). Today it is a reserved enum member that
silently transmits nothing (`CwIdMode.cs`'s own doc comment). This plan wires it to legacy's real
`OutputMMV` mechanism: parse a user-picked `.MMV` file (custom format, not WAV/MP3), resample it to
the current TX rate if needed, and play it back at the same TX dispatch point CW-ID/FSK-ID already
use.

Research (legacy citations, sample-rate table, resample method, playback point) is already done —
see `PROJECT_BRIEF.md`'s "Sound-file ID" section for the full citation trail. Not repeated here.

## Open design question — resolved **[round 2: adopted variant A′, not A]**

`AnalogFmSstvEncoder.GenerateFrequencySegments` yields `(double FrequencyHz, double DurationMs)`
tuples, consumed by one per-sample sine-generator loop (`EncodeAsyncCore`) and one duration-summing
loop (`EstimateSampleCount`). Neither has a way to inject arbitrary raw PCM samples — sound-file
playback needs exact stored amplitudes, not a synthesized tone.

Option B (generalize the segment type to carry an optional raw-sample payload, threaded through
every `(double, double)`-returning generator method — `VisHeader.*`, `FskStationIdEncoder.Generate`,
`CwMorseGenerator.Generate`, `GenerateFooterSegments`) is rejected: disproportionate blast radius for
a capability exactly one producer ever uses.

Plan-review round 1 confirmed Option A's basic shape (raw samples carried on
`StationIdTransmitOptions`, not threaded through the segment type) but flagged a real hazard in the
original sentinel-value design: `double.NegativeInfinity <= 0` is `true`, so the existing
`if (frequencyHz <= 0)` silence check (`AnalogFmSstvEncoder.cs:255`) would silently swallow the
sentinel as silence unless carefully ordered — and the test-only `RenderSegments` seam shares that
same `<= 0` shape, so a test routed through it would render silence and pass, the exact
self-consistent-but-wrong failure class CLAUDE.md §4 already warns about.

**Adopted: variant A′ — no sentinel value at all.** Since the sound-file payload is always the LAST
thing in the stream (legacy: `sys.m_CWID == 1` vs `== 2` are mutually exclusive at
`Main.cpp:7021-7025`, and `OutputMMV`/`OutputCWID` are the final calls in `SendSSTV`'s per-line
dispatch), there is no need to interleave it into the segment stream at all:

- Add `public ReadOnlyMemory<float>? SoundFileSamples { get; init; }` to `StationIdTransmitOptions`
  (non-null only when `CwIdMode == SoundFile` and the file parsed to a non-empty, playable payload —
  mirrors how `CwEnabled`/`FskIdEnabled` are pre-resolved booleans, not raw settings).
- **[round 2b, blocker] Exclusivity guard, one expression, required at BOTH consumption sites.**
  `CwEnabled` is read inside `GenerateFrequencySegments` (`:466`); `SoundFileSamples` is read in the
  new post-loop code, a DIFFERENT method scope. Round 2 review: a guard written at only one of the
  two sites lets `EncodeAsyncCore` and `EstimateSampleCount` disagree on whether a sound-file block
  plays at all — exactly the class `ResolveTransmitSettingsAsync`'s own doc comment says this
  architecture exists to prevent. Both `EncodeAsyncCore` and `EstimateSampleCount` must compute the
  identical local expression before doing anything else with the station-ID options:
  `var soundFile = stationId.CwEnabled ? null : stationId.SoundFileSamples;` (write `null`, not
  `default` — round 2b nit: `default` reads ambiguously against `default(ReadOnlyMemory<float>)`,
  an empty-but-non-null value, not the "nothing configured" `null` state this guard means) — then
  use `soundFile`
  (never `stationId.SoundFileSamples` directly) in the post-loop code at both sites. (The settings
  side already independently enforces this via `CwIdMode`'s tri-state exclusivity,
  `SstvSessionService.cs:2766`'s existing `CwIdMode == CwIdMode.Cw` gate — this encoder-side guard is
  the load-bearing one, a defensive belt-and-suspenders for a type that's public and could in
  principle be constructed with both fields set.)
- `EncodeAsyncCore` emits `soundFile`'s raw samples in a second loop placed AFTER the existing
  `foreach (... in GenerateFrequencySegments(...))` (`AnalogFmSstvEncoder.cs:231-303`), immediately
  before the trailing `await Task.CompletedTask;` — each raw sample runs through the SAME output
  bandpass filter call every tone/silence sample already goes through
  (`yield return (float)(txBpfEnabled ? bandpassFilter.ProcessSample(sample) : sample);`), and does
  NOT touch `phase` (no VCO advance — matches the silence-segment precedent, and matches legacy's own
  row-playback branch being a separate `else if` from the VCO branch entirely, `sstv.cpp:2899-2907`).
  Also does NOT run `txLpfEnabled`/`lpfAverage` smoothing (legacy's `avgLPF` is inside the tone-only
  `f>0` branch, `sstv.cpp:2867-2870` — raw samples never touch it). Calls
  `ct.ThrowIfCancellationRequested()` per raw sample, matching the tone loop's own per-segment check
  (`:233`).
- **[round 2b, blocker] `EstimateSampleCount`'s addition must happen in the SAME integer domain as
  `EncodeAsyncCore`'s real sample count, not by adding the raw count into the double accumulator
  first.** `EncodeAsyncCore` emits exactly `(long)idealSamplesSoFar` tone samples (the existing
  truncation, unchanged) and then `soundFile.Value.Length` raw ones — total
  `(long)idealSamplesSoFar + soundFile.Value.Length`. `EstimateSampleCount` must return that EXACT
  expression: `return (long)idealSamplesSoFar + (soundFile?.Length ?? 0);` — cast first, add the
  integer second. Adding the raw count into `idealSamplesSoFar` (the `double`) BEFORE the final cast
  is a different, NOT-guaranteed-equal computation (the two truncations can round across an integer
  boundary differently) — do not write it that way. **[round 2b nit]** `EstimateSampleCount`'s
  parameter is `StationIdTransmitOptions? stationId = null`, already coalesced inline
  (`AnalogFmSstvEncoder.cs:115`, `stationId ?? StationIdTransmitOptions.None`) — hoist that into a
  local (`var options = stationId ?? StationIdTransmitOptions.None;`) BEFORE computing the guard, so
  the guard expression (`options.CwEnabled ? null : options.SoundFileSamples`) is textually identical
  at both sites, not just equivalent.
- `GenerateFrequencySegments` itself is untouched — no new branch, no reserved value, no interaction
  with the `<= 0` silence check.
- `RenderSegments` (test-only seam) needs no change and has no exposure to this feature at all, since
  the segment stream itself never carries the raw-sample payload.

This is strictly simpler than the original Option A (no reserved frequency value, no branch inside
the hot per-segment loop, no `RenderSegments` exposure) while keeping the same "two small, isolated
additions, no change to any existing tone-generating helper" cost profile.

## File I/O / DSP layering

Matches the existing split: `ScanlineStudio.Application.SstvSessionService.ResolveTransmitSettingsAsync`
already owns the ONE site that resolves `StationIdTransmitOptions` shared by BOTH `TransmitAsync`'s
`EstimateSampleCount` call and its `EncodeAsync` call (see that method's own doc comment — it exists
specifically so a time-dependent macro-resolved value can't disagree between the estimate and the
real encode). Sound-file resolution slots into that exact site, which for free satisfies plan-review
round 1's "one resolution per transmission" requirement — no new mechanism needed, just adding the
MMV read/parse/resample call inside a method that already has the single-resolution guarantee.

1. **New Core.Sstv type** (pure DSP/parsing, no file I/O, mirroring `CwMorseGenerator`/
   `FskStationIdEncoder`'s internal-static-class shape but `public` since `Application` calls it
   across the assembly boundary — name TBD in plan-review, e.g. `MmvSoundFile`):
   - `ParseHeader(ReadOnlySpan<byte> fileBytes)` -> `(int SampleRateIndex, int PayloadOffset)?`
     (`null` = unplayable), per the header rules in "MMV format contract" below.
   - `Resample(ReadOnlySpan<short> pcm16, int sourceRateIndex, int targetSampleRateHz)` -> `float[]`
     (always a fresh, caller-owned array — never pooled/shared, since `AnalogFmSstvEncoder` captures
     it across a lazy `IAsyncEnumerable`), per "Resampling and amplitude" below.
2. **`SstvSessionService.ResolveTransmitSettingsAsync`** does the actual `File.ReadAllBytes` (I/O,
   needs `[LoggerMessage]` logging per `docs/logging-guidelines.md` — file-not-found/corrupt-header/
   oversized-file are expected, non-fatal conditions, not exceptions to propagate), then calls the
   two pure functions above, and sets `StationIdTransmitOptions.SoundFileSamples` only on success.
   Missing file / unreadable / empty / unparseable payload -> `SoundFileSamples` stays `null`,
   matching legacy's own `!sys.m_MMVID.IsEmpty()`-but-unconfigured silent-no-op behavior
   (`CwIdMode.cs`'s own doc comment). Re-parsed fresh on every `TransmitAsync` call, no caching —
   correctness-first, matches this port's existing "simplest shape first" bias; caching is a possible
   later optimization, not decided here.
3. **[round 2b, design call] Gate the actual file read/resample to the TX path only, not the
   read-only preview.** `ResolveTransmitSettingsAsync` is also the entire body of the read-only
   preview `GetStationIdTransmitOptionsAsync`, called by `TxControlsPaneViewModel`'s Identification
   summary on construction and on every Options-dialog close (`SstvSessionService.cs:2723-2724`).
   Doing a full file-read + resample + 4th-order IIR pass (up to the full 32 MB cap below) inline on
   every dialog close is a real UI-hitch/IO-regression risk the settings-load path already had to
   `Task.Run` around once for an unrelated reason (`:2755`) — round 2 flagged this as a design call
   worth settling now, not after the plumbing lands. **Decision:** add a `bool resolveSoundFile`
   parameter to `ResolveTransmitSettingsAsync` (`true` only from `TransmitAsync`'s own call site,
   `false` from `GetStationIdTransmitOptionsAsync`'s preview call site). When `false`, skip the file
   read/parse/resample entirely and leave `SoundFileSamples` `null` — the preview never needs the
   real audio, only whether identification is configured.
   **[round 2b]** Confirmed via direct read of `TxControlsPaneViewModel.LoadIdentificationSummaryAsync`
   (`:574-579`): it reads `FskIdEnabled`/`Callsign`/`CwEnabled`/`CwWpm`/`CwToneFrequencyHz` only —
   no `SoundFileSamples` branch, so gating is safe today. But `StationIdTransmitOptions` itself
   carries neither `CwIdMode` nor `SoundFileMmvPath` (confirmed: `StationIdTransmitOptions.cs:18-64`
   has no such fields), so once this ships, selecting SoundFile ID would leave the Identification
   summary reporting "no ID configured" even though one is. **Decision:** add
   `public bool SoundFileIdEnabled { get; init; }` to `StationIdTransmitOptions` (set to
   `CwIdMode == SoundFile && !string.IsNullOrEmpty(SoundFileMmvPath)`, independent of
   `resolveSoundFile` — cheap, no file I/O, safe to compute on the preview path too), and give
   `TxControlsPaneViewModel`'s summary one line reading it. The Identification card has no
   sound-file row today; this needs one new (localized) summary line, not a whole new UI element.
4. **Size cap (new, round 2):** legacy has no read-size cap (an unbounded `fseek`/`fread` loop), but
   an arbitrary user-picked file (this control has no format-specific extension filter beyond a
   loose `*.mmv`/`*.*`) could be a mispicked multi-gigabyte file, producing an OOM `float[]` at 2x
   the byte count. Cap the read at a fixed size (proposed: 32 MB raw file, comfortably larger than
   any real multi-second mono 16-bit ID clip at any supported rate — ask plan-review to confirm the
   number). Oversized files are treated as unplayable (`SoundFileSamples = null`, logged), same as
   any other unparseable-file case — a safety-only divergence from legacy's own unbounded read, not a
   fidelity change (legacy was never exercised against a multi-gigabyte "sound file ID," so there is
   no real behavior being diverged from).

## MMV format contract **[round 2, addresses round-1 blockers/risks]**

Precise byte-level contract, replacing the round-1 draft's looser "magic/no-magic" summary (which the
auditor found to have 3 unstated edge cases). PCM payload is 16-bit **signed, mono, little-endian**
(inferred from the `(short*)` cast in `Main.cpp` and single-channel `CSSTVMOD::SetRow` consumption —
no real `.mmv` fixture exists in `yoniq-old/` to confirm directly; flagged as the plan's own open
item, see "Not decided yet").

- **Minimum length:** fewer than 4 bytes total -> unplayable (`ParseHeader` returns `null`). Legacy's
  own `OutputMMV` requires `len >= 4` before reading the header at all (`Main.cpp:6857`).
- **Magic present** (`head[0]==0x55 && head[1]==0xAA`): `SampleRateIndex = head[2]`, clamped
  `> 8 -> 0` (legacy's own literal `>8`, not `>9` — index 9/48000 is unreachable via a magic header
  in legacy and this port preserves that exact quirk rather than "fixing" it, `Main.cpp:6862`).
  `PayloadOffset = 4`.
- **No magic:** `SampleRateIndex = head[0]`. Legacy's own no-magic path has NO clamp (`head[0]` can be
  0-255, indexing `SampTable[10]` directly is undefined behavior in the original C++ — there is
  nothing concrete to port for the out-of-range case). This port's rule (a deliberate, documented
  safety divergence, not a fidelity choice, since legacy's own behavior here is genuinely
  unspecifiable): `head[0] > 9 -> ParseHeader returns null` (unplayable, logged), matching the
  "unconfigured -> silent no-op" behavior the rest of this feature already uses for other failure
  cases. `PayloadOffset = 0` — the no-magic path re-seeks to file offset 0 (`Main.cpp:6865`,
  `fseek(fp, 0L, SEEK_SET)`), so the 4 sniffed header bytes ARE audio data, not skipped.
- **`SampTable` order** (`ComLib.cpp:68`): `{11025, 8000, 6000, 12000, 16000, 18000, 22050, 24000,
  44100, 48000}` — index-to-Hz for `SampleRateIndex` above. Confirmed by round-1 review as an exact
  match to the brief's citation.

## Resampling and amplitude **[round 2, addresses round-1 blockers]**

- **Target rate — deliberate divergence from legacy's `SampType`/`SampBase` ladder, stated
  explicitly:** legacy resamples to `InitSampType`'s quantized `SampBase` (`ComLib.cpp:106-201`),
  itself one of `SampTable`'s own 10 values, because legacy's ENTIRE TX pipeline only ever runs at
  one of those 10 rates. This port's own `AvailableSampleRates` list is NOT a subset of `SampTable`
  (it includes 14000 Hz, which has no `SampTable` entry at all) — porting the `SampBase` ladder would
  require inventing a mapping legacy itself never needed. Instead: resample directly from the file's
  own declared rate (via `SampTable[SampleRateIndex]`) to this port's actual **nominal** `SampleRate`
  (not the TX-offset-corrected `effectiveSampleRate` — matching the existing precedent already set
  for `bandpassFilter`/`lpfAverage` just above this code, both deliberately built from nominal
  `SampleRate`, `AnalogFmSstvEncoder.cs:198-218`). If the file's declared rate already equals the
  nominal `SampleRate` exactly, skip resampling AND the IIR filter entirely (legacy's own "already
  matches" fast path, `Main.cpp:6877`/`6895-6897`).
- **Index pick — truncating, not rounding:** `r = (int)((double)i * sourceRate / targetSampleRateHz)`
  bug-for-bug (legacy: `r = int(i * sfq / SampBase)`, `Main.cpp:6889` — integer truncation via C++
  `int()` cast, no rounding). Renamed in this doc from round 1's "nearest-index" (a misnomer the
  auditor flagged) to "truncating index pick."
- **Output sample count — ported exactly, in the same byte domain as legacy** (not re-derived in the
  sample domain, to avoid an off-by-one against legacy's own byte-masking step): compute
  `outputLengthBytes = (int)((double)payloadLengthBytes * targetSampleRateHz / sourceRateHz)`, then
  `outputLengthBytes &= ~1` (force even, matching legacy's `len &= 0xfffffffe`, `Main.cpp:6879-6884`),
  then `outputSampleCount = outputLengthBytes / 2`.
- **Odd trailing byte:** the no-resample fast path drops a trailing odd byte from an odd-length
  payload (`pos / 2` truncation, `Main.cpp:6898`) — port this truncation directly (`payload.Length / 2`
  shorts, ignoring one trailing byte if present), not an error case.
- **Anti-alias filter:** only applied when actually resampling (not on the fast path — see above). 4th
  order Butterworth IIR lowpass, fixed 2700Hz cutoff, `fs` argument = the **target** nominal
  `SampleRate` (matching legacy's own `iir.MakeIIR(2700, SampBase, 4, 0, 0)`, `Main.cpp:6887`/
  `fir.h:165`'s `MakeIIR(fc, fs, order, bc, rp)` signature confirms `fs` is the TARGET rate
  argument, not the source rate `sfq`), via the existing internal `IirFilter.Design`/`Process` (no
  new filter-design code).
- **[round 2b, blocker] Exact pipeline order, pinned unambiguously** (round 2 found the plan's own
  wording self-contradictory: "skips short re-quantization" vs "normalize each resampled **short**
  sample" — both can't be true at once, and step 1's golden vector is hand-computed FROM this exact
  order, so an ambiguous description makes that reference unverifiable). The real order, one
  sentence: for each output index, truncating-index-pick the source `short` (`sp[r]`, no
  quantization at this step) -> widen to `double` -> `IirFilter.Process` (double in, double out,
  legacy's own `Main.cpp:6890` quantizes this back to `short` before playback; **this port's pipeline
  does NOT** — skips that intermediate re-quantization entirely, a sub-LSB, documented deviation) ->
  divide the double result by `32768.0` -> narrow to `float`. On the no-resample fast path (rates
  match), skip both the index-pick and the IIR step, going straight from `short` to `float / 32768.0`
  with no intermediate quantization loss at all (a closer match to legacy than the resampling path,
  which is exactly what "fast path" implies).
- **Amplitude normalization — corrects round 1's factual error, confirmed correct by round 2 with a
  better-cited precedent.** The plan previously claimed row playback shares "the identical final
  gain/BPF stage" as tones. False: `sstv.cpp:2899` applies `d *= m_outgain` (default 24578,
  `sstv.cpp:2773`, NOT `Main.cpp:907` as round 1's citation said — that line is
  `sys.m_CWIDFreq = 1000`, an unrelated constant) to tones only; the row-playback branch
  (`sstv.cpp:2903-2907`, `d = (double)(*pRow++)`) applies NO gain at all — legacy plays sound-file
  samples at their own raw encoded amplitude, up to the device's full int16 scale (±32768), only the
  BPF (`:2914`) is actually shared. `/32768.0f` reproduces that ABSOLUTE playback level exactly (not
  a relative-to-tones ratio — this port's tones are already at full-scale `Math.Sin(phase)` = ±1.0,
  a pre-existing, unrelated divergence from legacy's own 0.75-of-full-scale tone level; not something
  this feature needs to reproduce or can cleanly reproduce without also touching tone generation).
  Direct precedent for exactly this short-PCM-to-float conversion already exists in this codebase:
  `ScanlineStudio.Core.Audio/WavFile.cs:169`'s `reader.ReadInt16() / 32768f` — cite that, not
  `LevelAgc.cs`'s scale bridge (round 2 found that citation runs the OPPOSITE direction, float-to-int16
  for legacy-constant reuse, a real but less-apt precedent for this specific conversion).

## Settings / UI wiring

- **`StationIdSettings`** (`Core.Sstv`): add `public string? SoundFileMmvPath { get; init; }` (raw
  path, `null`/empty means unconfigured — same nullable-means-unset convention as `CwText`).
- **`OptionsSettingsService`**: thread the new field through the existing `StationIdSettings`
  load/save round-trip (same shape as `CwText`/`CwWpm`).
- **`OptionsWindowViewModel`**: add `IsIdMethodSoundFileSelected` (same pattern as
  `IsIdMethodOffSelected`/`IsIdMethodCwSelected`, `[NotifyPropertyChangedFor]` wired the same way),
  a `SoundFileMmvPath` string property, and a `BrowseSoundFileCommand` calling a new
  `IFilePickerService.PickMmvFileAsync()` (mirrors `PickHamlibLibraryFileAsync`'s shape — single
  file, local path or `null` on cancel; file-type filter `*.mmv` or `*.*` since it's a custom
  format with no standard extension convention to verify).
- **`OptionsWindowView.axaml`**: enable the `SoundFile` `RadioButton` (drop `IsEnabled="False"` +
  its `Options.NotImplemented.Help` tooltip, bind `IsChecked="{Binding IsIdMethodSoundFileSelected}"`
  matching the Off/Cw buttons), and enable the existing stub controls: the label `StackPanel`
  (`:796-799`) AND the `TextBox`/`Browse` `Grid` (`:800-803`) are TWO separate sibling elements, not
  one — bind the `TextBox` to `SoundFileMmvPath`, wire `Browse` to `BrowseSoundFileCommand`, drop
  both `IsEnabled="False"`/`Options.NotImplemented.Help` bindings, and gate BOTH elements'
  visibility on `IsIdMethodSoundFileSelected` (matching the CW block's
  `IsVisible="{Binding IsIdMethodCwSelected}"` pattern — currently both are unconditionally visible,
  a small pre-existing bug worth fixing in the same edit).
- **[round 2b, nit]** `BrowseSoundFileCommand`'s async body must marshal its result back via
  `Dispatcher.UIThread.Post` (or otherwise not `ConfigureAwait(false)` past the picker call) before
  writing the bound property — `BrowseHamlibLibraryAsync`'s own doc comment
  (`OptionsWindowViewModel.cs:1435-1443`) documents a real crash from skipping this. Follow that
  existing command's exact shape, not a fresh pattern.
- **`SstvSessionService.ResolveTransmitSettingsAsync`**: read `SoundFileMmvPath` when
  `CwIdMode == SoundFile` AND `resolveSoundFile` is `true` (see "gate to TX path only" above), run
  the file-read/parse/resample pipeline, set `SoundFileSamples` accordingly on the
  `StationIdTransmitOptions` it constructs. `CwEnabled` is set `false` whenever `CwIdMode ==
  SoundFile` — made explicit at construction (an `if/else if` over `CwIdMode`'s 3 values when
  building `CwEnabled`/`SoundFileSamples`, mirroring legacy's own `if(==1) OutputCWID() else if(==2)
  OutputMMV()` precedence at `Main.cpp:7021-7025`, CW wins if both were somehow set), not left as an
  assumption. This is the settings-side belt; the encoder-side suspenders are the `soundFile =
  stationId.CwEnabled ? default : stationId.SoundFileSamples` guard specified above, required at
  BOTH `EncodeAsyncCore` and `EstimateSampleCount`. Test coverage for the guard: given a
  `StationIdTransmitOptions` with both `CwEnabled=true` and a non-null `SoundFileSamples` (a state
  the resolution code should never itself produce, but the encoder must still handle deterministically
  since it's a public settings-derived type), assert BOTH `EncodeAsync` emits CW-ID only AND
  `EstimateSampleCount` matches that CW-ID-only duration (not the sound-file-inclusive one) —
  covering both sites, not just one.
- **`OptionsSnapshot.cs`** (round 2 addition — missed in round 1's file list, flagged by review):
  this record already carries `CwIdMode`/`CwText`/etc. (`OptionsSnapshot.cs:66-67`); add
  `SoundFileMmvPath` alongside them, following the exact same pattern.
- **[round 2b, nit]** `StationIdTransmitOptions` is a `sealed record`; adding
  `ReadOnlyMemory<float>?` gives the record shallow (buffer-reference + offset + length) `==`/
  `GetHashCode`, not content equality, and puts a potentially multi-MB payload on a type documented
  as plain "configuration." Functionally harmless (nothing compares two of these by value today) —
  worth one doc-comment line on the new property so a future test author isn't surprised, not a
  design change.

## Footer-tone-shape interaction — resolved, no longer open **[round 2]**

Plan-review round 1 verified directly against `Main.cpp:6997-7012`: the footer-shape branch reads
only `sys.m_TXFSKID`, `sys.m_VOX`, and `SSTVSET.m_fTxNarrow` — `sys.m_CWID` appears nowhere in it.
`OutputMMV` is purely additive at the same dispatch point as `OutputCWID`, with no footer-shape
interaction of its own. No implementation concern here.

## Duration estimate / TX progress

`EstimateSampleCount`/TX-progress UI will reflect the sound-file's real duration once
`SoundFileSamples` is populated, automatically, via the post-loop addition in variant A′ above — no
separate estimate path needed.

## Files touched (expected)

- `src/ScanlineStudio.Core.Sstv/AnalogFmSstvEncoder.cs` (post-loop raw-sample emission, 2 sites:
  `EncodeAsyncCore`, `EstimateSampleCount`)
- `src/ScanlineStudio.Core.Sstv/MmvSoundFile.cs` (new)
- `src/ScanlineStudio.Core.Sstv/StationIdSettings.cs` (`SoundFileMmvPath`)
- `src/ScanlineStudio.Abstractions/Sstv/StationIdTransmitOptions.cs` (`SoundFileSamples`,
  `SoundFileIdEnabled`)
- `src/ScanlineStudio.UI/ViewModels/TxControlsPaneViewModel.cs` (Identification summary: one new
  line for `SoundFileIdEnabled` — round 2b addition)
- `src/ScanlineStudio.Abstractions/Sstv/CwIdMode.cs` (doc-comment update — no longer "deliberately
  unimplemented")
- `src/ScanlineStudio.Application/SstvSessionService.cs` (file read + parse/resample call in
  `ResolveTransmitSettingsAsync`, `CwEnabled`/`SoundFileSamples` exclusivity)
- `src/ScanlineStudio.Application/OptionsSnapshot.cs` (`SoundFileMmvPath` — round 2 addition)
- `src/ScanlineStudio.Application/OptionsSettingsService.cs` (settings round-trip)
- `src/ScanlineStudio.UI/ViewModels/OptionsWindowViewModel.cs` (new bindable properties/command)
- `src/ScanlineStudio.UI/Views/OptionsWindowView.axaml` (enable the 2 stub controls, add visibility gate)
- `src/ScanlineStudio.UI/Services/IFilePickerService.cs` / `FilePickerService.cs` (`PickMmvFileAsync`)
- `assets/locale/en.json` (drop the now-dead `Options.NotImplemented.Help` reference on these 2
  controls only if nothing else still uses that shared key — check before removing anything; add
  new keys for the file-picker dialog's title/filter label, round 2 addition — round 1 flagged this
  as missing)
- `docs/removed-features.md` — no change expected (this REPLACES a documented gap, doesn't remove
  anything); confirm nothing there needs updating once done.

## Build order (chopped, per this project's own DSP-port discipline)

1. `MmvSoundFile.ParseHeader`/`.Resample` in isolation, unit-tested against hand-built byte arrays
   for: too-short file (<4 bytes), magic-present (incl. the `>8` clamp case), magic-absent (incl.
   the `>9` unplayable case), matching-rate no-op (no filter applied), differing-rate resample.
   Golden-vector tolerance: since no real `.mmv` fixture exists yet (open item below), the
   differing-rate test's expected output is a **hand-computed `double` reference derived directly
   from the exact pipeline order** pinned in "Resampling and amplitude" above (truncating index pick
   -> `IirFilter.Design(2700, targetRate, 4)`/`.Process` in double -> `/32768.0` -> narrow to float,
   run in a small script/scratch harness to produce the reference sequence) — not a recorded legacy
   capture. **Tolerance: absolute per-sample difference ≤ 1e-6** between the hand-computed `double`
   reference and the port's `float` output — the only lossy step in this pipeline is the single
   `double`-to-`float` narrowing of an exactly-representable division (`/32768.0`), so a tolerance
   this tight is achievable and meaningful, not a padded "close enough."
2. `StationIdTransmitOptions.SoundFileSamples` + `AnalogFmSstvEncoder`'s 2 post-loop additions,
   unit-tested directly: a synthetic `StationIdTransmitOptions` with a known raw-sample array, assert
   `EncodeAsync`'s output contains those exact samples (post-BPF) immediately after the last tone
   segment, and `EstimateSampleCount` matches the raw length. Add the mutual-exclusivity test
   described in "Settings / UI wiring" above (`CwEnabled=true` + non-null `SoundFileSamples` ->
   CW-ID only).
3. `StationIdSettings`/`OptionsSnapshot`/`OptionsSettingsService` field plumbing, unit-tested via the
   existing round-trip test pattern (`ConfigurationPresetServiceTests`-adjacent).
4. `IFilePickerService.PickMmvFileAsync` + `OptionsWindowViewModel`/`.axaml` wiring, manual UI check
   (real window, per `feedback_verify_avalonia_rendering_with_real_window`) — enable, pick a file,
   confirm the path shows and the radio group behaves like Off/Cw.
5. `SstvSessionService` end-to-end wiring, one integration test: configure a real small `.MMV`
   fixture file, transmit, confirm the encoder's `StationIdTransmitOptions` carries non-null
   `SoundFileSamples` and the resulting audio is not silent at the expected position.

Round 2 review already covered steps 1-2's design on paper (this doc); once a lightweight
confirmation pass on this revision comes back clear, build steps 1-2 directly, then 3-5 as
mechanical plumbing with a single lighter code-review pass per CLAUDE.md §7.

## Verification (adds to CLAUDE.md §6 commands, same test projects)

- `dotnet build src/ScanlineStudio.Core.Sstv -c Debug`
- `dotnet test tests/ScanlineStudio.Core.Sstv.Tests -c Debug --filter "FullyQualifiedName~MmvSoundFile"`
- `dotnet test tests/ScanlineStudio.Core.Sstv.Tests -c Debug --filter "FullyQualifiedName~SoundFile"`
- `dotnet test tests/ScanlineStudio.Application.Tests -c Debug --filter "FullyQualifiedName~StationId"`
- Full `ScanlineStudio.UI.Tests`/`Application.Tests` once per milestone (not per edit), per
  `feedback_scope_test_runs_to_change`.

## Resolved by round 2 (no longer open)

- Amplitude normalization: `/32768.0f`, confirmed correct and re-cited to `WavFile.cs:169`.
- No-magic `>9` -> unplayable: confirmed as the safer of the two defensible choices, keep as-is.
- Silent-vs-toast on corrupt file: silent + logged, confirmed matches legacy and this plan's own lean.
- 32 MB size cap: confirmed reasonable (round 2: "~5.5 min mono 16-bit at 48 kHz — far past any ID
  clip"); not blocking either way.
- `MmvSoundFile` class name: no objection raised.

## Not decided yet (small, non-blocking per round 2 — confirm during implementation, not another round)

- PCM format contract (16-bit signed mono little-endian) is inferred, not confirmed against a real
  `.mmv` file — none exists in `yoniq-old/`. Check for a sample `.mmv`/`.MDT` file there before
  building step 1; if none exists, proceed on the inferred contract but flag it as unverified in the
  code's own doc comment, not as confirmed fact.
- File-picker filter pattern for `.MMV` (no known standard extension to verify against upstream
  YONIQ install data — check `yoniq-old` for any sample `.mmv`/`.MDT` files before deciding).
- Confirm `TxControlsPaneViewModel`'s Identification summary text doesn't branch on
  `SoundFileSamples` being non-null (it shouldn't need to, per the preview-gating decision above) —
  a quick read during implementation, not a design question.
