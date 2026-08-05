# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale.

## Resume here (2026-08-05, latest, ACTIVE) — Phase 3 (minimal UI) FULLY DONE, all 13 tasks, real e2e RX/TX proven over real hardware

**Plan file**: `/home/artien/.claude/plans/hidden-conjuring-curry.md` (all 13 tasks complete). Full solution
test suite: **740/740 green**.

**What shipped, beyond the tasks-1-5 entry below**: real design-tokens/theme layer, three real Dock
panes (Waterfall, RX Image, TX Controls — the radio/frequency readout is a fixed status strip in
`MainWindow` chrome, not a 4th pane, confirmed correct against both `spec/14-roadmap.md` and
`spec/09-ui.md`), a minimal scrolling grayscale `WaterfallControl`, and a real end-to-end RX/TX
demonstration.

**Aesthetic pivot, mid-session — now the durable spec, not just a chat note**: user gave a detailed
"Aesthetic Directive" (raw instrumentation look like cuSDR64/Perseus/SDR++, explicitly *not* modern
SaaS: no rounded corners, no gradients/shadows, 2-4px padding max, monospace for all numeric readouts,
bordered "module group" containers). Recorded verbatim in `spec/09-ui.md`'s "Visual design direction"
section (supersedes the earlier softer SDR++ paragraph). Applied immediately: `CornerRadius="0"`
globally on Button/ToggleButton/ComboBox/TextBox/CheckBox, spacing tokens shrunk from 8/16px to 2/4/6px,
a `YoniqMonospaceFontFamily` resource + `TextBlock.YoniqReadout` class, a `Border.YoniqModuleGroup`
style (Avalonia has no native GroupBox). **User also explicitly deprioritized the waterfall's visual
polish** relative to RX/TX image handling and templating (saved as memory
`feedback_ui_effort_allocation` — read that before sinking more effort into waterfall visuals in a
future session) — the waterfall control was deliberately kept simple (flat grayscale, fixed dB range),
consistent application of the design philosophy mattered more than polishing any one component.

**Real bugs/gaps caught by an actual second auditor round on the steps-6-13 continuation plan** (not
just by review — by re-reading the actual shipped code): (1) the original e2e-demo plan was
self-contradicting — `TransmitAsync` deliberately pauses capture during TX, so a *single* process
structurally cannot self-decode its own transmission; fixed by using two independent session instances
(mirrors two real `Yoniq.Host` processes) sharing one real virtual audio cable. (2) `TxControlsPaneViewModel`
would have pulled a `Yoniq.Core.Imaging` concrete reference into `Yoniq.UI` (failing the architecture
test) had `IImageFileLoader` not been moved to `Yoniq.Abstractions.Imaging` first. (3) `ReceivedImageBuffer`
copying only the *reported* row instead of the whole live canvas would have silently left every other
row blank for paired-line modes (PD/MP/RM8/RM12, `RowsPerTransmissionLine=2`) — caught before any code
shipped. (4) `AnalogFmSstvEncoder` throwing on any TX image size mismatch meant `ImageFileLoader` needed
to resize-to-mode, or no real picked file could ever transmit.

**Real end-to-end demo, actually run, not just claimed**: real `rigctld` + Hamlib Dummy rig (port 4534)
for the radio half — the running GUI genuinely showed `145.000000 MHz Fm` live in the status strip
(screenshotted). For the audio RX/TX half, GUI mouse automation wasn't available in this sandbox (no
xdotool/ydotool; `python3-xlib` + XTEST worked for simple clicks but not reliably for a full file-picker
flow), so the round trip was proven via a small script constructing two real, independent
`SstvSessionService` instances (mirroring two real app processes) over a real PipeWire virtual cable
(`pactl load-module module-null-sink`) — real `MiniAudioEngine`, real `AnalogFmSstvEncoder`/`Decoder`,
RM8 mode (~8s, chosen for a fast real-hardware round trip). Result: mode auto-detected correctly, all
120 lines received, PTT keyed `[True, False]` around the transmission, and the received image is
**pixel-identical** to the source test image (screenshotted side-by-side). This exercises the exact
same `ISstvSessionService.TransmitAsync` call path the real TX button invokes (already covered
separately by `TxControlsPaneViewModelTests`'s unit test with a fake session service) — genuinely proves
the two halves (UI-to-service wiring, service-to-hardware DSP round trip) rather than mocking either one.

**Also found and fixed along the way**: Phase 3 had no "Start Receiving" trigger anywhere in the UI —
added an auto-start-receiving call at `Yoniq.Host` startup (same pattern/graceful-degradation as the
existing radio auto-connect), discovered only because the actual e2e demo attempt surfaced it.

**Cleanup after the demo**: virtual cable module unloaded, `rigctld`/demo `Yoniq.Host` processes killed.
One thing NOT cleaned up: `~/.config/Yoniq/settings.json` still has the demo's radio/audio device config
(pointing at a now-unloaded virtual cable and a `rigctld` port that's no longer running) — harmless
(everything that reads it degrades gracefully via try/catch) but worth knowing if a next session runs
the real `Yoniq.Host` and wonders why RX/radio silently don't start.

**Next**: Phase 3 is done. Per `spec/14-roadmap.md`, Phase 4 is next (CAT protocols already partly
done early/Hamlib; full image tooling — crop/resize/filter/overlay, stock library, RX history; logbook).
Per this session's own explicit user guidance, image RX/TX/templating richness matters more than further
waterfall polish — see memory `feedback_ui_effort_allocation`.

## Resume here (2026-08-05, superseded by the entry above) — Phase 3 non-UI foundation DONE (tasks 1-5 of 13); UI shell work starts next

**Plan file**: `/home/artien/.claude/plans/hidden-conjuring-curry.md` (approved, being executed in order —
13 tracked tasks, 5 done). Full context/reasoning for every decision below lives there; this entry is
just the resume-cold summary.

**Done, tested, committed to working tree (not yet committed to git — ask before committing/pushing per
standing rule):**

1. **Pre-build spike** — Dock.Avalonia added (pinned `11.2.0.2`, confirmed resolved Avalonia stays
   `11.2.3` not silently bumped), localization core, `Translate` markup extension, one design token, one
   throwaway Dock pane. **Real finding, not just plumbing**: the original plan's decision #8 ("thin Dock
   `Tool`/`Document` shell wrapping a separate plain view-model via `Context`") rendered a real tab
   header but a **blank body**, every variant tried. Root-caused via Avalonia DevTools + Dock's own
   `DockMvvmSample` source: Dock's default templates resolve a pane's body by matching the ambient
   `ViewLocator` against the dockable **itself**, not its `Context`. Fixed: real panes now derive from
   `Tool`/`Document` directly (`SomePaneViewModel : Tool`) — simpler than the original plan, not more
   complex. `ViewLocator.Match` updated to accept `IDockable` too. Confirmed working end-to-end
   (screenshot, real window, real localized/token-styled content rendering).
2. **Localization core** — `ILocalizationService`/`JsonLocalizationService` (JSON-file-backed, always
   boots into English, `assets/locale/en.json`+`locales.json`), the CI-style hardcoded-string grep test
   (`Yoniq.UI.Tests`) and locale-key-subset-of-English test (`Yoniq.Core.Localization.Tests`) both wired
   in now, not deferred.
3. **Settings schema** — **real spec bug found and fixed**: `spec/12-settings.md`'s own code sample
   (`AppSettings(AudioSettings Audio, RadioConnectionSettings Radio, ...)`) can never compile —
   `Yoniq.Settings` sits at the bottom of the layering diagram, below `Yoniq.Abstractions`, so it can
   never reference a type from `Yoniq.Core.Radio` etc. without inverting that layering. Fixed with a
   named `Dictionary<string, JsonElement>` section bag + generic `GetSection<T>`/`WithSection<T>`
   extensions (caller supplies its own source-generated `JsonTypeInfo<T>`) — spec updated to match.
   Added `RadioConnectionSettings` (`Yoniq.Core.Radio`), `AudioDeviceSettings` (`Yoniq.Core.Audio`),
   `LocalizationSettings` (`Yoniq.Core.Localization`) — only the 3 sections Phase 3 actually needs, not
   all 7 from the spec's full v1 list.
4. **`IWaterfallSource`** — new (didn't exist before; legacy `Fft.cpp` was never ported, only the PLL
   demodulator path was). New standard radix-2 Cooley-Tukey FFT + Hann window (`Yoniq.Core.Sstv`) — a
   deliberate non-port, since CLAUDE.md's port-first rule is scoped to decode-affecting DSP math, not a
   visualization feature. Shaped like `ISstvDecoder.PushSamples` (a plain push method), not a direct
   `IAudioEngine.SamplesCaptured` subscription — keeps the DSP layer's existing "no hardware knowledge"
   purity and structurally guarantees decode/waterfall independence. Threading contract mirrors
   `IRadioController.StateChanges`'s already-established pattern (push synchronously, subscriber
   marshals itself) rather than inventing new scheduling machinery.
5. **`Yoniq.Application` services** — `IRadioSessionService`/`RadioSessionService` (thin facade +
   settings-driven connect) and `ISstvSessionService`/`SstvSessionService` (owns the isolated
   decoder/waterfall fan-out that actually fixes the original spike-era bug, plus the TX/RX interlock —
   capture pauses during TX, PTT keyed around playback, restored afterward only if RX was running
   before). `IReceivedImageBuffer` interface added to `Yoniq.Abstractions.Imaging` (concrete impl is
   task 6, next). **Another real bug found by the build, not by review**: giving `Yoniq.Application` real
   content for the first time made `Yoniq.UI/App.axaml.cs`'s bare `Application` base-class reference
   ambiguous with the new `Yoniq.Application` namespace (both reachable as `Application` from a sibling
   namespace under the shared `Yoniq` root) — fixed by fully-qualifying `Avalonia.Application`.

**Test status**: full solution green, 724/724 (up from 685 at Phase-3 start) — Sstv 548, Radio 99,
MiniAudio 51, Application 12 (new), Localization 7 (new), Settings 4 (new), UI 1 (new), Audio/Logbook 1
each (still template stubs, untouched, not this phase's concern).

**Next (task 6 of 13)**: `SixLabors.ImageSharp` license check (Six Labors Split License, not plain
MIT/Apache — must actually read it and record a `LICENSES.md` determination before adding the package,
unlike Dock which needed no entry) — then real `IReceivedImageBuffer` (snapshot-on-read contract) + a
basic TX image file selector. Then task 7 (DI wiring in `Yoniq.Host`), task 8 (architecture test — must
land before any view exists, since `Yoniq.Application` transitively drags in every `Core.*` concrete),
then the actual UI shell (tokens, Dock panes, waterfall control, localize-as-built, end-to-end demo).

## Resume here (2026-08-04, superseded by the entry above) — Starting Phase 3 (minimal UI), nothing built yet

**Decision**: after linked Hamlib landed (previous entry below — committed/pushed, `2392045`), asked
whether to jump to the rest of Phase 4 (image tooling, logbook, `TemplateCatProtocol`) or follow the
roadmap's own sequence. User chose **Phase 3 first** — this is the roadmap's actual next step; Hamlib
only jumped the queue because its packaging question was blocking and it was headlessly testable, same
as rigctld. Nothing from Phase 3 has been started yet — this entry exists so a cold session can begin
straight into it.

**What Phase 3 is** (`spec/14-roadmap.md`, "Phase 3 — Minimal UI, first end-to-end path"):
- [[09-ui]]: `MainWindow` walking skeleton — waterfall, RX image panel, basic TX button — wired to
  Phase 1/2 services through `Yoniq.Application`.
- [[10-localization]]: `ILocalizationService` + `Translate` extension in place from the start
  (retrofitting localization onto an already-built UI is far more expensive than building it in from
  the first window — CLAUDE.md's own no-hardcoded-UI-strings rule).
- [[07-image-pipeline]]: minimal `IReceivedImageBuffer`/basic TX image selection only (full
  crop/resize/filter/overlay stays Phase 4).

**Demo target**: a real over-the-air (or audio-cable-looped) SSTV RX/TX session, end-to-end, through the
UI, with a rig's frequency shown live via rigctld (or now, linked Hamlib).

**Starting-point state, checked directly (not assumed) so the next session doesn't have to rediscover
it**:
- `Yoniq.UI` already has real Avalonia MVVM scaffolding: `ViewLocator.cs`, `App.axaml.cs`,
  `ViewModels/MainViewModel.cs`, `ViewModels/ViewModelBase.cs`, `Views/MainWindow.axaml.cs` — likely
  from the original `dotnet new avalonia.mvvm` template. **Not yet inspected for how much is real vs.
  default template boilerplate** — read these fresh before assuming any of it is load-bearing.
- `Yoniq.Application` is **completely empty** — no `.cs` files at all, just a `.csproj` referencing
  `Yoniq.Abstractions`/`Yoniq.Settings`/`Yoniq.Core.Radio`/`Yoniq.Core.Radio.Cat`/
  `Yoniq.Core.Radio.Rigctld`/`Yoniq.Core.Audio`/`Yoniq.Core.Sstv`/`Yoniq.Core.Imaging`/
  `Yoniq.Core.Logbook`/`Yoniq.Core.Localization`. **Does not yet reference `Yoniq.Core.Radio.Hamlib`** —
  will need adding when the UI actually wires up a radio backend. This project is where
  `IRadioController`/`IAudioEngine`/etc. orchestration for the UI is supposed to live per
  `spec/01-architecture.md`'s layering — currently 100% unbuilt, this is most of Phase 3's real work.
- `Yoniq.Host` has a real `Program.cs`, references `Yoniq.UI`/`Yoniq.Application`/`Yoniq.Settings`/
  `Yoniq.Core.Audio.MiniAudio`, and already has per-OS `CopyNativeShim*` MSBuild targets (Linux verified,
  Windows/macOS unverified from this sandbox) that copy MiniAudio's native shim into the output dir —
  **Hamlib needs no equivalent target**, since it's discovered/loaded dynamically at runtime rather than
  built/copied at compile time (the whole point of "bring-your-own-libhamlib").
- No DI container wiring exists anywhere yet — `RigctldProtocolFactory`/`HamlibProtocolFactory` are
  still only ever constructed directly in tests, never registered in `Yoniq.Host`. Phase 3 is likely
  where this actually needs to happen for the first time.

**Before writing code**: this is new architecture (UI/Avalonia, first real `Yoniq.Application` content),
not a port — same discipline as Phase 2's radio layer and the Hamlib backend both got: design first,
`auditor` plan-review pass before implementation, restate the ADHD/scope rule in every subagent prompt
(CLAUDE.md §1/§7). Read `spec/09-ui.md`, `spec/01-architecture.md`, `spec/10-localization.md`, and
`spec/07-image-pipeline.md`'s minimal-scope section fresh before planning — none of the four have been
re-read this session, only referenced from the roadmap summary above.

## Resume here (2026-08-04, superseded by the entry above) — Linked Hamlib backend DONE, committed and pushed (`2392045`)

**"Compile Hamlib in like WSJT-X?" question answered, then built.** User asked how to bundle Hamlib
in-process. Investigated for real rather than assuming: researched WSJT-X's actual approach (they
maintain a private Hamlib fork, statically link it via a "superbuild" CMake step — one static binary,
no runtime swap) and ran it past an `/adhd` ideation pass (5 cognitive frames, 30 ideas) plus two rounds
of Opus `auditor` review. Landed on **"bring-your-own-libhamlib"**: Yoniq never builds/forks/vendors
Hamlib at all — `Yoniq.Core.Radio.Hamlib` P/Invokes whatever `libhamlib` the user's OS/package manager
already has installed, discovered at runtime, version-gated to major-4, falling back to the
already-working `rigctld` client on failure. Rejected the WSJT-X-style static approach specifically
because this is a solo hobby project with a constrained CI-minutes budget and no unmerged Hamlib
patches to justify carrying a fork. Full reasoning: `spec/03-cat-layer.md`'s "Linked Hamlib:
bring-your-own-libhamlib" section.

**Built and tested, this session:**
- `Yoniq.Core.Radio.Hamlib` (new project): `INativeLibraryLoader`/`NativeLibraryLoader` (seam around
  `NativeLibrary.TryLoad`/`GetExport`), `HamlibLibraryLocator` (3-tier discovery — user override tried
  *exclusively* when set, else bare-soname-then-known-extra-dirs), `IHamlibNative`/`HamlibNative` (the
  frozen P/Invoke surface — `rig_init`/`open`/`close`/`cleanup`, `token_lookup`/`set_conf`,
  `set_freq`/`get_freq`, `set_mode`/`get_mode`, `set_ptt`/`get_ptt`, `rig_version`), `HamlibVersionGate`
  (pure string parsing, major-4-only), `IHamlibRuntime`/`HamlibRuntime`/`IHamlibNativeFactory` (caches
  discovery+version-gate **eagerly in the constructor**, not lazily — matters because a lazy-on-first-
  query shape would put blocking native I/O on whatever thread first calls `ConnectAsync`, e.g. a future
  UI click handler), `HamlibRadioProtocol` (the actual `IRadioProtocol`), `HamlibProtocolFactory`.
  `HamlibConnectionSpec` added to `Yoniq.Abstractions`. 40 new tests in `Yoniq.Core.Radio.Tests`
  (fixture/fake-driven unit tests + 4 real-interop tests against this machine's actual installed
  `libhamlib.so.4` 4.5.5 driving Hamlib's own hardware-free Dummy rig backend — confirmed genuinely
  running, not skipping, via real ~120-165ms durations).
- **Real bugs the plan-review process caught before any code existed** (2 rounds of `auditor`
  plan-review, restated ADHD/scope rule each time): `hamlib_version2` is a `const char*` **data
  export**, not a function — P/Invoking it as a function delegate would have crashed the version probe
  itself; Hamlib error codes are negative and split into soft/hard via `RIG_IS_SOFT_ERRCODE` — a naive
  "nonzero = command-level" classification would have made a dead/unplugged rig spin `CommandFailed`
  forever instead of ever triggering `RadioController`'s reconnect; no thread was specified for the
  blocking native calls, which would have frozen the UI thread on a PTT keystroke; and library discovery
  was being re-run on every backoff reconnect instead of cached once. Round 2 caught residue from round
  1's own fixes (a missing UTF-8 null terminator, the semaphore/cancellation contract for an
  uncancellable native call, the `RIG_MODE_*` table stating bit *positions* where "exact-value equality"
  needed bit *values*). One more real design bug found later, writing tests: the locator's override-path
  precedence contradicted its own spec text (implemented as a last-resort fallback; spec said it should
  *win* over auto-detection) — fixed in both places before tests were written against it.
- Full implementation plan (context, every design decision, both plan-review rounds' findings, the
  files/tests list): `/home/artien/.claude/plans/temporal-launching-valiant.md`. Full spec:
  `spec/03-cat-layer.md`'s "Linked Hamlib: bring-your-own-libhamlib" section (discovery order, version
  gate, frozen P/Invoke surface, `IHamlibNative` seam, threading contract, license provenance).
  `LICENSES.md` got a new "Runtime dependencies consumed but not bundled" section (Hamlib LGPL-2.1,
  nothing bundled).

**Explicitly deferred, not built this pass** (see the plan's "Explicitly out of scope" section):
cross-backend auto-demotion to rigctld (no home for that policy yet — `IRadioController` has no "try the
next backend" concept, needs the `Yoniq.Application`/settings layer, which doesn't exist), and the
Settings UI for the manual library-override path (hard-coded as a constructor parameter for now).

**Test results**: full solution 678/678 (`Yoniq.Core.Radio.Tests` 95/95 including the 40 new Hamlib
tests; `Yoniq.Core.Sstv.Tests` 528/528 unchanged, confirming no DSP regression; everything else
unchanged). One pre-existing flake noted, not chased (off-scope per the ADHD rule):
`RigctldDummyRigIntegrationTests.Capabilities_PttUnsupportedOnTheDummyRig_IsProbedCorrectly`
intermittently fails only under the full parallel test run (a subprocess-connection-wait timing race,
confirmed by running 100% green twice with `xunit.parallelizeTestCollections=false`) — pre-existing test
infrastructure fragility exposed by adding more concurrent real-process/real-native tests, not a defect
in the new Hamlib code.

**Committed and pushed** (`2392045`, "Add linked Hamlib CAT backend (bring-your-own-libhamlib)") — 31
files. Docs also updated in the same commit beyond what's listed above:
`spec/02-radio-layer.md`/`spec/04-rigctld.md`/`spec/14-roadmap.md` (connection-spec/factory counts, the
`WSJT-X style` mislabel corrected, a new detailed roadmap narrative section, two stale Phase-4/"deferred"
references fixed).

**Next**: `TemplateCatProtocol` fallback backend, or the Application-layer wiring (DI registration,
Settings UI for the library-path override, actual cross-backend demotion policy) — nothing decided yet,
same "don't default silently" rule as usual.

## Resume here (2026-08-04, superseded by the entry above) — Phase 2 (radio layer) DONE, committed and pushed

**Done, tested, committed (`cb84f9b`), pushed to `origin/master`.** Built the first radio-layer code in the project:
`Yoniq.Abstractions.Radio` interfaces, `RadioController` (`Yoniq.Core.Radio`), and
`RigctldClientProtocol`/`RigctldProtocolFactory` (`Yoniq.Core.Radio.Rigctld`). Full solution
639/639 tests pass (528 pre-existing DSP, untouched — confirmed via `git status`; 111 new/other).
Plan file: `/home/artien/.claude/plans/starry-whistling-pearl.md`. Full narrative:
`spec/14-roadmap.md`, search "Phase 2 — Radio layer (no CAT rigs yet) — DONE".

**Key points if resuming cold**:
- An auditor plan-review pass ran *before* any code was written (this is new architecture, not a
  port) and changed 5 real design decisions — `IRadioProtocol` dropped its `IRadioTransport`
  parameter, `IRadioProtocolFactory`-based backend resolution (exactly-one-match), a poll-loop error
  taxonomy (protocol errors vs. transport errors, only the latter trigger backoff/reconnect),
  `\dump_caps` parsing rejected in favor of probing `f`/`m`/`t` at connect, and the buffer-survival
  contract on `IRadioTransport.ReadAsync`.
- Hamlib cloned locally at `hamlib/` (gitignored, same convention as `yoniq-old/YONIQ-main/`) —
  resolved the plan-review's flagged highest-risk unknown (rigctld's exact response framing) by
  reading `tests/rigctl_parse.c` directly. Also confirmed Hamlib's hardware-free "Dummy" rig backend
  (`RIG_MODEL_DUMMY`, model 1) is real.
- User suggested testing against that real Dummy rig instead of only fixtures — landed as
  `RigctldDummyRigIntegrationTests` (4 tests, real `rigctld` subprocess, best-effort/skips if
  unavailable). User installed `libhamlib-utils` mid-session so these actually run and pass here.
- Two real bugs the test suite itself caught (not review): a `Task.Run` closure race in `ConnectAsync`
  reading `_pollLoopCts` at execution time instead of capturing it first (real
  `NullReferenceException`, fixed), and one test-side ordering bug (asserted `LastKnownState` after
  `DisconnectAsync`, which deliberately clears it).
- Specs updated to match: `spec/02-radio-layer.md`, `spec/03-cat-layer.md`, `spec/04-rigctld.md`
  (Definition-of-done checkboxes reflect what's actually built now).

**Not started / explicitly out of scope this pass**: `RigctldServer` (server mode), linked Hamlib,
flrig/OmniRig-as-client backends, `TemplateCatProtocol`, and all `Yoniq.Application`/UI/settings-
persistence wiring — all Phase 3/4 per `spec/14-roadmap.md`.

**Next — live open question, not yet decided**: user wants to talk through, next session, whether to do
Phase 3 (minimal UI, first end-to-end path — the roadmap's default next step) or jump to implementing
linked Hamlib (`spec/03-cat-layer.md`'s backend #1, P/Invoke against `libhamlib`, currently a "Definition
of done" bullet with "packaging story not yet designed" — no code, no design pass yet). Nothing decided
yet either way — **start the next session by discussing this choice**, don't default to Phase 3 silently
just because it's what the roadmap lists first. If linked Hamlib is picked, the Hamlib reference clone
from this session (`hamlib/`, gitignored) is already in place and can be read directly for the native
API surface (`include/hamlib/rig.h`) rather than re-cloning.

## Resume here (2026-08-04, latest, ACTIVE) — Hamlib question RESOLVED: no hand-written CAT protocols at all

**Decided, docs updated, no code yet.** The open question logged below this entry (whether to bundle
Hamlib alongside hand-written per-rig `IRadioProtocol`s) resolved into something bigger once the user
clarified their actual position: they don't want **any** hand-written transceiver CAT code in this
project, full stop — "other people already doing that work." Not an `/adhd` run in the end; the user's
own clarification made the direction unambiguous before that was needed.

**New decision**: Yoniq is a pure client of external CAT backends, never a per-rig protocol
implementer. Backend priority order: Hamlib linked in-process (P/Invoke, WSJT-X style) → `rigctld`
client → flrig client → OmniRig-as-client (Windows COM, talking to an already-running instance, not
bundling its OCX) → `TemplateCatProtocol` user-authored hex-template fallback for anything none of the
above cover.

**Docs updated this session** (no code changed):
- `CLAUDE.md` §2/§4 — CAT framing (`cradio.cpp`) removed from the port-first scope; explicit new rule
  that CAT/rig control is never ported; binary-is-bytes example repointed at `TemplateCatProtocol`.
- `spec/03-cat-layer.md` — fully rewritten, "CAT Layer (per-rig protocol implementations)" →
  "External CAT Backends." Documents the 5-backend list above, the transport-vs-call-based split
  (rigctld/flrig use `IRadioTransport`; linked Hamlib/OmniRig are call-based and manage their own
  transport), and that legacy byte-fixture parity no longer applies (external backends own their own
  CAT correctness).
- `spec/02-radio-layer.md` — dropped `IRigRegistry`/`RigDefinition`/the native `RADIO_POLL*`→`rigId`
  migration table (nothing to register, no protocol chosen per rig); `RadioConnectionSpec` subtypes now
  one per backend; PTT section reframed (Hamlib owns its own PTT-type config, Yoniq doesn't implement
  RTS/DTR itself); OmniRig paragraph corrected — OmniRig-as-*client* (new) actually restores rig-sharing
  arbitration that native CAT never could, unlike what the pre-edit text implied.
- `spec/04-rigctld.md` — old "Non-goals" line rejecting Hamlib bundling removed/reversed; new section
  states rigctld-client and linked-Hamlib are complementary (arbitration vs. no-daemon-required), not
  either/or.
- `docs/removed-features.md` — new entry, "Native per-rig CAT protocol implementations" (the ~14 legacy
  `cradio.cpp` families), explicit that legacy's standalone/offline CAT mode has **no equivalent**
  now — every backend but the template fallback needs an external process/library/driver present.
  Existing OmniRig entry corrected to reflect the new OmniRig-as-client backend restoring arbitration.
- `spec/11-plugin-system.md`, `spec/13-testing.md`, `spec/14-roadmap.md` — cross-references and the
  Phase 2/4 plan updated to match (Phase 2: rigctld client first; Phase 4: linked Hamlib + template
  fallback first, flrig/OmniRig-as-client after if there's still demand).

**Open follow-up, not yet designed**: Hamlib native-binary packaging (per-OS bundling, ABI-churn
handling) needs its own design pass before the linked-Hamlib backend can actually be built — flagged in
`spec/03-cat-layer.md`'s Definition of done, not resolved here.

**Next**: Phase 2 code can start now that this no longer blocks `IRadioController`'s shape — `rigctld`
client first per the roadmap's own priority order.

## Resume here (2026-08-04, superseded by the entry above) — Original framing of the Hamlib question

Original framing was narrower than the eventual decision: "should Yoniq bundle Hamlib directly
(alongside rigctld), not just talk to rigctld" — i.e. Hamlib as an *addition* to the already-planned
hand-written per-rig `IRadioProtocol`s. The user's follow-up clarified they didn't want the hand-written
protocols at all, which is the actual decision recorded above. Kept here only for the historical
reasoning trail (constraints considered: `CLAUDE.md` §4's P/Invoke-isolation rule, Hamlib's LGPL
license compatibility, native binary bundling/ABI-churn cost) — not an open question anymore, don't
re-litigate from this framing.

## Resume here (2026-08-04, latest) — SHOULD backlog fully closed, merged to master, pushed. DSP core stable.

Both halves of the SHOULD backlog (RX cluster done directly in the main tree, TX cluster done in a
parallel `fork` worktree) went through a SECOND round of scrutiny before merge: two independent, fresh
(no shared context with each other or with either side's own prior per-item reviews) Opus `auditor`
calls, each given the FULL accumulated diff for its side and told to re-verify code AND every
comment/legacy-citation, not just re-run the original per-item checklist. Both came back with
comment/citation-only findings — a stale caller count, a misleading past-tense claim, an off-by-one
`Main.cpp` line citation propagated across 4 sites, a dangling doc-comment cross-reference, imprecise
citation ranges, a rate-dependent bias figure stated as a single number, an unacknowledged Auto Slant
interaction, a mode-scope note. Zero functional/behavioral findings on either side — real signal that
the underlying fixes were sound, not just an absence of a third check.

All findings fixed on both sides, both suites re-confirmed green (RX 521/521, TX 527/527), both
committed, merged into `master` (one expected conflict in `spec/14-roadmap.md` — both branches had
appended a new section at the same point; resolved by keeping both), full merged suite re-confirmed
(528/528), pushed to `origin/master` (`03c004e`). Fork worktree removed, its branch deleted (fully
merged, nothing lost).

**Where this leaves the DSP core**: every MUST bug from the full 3-phase milestone audit (4 total, 3
from Phase 1-2 + MUST 4 from Phase 3) is fixed. Every reachable SHOULD finding (11 of 13) is fixed or
closed via documentation; the remaining 2 (items 6, 10) are deliberately deferred with real reasoning
recorded (item 6 needs a filter-state-checkpoint architecture that doesn't exist yet; item 10 is a
judgment call about changing a silent clamp to a loud throw, not a research gap) — same tier item as
Band 5's "not worth it / correctly blocked" precedent, not something dropped. COULD items 14-16 and
NICE-TO-HAVE items 17-26 (`spec/14-roadmap.md`, "Findings, prioritized") remain open but are, by their
own original triage, low-urgency/cosmetic/bounded — none block Phase 2.

**Next**: no DSP work is queued. DSP core (Phase 1) is formally closed out — moving to Phase 2 (radio
layer). **Working the Hamlib-vs-rigctld-only architecture question first**, before any Phase 2
implementation starts — see `spec/14-roadmap.md`'s Phase 1/Phase 2 boundary note for the current
framing. Don't start `IRadioController`/rigctld code until this resolves, since it shapes that
interface's own scope.

## Resume here (2026-08-04) — TX-side SHOULD cluster DONE (items 4, 5). This is a fork worktree, NOT pushed/merged.

This is an isolated git worktree the main session spun off to work SHOULD items 4 (TX frequency-
mapping integer truncation) and 5 (OutHEAD pre-VIS leader-tone port) in parallel with the main
session's own RX-orchestrator cluster (items 6, 7, 8, 9, 10). Both items DONE, both code-reviewed
(2 rounds each -- round 1 on both caught real issues, both fixed properly, round 2 confirmed).

**Item 4**: `YCbCr.FromRgb`/new `YCbCr.ColorToFreq` now model legacy's real two-truncation TX chain
(GetRY's int-truncation + ColorToFreq's integer division) plus a third, RM8/RM12-specific truncation
found by reading `TMmsstv::LineRM` directly. Round-1 review caught a real floating-point-association
bug in my first `FromRgb` reparenthesization attempt (fixed) and a false claim in a golden-vector
comment (corrected -- that test never actually invokes the encoder, so its "unchanged" was a tautology
not evidence).

**Item 5**: New `VisHeader.GenerateOutHeadSegments`, wired as the very first TX segment for every mode
including AVT (confirmed against source: AVT's own header-generation branch is nested INSIDE the same
non-narrow path OutHEAD precedes, not a special case). Round-1 review caught something real: 5 test
files that strip a fixed header offset to reach a "headerless" body for sync-bypass testing were all
under-skipping post-fix, silently locking via the real VIS path instead of the bypass path they exist
to test -- passing for the wrong reason the whole time. Fixed all 5, and re-measuring afterward found
the OLD documented deltas for those files were themselves contaminated (measuring VIS-lock accuracy,
not bypass accuracy) -- genuine bypass locks turn out to be MORE precise than the old numbers ever
showed. Round 2 review also caught 2 of the 5 fixed files still citing stale pre-fix numbers in their
own comments -- fixed by re-measuring, not just editing prose.

527/527 tests pass (520 prior + 7 new). Full detail in spec/14-roadmap.md's "TX-side SHOULD cluster"
section.

**Not committed to the main branch yet -- this worktree's own commit(s) need review and merge by the
user/orchestrating session.** Do not delete this worktree until that happens.

## What this repo is
Yoniq v2: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork). Specs in `spec/00`-`spec/15`.
Legacy source lives locally (gitignored) at `yoniq-old/YONIQ-main/` — read it directly, don't infer from memory.
Secondary reference QSSTV lives locally (gitignored) at `QSSTV-main/` — inspiration/cross-check only, never authoritative.
Full rules: `CLAUDE.md` (short, read it). Key ones: port legacy DSP exactly (no invention), golden-vector/round-trip
tests for every DSP change, small reviewable commits, ask before pushing to origin.

## GitHub Actions status (updated 2026-08-02, ~20:20 CEST)

The `CI` workflow is manually disabled (`gh workflow disable CI` — was at 1,806/2,000 monthly Actions
minutes) — check `gh workflow list --all` for its current enabled/disabled state before assuming either
way. **Once CI was confirmed disabled, the user explicitly said to resume normal pushing** (`git push`
after every commit, as this project does throughout) — pushing costs nothing while the workflow stays
disabled. The earlier "don't push tonight" restriction is LIFTED; the cron job resuming this session
tonight was updated back to its normal push-after-each-item behavior. Re-enabling the `CI` workflow
itself is still the user's own call — don't run `gh workflow enable CI` without checking with them first,
since that's what actually costs Actions minutes again, not the pushes themselves.

## Resume here (2026-08-04, latest) — Item 8 self-corrected and fixed too. RX-side SHOULD cluster: 7 items done, 2 (6, 10) deliberately deferred.

User asked "are 6/8/10 not fixes, or just need more research?" -- prompted re-examining item 8, and the
first assessment (deferred, "needs a floor clamp threaded through consistently") turned out to be too
conservative. `FilteredRawSampleAt`'s own ternary means the problematic `index-1` read only happens for
`index >= 1`, so `Math.Max(0, _bandpassFilteredProcessedUpTo - 1)` (not a bare `-1`) is a safe, minimal,
one-line-per-site fix after all -- no floor-clamp architecture needed. Fixed both `TrimBuffers` sites,
code review PASS (independently re-derived the arithmetic identity and confirmed strict monotonicity --
the fix can only ever retain equal-or-more data, never less). No new test (currently unreachable, no
observable behavior to discriminate against) -- relied on the proof plus full suite staying green
(521/521, unchanged). Worth remembering: my own first-pass assessment of a "needs more work" item isn't
automatically right -- worth a second look when asked, especially for something this cheap to re-verify.

Items 6 and 10 remain genuinely deferred (not a research gap for either -- item 6 needs a real
filter-state-checkpoint architecture that doesn't exist; item 10 is a deliberate judgment call about
changing a silent-substitute into a loud-throw, not something more research would resolve differently).

## Resume here (2026-08-04, latest) — RX-side SHOULD cluster DONE (6 items). TX-side cluster running in a parallel fork.

User approved running two pipelines in parallel: a `fork` (isolated git worktree) handling the TX-side
SHOULD items (4: frequency-mapping truncation, 5: OutHEAD leader-tone port), while this session
continued the RX-orchestrator cluster (6, 7, 9, and boundary-hardening 8/10) directly. **All 6
RX-side items now resolved:**

- **Item 7 (narrow-FSK suspended during AVT training) — real fix.** Confirmed directly against source:
  legacy's `DecodeFSK` runs unconditionally throughout AVT training and aborts it on a valid narrow
  packet. Interleaved `TryNarrowFskScan` into `TryResolveAvtTraining`'s own per-sample loop (matching
  `TryInterleavedHeaderScan`'s established lockstep pattern) -- a first attempt (checking once in
  `TryDecodeHeader`) failed the new end-to-end test on a bulk push, caught and corrected during
  development. New test confirmed to discriminate (reverted, decoder locked "avt" instead of "mn110" as
  predicted). Code review: PASS.
- **Item 9 — documented, no code change.** Added a precise doc comment on `AbandonInProgressImage()`
  explaining exactly why its missing cursor-resync is safe today (both call sites), so a future new
  call site doesn't silently break the coupling.
- **Items 6, 8, 10 — investigated and deferred, not fixed.** Each looked cheap at first but turned out
  to need either a real architectural change (item 6: filter-state checkpoint/rewind, no such mechanism
  exists in `SearchBandpassFilter`/`HilbertFmDemodulator` today) or carried a genuine regression risk
  once traced through (item 8: the obvious one-line fix would introduce a NEW negative-index crash at
  stream start; item 10: changing a silent clamp to a loud throw is a real behavior change with unclear
  benefit for the highest-consequence reader in the file). Precisely documented in spec/14-roadmap.md
  so the investigation isn't lost, not fixed reactively without a concrete failure driving the design.

521/521 tests pass. Committed and pushed (this session's own work; the TX-side fork's work is still in
its own separate worktree, not yet merged -- check on it next).

## Resume here (2026-08-04, earlier) — Working the SHOULD backlog (13 items). 4 done so far.

**Luma `Limit256` clamp added to 3 RX decoders** (SHOULD item 11): traced legacy's real per-CHANNEL
clamp pattern directly (`Main.cpp:4275-4430`), not a uniform per-family rule. Found a genuinely
surprising legacy asymmetry, code-review-confirmed: in the PD/MP/MN family, Y1 gets `Limit256` but Y2
(a SECOND luma read via the IDENTICAL peak-pick path) does NOT -- a real legacy quirk, faithfully
preserved, not "fixed" toward symmetry. 3 new tests (`Limit256ClampTests.cs`), confirmed to discriminate
(reverted the clamp, all 3 failed with predicted values, restored, re-confirmed). Code review: PASS.
Full suite 520/520, including all real-legacy-capture golden vectors unchanged -- directly answers the
one real caution the review raised (the clamp boundary isn't strictly a no-op for peak-picked near-white
overshoot, but that's legacy-faithful behavior, not a port-introduced regression, and no real fixture
hits it).

## Resume here (2026-08-04, earlier) — Working the SHOULD backlog (13 items). 3 done so far.

User: "take the shoulds." Triaged all 13 open SHOULD items (10 from Phase 1-2, 3 from Phase 3) by effort;
working through them with the same test+review discipline as the MUST fixes. Progress:

**Done**: doc-only batch (event-scheduler contract on `LineDecoded`/`ModeDetected`/`DecodeRestarted`,
`LineDecoded`'s live-alias hazard, finding 13's status update) — committed `db13bf7`. TX image/mode
dimension-contract guard (`AnalogFmSstvEncoder.EncodeAsync` now throws synchronously on a mismatched
image instead of throwing mid-stream or silently cropping) — 4 new tests, code review PASS-WITH-RISKS
(one real risk independently checked and ruled out: all 8 fixture bmps confirmed exactly match their
mode's canvas size). Robot 36 tone-selector read-point hardening (SHOULD item 12) — **round-1 review
caught a real mistake**: the first attempt used an invented margin constant instead of porting legacy's
actual `m_SG`/`m_CG` decisive-window boundary (`sstv.cpp:664-665`), independently re-verified against
source and confirmed the review was right, then rewrote using the real legacy constant. Round-2 review:
PASS. Golden-vector deltas unchanged as expected (fix only affects noisy/real signals, not this clean
fixture). Both fixes committed together, 517/517 tests pass. Full detail in spec/14-roadmap.md's
"Working the SHOULD backlog" section.

**Remaining (10 items)**: TX frequency-mapping truncation, OutHEAD leader-tone port, Limit256 clamps (3
decoders), mid-image narrow restart stale cache, narrow-FSK-suspended-during-AVT-training, and 3
currently-unreachable boundary-hardening items (assess real-fix-vs-doc-only for each, per CLAUDE.md's
"don't guard against scenarios that can't happen" guidance).

## Resume here (2026-08-04, earlier) — MUST 4 (RX line-cursor rounding) FIXED. All 4 milestone-audit MUST bugs now closed.

Fixed the one confirmed bug Phase 3 found (see the entry below this one for the audit itself). Same
discipline as MUST fixes 1-3: dedicated test first (`LineCursorRoundingTests.cs`, Robot 72 step-edge,
confirmed to fail pre-fix with the predicted ~25px drift by reverting and re-running), then the fix
(`_idealLineStartSample`, a `double` accumulator in `AnalogFmSstvDecoder.cs` mirroring MUST fix 3's own
`segmentStartSample`/`pixelWalk` split one level up — between lines instead of within one), then
re-measurement, then code-level review (verdict EQUIVALENT, 4 nits, 2 fixed as cheap doc clarifications).

Golden-vector re-measurement confirmed the fix, not just the dedicated test: RX decode-vs-source deltas
improved most on exactly the modes predicted to have the largest per-line rounding error (robot-36
16.19→5.04, robot-72 14.57→4.41, rm8 13.76→4.17 — rm8's improvement is itself proof these are two
different bugs, since MUST fix 3 structurally couldn't touch RM8's single-scan-segment decoder). TX
deltas unchanged, exactly as predicted (fix is RX-only). Full detail in spec/14-roadmap.md's "MUST 4"
section.

513/513 tests pass. Committed and pushed.

**All 4 confirmed MUST bugs from the whole milestone audit (3 from Phase 1-2, 1 from Phase 3) are now
fixed.** Remaining open, none urgent: 3 new SHOULD-level landmines from Phase 3 (TX dimension-contract
guard, RX event-scheduler contract, RX `LineDecoded` live-alias) plus the pre-existing Phase 1-2
SHOULD/COULD/NICE-TO-HAVE backlog — none reachable without a live caller/UI yet.

## Resume here (2026-08-04, latest) — Phase 3 (chain/integration audit) DONE: 1 confirmed MUST bug found, not yet fixed

Ran Phase 3 of the milestone audit (docs/audit-playbook.md) via 2 parallel `auditor` calls (RX chain
seams, TX chain seams — isolated context each, since TX/RX share no runtime state). Full detail in
spec/14-roadmap.md's "Phase 3 — chain/integration audit" section.

**RX verdict: NOT EQUIVALENT.** Confirmed (independently re-verified against both this port's code AND
legacy source directly, not just trusted from the auditor) — **MUST 4**: `AnalogFmSstvDecoder.cs:1094/1125`
rounds the per-line sample cursor (`(int)Math.Round(_effectiveSamplesPerLine)`) every line; legacy
(`Main.cpp:4133-4148`) uses one continuous integer counter for the whole transmission and derives each
line boundary via unrounded `double` division. Same bug SHAPE as MUST fix 3 (trimmed-vs-full mismatch),
promoted one level up: MUST fix 3 was within-a-line/segment, this is between-lines. Cumulative horizontal
shear, worst on Robot 72/RM8/Robot 36 (their line pitch lands far from an integer at 11025Hz) — up to
~120 samples/~25-50px drift by the last line, predicted not yet measured. Structurally invisible to Auto
Slant (it tracks its own separate exact fractional grid, never sees this cursor's rounding error). NOT
fixed yet — reported for prioritization first, per the user's own standing preference.

Also 2 risks (no stated scheduler contract for `LineDecoded`/`DecodeRestarted`/`ModeDetected`;
`LineDecoded` hands out a live mutable pixel-buffer alias — both real landmines for the eventual UI
wiring, neither reachable today) and 2 nits (≤1px AFC-boundary edge case; `_afcBoundSample` uses nominal
not effective rate, already-tracked NICE-TO-HAVE 24 confirmed larger than originally estimated).

**TX verdict: EQUIVALENT-WITH-RISKS.** No MUST-4-shaped bug — confirmed structurally impossible: legacy's
TX accumulator (`sstv.cpp:2842-2846`) and this port's (`AnalogFmSstvEncoder.cs:36-46`) are both a single
accumulator spanning the whole transmission, reset once, never re-derived from a trimmed walk. One NEW
SHOULD finding: `EncodeAsync` never validates image dimensions against the mode before encoding — a
too-small image throws `IndexOutOfRangeException` mid-stream, a too-large one silently crops. Not
reachable today (every current caller passes correctly-sized images), but a real landmine once real image
input lands. 3 already-tracked nits re-confirmed at the seam, nothing new there.

**Nothing fixed this pass — findings documented, not yet actioned.** 512/512 tests still pass (none of
these are caught by any existing test). Next: decide fix order with the user (MUST 4 is the only
confirmed live bug; the 3 new SHOULD items are landmines for later phases, not urgent).

## Resume here (2026-08-04, latest) — TX-side golden-vector tests wired in, ALL 11 modes, prerequisite for Phase 3 satisfied

All 11 `<mode-id>_TX_RX.bmp` files came back from the user's real legacy install running under Wine
(user played each `TxCapture/<mode-id>_TX.mmv` via `File → Play` with legacy's sample rate set to
11025Hz, per `TxCapture/README.md`). Wired into a new theory test,
`LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage` in
`tests/Yoniq.Core.Sstv.Tests/GoldenVectorTests.cs` (11 cases) — compares each source `.bmp` against a
REAL legacy decode of THIS PORT'S OWN encoder output. This is the actual "TX-side verification tests,
built and confirmed" gate spec/14-roadmap.md's "Explicit prerequisite before Phase 3" note required
(see that file's own "TX-side golden-vector tests wired in" entry for full detail) — closes the gap
where TX only had internal round-trip coverage (this port's encoder decoded by this port's own decoder
agreeing with itself), the exact failure shape CLAUDE.md's Scottie incident warns about.

R24 needed special handling: its source `.bmp` is 120 rows (its real transmitted row count) but
legacy's saved `_TX_RX.bmp` is the usual 256-row canvas with each real row duplicated into 2 display
rows (legacy's own row-doubling display quirk, already documented on `SstvModeRegistry.R24`). Added
`CropToTopEvenRows` (takes rows 0, 2, 4, ..., 238 of the top 240) instead of reusing the existing
`CropToTop`, which would have misaligned 2x.

Deltas measured directly (temporary-zero-tolerance technique): robot-36 3.30, martin-m1 1.53,
scottie-s1 1.05, robot-72 3.21, pd90 2.40, rm8 5.15, mn110 2.60, avt 0.86, scottie-dx 1.03, mr73 3.22,
r24 3.70 — all restart-free, correct mode first-try, and every one BELOW the RX-direction baseline
numbers for the same modes despite going through this port's own encoder first. Genuinely good news,
not just a passing test: real evidence this port's TX output is valid, accurately decodable by a real
legacy receiver.

Full suite: 512/512 passing, solution-wide build clean. Committed and pushed.

**Phase 3 (chain/integration audit) can now run — its own explicit prerequisite is satisfied.** Not yet
started this session; next natural step per spec/14-roadmap.md's own plan.

## Resume here (2026-08-04, latest) — Milestone audit: ALL 3 MUST fixes DONE

After Band 4 closed, ran the milestone-audit playbook (`docs/audit-playbook.md`), scoped to DSP core +
TX/RX codec paths (radio/CAT/DI/localization/UI don't exist yet). Phase 1 (unit map) done directly by
the orchestrating session; Phase 2 (4 batches, fanned out to `auditor`) all returned, finding 3 confirmed
MUST-fix bugs — **all 3 are now fixed, tested, code-reviewed, and ready to commit.** **Phase 3
(chain/integration audit) has NOT run yet** — user's explicit directive: build and verify TX-side tests
first, do not skip straight to it.

**MUST fixes, all independently re-verified against legacy source by the orchestrating session (not
just trusted from the auditor):**
1. **AVT images never trimmed their buffers — DONE, committed (`e35a648`).** `InitializeAfc`/
   `InitializeSlant` return early for AVT (both trackers null), so the locked watermark never advanced
   for AVT's whole ~90s image. Fixed by conditionally excluding these two watermark terms only when
   their tracker is null, mirroring an already-established pre-lock pattern. Code-level review clean.
2. **`TryNarrowFskScan` whole-buffer pre-pass — DONE, committed (`cd9a05a`).** On a bulk push with an
   earlier non-narrow transmission followed by a later narrow one, the narrow scan could commit the later
   transmission first, skipping the earlier one entirely — confirmed by reverting the fix and observing
   `ModeDetected` drop from 2 events to 1. Fixed by interleaving narrow-FSK's own scan per-sample with
   the sync-bypass/VIS-lock loop instead of letting it run to completion first. Code-level review found
   a real secondary risk (the fix newly activates a previously-accidentally-muted false-positive-restart
   exposure at a SEPARATE, lower-priority call site) — deliberately deferred, documented, guarded by a
   new `Assert.Equal(0, restartCount)` test assertion rather than silently absorbed.
3. **Pixel-pitch trim accumulator drift — DONE, ready to commit.** The biggest of the three, touching 4
   decoder files identically. Multi-segment RX decoders started each scan segment after the first
   slightly early (measured, not assumed: a real 5px systematic shift on a synthetic step-edge test,
   confirmed by reverting the fix); confirmed directly against `Main.cpp:4454-4503`. Fixed by tracking
   each scan segment's own start position separately from its trimmed intra-segment pixel walk, then
   advancing by the segment's FULL untrimmed duration afterward. `RgbSequentialScanlineDecoder`/
   `RobotScanlineDecoder`/`YCbCrSequentialScanlineDecoder`/`YCbCrLinePairedScanlineDecoder` touched;
   `MonoAveragedPairedScanlineDecoder` (RM8/RM12) correctly left alone (single scan segment, structurally
   immune, confirmed by an unchanged golden-vector delta). Golden-vector re-measurement: most fixtures
   improved (martin-m1 nearly 3x better), two (robot-36, robot-72) worsened slightly but stay
   comfortably within their existing tolerances — an honestly-recorded, accepted tradeoff, not chased to
   zero. Code-level review found 2 real secondary risks, both documented rather than silently absorbed:
   a Robot-36 tone-selector read now sits right at a segment boundary (folded into the existing SHOULD
   item 12), and a test-coverage gap for the two "worsened" families that investigation showed needs a
   harder differential test design than initially assumed (tracked as a SHOULD-level follow-up).

None of these three needed/need TX golden vectors or new fixtures to fix/verify.

**Also documented, not yet actioned**: ~10 SHOULD items (TX frequency-truncation bias, missing
`OutHEAD` TX segment + its `removed-features.md` entry, a mid-image narrow-restart 1-line stale-cache
bug, narrow-FSK suspended during AVT training, 3 buffer-mechanism fragility risks, missing luma clamp
in 3/5 RX decoders, Robot 36 tone-selector timing, 3 golden-vector coverage gaps: Scottie DX/MR73/R24)
plus ~13 COULD/NICE-TO-HAVE items. **Full findings list, all prioritized, all reasoning: `spec/14-roadmap.md`,
search "Milestone audit, Phase 1+2"** — read that before doing any fix work, don't re-derive from scratch.

**Next**: all 3 MUST fixes done. Remaining SHOULD/COULD/NICE-TO-HAVE items from the original Phase 2
findings list are still open, plus 2 new deferred risks MUST fix 3's own code-level review surfaced
(both documented, see above). None of these block anything.

**Explicit user directive: before running Phase 3 (chain/integration audit), build TX-side verification
tests and confirm them first** — do not skip straight to Phase 3 with TX's zero-legacy-decode-coverage
gap (batch A's finding) still open. Likely means TX-side golden vectors (this port's encoder output run
through a real legacy decode) — bottlenecked on the user's own time with the real legacy binary, same
as Task #7. Full reasoning: `spec/14-roadmap.md`, "Milestone audit, Phase 1+2" → "Next steps".

**TX capture prep — DONE, ready to hand off.** User is setting up the legacy binary under Wine and
asked this session to prepare the TX-side files. Investigated first: no CLI/headless RX mode exists,
but legacy's `File → Play` can replay the same `.mmv` format the existing RX fixtures already use — no
audio hardware routing needed. Built `MmvFile.Write`/`BmpFile.Write` (new, in the test project). Per the
user's own explicit request, got an Opus code-level review of both BEFORE generating anything — caught
a real blocker in `MmvFile.Write`'s first draft (a full-scale +1.0 sample silently wrapped to -1.0 via
a float→double→short cast that doesn't saturate at the final narrowing step; would have corrupted every
generated file with impulse noise ~every 75 samples). Fixed, covered by 14 new round-trip tests
(confirmed to discriminate the original bug by reverting it). Generated all 11 TX `.mmv` files (8
existing RX-fixture modes + Scottie DX/MR73/R24, the 3 modes the audit flagged as having zero coverage
anywhere) into `tests/Yoniq.Core.Sstv.Tests/Fixtures/GoldenVectors/TxCapture/`. **Then the user asked a
follow-up that caught a real gap**: file-format round-trip tests don't prove the actual files are valid
decodable transmissions — added `TxCaptureFixturesTests.cs` (11 tests, kept permanently) that reads
each real file back and self-decodes it with this port's own decoder; all 11 pass (correct mode, zero
restarts, correct image). Full suite green (501/501). `TxCapture/README.md` has the exact steps for the
user's side (set legacy's sample rate to 11025Hz FIRST, or `File → Play` silently resamples through a
lowpass and the capture is worthless) — no rush, can wire fixtures in individually as results come back.
Full detail: `spec/14-roadmap.md`, "Milestone audit, Phase 1+2" → "TX-side capture prep".

## Resume here (2026-08-04, later) — Band 3 AND Band 4 both fully DONE and COMMITTED

S31 done (`b5a5cb4`). Band 3 (8 items: S31 already counted separately, then S7-S17 family) done and
committed across `739f4e5`/`34bad37`/`0bb7c60`/`7354469` — S12 (`m_sint2`/`m_sint3` freeze gating) was
the last item, landing after a plan-review round caught the original proposal only covered the
case-0↔1 boundary, not the full case-2/9/3 freeze the item is named for. Full detail:
`spec/14-roadmap.md`, search "S12 — m_sint2/m_sint3" and "AVT package: S7".

**Band 4 (4 items: S27, S29, S30, S21 — S13's doc half already closed earlier) — DONE and COMMITTED**
(`89cb135`). User asked for "a good deep documentation and comment update and audit."
All four are pure doc/test additions, zero DSP behavior change (confirmed by an unchanged full-suite
result, 472/472, before and after):
- **S27** — `docs/removed-features.md` CQ100 entry + `HilbertFmDemodulator.cs` stale comment fix.
  Investigated fresh rather than trusting the inventory table's narrow "FIR tap-tripling" framing: found
  `g_dblToneOffset` touches 44 lines across `sstv.cpp`, not ~30. **Code-level audit caught the first
  draft's own removed-features.md entry was still incomplete** — 2 more real `sys.m_bCQ100`-gated DSP
  effects (an `m_OFP` sync-timing shift, a narrowed AFC capture window) were missing, found and added
  before commit.
- **S29** — new `MakeFilter_OddTap_TrailingSlotStaysZero_NotSymmetric` test
  (`SearchBandpassFilterTests.cs`), pinning the already-documented-but-unpinned odd-tap trailing-zero
  divergence with an executable guard.
- **S30** — new `Constructor_MiddleDecimationTier_16To40kHz_SelectsTap24Df1_AndDecodesCorrectly` test
  (`HilbertFmDemodulatorTests.cs`), giving `CHILL`'s 16-40kHz middle decimation tier its first
  instance-level (not just raw-kernel) coverage.
- **S21** — new `MonoAveragedPairedScanlineDecoderTests.cs`, replacing the vague "a couple of levels"
  RM8/RM12 int-truncation estimate with an exact measured bound (max 2 levels, `-1..+2` signed) via an
  exhaustive sweep against an independently-replicated legacy two-truncation chain. Code-level audit
  independently re-derived the exact bound in closed form and confirmed it correct, plus fixed 2
  citation/wording nits in the test's own comments.

Full detail: `spec/14-roadmap.md`, search "Band 4 — documentation/test-only items".

**Nothing outstanding from the original 30-item DSP-simplification inventory** except Band 5
(documentation-only/correctly-blocked, no action needed — see the Band-3/4/5 summary further down this
file). Next: Task #8 (Phase 3 chain/integration audit, `docs/audit-playbook.md`) is the natural next
step now that Bands 1-4 are all closed, or await further user direction.

## Resume here (2026-08-03, later) — S31 (AVT never decodes on real capture) root-caused and FIXED

**S31 is done.** The working hypothesis logged in an earlier version of this section ("header timing
jitter over the long training sequence") was **wrong** — investigated properly per the user's explicit
request ("do 1 first" — confirm the mechanism empirically before designing a fix). Real root cause:
`VisLockStateMachine` (the only noise-tolerant detector in this port) deliberately discarded every AVT
match it found, by design; the only path allowed to act on one (the fixed-window `TryDecodeVisHeader`)
never reaches real header content on a real capture (same `OutHEAD`-leader/pre-TX-room-audio mechanism
already root-caused for other modes' anchor-precision gaps). Confirmed via temporary instrumentation
(added, run against the real `avt.mmv` fixture, then fully reverted before any fix code was written) —
`VisLockStateMachine` correctly decoded AVT's real VIS byte three separate times and discarded every
one. Full mechanism + fix detail: `spec/14-roadmap.md`, search "S31 — AVT's real capture never decoded".

**Fix went through the full normal loop**: design → auditor plan-review (caught a real, previously
undiscovered `TrimBuffers` watermark blocker before any code shipped) → implementation → auditor
code-level review (verdict: EQUIVALENT-WITH-RISKS, no blockers, ready to commit; a few stale-comment
nits found and fixed directly). `avt.mmv` now decodes cleanly (delta 5.80, restarts=0, correct mode
first-try) — comfortably in the same healthy range as the other five Task #7 fixtures.

**Uncommitted as of this writing** — nothing from this session has been committed yet (`git status`:
`spec/14-roadmap.md`, `src/Yoniq.Core.Sstv/AnalogFmSstvDecoder.cs`, `src/Yoniq.Core.Sstv/VisLockStateMachine.cs`,
fixtures `README.md`, `GoldenVectorTests.cs` modified; new `AvtNoiseTolerantDetectionTests.cs`). Full
suite confirmed green (458/458, solution-wide) before this brief was written. Ask the user before
committing, per standing rule.

## Resume here (2026-08-03, earlier) — Task #7 fixtures wired in (superseded by the S31 entry above)

**Six new golden-vector fixtures captured and wired in** (scottie-s1, robot72, pd90, rm8, mn110, avt —
Task #7 from the roadmap). 5 of 6 decode cleanly with healthy deltas, now in `GoldenVectorTests.cs`/
`GoldenVectorFixtureReaderTests.cs` alongside the original `martin-m1`/`robot-36`. Full detail: fixtures'
own `README.md` (capture/trim/measurement record) and `spec/14-roadmap.md`'s "Task #7" section (search
"Task #7 — six new golden-vector fixtures"). This session's own work (Task #7 fixture-wiring commit) was
already committed before the S31 investigation above started.

## Resume here (2026-08-03) — Band 2 is FULLY DONE. Autonomous session stopped here deliberately.

**S14, S6, and S15 are all resolved** (S14/S6 shipped as code — commits `7a153a0`, `6da0a65` — S15
closed via documentation, no code needed). **Band 2 is complete.** Full detail in `spec/14-roadmap.md`,
search "Band-2 item S14" / "Band-2 item S6" / "Band-2 item S15".

**S15's resolution, briefly**: before drafting a plan, found this port's own doc comments (from Band-1
item 2/S2, predating Band 2) had already investigated S15's core concern once — a "more ambitious
earlier draft" that tried periodically re-anchoring `_consumedSamples` to give the fixed-window header
paths another shot, reverted after confirming empirically it changed nothing (the existing continuous
fallback, `TryInterleavedHeaderScan`, finds the same headers at its own already-accepted precision
either way). An auditor plan-review round confirmed this settles S15's broader concern too, via 3
independent arguments (VisLockStateMachine covers extended VIS same as the fixed-window path; the
post-commit sync-anchor fold washes out the ~10ms precision difference; already observed working via an
existing test) — with one real, narrower exception: MN/MC's FSK packet decode genuinely has no
continuous equivalent, but that's not a new gap, it's the ALREADY-TRACKED Band-3 item S8 ("mid-image
narrow re-lock"), whose own load-bearing registry-coverage assumption got independently re-verified
along the way (confirmed real, `SstvModeRegistry.cs:998-999`/`1023-1026`). Auditor's explicit verdict:
building S15 as originally scoped would mean relitigating an already-evidenced-and-reverted design
decision, unattended, on a load-bearing (`TrimBuffers`) coupling, with an unmeasured benefit — exactly
this session's own hold criterion. Closed via documentation instead; **no code, deliberately.**

**Nothing is queued for further autonomous work right now.** The three explicit next steps below (Task
#7, Task #8, remaining Band 3/4/5 items) each either need the user's own time with the real legacy
binary (#7) or should follow it (#8, most Band-3 mode-family items). If resuming cold: read this file
fully, then `spec/14-roadmap.md`'s Band-2 S15 closing section for the complete reasoning before touching
this area again — don't re-open S15 without re-reading why it was closed.

## Current status: Band 1 DONE (all 4 items); Band 2 DONE (all 5 items)

**Pre-Phase-2 gate: shortcut/simplification audit.** User's call: before running the milestone-audit
playbook's Phase 3 chain audit (see `docs/audit-playbook.md`) or moving to Phase 2, first inventory
every known DSP-in-pipeline simplification this port carries, triage/fix the important ones, THEN
capture more real golden-vector fixtures, THEN run Phase 3. Full writeup + 30-item table + priority
bands + patterns: `spec/14-roadmap.md`, search "Pre-Phase-2 gate".

**Band 1 (must-fix-before-Phase-2) — all 4 DONE**: S4 exception-swallowing catch (`288d5d0`); S2
unbounded buffer memory growth (`86e3af6`); S3 chunk-timing sensitivity (`365d57b`/`765ba3c`); S1
lock-dependent bandpass filter switch, split into 4a (`fbafdea`, lazy forward-fill cache) + 4b
(`028bc8e`, the actual H1/H2 switch, gated on a *captured* lock-anchor index not live state). Full
per-item detail: `spec/14-roadmap.md`, search "Band-1 item".

**Band 2 (should-fix-during-Phase-2-bring-up) — 5 of 5 DONE (4 code + 1 closed via documentation),
order: S5 → S16 → S14 → S6 → S15.**
Before starting, asked the auditor to revisit its own "decide the streaming contract explicitly first"
recommendation now that Band 1 had real outcomes — withdrawn: the port already has both patterns that
recommendation wanted decided (persistent-detector+cursor, lazy-forward-fill+ring-buffer), so patching
individually, reusing those patterns, was the right call. Auditor also corrected the shape mapping
mid-stream (see below) — **don't assume similarly-shaped items share a fix, verify each against source**.
- **S5 DONE** (`e0563f5`) — `TryDecodeVisDataBits`' d11/d12/d19 tone detectors were cold-started fresh
  every call; converted to persistent lazy-forward-fill caches (mirroring `AgcSampleAt`). d13
  deliberately NOT converted — a plan-review round caught that legacy only feeds `m_iir13` during
  case 2/9, so it's not a pure function of sample index (same shape as S16's own d13-like surprise
  below); an index-keyed cache would have been wrong for it.
- **S16 DONE** (`c0b09ca`) — AVT's dedicated PLL had a clamped 2000-sample warm-up. First plan-review
  round caught its OWN initial misclassification before any code was written: legacy's `m_pll` is fed
  only during SyncMode cases 3-7 (intermittent, same shape as d13), so "make it fully persistent"
  (the S5-style fix) would have been a real regression — a PLL's phase has no fast, data-independent
  re-settling the way a resonator does. Real fix: kept per-attempt construction, widened the warm-up to
  legacy's actual ~1850ms contiguous pre-training feed span (derived from source, not guessed).
- Both plus 4b closed a systemic gap an auditor review flagged: 3 items in a row changed real behavior
  invisible to the test suite (decode outcomes stayed correct, but nothing pinned the mechanism). Closed
  in `LegacyDerivedSpansTests.cs` (S5/S16) + `BandpassCacheChunkInvarianceTests.cs` (4b, done earlier).
- **S14 DONE** (`7a153a0`) — `TryDecodeNarrowModeHeader`'s mark(1900Hz)/space(2100Hz) detectors were
  cold-started fresh every call, same shape S5 fixed for d11/d12/d19. Mark reuses `D19At` directly
  (legacy's narrow-mode mark IS d19, confirmed `sstv.cpp:1851/1858`); space gets a new `FskSpaceAt`
  cache mirroring D11At/D12At/D19At. Two rounds of auditor code-level review, both EQUIVALENT/clean;
  the one non-blocking follow-up (pin the D19-reuse decision itself, not just the new cache's own
  persistence) was closed before commit, not deferred. Full detail: `spec/14-roadmap.md`, search
  "Band-2 item S14".
- **S6 DONE** (`6da0a65`) — `HilbertFmDemodulator.ProcessSample` gained an `isNarrow` parameter
  (precomputed wide/narrow `(off, out)` pairs, mirroring 4b's per-call `useLocked` shape), gated via
  4b's own `_bandpassLockedFromSample` anchor. A prior trap note claiming `SetWidth` changes tap count
  cited the wrong legacy function (`SetBPF`'s `m_Skip`, an unrelated bandpass-quality setting) — fresh
  read found `CHILL::SetWidth` only changes two scalars, so 4b's "no warm-up needed" finding DID
  transfer after all, just for a different reason than originally assumed. Real finding, confirmed
  algebraically twice over: `isNarrow` is representationally inert at steady state in this port's Hz
  domain (encode/descale are exact algebraic inverses) — the only observable effect is a bounded,
  legacy-faithful transient at a width switch, not a decode-accuracy fix. Full detail: `spec/14-roadmap.md`,
  search "Band-2 item S6".
- **S15 CLOSED via documentation, no code needed** — see the "Resume here" section at the top of this
  file for the full reasoning. Its one real remaining gap is the already-tracked Band-3 item S8, not a
  new item.

**Test count**: 423/423 `Yoniq.Core.Sstv.Tests`, solution-wide build clean, golden-vector tests
unaffected throughout, noise-robustness tests unaffected (existing tolerance).

Full per-item plan-review + implementation + code-review detail: `spec/14-roadmap.md`, search
"Band-1 item" or "Band-2 item".

## Next up

**Band 1 and Band 2 are both fully done.** No DSP item is queued for autonomous continuation right now
— the remaining work below either needs the user's own time or should deliberately follow it:

1. **Task #7 — capture ~5-6 new real golden-vector fixtures** from the legacy binary, covering mode
   families the existing two fixtures (Martin M1, Robot 36) don't exercise: Scottie S1 (mid-line sync —
   the exact family that already produced one real synthetic-test-passes-while-wrong incident,
   `CLAUDE.md` §4), Robot 72 or R24, a PD/MP mode, RM8 or RM12, a narrow MN/MC mode, AVT. Bottlenecked
   on the user's time with the real legacy Windows binary, not on dev work.
2. **Task #8 — Phase 3 chain/integration audit** (milestone-audit playbook, `docs/audit-playbook.md`) —
   skip Phase 1/2, units are already individually verified to an unusual degree. Should follow #7, not
   precede it, so the audit runs against the widest available real-audio coverage.
3. Band 3/4/5 items from the 30-item DSP-simplification inventory (S1-S30) are cataloged in
   `spec/14-roadmap.md` but not yet scheduled — most Band-3 items (including S8/S9 MN/MC narrow-family
   work and S7/S11/S16/S17 AVT work) are gated on task #7's new fixtures (mode-family gaps get fixed
   when measured, not reasoned, per the auditor's own recommendation).

## Other candidates (not urgent)

- **Phase 2 — Radio layer**: `IRadioController` reference implementation against a fake
  transport/protocol, then `rigctld` client mode ([[02-radio-layer]], [[04-rigctld]] in the roadmap).
- **Windows/macOS real-hardware audio verification** (`spec/14-roadmap.md` Phase 1, Audio 1b): the
  native-shim build compiles in CI on both, but neither has been run against real/virtual hardware —
  needs a human on each OS, not agent-doable from this Linux sandbox.
- **cty.dat license pre-audit: DONE** (2026-08-02) — Clublog's `cty.dat` has no fee, but redistribution
  needs a human to email Clublog's helpdesk with the proposed use and get an API key before it can
  actually be downloaded/bundled; not a simple open-license drop-in. Full finding in `LICENSES.md`
  ("Candidate future asset" note) and `spec/14-roadmap.md` (search "Pre-audited"). Remaining before
  Phase 4: someone (not an agent) actually emails Clublog and gets the key.
- **Remaining small license-audit item**: confirm whether Chilkat/FastReport back a real legacy feature
  — needs the legacy binary running, not just source, so not doable from this sandbox.

## Completed work (full narratives in `spec/14-roadmap.md`, search "Piece N")

Before piece numbering started: AFC (`AfcTracker`, direct port of `CSSTVDEM::SyncFreq`), Auto Slant
(`SlantTracker`, clock-drift correction), and the full 7-piece VIS/preamble-lock state machine — commit
`8dbe790` onward, no known open DSP-correctness gap in Phase 1 from this area.

Piece 8 — **the actual root-caused fix for the Robot-36-at-11025Hz decode gap**: real golden-vector
capture from a working legacy install exposed a 5x-worse-than-synthetic gap; investigation found a
missing per-image sync-anchor re-correction (legacy's `SyncSSTV`/`m_wBgn` fold-and-argmax) — ported as
`SyncAnchorCorrector`, plus a real Scottie-family wraparound bug caught and fixed along the way.
Measured: Robot 36 68.06→9.95, Martin M1 11.78→2.40 average per-channel delta. Commits `23b025a`→`8f87146`.

Pieces 9-13 (all committed, all green): real VIS-bit dual-envelope tone race (piece 9), `GetPictureLevel`
peak-picking (piece 10), horizontal pixel-pitch trim (piece 11), RM8/RM12 RX gain correction (piece 12),
`DecodeFSK`'s real 5-phase FSK state machine (piece 13).

Pieces 14/15/B + noise harness: Hilbert demodulator (`CHILL`) replaces PLL as the main picture
demodulator (piece 14, commit `aeccfcc`); legacy's always-on 2-tap pre-filter (piece 15); noise-
robustness test harness (new infra, not a port); `SearchBandpassFilter` — legacy's `H2`/"search"
pre-AGC bandpass filter, continuous scope, no lock-state gating (piece B, commit `c3b9f46`) — extended
with the locked-state H1 counterpart by Band-1 item 4 above, closing the gap this piece's own doc
comment flagged.
Measured noise-floor improvement: martin-m1 9.0dB→3.0dB, robot-36 16.0dB→9.0dB.

**Windows CI fixed** (2026-08-01, commit `8be35b7`): `ilammy/msvc-dev-cmd@v1` was setting `Platform=x64`
as a job-level env var, and `Yoniq.sln` only has "Any CPU" configs — fixed with explicit
`/p:Platform="Any CPU"` on the dotnet steps. All three CI legs green on `master`.

## Working methodology (established across this project)
- Legacy is ground truth — verify against `yoniq-old/YONIQ-main/` source directly, no assumptions.
- Test early, test often — build + run tests after each sub-step, not just at the end.
- Get an Opus/`auditor` plan-review before writing code for non-trivial DSP ports (CLAUDE.md §7) — ask
  the auditor directly "is this ready to build now?" each round; soft 3-round backstop, then loop in the
  user. Restate the ADHD/scope rule and the ported-behavior framing in every subagent prompt.
- For a substantial piece, a final CODE-LEVEL auditor review after implementation (not just a plan
  review before it) is worth doing before calling it done — caught real issues in every Band-1 item's
  own implementation that plan-review alone couldn't (bugs only visible once the code actually exists).
- Independently re-verify Opus/agent findings against actual source before trusting/acting on them.
- When investigating a suspected bug, confirm the mechanism empirically (temporary instrumentation,
  added and fully reverted or converted to a permanent regression test, `git status`/`git diff`
  confirmed clean) before designing a fix — don't fix blind. Used successfully across every Band-1 item.
- If a fix's design doesn't demonstrably achieve what it's meant to (measured via a real test, not
  assumed), revert to the simpler alternative rather than keep the more complex one "just in case."
- When a design choice has real, uncertain tradeoffs (not a lookup, not a bug with a known root cause),
  the `/adhd` skill (parallel divergent ideation across cognitive frames, scored/clustered, top ideas
  deepened) is worth running before committing to a direction — used for the "move to a streaming
  architecture now vs. defer" question during Band-1 item 4; the deepening pass surfaced a concrete,
  buildable design (4a) that a straight architecture debate hadn't converged on.
- A throwaway spike to get a REAL number beats reasoning about magnitude in the abstract — but measure
  the thing that actually matters (an early spike measured the wrong quantity — raw pushed-sample count
  instead of the actual cache cursor position — and had to be corrected before its result meant anything).
- A pre-computed "this item is shaped like that other item" classification (even the auditor's own) is a
  starting hypothesis, not a fact — verify each item's actual legacy feed schedule from source before
  reusing a sibling's fix. S16 was pre-classified "same conversion as S5"; a plan-review round caught
  that AVT's PLL is fed intermittently (like d13), not every sample (like S5's d11/d12/d19), BEFORE any
  code was written — the fix that shape actually needed was much smaller than planned.
- When a review flags the same finding more than once across rounds (e.g. "this doc comment is stale"),
  verify against the CURRENT file before re-fixing — an already-applied fix can show up as a stale
  finding in a later round if the reviewer's own context predates it.
- Document steps + results durably in `spec/14-roadmap.md` as you go; keep this file trimmed to
  "what's needed to resume," not a running history (that's the roadmap's job).
- Cloud-scheduled routines (RemoteTrigger/`/schedule`) run in an isolated environment with a fresh git
  checkout — no access to `yoniq-old/YONIQ-main/` or `QSSTV-main/` (both gitignored, local-only).
  Session-local `CronCreate` reminders work fine instead (no isolation issue) but only last the session.

## Build/test commands
```
dotnet build src/Yoniq.Core.Sstv -c Debug
dotnet test tests/Yoniq.Core.Sstv.Tests -c Debug
dotnet test tests/Yoniq.Core.Sstv.Tests -c Debug --filter "FullyQualifiedName~<substring>"
```
