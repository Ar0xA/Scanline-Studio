# Windows test-suite results

A full `dotnet test` run of the whole solution on a real Windows machine, with every failure traced
to a specific cause. Recorded because the suite has, until now, only ever been green on Linux and
CI: this is the first time the Windows column has been read end to end.

## Run

| | |
|---|---|
| Commit | `3aace07` ("Fix four blockers the audit found in the Windows tests") |
| Machine | Snapdragon X Plus, Windows 11 build 26200, **ARM64** |
| SDK | .NET 8.0.424 (arm64) |
| Native shim | `scanlineaudio.dll` compiled ARM64 via `vcvarsall.bat arm64` |
| Command | `dotnet test ScanlineStudio.sln /p:Platform="Any CPU"` |
| Duration | ~35 min (dominated by `Core.Sstv.Tests` at 30 m) |

The test host runs on the **ARM64** runtime, so the native audio shim must be built ARM64 for these
tests — not the `win-x64` shim that `build_windows.md`'s standalone publish produces. Building the
x64 shim and then running `dotnet test` leaves a shim the arm64 test host cannot load. See
"Native shim is not RID-aware" below.

## Result

**110 failed, 4945 passed, 68 skipped** — 5123 total across 13 test projects.

| Project | Failed | Passed | Skipped |
|---|---:|---:|---:|
| Core.Logbook | **78** | 176 | 0 |
| Core.Radio | **15** | 277 | 12 |
| Core.Sstv | **6** | 1809 | 15 |
| Host | **6** | 68 | 0 |
| UI | **3** | 1625 | 0 |
| Settings | **1** | 99 | 2 |
| Application | **1** | 514 | 0 |
| Core.Audio.MiniAudio | 0 | 60 | **39** |
| Core.Imaging | 0 | 138 | 0 |
| Core.Cw | 0 | 97 | 0 |
| UI.Font | 0 | 35 | 0 |
| Core.Audio | 0 | 24 | 0 |
| Core.Localization | 0 | 23 | 0 |

**Only one of the 110 failures is a production defect.** 103 are tests encoding Linux-specific
assumptions, and 6 are diagnostic probes that fail by design. The more serious finding is not in the
failure list at all — it is in the *skips*.

---

## 1. Windows file locking — 84 failures — test-only

`Core.Logbook` (78) and `Host` (6). Every one is the same `IOException`, and in every case the
assertions under test had already **passed**; the failure is in cleanup or in a move/delete step.

```
System.IO.IOException : The process cannot access the file
'...\Temp\scanline-studio-logbook-test-<guid>.db' because it is being used by another process.
  at System.IO.FileSystem.DeleteFile(String fullPath)
  at SqliteLogbookRepositoryTests.DeleteDb(String path):line 554
```

Checked exhaustively: **78 of 78** Logbook failures are this, none are assertion failures.

| Class | Count | File held open by |
|---|---:|---|
| `SqliteReceiveHistoryStoreTests` | 61 | SQLite connection (pooled) |
| `SqliteLogbookRepositoryTests` | 17 | SQLite connection (pooled) |
| `FileLoggerProviderTests` | 6 | the logger's own `FileStream` on `app.log` |

**Why only on Windows.** POSIX `unlink()` succeeds on a file that is still open — the directory
entry goes immediately and the inode dies with the last handle. Windows refuses `DeleteFile` and
`MoveFile` while any handle is open without `FILE_SHARE_DELETE`. Identical test code therefore
passes on Linux and fails here. Nothing about the production behaviour differs.

**Fix (SQLite, 78).** `Microsoft.Data.Sqlite` pools connections, so disposing the repository does
not necessarily close the underlying handle. Either:

- call `SqliteConnection.ClearPool(connection)` (or `ClearAllPools()`) in the test helper before
  `File.Delete`, or
- add `Pooling=False` to the connection string used by tests only, or
- give each test a temp *directory* and delete the directory at fixture teardown, tolerating a
  residual file.

The first is the narrowest and keeps production paths untouched.

**Fix (logger, 6).** `FileLoggerProviderTests` exercises relocation and rollback, so the writer is
deliberately live while the file moves. Either dispose the writer before the move in the test, or —
better, because it also matches what the production relocation path wants — open the log stream with
`FileShare.ReadWrite | FileShare.Delete` so Windows permits rename/delete underneath an open handle.
That second option is a production change and should be weighed on its own merits; it would make
Windows behave like the Linux path the code was designed against.

---

## 2. Hamlib locator tests assume a Linux soname — 14 failures — test-only

All in `Core.Radio`: `HamlibLibraryLocatorTests` (5), `HamlibRuntimeTests` (3),
`HamlibProtocolFactoryTests` (3), `HamlibDiscoveryServiceTests` (3).

`tests/ScanlineStudio.Core.Radio.Tests/HamlibLibraryLocatorTests.cs:12` hardcodes:

```csharp
private const string LinuxSoname = "libhamlib.so.4";
```

and registers that path with the fake loader. On Windows `HamlibLibraryLocator` never yields that
name — it yields `hamlib-4.dll` and `libhamlib-4.dll`, each also joined against
`AppContext.BaseDirectory`. So nothing the fake registered is ever attempted and every auto-detect
test throws `HamlibUnavailableException`:

```
- hamlib-4.dll: failed to load ((fake) not registered as loadable)
- libhamlib-4.dll: failed to load ((fake) not registered as loadable)
- ...\bin\Debug\net8.0\hamlib-4.dll: failed to load ((fake) not registered as loadable)
- ...\bin\Debug\net8.0\libhamlib-4.dll: failed to load ((fake) not registered as loadable)
```

The file's own header comment concedes the design: it was written around "a single Linux candidate".
The Runtime/Factory/Discovery failures are downstream of the same fake.

**This is not a production bug.** The real locator works on Windows: the shipped app loads
`libhamlib-4.dll` (Hamlib 4.7.2, 64-bit) and drives a real rig over CAT.

**Fix.** Replace the constant with a platform-conditional candidate set, and assert against the
locator's *actual* candidate list rather than a hardcoded name — e.g. expose the candidate sequence
(it is already enumerable) and have the fake register whatever the locator will really try. That
keeps the tests honest on all three platforms instead of pinning one.

---

## 3. A managed assembly *is* natively loadable on Windows — 1 failure — test-only

`NativeLibraryLoaderTests.TryLoad_PathExistsButIsNotALoadableLibrary_ReturnsFalseWithARealErrorDetail`

```
Assert.False() Failure
Expected: False
Actual:   True
```

The test picks its own assembly as a file that "exists on disk but is not a native shared library
the OS loader can load". That premise holds on Linux — a .NET assembly is not an ELF, so `dlopen`
rejects it. On Windows a managed assembly is still a perfectly valid PE image, and `LoadLibrary`
maps it happily, so `TryLoad` correctly returns `true`.

**Fix.** Use a fixture that is not a valid image on any platform — write a few bytes of text to a
temp file named `*.dll` and point the test at that. The test's real intent (that `errorDetail` never
reports "operation completed successfully") is worth keeping; only the fixture is wrong.

---

## 4. Hardcoded POSIX paths in UI tests — 3 failures — test-only

`UI.Tests`: `PaneViewModelTests` (2), `ImageViewerWindowViewModelTests` (1). All are string equality
against literals like `"/tmp/history"`, `"/tmp"`, `"/some\\other\\folder"`, compared to values the
code builds with `Path`-aware APIs. On Windows the produced path has different separators/rooting.

**Fix.** Build the expected value with `Path.Combine` from a temp root rather than a literal, so the
assertion says "the containing directory of X" instead of "this exact POSIX string".

---

## 5. PTT-safety race resolves differently under Windows scheduling — 1 failure — needs a look

`Application.Tests.SstvSessionServicePttSafetyTests`
`.Round6_DisposeAsync_OverlappingTransmits_StillBackstopsWhenNewerCallClearsOlderCallsRegistration`

```
Assert.Throws() Failure: Exception type was not an exact match
Expected: typeof(System.InvalidOperationException)
Actual:   typeof(System.TimeoutException)
```

It took 4 s — the test waited out a timeout instead of hitting the expected invalid-state path. This
is the only failure that is **not** obviously a platform assumption in the test. It concerns PTT
safety (a transmitter keyed when it should not be is the one class of bug with real-world
consequence), so it deserves a proper look rather than a tolerance bump: either the overlapping
dispose genuinely races differently under Windows thread scheduling, or the test's timeout is too
tight for a slower/ARM64 host. Reproduce in isolation and under repetition before concluding.

---

## 6. SSTV probes fail by design — 6 failures — expected

`Core.Sstv`: `HorizontalRegistrationProbe` (2), `EdgeContaminationSizingProbe`,
`IdealAudioRegistrationProbe`, `LegacyAudioRegistrationProbe`, `LegacyGreenBiasProbe`.

These are measurement harnesses for known defects tracked in `BACKLOG.md` / `docs/known-decode-defects.md`.
They report their findings *through the failure message* — a markdown table of per-mode metrics — 
because that is how the numbers surface in test output. Example:

```
Edge contamination, per channel, FLAT WHITE source, real chain at 44100.
Affected modes: 38 of 43. More than one channel corrupted at the tail: 23. Leading edge also corrupted: 9.
```

Platform-independent; they fail identically on Linux. They also dominate runtime — 10 m 23 s, 9 m 17 s,
3 m 49 s, 3 m 42 s — and are the reason the suite takes ~35 minutes.

**Suggestion.** These are the odd ones out: every other harness of this kind in `Core.Sstv` is gated
off by default (23 such tests skip). Gating these behind the same opt-in would make a clean run
actually mean "clean" and cut suite time by roughly 80%.

---

## 7. The real defect: `settings.json` is not private on this machine — 1 failure

`Settings.Tests.WindowsSettingsFileProtectionTests.AFileCreatedWhereSettingsLive_GrantsNoAccessToEveryoneOrAuthenticatedUsers`

```
a file created where settings.json lives grants read to S-1-15-3-3557520199-...
JsonSettingsStore skips owner-only permissions on Windows on the grounds that the settings
directory is already private. On this machine it is not, and settings.json can hold a
QRZ.com password in plaintext.
```

Verified independently against the real ACLs:

```
%APPDATA%\ScanlineStudio\settings.json
  S-1-15-3-3557520199-...-3692855932   FullControl   Allow   (inherited)
  <user>                               FullControl   Allow   (inherited)
  NT AUTHORITY\SYSTEM                  FullControl   Allow   (inherited)
  BUILTIN\Administrators               FullControl   Allow   (inherited)
```

The extra ACE is an **app-capability SID** (`S-1-15-3-…`, unresolvable to a name), and it is
**inherited** — the same ACE sits on `%APPDATA%` itself and on `%APPDATA%\Microsoft`. So the profile
put it there, not this application.

That does not make the assumption safe. `JsonSettingsStore` deliberately skips owner-only
permissions on Windows *because* it assumes `%APPDATA%` is already private, and on a real machine it
is not. The Unix counterpart (`SaveAsync_RestrictsSettingsFileToOwnerOnlyPermissions`, which uses
`File.SetUnixFileMode`) is skipped on Windows, so nothing else covers this.

**Fix.** Do on Windows what the Unix path already does: set an explicit DACL on `settings.json` at
write time — owner + SYSTEM + Administrators, inheritance disabled — instead of inheriting whatever
the profile happens to carry. `FileSecurity` with `SetAccessRuleProtection(true, false)` expresses
this. Worth pairing with not storing the QRZ password in plaintext at all (DPAPI
`ProtectedData.Protect` is the low-effort Windows answer), but the ACL is the immediate gap the test
names.

---

## 8. The bigger gap: no real audio coverage on Windows — 39 skips

`Core.Audio.MiniAudio` reports **0 failed, 60 passed, 39 skipped**. That looks green, but the 39
skips are exactly the tests that touch real audio hardware:

| Class | Skipped |
|---|---:|
| `MiniAudioEngineTests` | 16 |
| `MiniAudioCaptureSessionTests` | 8 |
| `MiniAudioPlaybackSessionTests` | 6 |
| `MiniAudioDeviceEnumeratorTests` | 4 |
| `HotplugDisposeTests` | 2 |
| `MiniAudioDeviceMuteQueryTests` | 2 |
| `MiniAudioEngineSstvRoundTripTests` | 1 |
| `MiniAudioSpikeGateTests` | 1 |

All are gated by `[RequiresPipeWireFact]`, whose probe shells out to `pactl`:

```
Skip = "Requires a running PulseAudio/PipeWire-pulse server reachable via pactl
        -- not available in this environment."
```

`pactl` does not exist on Windows, so the gate skips unconditionally. The consequence is that
**playback, capture, device enumeration, hotplug and the full encode → device → decode round trip
have zero automated coverage on Windows.** The one that stings most is
`MiniAudioEngineSstvRoundTripTests.EncodeThenDecode_ThroughRealMiniAudioEngine_RoundTripsWithinTolerance`,
described in its own docs as "the real payoff test" — it never runs here.

**Fix.** The gate conflates "needs a real audio server" with "needs PulseAudio". Split it: keep the
`pactl` probe for the Linux virtual-cable path, and add a Windows arm that uses a WASAPI loopback
device or a virtual cable (VB-CABLE and similar are the usual choice) with its own opt-in variable,
following the tiering `WindowsAudioFactAttributes` already establishes. Until then, the Windows
audio path is verified only by hand.

### What *does* pass on Windows audio

The Windows-only audio tests added in `b99f678` all pass: WASAPI device-id conversion and the
`IAudioEndpointVolume` mute query. Running the opt-in exclusive tier —

```
set SCANLINE_WINDOWS_AUDIO_EXCLUSIVE=1
dotnet test tests\ScanlineStudio.Core.Audio.MiniAudio.Tests --filter FullyQualifiedName~WasapiMuteQueryTests
```

— gives **4 passed, 0 skipped**, including
`MuteQuery_TracksTheRealMuteState_WhenItIsChangedUnderneath`, which mutes and unmutes the real
default endpoint and confirms the query follows it. That is the first real-hardware verification of
the COM mute-query path.

---

## Other Windows notes worth recording

**Native shim is not RID-aware.** `ScanlineStudio.Core.Audio.MiniAudio.csproj`'s
`BuildNativeShimWindows` target is incremental on `$(OutDir)scanlineaudio.dll`, and that path
contains no RID. Building for one architecture and then another silently reuses the first shim, with
no build error — the mismatch only shows at load time. Delete
`bin/<cfg>/net8.0/scanlineaudio.dll` and the matching `obj/<cfg>/net8.0/scanline_audio.obj` when
switching. Putting the RID into that target's output path would remove the trap.

**Test-run remnants.** Test temp files are left behind on Windows precisely because the deletes
above fail: `%TEMP%\scanline-studio-*-<guid>.db` and `%TEMP%\FileLoggerProviderTests-<guid>\`
accumulate across runs. Fixing §1 clears this too.

## Priority

1. **§7 settings ACL** — the only production defect, and it concerns a plaintext credential.
2. **§8 audio coverage gap** — the round trip is unverified on the platform most users run.
3. **§5 PTT race** — small, but it is the transmitter safety path; confirm it is only a test timeout.
4. **§1 file locking** — 84 failures, one narrow fix each; largest noise reduction.
5. **§2/§3/§4** — mechanical test-portability fixes.
6. **§6 probe gating** — optional, but would make a clean run legible and cut ~80% of suite time.
