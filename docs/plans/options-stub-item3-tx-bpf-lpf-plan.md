# Options stub backlog item 3: TX BPF / TX LPF

**Item 3 (TX BPF/LPF): DONE.** 2 plan-review rounds (round 1: 3 blockers, all paper-fixed; round 2:
explicit "yes, ready to build now" after one wrong-legacy-fact sentence fixed). 1 code-review round:
"no correctness defect found in any shipped code path" — every clamp verified two-sided at every real
construction/mutation path, the odd/even UB decision confirmed correct. 2 real test-coverage gaps
found and closed, both the same failure shape items 1/2 already hit once each: the `+0.5` LPF-window
rounding formula had no test that could actually fail if it were dropped (the chosen test frequency
happened to make rounding and truncation agree) — extracted into a shared `ResolveLpfWindowSize`
helper and pinned at the real 2000Hz default, mutation-verified; `FakeSstvEncoder`'s new TX-filter
tracking fields were written but never asserted anywhere — closed with a new
`SstvSessionServiceTxAudioFilterTests.cs` mirroring the sibling `TxSampleRateOffsetHz` test file's own
exact shape, mutation-verified. Auditor's own explicit "no further review round needed" — not sent
for round 2. `docs/removed-features.md`'s former BPF-toggle entry deleted (capability restored, not
removed). Not committed.

Part of `docs/plans/options-advanced-stub-backlog-plan.md`'s 6-item backlog. Full heavy loop per the
user's standing instruction ("don't ask, just go until they are all done"). This item resolved the
backlog's own round-1 open question ("does `RestartableSstvEncoder` already have BPF/LPF stages to
gate?") via direct research (a dispatched fork plus my own reading of `AnalogFmSstvEncoder.cs`) —
**the answer is different for each half**, reshaping this item's real scope substantially from the
backlog's original "medium, on/off toggle" guess.

## Scope-reshaping finding: TX BPF is a REVERSAL of a documented removal, not a stub fill

`TxOutputBandpassFilter.cs` already exists — a complete, verified, literal port of legacy's `CFIR2`
windowed-FIR filter class (`sstv.h:805`, `fir.cpp:1063-1177`, `att=40` Kaiser-window branch), applied
**unconditionally** at a fixed 24 taps in `AnalogFmSstvEncoder.cs:177,275`, matching legacy's own
shipped DEFAULTS (`m_bpf=1`/on, `m_bpftap=24`, `sstv.cpp:2759,2764`) exactly. The filter MATH is
already correct — zero new DSP-correctness risk here.

What's missing is the USER-FACING toggle (`m_bpf`/`CBTXBPF`) and tap-count control (`m_bpftap`/
`TxBpfTap`), which `docs/removed-features.md`'s own "TX output bandpass filter toggle/tap setting"
entry (found 2026-08-21, Tier A Batch 7 chunk 7d) documents as a **deliberate, considered removal**:
"unaffected at shipped defaults... but it is a real, silently-dropped user-facing capability." That
entry's own reasoning doesn't argue the toggle is unwanted, just that it was out of scope for the
functional-audit pass that found it. Restoring it now is the right call — the underlying filter is
already correct, restoring user control is strictly additive, and it directly closes a literal stub
in the Options window (a disabled checkbox with a "not implemented" tooltip) — but it IS a reversal of
a standing documented decision, not a mechanical stub-fill, and is called out here explicitly rather
than silently overwritten. The `docs/removed-features.md` entry will be deleted once this ships (the
capability is no longer removed).

## TX LPF: genuinely new work, AND a real architecture-bridging question

`m_lpf`/`avgLPF`/`m_lpffq` (`CSSTVMOD`, `sstv.h:794-795,812`, `sstv.cpp:2760-2761,2869,2929`) have
**no C# equivalent at all** — not a removed/documented capability like BPF, never built. Real legacy
behavior, read directly:

```cpp
else if( m_Cnt ){
    int f = m_TXBuf[m_rPnt];
    if( f > 0 ){
        d = double((f & 0x0fff) - 1100)/double(2300-1100);   // normalize
        if( m_lpf ) d = avgLPF.Avg(d);                         // smooth (moving average), PRE-VCO
        d = m_vco.Do(d);                                       // convert to phase-continuous audio
    }
    else {
        d = 0;                                                 // silence: no smoothing, no VCO advance
    }
    ...
}
```

`avgLPF` is `CSmooz` — **the exact same class already fully ported this session as `MovingAverage.cs`
(item 2)**, no new filter class needed here. `CalcLPF`'s window-size line is `avgLPF.SetCount(int(
SampFreq/m_lpffq + 0.5))` (`sstv.cpp:2929`) — **rounds via `+0.5` before truncation, a REAL, easy-to-
miss difference from `CFQC::CalcLPF`'s own bare-truncation window-size line** (item 2's own
`ZeroCrossingFrequencyCounter.SetTuning`) — do not copy that formula verbatim, use the rounding one.

**The real design question**: legacy re-reads and re-smooths the SAME per-scanline-fixed value `d`
on EVERY audio sample within a segment (`m_TXBuf` holds one discrete frequency code per requested
duration, `Do()` is called once per audio sample) — `avgLPF` converges toward each new segment's
target value like a first-order lag/glide filter, producing an audible glide between adjacent tone
values rather than a hard instantaneous frequency jump. This port's `AnalogFmSstvEncoder.EncodeAsyncCore`
has a DIFFERENT shape: `frequencyHz` is a per-segment CONSTANT (no interpolation across the segment's
sample loop at all today), and `phaseIncrement` is derived directly from it. Porting `avgLPF` faithfully
means: call `MovingAverage.Add(frequencyHz)` **once per audio sample** (not once per segment), always
feeding the CURRENT segment's raw target frequency (mirroring legacy's own per-sample re-read of a
per-segment-fixed buffer value), and derive `phaseIncrement` from the SMOOTHED output each sample
instead of the raw per-segment `frequencyHz` — this maps directly onto the existing per-sample loop
with no deeper architecture rework needed. Legacy explicitly skips `avgLPF` (and the VCO) during a
silence/gap sample (`f<=0` -> `d=0`, no `Avg` call) — the port's own `frequencyHz <= 0` branch must
skip feeding the moving average too, so history holds through a gap and resumes smoothing (not resets)
once a real tone follows, matching legacy exactly.

**Smoothing the raw Hz value directly (not legacy's normalized `d`) is a legitimate simplification,
not a divergence** — `MovingAverage`/`CSmooz` is a plain arithmetic mean, a linear operator:
`mean(a*x+b) = a*mean(x)+b`. Smoothing `(f-1100)/1200` then denormalizing is algebraically identical
to smoothing `f` directly and skipping the round trip — the same "work in real Hz, not legacy's
normalized-then-denormalize" convention `PllFmDemodulator`/`ZeroCrossingFrequencyCounter` already both
document and rely on.

## Real, cross-verified defaults, ranges, and persistence (single global instance, simpler than items 1/2)

`m_bpf=1` (on), `m_bpftap=24`, `m_lpf=0` (off), `m_lpffq=2000` — all from `CSSTVMOD`'s own constructor
(`sstv.cpp:2759-2761,2764`). **Real, two-sided legacy validation ranges, confirmed at the Save
handler** (`Option.cpp:452-459`, corrected after round-1 plan-review — an earlier draft of this doc
guessed wrong): `m_lpffq` accepted only if `(d >= 100.0) && (d <= 3000.0)`; `m_bpftap` accepted only
if `(dd >= 2) && (dd <= TAPMAX)`. **`TAPMAX=512`** (`fir.h:27`). Ini keys (`[Define]`):
`TXBPF`/`TXLPF`/`TXBPFTAP`/`TXLPFFQ`, load `Main.cpp:1910-1913`, save `Main.cpp:2416-2419` — note
`TXLPFFQ` persists via `ReadInteger`/`WriteInteger` even though `m_lpffq` is a `double` (a real legacy
quirk: a fractional entry silently truncates to whole Hz on save). This port's own settings are JSON,
not ini — no reason to inherit that truncation; persist `TxLpfFrequencyHz` as a real `double`,
explicitly NOT copying the quirk. **No profile-slot involvement** — `pMod = &pSound->SSTVMOD`
(`Main.cpp:957`) is a single global modulator instance, unlike items 1/2's per-profile PLL/zero-crossing
fields; no `SetProFile`/`InitProfile` reassert to worry about, no version-migration gate. `CalcFilter()`
is called from exactly 2 sites: ini-load (`Main.cpp:1914`, entirely UNVALIDATED there — legacy's own
ini-load path feeds `CalcFilter` raw with no range check at all, only the Options-Save path validates
— this port's settings-boundary clamp is a deliberate improvement over legacy, not ported behavior, say
so explicitly rather than presenting it as a straight port) and Options-Save (`Option.cpp:460`) — this
port's equivalent is simply: apply on Options Save, matching every prior item's own live-push shape.

**`TxBpfTap`/`TxLpfFreq` preset lists: UNCONFIRMED** (same situation as item 2's `crossSmooz` —
`Option.dfm` is a binary DFM, unreadable). Build both as free-entry `NumericUpDown`s, matching item
2's own resolution for the same uncertainty — now bounded by the REAL confirmed ranges above, not a
guess.

**Odd tap counts are a real, reachable legacy divergence, not just a theoretical corner — corrected
after round-2 plan-review, which caught round 1's own claim stated backwards.** Legacy's Save handler
accepts any integer in `[2,512]`, including odd values. `MakeFilter` writes `2*(n/2)+1` coefficient
entries — for an EVEN `tap` (e.g. the default 24): `(24/2+1)+(24/2)` = 13+12 = 25 = `tap+1`, exactly
filling `CFIR2::Create`'s `m_pH = new double[tap+1]` allocation (`fir.cpp:1102`), no UB. For an ODD
`tap` (e.g. 25): `(25/2+1)+(25/2)` = 13+12 = 25 writes into a 26-element array, leaving `m_pH[25]`
uninitialized — `Do`'s own `i <= m_Tap` loop (`fir.cpp:1123`) then reads that garbage. **UB is on ODD
tap counts, not even ones** — round 1's own sentence had this exactly backwards, even though the
underlying decision (clamp to even) was already correct. **Resolution unchanged**: restrict this
port's own settings clamp to EVEN tap counts only (round to the nearest even value, matching how this
port already treats every other legacy dialog-side reject-and-keep as a clamp-and-round) — the
default (24) is already even, and EVEN is the well-defined case, not merely a safe simplification.

## Design

1. **`TxOutputBandpassFilter.cs`**: `Tap` (currently `private const int Tap = 24`) becomes an
   **instance field**, set from a new constructor parameter (`tapCount = 24`), clamped to `[2, 512]`
   AND rounded to even (see above) before use — both-direction clamp from the start, applying item
   1/2's own hard-won lesson. `MakeFilter` (currently `internal static`) must take `tap` as an
   explicit parameter instead of reading the `const` (its own internal `const int half = Tap / 2;`
   line becomes `var half = tap / 2;`). `_z`'s allocation (`new double[Tap + 1]`) and `ProcessSample`'s
   loop bound (`i <= Tap`) both read the instance field instead of the constant. **Fan-out this
   touches** (confirmed by reading the real files, not paraphrased): `TxOutputBandpassFilterTests.cs`
   has 6 static `MakeFilter(sampleRate)` call sites (`:60,68,80,92,106,121`) that need a `tap` argument
   added, PLUS a test literally named `MakeFilter_TapCountAndLength_IsFixed24_RegardlessOfSampleRate`
   that needs rewriting (the fixed-at-24 claim it asserts is exactly what's being made variable);
   `AnalogFmSstvEncoderSilenceTests.cs:91`'s `const int tapCount = 24; // ... kept in sync manually`
   local constant can be deleted once the real constructor accepts a real tap-count argument to mirror
   instead of a hand-kept-in-sync copy.
   **On/off gating happens at the ENCODER CALL SITE, not inside the filter class** (round-1
   plan-review correction — an earlier draft proposed an `enabled` ctor param): mirrors legacy's own
   `if(m_bpf) d = m_BPF.Do(d);` shape (`sstv.cpp:2914`) exactly — when off, the delay line is never
   advanced at all, not merely bypassed post-construction. `EncodeAsyncCore`/`RenderSegments` become
   `txBpfEnabled ? bandpassFilter.ProcessSample(sample) : sample` instead of an unconditional call.
   Keeps the filter class itself pure (construct = configure, no runtime mode switch), halves the
   constructor-parameter churn.
2. **New TX-LPF smoothing, added inline to `AnalogFmSstvEncoder.EncodeAsyncCore`'s existing per-sample
   loop** (and mirrored in the `RenderSegments` test seam): a `MovingAverage` constructed once per
   `EncodeAsyncCore` call — **NOT the same reasoning as the bandpass filter's own fresh-per-call
   choice** (round-1 plan-review correction — an earlier draft wrongly claimed this matches legacy's
   `InitTXBuf`; legacy's `InitTXBuf` clears `m_BPF` only, `sstv.cpp:2827` — `avgLPF`'s own state
   genuinely PERSISTS across transmissions in legacy, only `SetCount` clears it, `sstv.cpp:2929`).
   Fresh-per-call here is a deliberate, documented DIVERGENCE from legacy (simpler, deterministic, and
   practically indistinguishable — a partially-filled boxcar converges within one window's worth of a
   constant leading tone regardless), not a faithfully-ported behavior — say so plainly in the code
   comment, don't claim false legacy parity.
   `SetCount`'d from `(int)(sampleRate / lpfFrequencyHz + 0.5)` (the rounding formula, NOT `CFQC`'s
   bare-truncation one) — **using the NOMINAL `SampleRate`, not `effectiveSampleRate`** (round-1
   plan-review finding: both are live in scope at the insertion point; legacy's own `CalcFilter` uses
   nominal `SampFreq`, `sstv.cpp:2929`, matching the ALREADY-AUDITED precedent
   `TxOutputBandpassFilter`'s own construction already established, `AnalogFmSstvEncoder.cs:173-176`,
   guarded by `AnalogFmSstvEncoderSampleRateOffsetTests.cs:14` — the smoothed VALUE still feeds
   `phaseIncrement`, which correctly keeps using `effectiveSampleRate`, only the WINDOW-SIZE
   computation pins to nominal). When TX LPF is enabled AND `frequencyHz > 0`: feed `frequencyHz` into
   `MovingAverage.Add` every sample, use the returned smoothed value in place of the raw `frequencyHz`
   for that sample's `phaseIncrement`. When disabled, or during a silence/gap sample
   (`frequencyHz <= 0`): skip the moving average entirely, using the raw `frequencyHz` (0, for
   silence) directly — matches legacy's own `if(f>0)` gate precisely.
3. **Settings placement: `AudioDeviceSettings`, not `SstvDecoderSettings`** (round-1 plan-review
   resolved this from the real code, not left as an open question) — the exact same section
   `TxSampleRateOffsetHz` (the Clock-calibration sibling field) already lives in
   (`AudioDeviceSettings.cs:119`), consumed by the identical resolution site
   (`SstvSessionService.ResolveTransmitSettingsAsync`, `SstvSessionService.cs:2730`) that already
   reads `AudioDeviceSettings` for that field. 4 new nullable fields (`TxBpfEnabled: bool?`,
   `TxBpfTapCount: int?`, `TxLpfEnabled: bool?`, `TxLpfFrequencyHz: double?`) — nullable because 3 of
   the 4 desired defaults (`true`/24/2000.0) are NOT the CLR default for their type, same reasoning as
   `AutoStopEnabled`'s own doc comment elsewhere in this codebase. Resolution (inline at the
   `ResolveTransmitSettingsAsync` read site, matching `TxSampleRateOffsetHz`'s own
   `is >= -1500.0 and <= 1500.0 ? ... : 0.0` shape exactly, not a separate `Resolve()` method — this
   settings section has no such method today): `TxBpfEnabled ?? true`; `TxBpfTapCount` clamped
   `[2,512]` + rounded to even, falling back to 24 if absent; `TxLpfEnabled ?? false`;
   `TxLpfFrequencyHz` clamped `[100.0,3000.0]`, falling back to 2000.0 if absent.
4. **Live-apply: a per-call `EncodeAsync` parameter, NOT deferred-request plumbing** (round-1
   plan-review resolved this from the real code too) — `AnalogFmSstvEncoder` is a fully stateless DI
   singleton whose constructor takes only `sampleRate` (`AnalogFmSstvEncoder.cs:16-19`); it has no
   settings dependency to mutate live, matching `sampleRateOffsetHz`/`stationId`'s own established
   per-call-parameter shape on `ISstvEncoder.EncodeAsync` (`ISstvEncoder.cs:34-39`), resolved ONCE per
   transmission by `SstvSessionService.ResolveTransmitSettingsAsync` and threaded through
   `TransmitAsync`'s own `_encoder.EncodeAsync(...)` call (`SstvSessionService.cs:2701-2702`) exactly
   like `sampleRateOffsetHz` already is. **`EstimateSampleCount` needs NO new parameter** — TX BPF/LPF
   change the AUDIO CONTENT of samples, never the sample COUNT, so the existing estimate is already
   correct regardless of these settings.
   **New parameters, explicit defaults (round-2 plan-review finding: state these, not just "4 new
   params")** — placed before `ct`, same convention as `sampleRateOffsetHz`:
   `bool txBpfEnabled = true, int txBpfTapCount = 24, bool txLpfEnabled = false, double
   txLpfFrequencyHz = 2000.0`. These defaults are what make design step 7's golden-fixture invariant
   actually true: every existing 2-arg `EncodeAsync(mode, image)` call site (dozens across the test
   suite) keeps producing byte-identical output with no source change needed anywhere.
   Fan-out (round-2 plan-review completed this list): `ISstvEncoder.EncodeAsync`'s signature,
   `RestartableSstvEncoder.EncodeAsync`'s plain-delegating forward (`RestartableSstvEncoder.cs:47-61`),
   **`tests/ScanlineStudio.Application.Tests/FakeSstvEncoder.cs:69`'s own `EncodeAsync` implementation**
   (compile-caught, but call out explicitly rather than discovering it via a build error), and the
   Loopback self-test's own explicit-defaults call site (`SstvSessionService.cs:2612`) — confirm during
   implementation whether that self-test should pass the REAL resolved settings too (matching every
   other resolved-settings field it already threads through) or deliberately keep explicit legacy
   defaults for self-test determinism; lean toward real settings, matching the self-test's own general
   "exercise what a real transmission would actually do" purpose, but confirm against that call site's
   own existing convention during implementation.
   Four loose params (not a single grouped record, e.g. a `TxAudioFilterOptions`-style type mirroring
   `StationIdTransmitOptions`'s own precedent) is a deliberate choice, not an oversight — matches
   `sampleRateOffsetHz`'s own single-scalar-parameter shape exactly, and these 4 fields don't share the
   internal cohesion `StationIdTransmitOptions`' own fields do (CW/FSK/NR-RST text all describe ONE
   station-ID concept; BPF-enabled/BPF-taps/LPF-enabled/LPF-frequency are two independent, unrelated
   toggles that happen to both be TX-audio-shaping options).
5. **`OptionsWindowView.axaml`/`OptionsWindowViewModel.cs`/`assets/locale/en.json`** (round-2
   plan-review finding: this step's own control list wasn't paired with the ViewModel/locale plumbing
   it needs — completed here): real checkbox for `Options.Advanced.TxBpf` (currently disabled,
   `IsEnabled="False"`) bound to a new `TxBpfEnabled` `[ObservableProperty]`; a new `NumericUpDown` for
   tap count bound to a new `TxBpfTapCount` property, `[2,512]`, `Increment="2"` (even-only), grey-
   gated on `TxBpfEnabled` (matches legacy's own `TxBpfTap->Enabled = CBTXBPF->Checked`,
   `Option.cpp:223`); real checkbox for `Options.Advanced.TxLpf` bound to a new `TxLpfEnabled`
   property; a new `NumericUpDown` for LPF frequency bound to a new `TxLpfFrequencyHz` property,
   `[100,3000]`, grey-gated on `TxLpfEnabled` (matches `TxLpfFreq->Enabled = CBTXLPF->Checked`,
   `Option.cpp:225`). 2 new locale keys for the 2 new numeric-field row labels (e.g.
   `Options.Advanced.TxBpf.TapCount`, `Options.Advanced.TxLpf.FrequencyHz`) — this project's own
   no-hardcoded-UI-strings rule, `NoHardcodedAxamlStringsTests` will catch a miss either way. Load-
   from-snapshot / save / `OptionsSnapshot`/`OptionsSettingsService` 3-site wiring mirrors items 1/2's
   own established shape exactly (`AudioDeviceSettings` this time, not `SstvDecoderSettings`).
6. **`docs/removed-features.md`**: delete the "TX output bandpass filter toggle/tap setting" entry —
   the capability is restored, not removed.
7. **Golden-fixture invariant (state explicitly, don't just imply it)**: defaults MUST remain
   bpf=on/tap=24/lpf=off after this change — `TxCaptureFixtures*`/`GoldenVectorTests.cs:812` and every
   other existing TX-output-comparing test must produce BYTE-IDENTICAL output with no settings
   supplied, same as today. Add this as an explicit assertion somewhere in the new test suite, not
   just an assumption resting on "the defaults happen to match."

## Testing (mirrors items 1/2's discipline)

- `TxOutputBandpassFilterTests.cs` additions: disabled mode (call-site gate, tested via
  `EncodeAsyncCore`/`RenderSegments`, not the filter class itself) is bit-exact passthrough; variable
  tap-count clamp both directions (`[2,512]`, even-rounding); a real spectral/impulse-response
  difference between two different (both in-range, both even) tap counts, not just "doesn't crash";
  the rewritten `MakeFilter_TapCountAndLength_...` test now varies tap count instead of asserting it's
  fixed.
- `AnalogFmSstvEncoderTests.cs` (or wherever `EncodeAsyncCore`/`RenderSegments` is already tested)
  additions: TX LPF disabled reproduces today's EXACT hard-frequency-jump behavior byte-for-byte (a
  regression guard, not just "still works" — this must NOT change existing golden-vector-adjacent TX
  output when off, matching legacy's own `m_lpf=0` default); TX LPF enabled produces the EXACT boxcar
  linear ramp `CSmooz` guarantees (round-1 plan-review correction — NOT "asymptotically approaching,"
  that phrasing would also pass against a wrong exponential-IIR implementation; the correct, hand-
  pinnable identity is sample `k` after a step from `old` to `new` equals
  `(k*new + (N-k)*old)/N` for a window size `N`, reaching `new` EXACTLY at sample `N`, matching
  `MovingAverageTests`' own existing boxcar-average-of-a-known-sequence precedent — **the identity only
  holds after >=N samples of `old` have already filled the window** (round-2 plan-review finding: with
  design step 2's own fresh-per-call `MovingAverage`, a COLD average starts empty, so `Add(new)` on an
  empty buffer returns `new` immediately, not a diluted blend — feed enough `old`-frequency samples to
  fill the window BEFORE the step, or the test fails against genuinely correct code, not a bug); a
  silence/gap sample does not disturb the moving average's held state (feed a tone, silence, tone
  again — confirm the average resumes from where it left off, not reset); the golden-fixture invariant
  from design step 7 above.

**Perf note, not a correctness concern (round-2 plan-review nit)**: `TxOutputBandpassFilter.ProcessSample`
does a full `Array.Copy` shift of the delay line every sample (not legacy's own doubled-ring-buffer
trick, `fir.cpp:1099,1117-1127`, which this port's own class doc comment already documents as a
deliberate simplification). Negligible at the 24-tap default; noticeably heavier at the now-reachable
512-tap ceiling. Out of scope for this item (a perf concern, not a behavioral one) — flagged for a
future pass if a user ever actually cranks the tap count that high, not chased here.

## Plan-review status

Round 1 (auditor): "not equivalent" — 3 blockers, all paper-fixable, all folded in above: (1) wrong
clamp ranges (real legacy ranges are `lpffq [100,3000]`/`tap [2,512]`, not the earlier draft's
guesses); (2) wrong TX-LPF test-assertion shape (exact boxcar linear ramp, not "asymptotic"); (3)
unstated which sample rate feeds the LPF's `SetCount` (nominal, matching the BPF's own already-audited
precedent). Also folded in: the odd-tap-count UB divergence, the call-site-gate-not-ctor-param
correction, the settings-placement and live-apply-shape answers (both determinable from the real code,
closing 2 of the plan's own open items), the `RestartableSstvEncoder`/self-test fan-out, the
`TXLPFFQ` int-persistence quirk (explicitly NOT replicated), and the golden-fixture-invariant
requirement. Auditor's own explicit opinion on the removal-reversal question: **restore both halves**
— the tap count is an easy yes (validated range, zero risk at default, real ham-radio use case); the
on/off toggle is the genuinely arguable one (a footgun whose only real use is A/B debugging) but the
removed-features entry itself never argued the capability was unwanted, only that it was out of scope
for the audit pass that found it — "no standing rationale being overturned, just an unfinished item
being finished."

Round 2 (auditor, verification): explicit "yes, ready to build now" after one 30-second paper fix —
all 3 round-1 blockers independently re-derived and confirmed genuinely correct against the real
source (not just textually present), including a NEW positive confirmation that legacy's own VCO is
separately re-pointed at the offset-corrected rate (`Main.cpp:959,7948`), making "nominal for the
window, effective for phaseIncrement" not just precedent-matching but literally what legacy does. One
real error found: round 1's own odd/even UB claim was stated BACKWARDS (UB is on ODD tap counts, not
even — the underlying clamp-to-even decision was already correct, only the stated reasoning was
wrong) — fixed above. Remaining findings (explicit parameter defaults, UI/loc-key/ViewModel fan-out,
`FakeSstvEncoder.cs` fan-out, boxcar test warm-up requirement, `Array.Copy` perf note) all folded in
above as additive completeness items, not design changes. Proceeding to implementation.
