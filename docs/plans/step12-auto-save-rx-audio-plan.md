# Step 12: Auto-save RX audio (ui_transition_plan.md step 12)

Status: **DONE, committed (`9051699`).** An always-on, bounded, per-reception audio slice
auto-saved alongside each RX history image, gated by an Options setting. Went through 4 full design
rounds before implementation (each round found a real correctness bug in the previous one's
correlation design) — the load-bearing conclusions are kept below since they aren't written
anywhere else; the round-by-round debate itself is not.

## What shipped

- `SstvSessionService` gains a two-tier capture buffer (a small always-on pre-roll ring +
  a mode-sized append buffer allocated on `ModeDetected`), an arm/close state machine, and
  `AudioSliceReady`/`TrySaveReceptionAudioAsync`.
- `RxAudioAutoSaver` (`Core.Application`) joins `AudioSliceReady` with `IReceiveHistoryStore`'s
  `Recorded` event by reception identity, writes the final WAV, and patches the history row.
- `ReceiveHistorySettings.AutoSaveAudioEnabled`/`AudioDirectory`, an Options row, Gallery/RX-details
  delete-linked-WAV + storage-bytes + "open audio file location" actions, and a status-bar chip
  showing whether a slice is actively armed (bound to real state, not a static label — a prior
  identical-looking chip was removed for exactly that reason, commit `2e009a9`).

## Non-obvious design decisions worth preserving (not written anywhere else)

- **Reception identity is assigned upstream, once, at the moment of arming — not reconstructed
  downstream from event arrival order.** The first 3 design rounds all tried to correlate the audio
  side and the image side by pairing events as they arrived (a shared "last" field, then a
  rendezvous FIFO) and each was broken by a real ordering case: concurrent completions, an orphaned
  entry permanently desyncing every later pairing, etc. The shipped design instead adds
  `ISstvDecoder.ReceptionSequence` — a monotonic id incremented once per reception, before the
  `ModeDetected` event fans out — and keys a `Dictionary<long, ...>` join on it. An orphan now
  strands only its own dictionary entry; it can never steal or block an unrelated pairing.
- **`DecodeRestarted` does not always mean "new reception."** Legacy has two real event orderings:
  dominant (`DecodeRestarted` for the old image, then a later `ModeDetected`) and minority (AVT,
  `ForceMode`: `ModeDetected` for the new reception fires first, `DecodeRestarted` for the old one
  follows in the SAME `PushSamples` call). A same-`PushSamples`-call `DecodeRestarted` necessarily
  refers to an OLDER reception and must never close the arm that call's own `ModeDetected` just
  opened — enforced via a push-epoch counter, not mode-identity comparison (mode definitions are
  shared singletons, so "does this restart's mode match what I armed" can't distinguish a
  same-mode-to-same-mode restart from the arm's own already-accounted-for restart).
- **Close-trigger sample count must derive from transmission-line count via `mode.ColorEncoding`,
  never from `ImageHeight`.** PD/MP/RM/MN modes (`ColorEncoding.YCbCrLinePaired` or
  `MonoAveragedPaired`) declare `ImageHeight` as `transmissionUnits * 2` — using it directly would
  double a slice's real duration for that whole mode family. MC is NOT in this family despite the
  superficial name similarity to MN (it declares `RgbSequential` with a literal height).
  A capacity-formula version of this same bug was caught and fixed twice, independently, in two
  different sections of the design before shipping.
- **Slices are written to a scratch file immediately on close, on a background thread — never held
  in memory waiting for a save path, never encoded on the audio drain thread.** An earlier design
  that retained the finished buffer in memory cost ~226 MB resident worst case; peak resident memory
  in the shipped design is honestly ~2 transient full-mode buffers (~224 MB) only for the brief
  window where a new arm starts before the previous slice's background write finishes releasing its
  own buffer.
- **`AudioCaptureReset` (sample-rate/device change, TX pause/resume, file-decode entry/exit) drops
  any currently-OPEN arm, but must NOT clear the join dictionary or touch already-closed scratch
  files.** An earlier draft cleared the whole join on every reset — that destroyed genuinely valid
  pending pairs whenever a reset happened to land right after a reception's slice closed.
- **Accepted v1 gaps, stated explicitly rather than silently absent**: a live reception interrupted
  by a file decode loses its audio (a miss, never a wrong attach); no shutdown flush (in-flight
  encode/join work is abandoned on app close/crash — a deliberate, user-approved scope cut, the
  real exposure window being on the order of ~1 second per reception); orphaned join/scratch
  entries are bounded by count-based eviction, not individually reclaimed.
- Pre-roll window is 10 s, sized against the real AVT worst case (`ModeDetected` fires ~8.8 s after
  header start for AVT, not before — an earlier draft had this backwards) with ~13% margin.

## Verification

Full test list (ring bounding, both event orderings, close-threshold correctness for the doubling
mode family, join correctness under concurrent completions, eviction, startup purge liveness check,
settings propagation, end-to-end re-decode tolerance) is implemented in the corresponding test
projects under `tests/ScanlineStudio.Application.Tests/` and `tests/ScanlineStudio.Core.Logbook.Tests/`
— not re-listed here; read the tests for current coverage rather than this historical plan.
