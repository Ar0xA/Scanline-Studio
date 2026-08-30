# Sound-file station ID (`CwIdMode.SoundFile`, legacy `OutputMMV`)

Status: **DONE, committed (`9051699`).** `MmvSoundFile.cs` (parse/resample), wired through
`AnalogFmSstvEncoder`, `SstvSessionService.ResolveTransmitSettingsAsync`, and the Options
Identification UI. `CwIdMode.SoundFile` is no longer a silently-no-op reserved enum member.

## What shipped

Parses a user-picked `.MMV` file (custom binary format, 16-bit signed mono PCM), resamples it to
the current TX sample rate if needed, and plays it back at the same TX dispatch point CW-ID/FSK-ID
already use — appended as a raw-sample block after the normal tone-segment stream, not interleaved
into it (no change to `GenerateFrequencySegments` or the existing segment type). Gated to the TX
path only; the read-only Identification preview never does the actual file read/resample.

## Non-obvious facts worth preserving (not written anywhere else)

- **MMV header contract**: magic-present (`0x55 0xAA`) selects the sample-rate byte at offset 2,
  payload starts at offset 4. No-magic path treats byte 0 as the rate index and re-seeks to offset
  0 — the 4 sniffed bytes ARE audio data. Rate table:
  `{11025,8000,6000,12000,16000,18000,22050,24000,44100,48000}`. No real `.mmv` fixture exists in
  `yoniq-old/` to confirm the PCM format against — inferred from the `(short*)` cast in legacy, not
  independently verified; flagged as such in the code's own doc comment.
- **Resampling deliberately does NOT reproduce legacy's `SampType`/`SampBase` quantized rate
  ladder** — this port's own sample-rate list includes rates (e.g. 14000 Hz) with no `SampTable`
  entry at all, so porting the ladder would require inventing a mapping legacy never needed.
  Resamples directly from the file's declared rate to this port's nominal `SampleRate` instead.
- **Amplitude is NOT normalized relative to tone level.** Legacy's row-playback branch applies no
  gain at all (unlike tones, which get `× outgain`) — sound-file samples play at their own raw
  encoded amplitude, `/32768.0f` reproducing that absolute level, not a ratio to tone volume.
- **A same-transmission exclusivity guard is required at BOTH `EncodeAsyncCore` and
  `EstimateSampleCount`**, computed as one identical local expression at each site
  (`CwEnabled ? null : SoundFileSamples`) — a guard written at only one site would let the two
  methods disagree on whether a sound-file block plays at all, producing a TX duration estimate
  that doesn't match the real encode.
- 32 MB read cap (legacy has none) — a safety divergence against a mis-picked oversized file, not a
  fidelity change; legacy was never exercised against a multi-gigabyte "sound file."
