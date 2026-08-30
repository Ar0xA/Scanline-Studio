# Test-suite fixes, Phase 1 — plan

**Status: DONE, 2026-08-30.** All 10 items implemented, 1 code-review round (GO, no blockers, ship
as-is), every touched project's test suite green, full solution builds clean. Uncommitted — sitting
in the working tree. See `production_audit.md`'s Test-suite Tier 0/1 sections for the corresponding
`DONE` markers, and `PROJECT_BRIEF.md` for the session summary. Kept here as the implementation
record (each item's plan-review corrections are load-bearing context for why the code looks the way
it does) — not deleted now that it's done.

Source: `production_audit.md`'s "Test-suite audit" section (2026-08-30). Scope: **test-only fixes**
— items that close a real test-suite gap without requiring a paired production-code fix. TT0-1 and
TT0-2 are explicitly excluded: both are designed to land as the regression gate for the T0-2
(`ISettingsStore.UpdateAsync`) and T0-1 (Hamlib lock timeout) *production* fixes, which are separate,
larger efforts already sequenced in `production_audit.md` — writing them now would just be a failing
test with no fix to make it pass.

10 items, grouped by project (a would-be 11th, item 9, is deferred to its own plan — see below). Each
states: what's broken/missing, the change, the file(s), and how to verify. No production `src/`
behavior changes except where noted (1 item adds a testability seam with an unchanged default; 2
items add a small, fully-specified production fix alongside their test — called out explicitly).

**Revised 2026-08-30 across 3 plan-review rounds** (round 1: 6/11 items ready as-drafted, 5 needed
correction, item 9 pulled out entirely. Round 2: 2 of the 5 corrections had their own gaps —
item 6.3's UTC handling, item 11's fail-vs-skip decision. Round 3: **GO** — confirmed ready to build,
one doc-consistency edit applied, no code changes required). All findings are folded in below,
inline, marked by round.

---

## 1. `Core.Radio.Tests` — stop silent-pass real-interop tests (TT0-3)

**Problem:** `HamlibDummyRigIntegrationTests.cs`, `RigctldDummyRigIntegrationTests.cs`,
`HamlibNativeTests.cs` early-`return` when their native dependency is absent — xUnit reports these as
**passed**, not skipped. `OmniRigProtocolFactoryTests.cs:37-40` no-ops on Windows, the only OS its
code runs on.

**Change:** add `RequiresHamlibFactAttribute`/`RequiresRigctldFactAttribute` (copy the existing
pattern at `tests/ScanlineStudio.Core.Audio.MiniAudio.Tests/RequiresPipeWireFactAttribute.cs` — a
`FactAttribute` subclass setting `Skip` from a process-wide `Lazy<bool>` availability probe). Replace
every early-`return` with the attribute so absence shows as an honest skip.

**Corrected (plan-review round 1):** `OmniRigProtocolFactoryTests.cs:35-43` is the *inverse* shape —
it asserts `PlatformNotSupportedException` and already no-ops **on Windows**, the only OS where it's
meaningful; it needs a *skip-on-Windows* attribute, not a *requires*-Windows one. Add
`SkipOnWindowsFactAttribute` (same `FactAttribute`-subclass pattern, `Skip` set when
`OperatingSystem.IsWindows()`), not `RequiresWindowsFact`.

**Files:** new `RequiresHamlibFactAttribute.cs`, `RequiresRigctldFactAttribute.cs`,
`SkipOnWindowsFactAttribute.cs` in `tests/ScanlineStudio.Core.Radio.Tests/`; edit the 4 test files
above to use them instead of `if (!available) return;`. Early-`return` sites, confirmed exact:
`HamlibNativeTests.cs:22,44`; `HamlibDummyRigIntegrationTests.cs:32,52,70,95,113`;
`RigctldDummyRigIntegrationTests.cs:100`. All are `[Fact]`, not `[Theory]` — no `Theory` variant of
the attributes needed.

**Verify:** `dotnet test tests/ScanlineStudio.Core.Radio.Tests -c Debug` — on this dev machine
(no libhamlib/rigctld installed), the affected tests must show as **Skipped**, not **Passed**, in the
test-run summary.

*Not in this phase:* the `SCANLINE_REQUIRE_NATIVE_CAT=1` hard-fail env var and its CI wiring — that's
a CI-config change, not a test-code change; flag as a follow-up for whoever owns `.github/workflows/`.

---

## 2. `Core.Radio.Tests` — OmniRig CLSID/IID/DispId reflection test (TT0-4)

**Problem:** `OmniRigComClient.cs`'s 11 GUID/DISPID literals have zero test coverage; only
`FakeOmniRigComClient` exists, which can't catch a marshaling regression by construction.

**Change:** new `OmniRigComClientAttributeTests.cs`. Use `InternalsVisibleTo` +
`BindingFlags.NonPublic` reflection to read `GuidAttribute`/`InterfaceTypeAttribute` off the two
`[ComImport]` interfaces and `DispIdAttribute` off their members. Assert each against the value
transcribed from `yoniq-old/YONIQ-main/OmniRig_TLB.h`/`.cpp` (already verified correct by the audit —
transcribe from source, not from `OmniRigComClient.cs` itself, or a copy-paste bug in both places
would go uncaught). One test case per literal, named for what it pins (e.g.
`IRigX_Tx_HasDispId17`), citing the `OmniRig_TLB.h:<line>` it came from in a comment.

**Files:** new `tests/ScanlineStudio.Core.Radio.Tests/OmniRigComClientAttributeTests.cs`.
`InternalsVisibleTo` already covers this test project — confirmed present at
`src/ScanlineStudio.Core.Radio.OmniRig/AssemblyInfo.cs:6` — no `.csproj` change needed. Transcribe
literals from `yoniq-old/YONIQ-main/OmniRig_TLB.cpp:44,46,47` (the 3 GUIDs, byte-split form) and the
matching `.h` DISPID declarations, not from `.h` alone — the `.cpp` form is easier to transcribe
correctly.

**Verify:** runs and passes on this (Linux) dev machine — no COM activation involved, pure attribute
metadata reflection. `dotnet test tests/ScanlineStudio.Core.Radio.Tests -c Debug --filter "FullyQualifiedName~OmniRigComClientAttributeTests"`.

---

## 3. `Settings.Tests` — corrupt-file recovery for `ConfigurationPresetStore` (TT0-5)

**Problem:** `ConfigurationPresetStore.ReadPresetFileAsync` (`src/ScanlineStudio.Settings/
ConfigurationPresetStore.cs:257-276`) has no try/catch — a hand-edited/corrupt preset throws straight
out of `LoadPresetAsync` from an interactive menu click. No test masks or catches this; it's a live,
untested defect.

**Change (production code, minimal, mirrors an existing pattern):** wrap `ReadPresetFileAsync`'s body
to catch `JsonException`/`IOException`/`UnauthorizedAccessException`, log via `[LoggerMessage]`
(mirror `JsonSettingsStore.LoadAsync`'s existing catch filter at `JsonSettingsStore.cs:75-79` and its
`Log.LoadFailed` declaration at `:206-207`), return `null`. `_logger` is already ctor-injected
(`ConfigurationPresetStore.cs:271`) — no new parameter needed.

**Corrected (plan-review round 1) — `LoadPresetAsync` already returns `null` for "preset not found"**
(`:104,112`)**, so a corrupt file now maps to the same `null` result. This is a deliberate,
documented choice, not an oversight** — note it in the fix's doc comment, and confirm the caller
(`ConfigurationsManagerWindowViewModel` or wherever `LoadPresetAsync`'s result is consumed) doesn't
show a "not found" message for what was actually a corrupt file; if it does, that's a real UX gap to
flag but not fix in this phase.

**Corrected — `ClonePresetAsync`'s contract, not just its test, needs deciding on paper first.**
`ClonePresetAsync` (`:161-162`) passes `ReadPresetFileAsync`'s result straight into
`WritePresetFileAsync` with no null check — once `ReadPresetFileAsync` can return `null`, this either
fails to compile or silently writes an empty-but-valid preset from a corrupt source, which is worse
than today's throw. **Decision: `ClonePresetAsync` must explicitly check for `null` and throw
`InvalidOperationException`**, matching its own existing "already exists" failure path at `:158` —
cloning a corrupt preset should fail loudly, not produce a silent empty clone.

**Corrected — drop the planned `ListPresetsAsync` corrupt-file test.** `ListPresetsAsync` (`:81-102`)
only enumerates `*.json` filenames; it never reads file content and structurally cannot throw on a
corrupt file. A test asserting it "skips" a corrupt file would be vacuously green — not real coverage.

**Final test list for `ConfigurationPresetStoreTests.cs`:**
`LoadPresetAsync_CorruptFile_ReturnsNullAndLogs` (truncated JSON) and
`ClonePresetAsync_SourceIsCorrupt_ThrowsInvalidOperationException`.

Also add the equivalent test for `JsonSettingsStore` — its own corrupt-file/permission-denied
hardening (already in source, fixing a documented prior "single bad byte bricked startup" bug) has
**zero tests today**, so a future edit narrowing that catch back down would ship silently green.
3 tests: truncated JSON, wrong-typed root (`"42"`), and a `chmod 000` file (reuse the exact Unix-only
pattern already at `AppLocationOverridesTests.cs:38-66`). Swap `NullLogger` for a `RecordingLogger`
in these 3 (every existing test in the file uses `NullLogger`, so no log assertion is possible today)
and assert `Log.LoadFailed` fires.

**Files:** `src/ScanlineStudio.Settings/ConfigurationPresetStore.cs`,
`tests/ScanlineStudio.Settings.Tests/ConfigurationPresetStoreTests.cs`,
`tests/ScanlineStudio.Settings.Tests/JsonSettingsStoreTests.cs`.

**Verify:** new tests fail on current `ConfigurationPresetStore` (proving the gap is real), pass after
the catch + `ClonePresetAsync` null-check are added. `dotnet test tests/ScanlineStudio.Settings.Tests -c Debug`.

---

## 4. `Host.Tests` — DI-graph exhaustive validation (TT0-6, first half)

**Problem:** `SstvCompositionRootTests.cs` resolves exactly 4 roots; a missing registration
(confirmed: `MacrosReferenceWindowViewModel`, `ConfigurationsManagerWindowViewModel`,
`IApplicationRestarter` are never exercised by any current test) surfaces as an unguarded crash in
the real app, not a test failure.

**Change (test-only — build a *separate* `ServiceProvider` instance inside the test with strict
validation options; do not modify `Program.cs`):**
1. `RegisterServices_AllDescriptors_ResolveWithoutThrowing` — build with
   `new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }`. Catches
   type-registered descriptors missing a dependency.
2. `RegisterServices_EveryRegisteredService_CanBeResolved` — enumerate every descriptor in the
   `ServiceCollection` and resolve each by its service type in a loop. `ValidateOnBuild` cannot see
   into factory-lambda registrations (confirmed ~10 of them: `JsonSettingsStore`,
   `ConfigurationPresetStore`, `ILocalizationService`, the Hamlib protocol factory, `ISstvSessionService`,
   etc.) — this loop is what actually exercises those. **Skip open generic type definitions**
   (`descriptor.ServiceType.IsGenericTypeDefinition`) — `services.AddLogging()` registers `ILogger<>`/
   `IOptions<>` as open generics, and `GetService(typeof(ILogger<>))` throws.

**Also fix while touching this file:** the 4 existing tests construct the real `SqliteReceiveHistoryStore`
against its default `history.db` path (the `ISettingsStore` substitution doesn't cover this
constructor's fixed default). Substitute `IReceiveHistoryStore` with a fake or a temp-path-backed
instance in the test's service overrides so CI/dev runs stop touching a real database file.

**Files:** `tests/ScanlineStudio.Host.Tests/SstvCompositionRootTests.cs`.

**Verify:** both new tests currently fail (a missing registration exists) or pass cleanly against the
current graph — confirm which before writing the plan's next step; either result is informative. No
real `history.db` file should be touched by a fresh `dotnet test` run (check via `git status`/file
mtime, not committed to source control anyway, but verifies the substitution worked).

---

## 5. `Host.Tests` — injectable shutdown timeout, testability seam only (TT0-6, second half)

**Problem:** `HandleLifetimeExitTests.cs`'s 6 tests call `HandleLifetimeExit` from a bare thread-pool
thread with no `SynchronizationContext` — the UI-thread-capture condition that makes the real
shutdown-deadlock bug (T0-5, tracked separately) dangerous cannot occur in the harness as written.

**Change (production code — testability seam only, zero behavior change at the default):** add an
internal optional `TimeSpan? disposeTimeout = null` parameter to `HandleLifetimeExit` in
`src/ScanlineStudio.Host/Program.cs` (confirmed hardcoded `TimeSpan.FromSeconds(10)` at `:314` — use
`disposeTimeout ?? TimeSpan.FromSeconds(10)` inside the method; **not** a `TimeSpan` default
parameter — that's not a compile-time constant, won't compile). No behavior change for the real
caller, which won't pass this parameter. This does **not** fix T0-5's actual bug (wrapping the dispose
in `Task.Run` so it can't capture the caller's context) — that fix is tracked separately in
`production_audit.md`'s Tier 0 and is out of scope here. Note: the disposed object is
`IAsyncDisposable host` (`Program.cs:307`'s actual parameter type), not `IHost` — the fake in the new
test should implement `IAsyncDisposable` directly, not a full `IHost`.

Add `HandleLifetimeExit_HostDisposeCapturesCallingContext_CompletesWithoutDeadlock` — install a
single-threaded `SynchronizationContext` test double on the calling thread, use a fake
`IAsyncDisposable` whose `DisposeAsync` awaits a gate **without** `ConfigureAwait(false)`, pass a
short `disposeTimeout` (e.g. 200ms). This test is a **characterization test today** (it will show the
current 10s-stall-then-recover behavior, bounded to 200ms by the injected parameter) — it becomes the
actual regression gate once T0-5's `Task.Run` fix lands separately.

**Files:** `src/ScanlineStudio.Host/Program.cs` (add the parameter, default preserves current
behavior exactly), `tests/ScanlineStudio.Host.Tests/HandleLifetimeExitTests.cs`.

**Verify:** existing 6 `HandleLifetimeExitTests` still pass unchanged (default parameter value is a
no-op for them). New test completes within its bounded timeout rather than hanging indefinitely.
`dotnet test tests/ScanlineStudio.Host.Tests -c Debug`.

---

## 6. `Core.Logbook.Tests` — ADIF non-ASCII and malformed-date coverage (TT0-7)

**Problem:** all 17 `AdifImporterTests.cs` tests use an in-memory ASCII `StringReader` — the
production decode path (`LogbookSessionService.cs:135`, `new StreamReader(filePath)`, defaults to
UTF-8-with-BOM-detection regardless of the real file's encoding) is structurally unreachable from any
fixture. Malformed-date coverage stops at field-length/missing-field; a short/invalid date currently
throws the wrong exception type (`ArgumentOutOfRangeException` vs. every other malformed path's
`FormatException`).

**Change:**
1. `Import_NonAsciiFieldValue_SlicesByUtf8ByteCountNotCharCount` — fixture
   `<NAME:5>Jörg<CALL:6>N0CALL<EOR>` (5 UTF-8 bytes for "Jörg", not 5 chars). Assert `Name == "Jörg"`
   **and** `Callsign == "N0CALL"` — the callsign assertion is what actually detects a char-count bug
   (it would misalign every field after the wrongly-sliced one). **Corrected (plan-review round 1):**
   `AdifImporter.cs:120-127` already slices by UTF-8 byte count correctly (documented at `:8-16`) —
   this test **passes today**, unchanged behavior. It's still worth adding as a characterization test
   pinning correct behavior, not a bug-fix regression test — the Verify step below reflects this.
2. **Removed (plan-review round 1) — rescoped out of this phase.** The CP1252-source-file test as
   originally planned targets the wrong layer: `AdifImporter.Import(TextReader)`
   (`AdifImporter.cs:38-40`) immediately does `Encoding.UTF8.GetBytes(reader.ReadToEnd())` — by the
   time a `TextReader` reaches this class, the encoding decision has already been made by the caller.
   The actual encoding bug lives in `ScanlineStudio.Application`'s
   `LogbookSessionService.ImportAdifFileAsync` (`new StreamReader(filePath)`, defaults to UTF-8
   regardless of the real file's encoding) — a test in `Core.Logbook.Tests` against `AdifImporter`
   directly cannot exercise or prove anything about that bug; it would have to pick the encoding
   itself, which proves nothing about production. Correctly scoped, this belongs as an
   `Application.Tests` test against `LogbookSessionService` (not `AdifImporter`), paired with fixing
   `ImportAdifFileAsync` to actually detect/declare its source encoding — that's source-audit item
   T1-18, tracked separately and out of scope for this phase.
3. `[Theory]` on `AdifImporter.cs:300-307`'s date/time parsing (`ParseDateTime`) — **corrected
   (plan-review round 1): a length-only guard is insufficient.** The method throws
   `ArgumentOutOfRangeException` from two distinct causes: short-string `AsSpan` slicing (`"2026"`,
   a 1-char time) **and** an out-of-range component reaching the `DateTimeOffset` constructor (e.g.
   `"20269999"` → month 99) — a length check alone leaves the second class throwing the wrong
   exception type.

   **Fix specification, fully pinned down (plan-review round 2 — round 1's spec omitted UTC/offset
   handling, a real bug: `DateTimeOffset.TryParseExact` with no explicit style stamps the *local
   machine's* offset, silently shifting every imported QSO instant by up to 14 hours; the current code
   is explicitly zero-offset (`new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero)`,
   `AdifImporter.cs:306`) and ADIF timestamps are UTC by spec — this must not regress on a non-UTC
   dev machine while shipping green from UTC CI runners):**
   ```
   DateTime.TryParseExact(dateSpan, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
   DateTime.TryParseExact(timeSpan, new[] { "HHmmss", "HHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
   // on success: new DateTimeOffset(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, TimeSpan.Zero)
   // on either failure: throw new FormatException(...)
   ```
   This preserves today's `TimeSpan.Zero` construction exactly — no offset/culture ambiguity, no
   local-machine-dependent behavior. **Note the acceptance narrowing:** current code accepts *any*
   time string ≥4 chars (4 chars → `HHmm`, ≥6 chars → truncated to `HHmmss`, per
   `AdifImporter.cs:303-305`); the `{"HHmmss","HHmm"}` exact-format list is stricter — a 5-char or
   7+-char time string that used to silently truncate now throws `FormatException`. This is a
   deliberate narrowing to ADIF's own defined 4-and-6-digit time formats, not an accident — state it
   in the commit/PR description. 4-digit (`HHmm`) support is **mandatory**, not optional: existing
   tests already depend on it (`AdifImporterTests.cs:235,256`, `TIME_ON:4`).

   Theory cases: `"2026"` (short date), a 1-char time string (short time), `"20269999"` (out-of-range
   month, previously the wrong exception type), `"abcdefgh"` (already throws `FormatException` today
   per current source — include as a characterization case, not a new bug).

**Files:** `tests/ScanlineStudio.Core.Logbook.Tests/AdifImporterTests.cs`,
`src/ScanlineStudio.Core.Logbook/AdifImporter.cs:300-307` (the `TryParseExact` guard — genuine small
production fix, the test can't pass without it).

**Verify:** item 1's test passes immediately (characterization of already-correct behavior — do not
expect a failure here). Item 3's theory fails against current source for the short-string and
out-of-range-month cases (wrong exception type today), passes after the `TryParseExact` guard.
`dotnet test tests/ScanlineStudio.Core.Logbook.Tests -c Debug`.

---

## 7. `Core.Sstv.Tests` — pin the noise-floor assertion (TT1-3)

**Problem:** `NoiseRobustnessTests.cs:62-123` computes `noiseFloorDb` across a 10-level SNR sweep and
never asserts on it — a DSP regression halving the usable noise floor is invisible.

**Change:** run the test once, record the current measured floor per mode, add
`Assert.True(noiseFloorDb &lt;= &lt;measured value&gt; + <one SNR step>, ...)` with a one-line comment
matching this file's existing house style ("re-measure and update deliberately, don't loosen"). Add
one SNR step of slack (plan-review round 1) — the sweep is discrete and seeded (`seed: 12345`), so
flake risk is low, but the CI matrix includes Windows/macOS legs where float rounding could shift a
borderline mode by one step; a bare pinned value risks a spurious cross-platform failure.

**Files:** `tests/ScanlineStudio.Core.Sstv.Tests/NoiseRobustnessTests.cs`.

**Verify:** `dotnet test tests/ScanlineStudio.Core.Sstv.Tests -c Debug --filter "FullyQualifiedName~NoiseRobustnessTests"` passes with the new assertion in place.

---

## 8. `UI.Tests` — fix 2 false-confidence marshaling tests (TT1-8)

**Problem:** `PaneViewModelTests.cs:109-129,1681-1703` raise their event from the same headless
thread the assertion runs on (`[AvaloniaFact]`'s headless UI thread) — they'd pass identically whether
`Dispatcher.UIThread.Post` marshaling exists or not.

**Change:** raise the event via `await Task.Run(() => sstvSession.RaiseModeDetected(mode));` (a
genuine non-UI thread), then `RunJobs()`, then assert. Add a captured
`Dispatcher.UIThread.CheckAccess()` inside the `PropertyChanged` handler to make the marshaling claim
explicit. Mirror the existing pattern already established at
`Fakes.cs:1307-1315` (`FakeFilePickerService.CompletePickHamlibLibraryFileOnBackgroundThread`).
**Note (plan-review round 1):** `[AvaloniaFact] public void` methods can't `await` directly — convert
the two affected tests to `async Task` (verify `Avalonia.Headless.XUnit` pumps the dispatcher
correctly for an `async Task` test method against the version pinned in this repo before relying on
it), or keep the method `void` and use `Task.Run(...).GetAwaiter().GetResult()` — safe here since the
VM only `Post`s work rather than needing the calling thread's own pump to proceed.

**Files:** `tests/ScanlineStudio.UI.Tests/PaneViewModelTests.cs`.

**Verify:** both tests still pass against current (correct) production marshaling. To confirm they'd
actually catch a regression, temporarily comment out the `Dispatcher.UIThread.Post` call in the
relevant ViewModel, confirm the test now fails, then restore it — don't leave this step's temporary
edit in the final diff.

---

## 9. `Core.Radio.Tests` — deterministic gates for the flaky 3-way race test (TT1-13)

**Pulled out of this phase (plan-review round 1) — needs its own plan + review round.** This is
concurrency work gating a keyed-transmitter-on-a-disposed-handle bug (`HamlibRadioProtocolTests.cs:
393-427`, `tests/ScanlineStudio.Core.Radio.Tests/FakeHamlibNative.cs`), which per this project's own
rule (CLAUDE.md §4/§7) is non-negotiable for full review weight — not a batch-in item. It also has
two unresolved design problems the first draft didn't account for: (1) `HamlibRadioProtocol._lock` is
`private readonly` (`HamlibRadioProtocol.cs:129`), so the test can't observe a queued waiter without
either reflection or a new `internal` seam, which contradicts "test-only" scope, and
`SemaphoreSlim.CurrentCount` can't distinguish "PTT is queued" from "nothing is queued" (both read
`0`) — a real replacement signal for the second `Task.Delay(20)` still needs to be designed. (2) A
gate placed unconditionally inside `FakeHamlibNative.Enter` would block *every* native call routed
through it, including the `rig_close`/`rig_cleanup` the dispose path itself needs — any gate must be
scoped to a specific call name (e.g. `GateOnCall = "rig_get_freq"`), and `CallDelay` is reused by a
second test (`HamlibRadioProtocolTests.cs:519`) so it must stay, with the gate added alongside it, not
replacing it. Track as a follow-up plan once these two are resolved on paper.

---

## 10. `Settings.Tests` — fix the shared-`/tmp`-path landmine (TT1-17)

**Problem:** `JsonSettingsStoreTests.cs:102,121,156` write to fixed shared paths
(`/tmp/relocated`, `/tmp/fresh-install-target`, `/tmp/conflict`) with inline cleanup that's skipped
on any assertion failure — one failed run permanently poisons every subsequent run on that machine.

**Change:** derive these from the test's own per-test temp subdirectory (however the class already
establishes one — check the constructor/`IDisposable.Dispose` pattern other tests in the file use)
and move cleanup into `Dispose()`, not an inline trailing statement.

**Files:** `tests/ScanlineStudio.Settings.Tests/JsonSettingsStoreTests.cs`.

**Verify:** deliberately fail one of the 3 affected tests (temporarily break an assertion), confirm no
leftover directory remains at the old fixed paths afterward, then restore the assertion.

---

## 11. `Core.Sstv.Tests` — stop the stale/vacuous TX golden-vector test from overclaiming (TT1-1)

**Problem:** `GoldenVectorTests.cs:120-180`
(`LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage`) reads two static BMPs and compares them
— invokes **zero production code** — while its name claims to validate the current TX encoder. Its
reference `_TX_RX.bmp` fixtures are stale relative to the `.mmv` set (which was regenerated against
the current encoder; the BMPs were not — self-documented at `GoldenVectorTests.cs:133-143`).

**Change:** (a) rename to `TxCaptureFixtureBmps_AreInternallyConsistent` so the name stops
overclaiming what it checks. (b) Add a provenance marker: a checked-in `_TX_RX.provenance` file per
mode recording the SHA-256 of the `_TX.mmv` it was decoded from; the test compares the current
`.mmv`'s hash against it — see the round-2 skip decision below for what happens on a mismatch (not a
hard failure).

**Corrected (plan-review round 1) — the provenance value itself must be specified, or the obvious
implementation makes this permanently and silently green.** The `.mmv` set was regenerated *after*
the BMPs (self-documented at `GoldenVectorTests.cs:133-143`), so the historical `.mmv` blob the BMPs
were actually decoded from no longer exists on disk — hashing the *current* `.mmv` (the easy,
obvious move) would produce a provenance file that always matches, which is worse than no check at
all: a permanently green test lying about freshness. **Use an explicit sentinel value** —
`UNKNOWN-STALE-PENDING-RECAPTURE` (or similar) — as the `.provenance` file content for all 11 modes
initially. (Recovering the historical `.mmv` via `git log`/`git show` on the fixtures directory was
considered and rejected for this phase — it adds git-archaeology risk for no benefit over the
sentinel, since the end state either way is "flags as stale until re-captured.")

**Decided (plan-review round 2) — skip, not fail.** Leaving 11 theory cases hard-`Assert.Fail`-red in
`Core.Sstv.Tests` would break the green-means-something convention for every *other* change in that
26,300-line project for however long the human re-capture session (which needs a real legacy install,
`TxCapture/README.md:57-73`) is pending — including undermining item 7's own "passes with the new
assertion" verification step in the same test run. Use `Skip`, via the same mechanism item 1
introduces — matches this repo's own established pattern for "this needs an external action to be
meaningful" (`RequiresPipeWireFactAttribute.cs`, and item 1's `RequiresHamlibFact`/
`RequiresRigctldFact`/`SkipOnWindowsFact`).

**Mechanism corrected (plan-review round 3) — xUnit 2.5.3's `TheoryDiscoverer` short-circuits on a
non-null `Skip` and emits exactly one skipped test case for the whole method, never enumerating
`[MemberData]`.** A per-mode `Skip` message on a `TheoryAttribute` subclass is therefore
unachievable without a new package (`Xunit.SkippableFact`) — not worth adding for this phase, since
all 11 modes share one sentinel state today anyway (a per-row skip only matters once re-capture is
*partial*, which is explicitly out of scope). **Implementation:** a static
`Lazy<IReadOnlyList<string>> StaleModes` that compares each mode's checked-in `.provenance` sentinel
against its current `.mmv`'s real SHA-256, and a `StaleFixtureTheoryAttribute : TheoryAttribute`
whose constructor sets `Skip` to one message listing every stale mode id (e.g. `"TX capture fixtures
stale for: robot-36, martin-m1, ... — re-capture per TxCapture/README.md"`) when `StaleModes` is
non-empty, `null` otherwise — sourced from the same comparison the test itself would otherwise run,
so the message and the sentinel can't drift apart. Expect **one skipped theory** in the run summary
(not "11 skipped"), naming every stale mode in its message. State explicitly in the test's own doc
comment that this comparison is inert (equivalent to a fixed `Skip`) until real post-recapture hashes
replace the sentinels — so a future reader doesn't "fix" this by hashing the current `.mmv` and
silently restoring the round-1 always-green bug.

**This phase does not include the actual human re-capture session** (needs a real legacy install per
`TxCapture/README.md:57-73`) — that's flagged in `production_audit.md` as its own step, out of scope
for a test-code-only phase.

**Files:** `tests/ScanlineStudio.Core.Sstv.Tests/GoldenVectorTests.cs`; new `.provenance` files
alongside the existing `Fixtures/GoldenVectors/TxCapture/*.mmv` — confirmed path:
`tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/TxCapture/` (`GoldenVectorTests.cs:83`).

**Verify:** the renamed test shows as **Skipped** today (one skipped theory, message naming all 11
stale modes) — that skip IS the correct, honest signal this step is meant to produce. Do not "fix" it
by regenerating BMPs in this phase; that's the separate human-capture step. The rest of
`Core.Sstv.Tests` (including item 7's own verification) stays green throughout.

---

## Explicitly out of scope for this phase

- TT0-1, TT0-2 (settings-race and Hamlib-hang regression tests) — pair with T0-2/T0-1 source fixes.
- TT1-2 (Core.Sstv worst-row/outlier golden-vector metrics) — needs a full measurement pass across
  all 19 fixtures first to set bounds; sizeable enough to be its own follow-up, not bundled here.
- TT1-13 (item 9, above) — pulled out for its own plan/review round; concurrency work with two
  unresolved design questions (see item 9's note).
- The CP1252 `LogbookSessionService`-level test (originally item 6.2) — correctly scoped to
  `Application.Tests`, paired with the T1-18 encoding-detection fix in `LogbookSessionService`; not
  this phase's `Core.Logbook.Tests`.
- Everything else in `production_audit.md`'s Test-suite Tier 1/2/3 lists.
- Any item in either the source-code Tier 0/1/2/3 lists (this plan is test-code only).

## Sequencing

Items 1, 2, 4, 7, 8, 10 have no dependencies and no production-code changes — safe to do in any
order, even in parallel; ready to build now (plan-review round 1 confirmed these sound as revised).
Items 3 and 6 each include one small, now-fully-specified production fix (a catch-and-log addition +
`ClonePresetAsync` null-check; a `TryParseExact`-based date guard) — do these as their own reviewable
diff each, not bundled with unrelated test files. Item 5's production change is a pure testability
seam (default-preserving optional parameter) — lowest-risk of the 3 production touches, but still its
own diff. Item 11 last, since its correct result is a newly-skipped (by design) sentinel-provenance
test with a specific stale-fixture message, which should be reviewed and understood on its own rather
than mixed into a batch of unrelated changes. Item 9 is deferred entirely — see above.
