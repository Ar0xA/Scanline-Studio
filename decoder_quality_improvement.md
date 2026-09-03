# SSTV Decoder Quality Improvement Review

Date: 2026-09-01

Status: source-level audit and recommendation report. No production or test code was changed as part of this review, and no tests were run.

## 1. Executive conclusion

Scanline Studio already has a broad, legacy-faithful SSTV receiver: all 43 registered modes map to a decoder, all 43 have synthetic 44.1 kHz round-trip coverage, and the three YONIQ demodulator choices are present. Hilbert should remain the compatibility default.

The best near-term improvements are not a wholesale replacement of Hilbert. They are, in order:

1. Establish a paired reception-quality harness using the existing real legacy captures, then expand real legacy coverage from 8/43 modes to 43/43.
2. Close confirmed legacy-parity gaps, especially the missing H3 locked filter for MN/MC, incomplete buffered sync-accuracy/replay behavior, incomplete AFC retuning of post-lock tone detectors, and AVT's filter-stage difference.
3. Use the evidence to select one enhanced real-time experiment at a time. The strongest candidates are robust Robot 36 selector estimation, robust sync qualification and reacquisition, bounded timing/AFC tracking, and robust per-pixel frequency estimation.
4. Treat multi-pass search, demodulator ensembles, repeated-transmission combining, and image restoration as explicit offline experiments. Always preserve the raw/compatibility decode.

The important architectural boundary is therefore not “old versus new decoder,” but three explicit receive profiles:

| Profile | Purpose | Expected behavior |
|---|---|---|
| Legacy/compatibility | Preserve and validate YONIQ behavior | Hilbert + Wide BPF by default; retain the existing PLL, zero-crossing, BPF, AFC, peak-pick, and Auto Slant choices. Only evidence-backed parity fixes enter this profile. |
| Enhanced real-time | Deterministic signal-domain improvements | Opt-in alternatives for marker estimation, pixel estimation, sync, AFC, filtering, and reacquisition. The unenhanced result remains available. |
| Experimental/offline | More compute, latency, or invented samples | Multi-pass parameter search, full rerender, demodulator ensembles, repeated-reception combining, concealment, and postprocessing. Never silently replace the received/raw result. |

## 2. Review scope and sources

The review followed repository precedence:

1. Current Scanline Studio implementation and locked project decisions.
2. Legacy YONIQ behavior from the local CP932/Windows-31J source clone.
3. The local QSSTV reference clone.
4. Current tests and specifications.
5. Independent open-source SSTV decoders on GitHub.

The external projects were used as idea and behavior references, not as evidence that an algorithm will improve Scanline Studio:

- [QSSTV](https://github.com/ON4QZ/QSSTV): relative-energy sync qualification, persistent pulse-chain tracking, dropout recovery, quadrature demodulation, and longer-chain slant fitting.
- [xdsopl/robot36](https://github.com/xdsopl/robot36): explicit complex mix-down, Kaiser low-pass filtering, consecutive-phase FM discrimination, Schmitt-trigger sync detection, pulse-width classification, and measured sync-frequency offset. The relevant implementation is its [Demodulator.java](https://github.com/xdsopl/robot36/blob/v2/app/src/main/java/xdsopl/robot36/Demodulator.java).
- [F4JTV/sstv_decoder](https://github.com/F4JTV/sstv_decoder): raw frequency-stream retention, iterative outlier rejection/RANSAC for slant, calibration locking, rerendering, weak-signal thresholds, sync-quality reporting, and averaging samples assigned to a pixel. This is a very young repository and is an idea source, not validation; see [sstv_decoder.cpp](https://github.com/F4JTV/sstv_decoder/blob/main/src/sstv_decoder.cpp).
- [smolgroot/sstv-decoder](https://github.com/smolgroot/sstv-decoder): a browser implementation derived from xdsopl/robot36. Its bidirectional smoothing and browser sample-rate handling are possible experiments, but its documented false-sync behavior means it should not be treated as a reference implementation.
- [SSTV-MEL](https://github.com/kevinnz/SSTV-MEL): manual phase and skew controls reinforce the value of an operator fallback when automatic correction cannot converge.

QSSTV is GPL-2.0-or-later, while this project is LGPL-3.0-or-later. F4JTV's repository does not clearly state a repository-wide license. Algorithms may be independently evaluated, but source, coefficient tables, or other assets must not be copied without an explicit licensing decision and a `LICENSES.md` entry where required.

## 3. Current receiver baseline

### 3.1 Demodulators

- Hilbert is the current default. It constructs an analytic signal and uses consecutive phase difference followed by smoothing; it is already a phase discriminator, not merely a raw Hilbert transform ([HilbertFmDemodulator.cs](src/ScanlineStudio.Core.Sstv/HilbertFmDemodulator.cs)).
- PLL is the legacy selectable closed-loop alternative. Its acquisition and settling behavior differ from the feed-forward Hilbert path ([PllFmDemodulator.cs](src/ScanlineStudio.Core.Sstv/PllFmDemodulator.cs)).
- Zero crossing is the legacy selectable frequency-counter alternative with interpolation and configurable smoothing ([ZeroCrossingFrequencyCounter.cs](src/ScanlineStudio.Core.Sstv/ZeroCrossingFrequencyCounter.cs)).
- AVT uses its own training PLL and must not be folded into assumptions about ordinary per-line sync/AFC behavior.

The existing real-fixture comparison is sparse. For MN110, zero crossing currently has a substantially larger mean pixel delta than PLL or Hilbert; that is a reason not to make zero crossing the narrow-mode default, not proof that it has no useful impairment regime ([GoldenVectorTests.cs](tests/ScanlineStudio.Core.Sstv.Tests/GoldenVectorTests.cs)).

### 3.2 Modes and decoder strategies

All 43 modes are registered and mapped across five decoder strategies:

| Decoder strategy | Modes | Real legacy-audio RX coverage |
|---|---|---:|
| RGB sequential | Martin M1/M2; Scottie S1/S2/DX; AVT; P3/P5/P7; MC110/140/180; SC2-180/120/60 | 3/15: M1, S1, AVT |
| Robot YCbCr | Robot 36 | 1/1 |
| Sequential YCbCr | Robot 72, R24, MR73/90/115/140/175, ML180/240/280/320 | 1/11: Robot 72 |
| Line-paired YCbCr | MP73/115/140/175; PD50/90/120/160/180/240/290; MN73/110/140 | 2/14: PD90, MN110 |
| Mono averaged/paired | RM8/RM12 | 1/2: RM8 |

The registry and strategy mapping are in [SstvModeRegistry.cs](src/ScanlineStudio.Core.Sstv/SstvModeRegistry.cs) and [ScanlineCodecFactory.cs](src/ScanlineStudio.Core.Sstv/ScanlineCodecFactory.cs). Synthetic coverage is broad, but an internal encoder/decoder round trip can remain self-consistently wrong and cannot establish weak-signal reception quality.

### 3.3 Current evidence limitations

- Real legacy-transmitted audio is decoded by the current RX path for only 8/43 modes. Existing specification language implying 11-mode full RX coverage overstates the current tests: Scottie DX, MR73, and R24 occur in a skipped/pending TX-direction table, not current RX execution.
- The noise harness covers only Martin M1 and Robot 36, uses this port's own encoded audio, one deterministic seed, and stationary AWGN ([NoiseRobustnessTests.cs](tests/ScanlineStudio.Core.Sstv.Tests/NoiseRobustnessTests.cs)).
- The noise sweep says it stops at the first failure but does not break; a later lower-SNR pass can overwrite the first failing result and mask non-monotonic behavior.
- Golden RX tests compare the current decode with the original source image, while the paired legacy `_RX.bmp` is evaluated separately. A direct current-RX-versus-legacy-RX measurement is missing ([GoldenVectorTests.cs](tests/ScanlineStudio.Core.Sstv.Tests/GoldenVectorTests.cs)).
- The image corpus is primarily smooth gradients. Mean absolute channel error alone is insensitive to some structural failures such as line displacement, chroma-state swaps, mirror/flip errors, local dropouts, and residual slant.
- Non-Hilbert real-fixture comparison covers only Martin M1 and MN110. It is not enough to select a demodulator by family or impairment.

## 4. Measurement foundation: first priority

Do not wait for a complete 43-mode OTA corpus before learning anything. Build the evidence in three increments.

### P0a — use the existing eight real captures

For every current fixture, run two paths:

- Automatic acquisition: measures leader/header/VIS/FSK detection, mode choice, anchor accuracy, false starts, and restarts.
- Forced correct mode: isolates demodulation, AFC, sync, slant, scanline reconstruction, and color conversion.

Compare:

- current RX directly with paired legacy `_RX.bmp`;
- current RX and legacy RX separately with the original source;
- each selectable demodulator and BPF setting on identical immutable PCM.

This distinguishes compatibility, actual source quality, and changes that are different from legacy without being worse.

### P0b — clean coverage for all 43 modes

Add clean legacy-transmitted audio and paired legacy RX output for every mode. Multiple fixtures are necessary for special paths rather than assuming a family representative covers them:

- normal VIS and extended VIS;
- 1200 Hz and 1900 Hz sync families;
- Scottie mid-line sync and Scottie DX;
- AVT training/no ordinary line sync;
- Robot 36 stateful chroma selector and Robot 72 fixed markers;
- line-paired output, forced parity, row doubling, and high-resolution/long-duration modes.

Use a corpus containing color bars, hard vertical and horizontal edges, a registration grid, fine alternating detail, grayscale ramps, saturated chroma, and natural photographs.

### P0c — controlled impairments, then independent OTA validation

Apply deterministic impairments to frozen legacy audio:

- AWGN with several held-out seeds;
- colored noise and adjacent/co-channel tones;
- static carrier offset, linear drift, wandering drift, and Doppler-like trends;
- sample-clock error in both directions;
- hard and soft clipping;
- isolated impulses and burst noise;
- dropouts placed independently in headers, sync intervals, and picture scans;
- slow fading, selective attenuation/notches, and echo/multipath;
- a small, predeclared set of realistic combined impairments.

Then validate leading candidates on an independent corpus captured through real receivers. Controlled impairments provide repeatability; OTA captures provide external validity. They should not be conflated.

### Required metrics and gates

| Area | Metrics |
|---|---|
| Acquisition | Correct-mode rate, false-lock rate on noise-only/corrupt-header input, lock latency, anchor error, restart count |
| Continuity | Decoded-line fraction, missing/duplicated lines, longest damaged run, lines required to reacquire |
| Geometry | Per-line edge displacement, sync residual/jitter, end-of-image displacement, residual slant ppm |
| Image | Current-versus-legacy RX delta, source MAE by luma/chroma/channel, SSIM, edge-map displacement, p95/p99 line error |
| Robustness | Quality-versus-impairment curve, area under the curve, failure threshold and confidence interval across seeds/captures |
| Runtime | p95 real-time factor, allocations, bounded memory, latency, and rerender cost on a documented machine |

Clean-audio parity, false-lock safety, mode-family non-regression, and faster-than-real-time headroom should be hard gates. A candidate should advance only when its paired improvement has a confidence interval excluding zero on its targeted impairment and it remains inside predeclared clean/family regression budgets.

## 5. Confirmed parity and implementation gaps

These are source-verified gaps or explicit divergences. They should be evaluated before novel algorithms because their expected behavior is better defined.

### 5.1 Port the missing H3/HBPFN locked narrow filter

YONIQ constructs a distinct, mode-specific H3 filter for MN/MC and uses it after lock. Scanline Studio explicitly omits H3 and keeps narrow modes on the search filter ([SearchBandpassFilter.cs](src/ScanlineStudio.Core.Sstv/SearchBandpassFilter.cs), [AnalogFmSstvDecoder.cs](src/ScanlineStudio.Core.Sstv/AnalogFmSstvDecoder.cs)).

Affected modes: MN73/110/140 and MC110/140/180.

Expected benefit: better rejection of adjacent signals and out-of-band interference, especially with Narrow or Very Narrow selected. Validate at every supported sample rate and against clean golden vectors before claiming improvement.

### 5.2 Fix buffered replay before restoring full Sync Accuracy behavior

The current replay path deliberately jumps over one resume row/window and later discards earlier staged history. This can leave a row unredrawn and prevents later correction from reprocessing the entire earlier image ([AnalogFmSstvDecoder.cs](src/ScanlineStudio.Core.Sstv/AnalogFmSstvDecoder.cs)).

YONIQ performs a buffered refresh around line 16 and, at its higher Sync Accuracy level, another around line 32. Scanline Studio only implements the first accuracy bit and gates the line-16 replay because replay currently sacrifices a row. The correct order is:

1. make replay lossless and bounded;
2. verify that repeated correction does not corrupt sync-detector state;
3. restore the legacy refresh policy and 3-versus-4-line folding behavior.

The initial folding difference particularly matters to Scottie DX, PD240, MP140, MP175, and MN140.

### 5.3 Share AFC correction with all applicable post-lock tone detectors

The AFC estimate currently retunes the slant sync-envelope detector, while separate locked VIS/FSK/sync-bypass resonators remain at nominal centers. This makes mid-image restart and narrow FSK paths less tolerant of persistent carrier offset ([AfcTracker.cs](src/ScanlineStudio.Core.Sstv/AfcTracker.cs), [VisLockStateMachine.cs](src/ScanlineStudio.Core.Sstv/VisLockStateMachine.cs), [AnalogFmSstvDecoder.cs](src/ScanlineStudio.Core.Sstv/AnalogFmSstvDecoder.cs)).

Use one bounded, confidence-qualified offset for every applicable post-lock detector. Keep initial pre-AFC header acquisition separate, and do not route generic line-sync AFC into AVT's training PLL.

### 5.4 Use the measured VIS/header origin

The ordinary fixed-window VIS path can accept a header with varying lead-in displacement but commit a fixed assumed origin. The code documents up to roughly 185 ms of anchor shift, which is especially visible for AVT and can fold several lines in fast non-AVT modes. The narrow path already returns a measured trigger origin; the normal/extended path should be evaluated against the same principle.

### 5.5 Replace the sync-bypass midpoint approximation with verified staged alignment

When VIS is missing or damaged, the current mode registry uses an acknowledged midpoint approximation rather than YONIQ's staged `m_wBgn` alignment. This can misplace the first line in recovery mode. Port and validate the staged bootstrap before adding more speculative sync-bypass heuristics.

### 5.6 Restore VIS tracker freeze semantics if captures show false acquisition

The primary sync-interval tracker can advance during VIS bit phases where YONIQ held it. This can contaminate interval history or start sync-bypass acquisition during an impaired but valid header. The difference is real, but its practical impact is unproven; rank it below H3, replay, AFC retuning, and anchor work unless a capture reproduces it.

### 5.7 Correct AVT's legacy training filter stage

AVT training currently uses the search H2 filter rather than YONIQ's H1 stage. This is a clearer AVT quality/parity candidate than applying ordinary line-sync correction to AVT. AVT capture/replay is also absent, but any replay design must respect its separate training PLL and lack of normal per-line sync.

### 5.8 Optional legacy calibration and differentiator

YONIQ supports demodulator calibration/asymmetric black-white correction at pixel reads and an optional differentiator for sharpening. Scanline Studio's default mapping captures the normal linear constants but lacks the general calibration hook and optional differentiator ([PixelSampleReader.cs](src/ScanlineStudio.Core.Sstv/PixelSampleReader.cs), [YCbCr.cs](src/ScanlineStudio.Core.Sstv/YCbCr.cs)).

These are compatibility features, not default weak-signal improvements. Calibration may correct a biased audio/demodulation chain; differentiation can sharpen clean images while amplifying noise. Keep both optional and test them separately.

## 6. Enhanced real-time experiment queue

This is a hypothesis queue, not an implementation batch. Select the next experiment from P0 results, change one stage at a time, and measure attribution before combining improvements.

### 6.1 Robust Robot 36 selector estimation

Current Robot 36 selection is determined from a single sample near the decisive window; ambiguity toggles the previous state. One bad decision can put alternating R-Y/B-Y reconstruction into the wrong state across lines ([RobotScanlineDecoder.cs](src/ScanlineStudio.Core.Sstv/RobotScanlineDecoder.cs)). QSSTV instead gathers evidence across the selector/gap interval.

Experiments:

- average, trimmed mean, or median frequency over the stable interior of the marker;
- compare energy near the two selector tones using short matched/Goertzel estimators;
- attach a confidence score and use the expected alternation only as a prior, not as fabricated evidence;
- retain legacy single-sample behavior in compatibility mode.

Acceptance: per-line R-Y/B-Y classification accuracy, maximum consecutive swapped-chroma run, clean-fixture parity, and quality curves under noise, offset, impulses, and selector-window dropouts.

### 6.2 Robust per-pixel frequency estimation

Current pixel reads use one sample or the maximum of two, so a positive impulse can dominate a pixel and a single noisy demodulated sample becomes image data ([PixelSampleReader.cs](src/ScanlineStudio.Core.Sstv/PixelSampleReader.cs)). QSSTV's active picture path also uses a single current sample; its unused accumulator is not an averaging method to port.

Experiments:

- central-window mean, trimmed mean, or median within each pixel dwell;
- fractional-position interpolation rather than `ceil`-selected samples;
- a short phase-slope/least-squares frequency estimate over the stable middle of the dwell;
- amplitude/envelope confidence weighting for phase estimates during fades;
- mode-dependent window width and filter bandwidth.

Risk: transition bleed and horizontal blur, especially in Martin M2, Scottie S2, SC2-60, PD50, MR73, and other short-pixel modes. Gate on edge displacement/MTF and p95 line error, not only mean image delta.

### 6.3 Relative-energy and matched-pulse sync qualification

Current sync anchoring relies heavily on a largest envelope sample, and the level AGC uses a largest absolute sample over its window. Both are vulnerable to impulses. QSSTV qualifies 1200/1900 Hz energy relative to input energy and maintains pulse chains; xdsopl/robot36 uses smoothing, Schmitt hysteresis, pulse-width classification, and measured frequency offset.

Experiments:

- narrowband-to-broadband energy ratio plus an absolute minimum level;
- Schmitt hysteresis and refractory timing;
- pulse-width and expected-interval consistency;
- matched-pulse correlation or a centroid instead of a single envelope maximum;
- percentile or dual-time-constant level tracking instead of maximum-only AGC.

Initial acquisition, per-line anchoring, and mid-image reacquisition must be measured separately. Normal 1200 Hz sync, narrow 1900 Hz sync, Scottie mid-line sync, and AVT training are distinct strata; one threshold set should not be assumed to suit all four.

### 6.4 Persistent sync-chain tracking and dropout reacquisition

QSSTV validates a chain of sync pulses, handles split/long pulses, pauses on loss, searches around the predicted next line, rewinds buffered demodulated samples after reacquisition, and catches up. A bounded form could improve burst-fade survival.

Keep signal recovery distinct from concealment:

- timing prediction may gate where to search for the next real sync;
- predicted pixels/rows must be flagged and must not feed AFC or timing estimators;
- retain a partial/raw result if reacquisition fails;
- measure delayed termination and false-chain lock as well as lines recovered.

### 6.5 Robust slant and sample-clock tracking

The current legacy estimator uses a short recent-sync history and buffered replay. F4JTV uses iterative outlier rejection/RANSAC and freezes calibration after low jitter; QSSTV fits a longer accepted chain and redraws buffered data.

Experiments:

- robust regression with outlier rejection over accepted syncs;
- bounded alpha-beta/Kalman-like clock tracking for slowly varying error;
- explicit lock/freeze/unfreeze hysteresis so a late fade cannot corrupt a good estimate;
- piecewise tracking for non-linear drift rather than assuming a single frame-wide line;
- bounded rerender windows for real-time operation.

Acceptance: residual slant ppm, end-of-image edge displacement, completion rate, memory, and latency at ±50/100/500/1000 ppm plus combined drift/fading. Full-frame rerender belongs to the offline profile unless strict bounds preserve live behavior.

### 6.6 Bounded continuous AFC and Doppler tracking

The existing AFC uses known sync-tone frequency but has a bounded acquisition window and cooldown. For MN/MC, the accepted high-side offset is especially narrow. Test a coarse acquisition estimate followed by a lower-noise tracking loop:

- robust median/trimmed sync-frequency estimate;
- bounded slew and confidence gating;
- distinguish constant tuning offset from sample-clock timing error;
- track a line-by-line frequency trend for satellite/Doppler reception;
- freeze or decay gracefully through missing sync rather than accepting picture tones as references.

Measure residual Hz, acquisition range, cycle/lock oscillation, false correction, and quality separately for normal, narrow, Scottie, and AVT paths.

### 6.7 Mode-aware locked filtering and impulse handling

After the missing legacy H3 is restored, test rather than assume automatic filter selection:

- acquire wide, then switch to a mode-aware locked passband;
- preserve separate sync-tone evidence when the picture band is narrowed;
- detect persistent narrow interferers and offer a bounded notch;
- use envelope/frequency plausibility to hold or interpolate isolated discriminator outliers;
- test impulse blanking before and after FM demodulation.

Very Narrow is not automatically “best”: narrower filters improve interference rejection but increase settling, group delay, ringing, and fast-pixel blur. Report false blanking and clean-edge loss.

### 6.8 VIS and FSK soft decisions

For discrete tones, use time integration rather than picture-pixel methods:

- matched filters or Goertzel bins for 1100/1200/1300 Hz VIS and 1900/2100 Hz narrow FSK;
- integrate the stable middle of a bit and exclude transitions;
- retain soft confidence for parity/candidate scoring;
- combine VIS confidence with plausible line interval without turning a weak guess into an automatic lock;
- keep forced-mode/manual-start fallback.

This is a better use of tone banks than applying Goertzel bins to the continuously varying 1500–2300 Hz picture signal.

## 7. Demodulator-specific recommendations

### 7.1 Hilbert: keep as default and improve around it

Hilbert's current analytic-signal/consecutive-phase discriminator is a strong low-latency baseline. Candidate improvements are:

- gate or down-weight phase estimates when analytic-signal magnitude collapses;
- reject isolated implausible output frequencies with a confidence flag rather than unconditional clipping;
- benchmark mode-specific post-discriminator smoothing;
- account explicitly for filter group delay at mode transitions and anchors;
- expose quality telemetry such as envelope floor, outlier rate, and sync residual.

Do not add a nominally different “complex phase discriminator” unless it makes a materially distinct front-end hypothesis.

### 7.2 Explicit complex mix-down/IQ filtering: benchmark the front end, not the name

QSSTV and xdsopl/robot36 mix around 1900 Hz, apply a complex low-pass/channel filter, then use consecutive complex phase difference. The discriminator principle substantially overlaps the current Hilbert path. The meaningful experiment is whether explicit mix-down plus a deliberately designed complex channel filter provides better adjacent-channel rejection, envelope confidence, or alias control.

It must beat Hilbert on a predeclared impairment family while meeting clean parity, transient, group-delay, CPU, and latency gates. QSSTV's fixed 12 kHz coefficients must not be copied or reused for Scanline Studio's sample rates.

### 7.3 PLL: adaptive loop bandwidth as an experiment

The legacy PLL can reject some noise but incurs acquisition/settling lag. An enhanced PLL could use:

- a wide loop during acquisition and a narrow loop after lock;
- a lock metric based on phase error and cycle-slip detection;
- bounded transitions between loop settings;
- separate mode tuning, especially for narrow MN/MC;
- preservation of AVT's independent training behavior.

Measure acquisition time, cycle slips, residual error, transient edge response, and quality versus SNR/drift. Do not replace the legacy selectable PLL without evidence.

### 7.4 Zero crossing: retain compatibility role and define its winning regime

Zero crossing is amplitude-independent in principle but is vulnerable to noise/harmonic-created crossings and sparse timing at low sample rates. Possible enhanced variants include:

- Schmitt/hysteretic crossings;
- slope and amplitude validity gates;
- robust median of several half-period estimates;
- explicit hold behavior through implausible intervals;
- higher-rate internal timing interpolation.

The idea that it is best for clipped signals is plausible but currently unproven. Run hard/soft clipping sweeps against Hilbert and PLL before labeling or demoting it.

### 7.5 Other estimators

- Short phase-slope/Kay-style frequency estimators are plausible per-pixel experiments under AWGN, but transitions and multipath can bias them.
- Teager-Kaiser energy and time-frequency ridge estimators are research-tier ideas with uncertain benefit for a slowly swept audio tone; do not prioritize them before the simpler experiments.
- A bank of correlators is well suited to discrete VIS/sync/selector tones, but inefficient and quantizing for continuous picture frequency.

## 8. Mode-specific priorities

| Family/path | Highest-value concerns and experiments |
|---|---|
| MN/MC | Restore H3 first; separately validate 1900 Hz sync, 1900/2100 FSK, AFC acquisition range, locked detector retuning, and Hilbert/PLL behavior. |
| Robot 36 | Robust information-bearing selector; prevent one ambiguous marker from producing a long swapped-chroma run; test selector-window dropouts. |
| Robot 72 | Do not inherit Robot 36 assumptions: its markers are fixed and it uses sequential chroma. |
| Scottie | Mid-line sync needs its own anchor tests; S2 stresses short pixels; DX stresses long-duration timing and has the deliberate bare-sampling exception. |
| AVT | Correct H1 training filtering; measure training-PLL lock and fixed-window origin. Exclude it from generic per-line sync/AFC/slant logic. |
| PD/MP/MN line-paired | A damaged transmitted unit can affect two output rows; measure paired-row propagation and chroma sharing. PD50 stresses fast pixels; PD240/290 and long MP/MN stress timing. |
| MR/ML/MP | Extended VIS is a separate acquisition stratum. Test all extended codes, hold gaps, long frames, and high-resolution cumulative timing. |
| Pasokon P3/P5/P7 | Add real fixtures for high-resolution timing, acquisition, memory, and fine-edge detail. |
| SC2 | SC2-60 is a fast-pixel/settling stress case; do not tune smoothing only on SC2-180. |
| R24 | Validate its 120 received rows and display-row doubling; mean image error can hide the wrong shape. |
| RM8/RM12 | RM12 requires explicit forced-parity evidence. Monochrome is not permission for stronger denoising without edge/detail tests. |

## 9. Experimental and offline ideas

### 9.1 Bounded multi-pass parameter search

For saved audio, decode a small predeclared grid of frequency offset, clock ppm, BPF, demodulator, and estimator settings. Score candidates using sync coverage/jitter, valid line count, residual slant, tone consistency, and conservative image statistics. Choose on held-out captures and impose a fixed compute budget to avoid fixture overfitting.

This may deliver more value than per-sample algorithm switching because every pass remains internally consistent. Preserve all parameters and the first-pass compatibility image in provenance metadata.

### 9.2 Demodulator ensemble

Run Hilbert, a filtered mix-down variant, and optionally PLL or zero crossing from the same stored audio. Select per frame or stable line segment using confidence and consensus. Do not switch blindly per sample: different filters have different delay, settling, and error statistics.

Advance only if individual P1 experiments show that no single demodulator dominates and the ensemble beats the best single path on held-out captures within a fixed compute/memory budget.

### 9.3 Repeated-transmission combining

For known repeats of the same image, align by mode, sync lines, geometry, and confidence, then robustly combine demodulated frequency or pixel evidence. This is promising for satellite/repeater events, but analog SSTV provides no reliable content identity. Combining must be explicit user action with a hard refusal when mode/content/alignment confidence is inadequate.

### 9.4 Diversity input

If future capture exposes two receiver channels or true I/Q, compare channel selection, coherent I/Q input, or confidence-weighted diversity. xdsopl/robot36 already accepts left, right, sum, or complex stereo I/Q. This is a future input-path experiment, not a current mono decoder recommendation.

### 9.5 Postprocessing and ML

Prefer signal-domain correction before RGB-domain filtering:

- isolated-impulse median/robust filtering on frequency evidence;
- edge-aware horizontal filtering with stronger chroma than luma smoothing only when measured;
- confidence-marked interpolation of dropouts;
- optional light final median filtering as a presentation layer.

ML restoration can hallucinate detail and callsigns that were not received. If explored, label it visibly, retain the raw decode, record model/settings, evaluate blind held-out images, and never use restored pixels as decoder evidence.

## 10. Suggested execution order

1. **P0a measurement harness:** reuse the eight real captures; split auto/forced mode; directly compare current and legacy RX; add structural and acquisition metrics.
2. **Parity tranche 1:** H3 narrow filter, lossless replay foundation, AFC retuning coverage, measured VIS origin, and AVT H1 training behavior. Each item receives its own legacy-captured golden evidence.
3. **P0b coverage:** capture paired legacy audio/RX for the missing 35 modes, prioritizing MC, extended VIS, Scottie DX/S2, Pasokon, RM12, R24, and timing extremes.
4. **P0c impairments:** controlled sweeps first, then an independent OTA corpus.
5. **First enhanced experiment:** choose the limiting stage shown by data. Robot 36 selector integration is the most targeted candidate; sync-chain recovery is likely the broadest; pixel-window estimation has high upside but greater edge-smear risk.
6. **Demodulator A/B work:** only if evidence shows the Hilbert front end is limiting. Benchmark explicit filtered mix-down and adaptive PLL independently.
7. **Offline experiments:** bounded multi-pass search, ensemble, repeated-frame combining, and optional presentation processing.

Do not land P1 ideas as a batch. Filtering, demodulation, sync, AFC, timing, and concealment interact; one-change-at-a-time paired experiments are required to attribute gains and catch mode-specific regressions.

## 11. Items not recommended as immediate work

- Replacing Hilbert globally: no current evidence supports it.
- Copying QSSTV pixel averaging: its active picture path does not use the calculated average.
- Treating a mix-down phase discriminator as fundamentally different from the existing Hilbert phase discriminator without a distinct filter hypothesis.
- Making Very Narrow automatic everywhere: settling and edge loss can outweigh interference rejection.
- Applying ordinary line-sync correction to AVT.
- Letting concealed/predicted pixels update AFC or timing state.
- Using RGB denoising or ML output as the canonical received image.
- Tuning against only a smooth gradient, one AWGN seed, one sample rate, or aggregate MAE.
- Copying GPL source/coefficient tables or unlicensed code into the LGPL project.

## 12. Final recommendation

Preserve Hilbert and the current decoder choices as the compatibility baseline. First make the receiver measurably faithful where known legacy gaps remain. Then add an opt-in enhanced profile whose algorithms are selected by paired evidence rather than intuition. The most promising quality gains are better narrow-mode filtering, lossless timing replay, consistent AFC/tone retuning, robust sync/reacquisition, and robust marker/pixel evidence. More novel methods are best pursued offline, where multiple internally consistent passes can be compared without destabilizing live reception or obscuring what was actually received.
