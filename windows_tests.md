# Windows test-suite results

Full `dotnet test` runs of the whole solution on a real Windows machine, with every failure traced
to a specific cause. Recorded because the suite had, until the first of these runs, only ever been
read on Linux and CI.

| Run | Commit | Result |
|---|---|---|
| 1 — baseline | `3aace07` | 110 failed, 4945 passed, 68 skipped (~35 min) |
| 2 — after fixes | `ca56010` | **9 failed, 5039 passed, 80 skipped** (~18 min) |

## How to run it here

| | |
|---|---|
| Machine | Snapdragon X Plus, Windows 11 build 26200, **ARM64** |
| SDK | .NET 8.0.424 (arm64) |
| Command | `vcvarsall.bat arm64` then `dotnet test ScanlineStudio.sln /p:Platform="Any CPU"` |

The test host runs on the **ARM64** runtime, so the native shim must be built ARM64 for these tests —
not the `win-x64` shim that `build_windows.md`'s standalone publish produces. Use `vcvarsall arm64`
for tests and `vcvarsall arm64_x64` for the x64 publish. Since `f0bc793` the shim target stages
per-architecture output, so the two no longer overwrite each other.

## Run 2 — where it stands

| Project | Failed | Passed | Skipped |
|---|---:|---:|---:|
| Host | **6** | 67 | 1 |
| UI | **2** | 1626 | 0 |
| Application | **1** | 514 | 0 |
| Core.Sstv | 0 | 1809 | 21 |
| Core.Radio | 0 | 295 | 12 |
| Core.Logbook | 0 | 254 | 0 |
| Core.Imaging | 0 | 138 | 0 |
| Settings | 0 | 97 | 5 |
| Core.Cw | 0 | 97 | 0 |
| Core.Audio.MiniAudio | 0 | 60 | **41** |
| UI.Font | 0 | 35 | 0 |
| Core.Audio | 0 | 24 | 0 |
| Core.Localization | 0 | 23 | 0 |

---

## Fixed since run 1

**§1a SQLite file locking — 78 → 0.** `SqliteReceiveHistoryStoreTests` (61) and
`SqliteLogbookRepositoryTests` (17) all passed. Logbook is now fully green, 254/254.

**§2 Hamlib Linux soname — 14 → 0.** The locator/runtime/factory/discovery tests no longer register
a `libhamlib.so.4` candidate the Windows locator will never try. `ca56010` also added coverage for
the Windows branch itself.

**§3 Managed assembly natively loadable — 1 → 0.**

**§4 POSIX path literals — 3 → 2.** Partially done; see below.

**§6 Decode probes — 6 → 0.** Now gated behind `RequiresDecodeMeasurementFactAttribute`, so they no
longer fail by design in an ordinary run. This is also where the runtime halved: those four probes
alone cost ~27 minutes.

**§7 The settings ACL defect — fixed, and verified beyond the green test.** A passing test could
equally have meant a loosened assertion, so the production path was read directly:
`JsonSettingsStore.SaveAsync` now calls `TrySetOwnerOnlyPermissions`, and `SetWindowsOwnerOnlyAcl`
does `SetAccessRuleProtection(isProtected: true, preserveInheritance: false)` — breaking inheritance
and granting owner + SYSTEM + Administrators only. That is the right fix.

> **Existing installs are not retrofitted until the next save.** On this machine `settings.json`
> still carries the inherited ACE granting `FullControl` to an app-capability SID (`S-1-15-3-…`),
> because nothing has written settings since the fix landed. Launching the new build and changing any
> setting once repairs it. Anyone who has ever stored a QRZ password should do that.

---

## Still failing — 9

### A. Log file held open across move/delete — 6 failures — needs a product decision

`Host.Tests.FileLoggerProviderTests`, unchanged from run 1:

```
System.IO.IOException : The process cannot access the file
'...\Temp\FileLoggerProviderTests-<guid>\app.log' because it is being used by another process.
```

Run 1's §1 had two halves; only the SQLite half was fixed. This half is harder because the tests
exercise relocation, rollback and failure-recovery **while the writer is deliberately live** — so
"dispose the writer first" would delete the very scenario under test.

**Suggested fix — change the product, not the test.** Open the log `FileStream` with
`FileShare.ReadWrite | FileShare.Delete`. Windows then permits rename/delete underneath the open
handle, which is the behaviour the relocation logic was designed against on Linux. This is worth
doing on its own merits: without `FileShare.Delete`, a user with `app.log` open in a viewer can
block the app's own log rotation, and `RelocateAsync` can fail in the field for the same reason the
tests fail here.

If that is judged too invasive, the fallback is to keep the writer live but assert on a copy — but
that weakens exactly the races these tests exist to pin down.

### B. POSIX path literal — 2 failures — test-only

`UI.Tests.PaneViewModelTests`:

```
Assert.Contains() Failure: Item not found in collection
Collection: ["/tmp/scanlinestudio-history"]
```

Two of run 1's three are fixed; these two still compare against hardcoded `/tmp/...` strings.

**Suggested fix.** Same as the ones already corrected: build the expectation with `Path.Combine`
from a temp root so the assertion means "the containing directory of X", not "this exact POSIX
string".

### C. Tune cancellation does not throw — 1 failure — investigate as a possible real bug

`Application.Tests.SstvSessionServiceTests.TuneAsync_TokenCancelledMidTone_StillUnkeysPttAndRestartsCapture`

```
Assert.Throws() Failure: No exception was thrown
Expected: typeof(System.OperationCanceledException)
```

Run 1's failure in this area (`SstvSessionServicePttSafetyTests.Round6…`) is gone, and this one
appeared in its place. Note that `ca56010` is titled "Convert the last silent-pass tests" — so the
likeliest reading is that this test previously passed vacuously and now genuinely asserts, meaning
it is **exposing a pre-existing cancellation gap rather than a new regression**.

**Suggested handling.** Do not treat this as a test-portability item. It is on the transmitter
keying path, where a missed cancellation means a rig stays keyed. Reproduce it in isolation and
under repetition, on Windows and Linux, and establish whether `TuneAsync` genuinely fails to observe
the token mid-tone. Only if it proves to be a timing artifact of a slower ARM64 host should the test
be adjusted.

---

## The real gap: no round-trip coverage on Windows — 41 skipped

`Core.Audio.MiniAudio` reads **0 failed, 60 passed, 41 skipped**, which looks green but is not. All
41 skips are `[RequiresPipeWireFact]`, whose probe shells out to `pactl`:

```
Skip = "Requires a running PulseAudio/PipeWire-pulse server reachable via pactl
        -- not available in this environment."
```

`pactl` does not exist on Windows, so the gate skips unconditionally. Attribute census for that
project: 51 plain `[Fact]`, **41 `[RequiresPipeWireFact]`**, 7 `[WindowsAudioReadOnlyFact]`,
3 `[WindowsAudioExclusiveFact]`.

That 41 covers playback, capture, device enumeration, hotplug — and
`MiniAudioEngineSstvRoundTripTests.EncodeThenDecode_ThroughRealMiniAudioEngine_RoundTripsWithinTolerance`,
described in its own docs as "the real payoff test". **It has never run on Windows.**

`f0bc793` improved the position but did not close it. It added `WasapiLoopbackCaptureTests`, gated
`[WindowsAudioExclusiveFact]`, and both tests pass here:

```
set SCANLINE_WINDOWS_AUDIO_EXCLUSIVE=1
dotnet test tests\ScanlineStudio.Core.Audio.MiniAudio.Tests --filter FullyQualifiedName~WasapiLoopbackCaptureTests
→ Passed! Failed: 0, Passed: 2, Skipped: 0
```

So Windows now has a working loopback capture primitive — but the round trip was left on the
PipeWire gate and still skips.

**Suggested fix, in order of value:**

1. **Move the round-trip test onto the loopback primitive that now exists.** It is the single
   highest-value test in the suite for this platform, and the missing piece (a way to capture what
   was played) is no longer missing.
2. **Replace the PipeWire gate with a capability gate.** `RequiresPipeWireFact` conflates "needs a
   real audio server" with "needs PulseAudio". A `RequiresRealAudioFact` that probes `pactl` on
   Linux and WASAPI loopback on Windows would let most of the 41 run on both platforms, instead of
   one platform testing everything and the other testing nothing.
3. **Keep the tiering.** `WindowsAudioFactAttributes` already distinguishes read-only from
   device-mutating, with the opt-in variable for the latter. That design is sound — extend it rather
   than replace it.

### What Windows-specific coverage does pass

The Windows-only tests from `b99f678` all pass, including the opt-in exclusive tier:

- WASAPI device-id conversion
- `IAudioEndpointVolume` mute query — including
  `MuteQuery_TracksTheRealMuteState_WhenItIsChangedUnderneath`, which mutes and unmutes the real
  default endpoint and confirms the query follows it (4 passed, 0 skipped under the opt-in variable)
- WASAPI loopback capture, both tests

---

## Suggestions for the suite as a whole

1. **Add a Windows CI leg that runs the `WindowsFact` and read-only audio tiers.** Everything in this
   document was found by hand, twice. Without a Windows job these regress silently — and the two
   platform-assumption families (file-handle semantics, path literals) are exactly the kind that
   reappear with every new test.
2. **Run the opt-in exclusive tier on a schedule, not per-commit.** It mutes a real endpoint, so it
   does not belong in a per-push job, but it is the only thing verifying the COM mute path end to
   end. A nightly or weekly job on a dedicated machine is the right home.
3. **Prefer a capability probe to a platform probe** whenever a test gate is written. The `pactl`
   gate is the cautionary example: it reads as "needs audio" but behaves as "needs Linux", and the
   resulting skip is silent.
4. **Keep converting silent-pass tests.** That pass (`ca56010`) is what surfaced item C above. A test
   that cannot fail is worse than no test, because it reports coverage that does not exist.
5. **Treat "0 failed with N skipped" as unread until N is explained.** Both audio findings in this
   document were invisible in the failure column. A run summary that prints skip *reasons* grouped by
   gate would make this self-evident.

## Priority

1. **Round trip onto WASAPI loopback** — the main user-facing path, still unverified on Windows.
2. **Item C, tune cancellation** — small, but it is transmitter keying; confirm it is not a real gap.
3. **Item A, `FileShare.Delete`** — 6 failures, and a genuine field robustness improvement.
4. **Windows CI leg** — stops all of the above from silently returning.
5. **Item B, path literals** — mechanical.
