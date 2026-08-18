# Testing

## Related

Cross-cutting — applies to every module in [[01-architecture]]'s layering diagram. Satisfies CLAUDE.md's "Write tests for all new functionality" and the >80% coverage success criterion in [[00-project-overview]].

## Philosophy

The architecture in [[01-architecture]] exists largely *in service of* testability: every hardware/OS boundary (`IRadioTransport`, `IAudioEngine`, `IRadioProtocol`) is an interface specifically so the layer above it can be tested without real hardware. Testing strategy is therefore mostly "use the seam that's already there," not a bolted-on afterthought.

## Test layers

| Layer | Tool | What it covers | Runs where |
|---|---|---|---|
| Unit | xUnit | Pure logic: CAT backend client response parsing ([[03-cat-layer]]), DSP encode/decode ([[06-sstv-dsp]]), settings migration ([[12-settings]]), ADIF import/export ([[08-logging]]) | Every PR, every OS in CI matrix |
| Integration (in-process fakes) | xUnit | `IRadioController` orchestration against `FakeRadioTransport`/fake protocol; `RigctldClientProtocol` against scripted rigctld responses via `FakeRadioTransport` ([[04-rigctld]] — a standalone `RigctldServer` was scoped in an earlier draft and later dropped, see that spec's own note); plugin load/unload ([[11-plugin-system]]) | Every PR |
| View-model / headless UI | xUnit + Avalonia.Headless | View-model behavior against faked `ScanlineStudio.Application` services ([[09-ui]]) | Every PR |
| Architecture tests | NetArchTest (or equivalent) | Layering rules: UI never references radio/audio/DSP concretes ([[01-architecture]], [[09-ui]]); plugins only see `ScanlineStudio.Abstractions` ([[11-plugin-system]]) | Every PR |
| Manual hardware verification | n/a | Real rig CAT behavior, real audio device round-trip, real `rigctld` interop | Before each release, checklist-driven, tracked in [[14-roadmap]] |

Real hardware (actual radios, actual sound cards) is deliberately **not** part of the automated suite — it can't run in CI. Every hardware-facing interface is instead tested against a fake at the seam, with a documented manual checklist bridging the gap before release.

## Coverage target

≥80% line coverage on every `ScanlineStudio.Core.*` and `ScanlineStudio.Application` project is the long-term target, measured via `coverlet` (`coverlet.collector`) and enforced in CI (`.github/workflows/ci.yml`'s `coverage` job, gated against `coverage-thresholds.json` via `scripts/check-coverage.py`). **As currently enforced, the gate is not yet a blanket 80% floor**: per that file's own comment, each project's threshold is set a few points below its own currently-measured coverage (ranging from 36% for `ScanlineStudio.Core.Audio.MiniAudio` up to 97% for `ScanlineStudio.Core.Radio.Cat`) to catch a regression from today's baseline, not to retroactively demand 80% everywhere in one PR — `ScanlineStudio.Core.Audio.MiniAudio`'s native interop shims are the concrete example of a project that's legitimately harder to cover meaningfully than others. `ScanlineStudio.UI` and `ScanlineStudio.Host` are exempt from the numeric threshold (view XAML and composition-root wiring are better verified by the headless view-model tests and manual smoke testing respectively) but still carry view-model tests per the table above. `ScanlineStudio.Core.Sstv` is also excluded from the numeric gate (its 900+-test suite runs uninstrumented in the regular 3-OS matrix, but coverage instrumentation for it is currently cost-prohibitive in CI).

## Golden-vector testing (legacy parity)

Added after review, because it's the one gap a purely self-consistent test suite cannot close: a round-trip test (encode then decode, or serialize then deserialize) can pass while a new implementation is systematically wrong, as long as both halves are wrong the same way. That's a real risk here specifically because the DSP port moves from `double` (legacy C++) to `float` (spec/06's DSP core) — silent numeric drift is exactly the failure mode a round-trip test is blind to. This does **not** apply to [[03-cat-layer]] — no CAT protocol is ported/reimplemented in this codebase, so there is no legacy CAT byte-framing to hold parity against (see that spec's Testing section for what CAT backend clients are tested against instead).

**Golden vectors** close this gap: reference input/output pairs captured from the actual legacy implementation (not re-derived by re-reading the C++ a second time), checked into the relevant fixtures directory, and asserted against with a documented tolerance.

| Where | What to capture | Tolerance |
|---|---|---|
| [[06-sstv-dsp]] | FIR filter output, FFT magnitude frames, and final decoded pixel values for known inputs, captured from a real run of the legacy binary | Documented per-stage tolerance (e.g. max pixel ΔE for the final image; relative error bound for intermediate filter/FFT stages) |
| [[08-logging]] | ADIF round-trip against real-world third-party exports | Exact field match after documented lossy-field list |

Capturing legacy output requires being able to run the legacy binary (Windows, C++Builder runtime) at least once during development — this is a manual, one-time capture step per fixture, not part of CI (CI only replays the already-captured fixture against the new implementation).

## Test data / fixtures

- CAT backend client fixtures: as implemented, `rigctld` wire responses are scripted inline inside the test files themselves (e.g. `RigctldClientProtocolTests.cs`, verified against a local Hamlib source clone) rather than checked into a `Fixtures/` directory — `tests/ScanlineStudio.Core.Radio.Tests/Fixtures/` does not exist yet ([[03-cat-layer]]).
- SSTV audio/image fixtures: a small fixed set of test images (color bars, a photo, a synthetic edge-case image) and their known-good encoded waveforms, checked into `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/` ([[06-sstv-dsp]]).
- ADIF fixtures: not built yet — current ADIF tests (`AdifImporterTests`/`AdifExporterTests`) round-trip synthetic in-code `QsoRecord`s, not real-world third-party exports; `tests/ScanlineStudio.Core.Logbook.Tests/Fixtures/` does not exist yet ([[08-logging]]).
- Legacy settings fixtures: not built yet — no legacy `Mmsstv.ini` importer exists ([[12-settings]]'s own Definition-of-done confirms this is still open), so there is nothing to fixture against yet.

Fixtures are treated as test code: reviewed in PRs, not regenerated casually, since several (CAT byte sequences, ADIF samples) encode real-world protocol quirks that would be lost if "cleaned up."

## CI

GitHub Actions matrix across `windows-latest`, `ubuntu-latest`, `macos-latest`, running `dotnet test` with coverage collection on every PR, satisfying the "GitHub Actions CI" success criterion in [[00-project-overview]]. Audio-engine tests that require a real backend initialization (not just `FakeAudioEngine`) are tagged and skipped in CI (headless runners typically have no audio device) but run in the manual pre-release checklist.

## Definition of done

- [x] CI matrix (3 OSes) running unit + integration + view-model + architecture tests on every PR (`.github/workflows/ci.yml`'s `build-and-test` job, `dotnet test` across the whole solution).
- [ ] Coverage gate enforced per-project at ≥80% for all `ScanlineStudio.Core.*`/`ScanlineStudio.Application` projects.
- [ ] Fixture directories established for CAT, SSTV, ADIF, legacy-settings as described above.
- [ ] At least one golden-vector fixture (captured from a real legacy-binary run, not re-derived from source) exists for each row in the golden-vector table above, with its tolerance documented alongside it.
- [ ] Manual pre-release hardware checklist documented (real rig(s), real audio device, real `rigctld`) and linked from [[14-roadmap]] release gates.
