# Options tab non-functional controls — master backlog plan

Status: **DONE.** All 6 items in the final backlog resolved and committed (`9051699`). 4 items
shipped as real features (PLL tuning, Zero-crossing tuning, TX BPF/LPF, spectrum colors kept as a
prior design decision); 2 shipped as deliberate removals (Loopback mode, Polynomial calibration).
2 earlier items (RTS on RX, Tune satellite) were removed outright before the final backlog, and 3
items (Sound-file ID, VOX, OmniRig) were pulled out to their own dedicated plans — Sound-file ID is
also done (`docs/plans/sound-file-id-plan.md`), OmniRig is done (`spec/03-cat-layer.md`), VOX
remains unbuilt.

Per-item detail lives in `docs/plans/options-stub-item1-pll-tuning-plan.md`,
`-item2-zerocrossing-tuning-plan.md`, `-item3-tx-bpf-lpf-plan.md`. This file is kept only as the
historical index of the full 13-feature inventory and the scope decisions behind each disposition.

## Full inventory and final disposition

| # | Item | Disposition |
|---|---|---|
| 1 | General: 7 spectrum/waterfall colors | Removed as a stub — a deliberate prior design already replaced per-element legacy colors with a fixed SDR-style scheme (`WaterfallPalette.cs`). `docs/removed-features.md` documents it. |
| 2 | Radio: RTS on RX | Removed — never functional in legacy either. |
| 3 | Radio: PTT Lock | Removed — narrow applicability (2 of several PTT types), user declined after being shown Hamlib's `ptt_share` alternative. |
| 4 | Advanced: PLL tuning | Built. See `options-stub-item1-pll-tuning-plan.md`. |
| 5 | Advanced: Zero-crossing tuning | Built. See `options-stub-item2-zerocrossing-tuning-plan.md`. |
| 6 | Advanced: TX BPF / TX LPF | Built — a reversal of a prior documented removal (BPF toggle) plus new TX LPF work. See `options-stub-item3-tx-bpf-lpf-plan.md`. |
| 7 | Advanced: Loopback mode | Removed — reproducing it needs an architecture change (simultaneous TX/RX) already declined in favor of `RunLoopbackSelfTestAsync`. |
| 8 | Advanced: Polynomial calibration + Level Calibration Wizard | Removed — the calibration table's own application hook (`GetPictureLevelDiff`/`GetPixelLevel`) has no equivalent in this port's decoder; nothing to calibrate. |
| 9 | Identification: Tune satellite | Removed by explicit user decision (real in legacy, but no workflow this app needs to support). |
| 10 | Identification: Sound-file ID | Built, separately. See `docs/plans/sound-file-id-plan.md`. |
| 11 | Identification: VOX mode + Edit tone | Not built — needs an audio-level-triggered PTT path `IRadioSessionService` doesn't have. Revisit only if raised again. |
| 12 | Radio: OmniRig backend | Built, separately. See `spec/03-cat-layer.md`. |

## Process used

Every item got the full heavy loop (CLAUDE.md §7): plan-review before code, code-review-until-go
after. Full detail of blockers found/fixed per item lives in each item's own plan file.
