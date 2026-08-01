# CLAUDE.md — Yoniq v2 (YONIQ/MMSSTV → .NET 8 + Avalonia)

> Standing instructions, loaded every session. Detailed specs live in `spec/` and
> `docs/`, not here. Keep it tight: if removing a line wouldn't cause a mistake, cut it.
> **Restate the Collaboration rule (§1) explicitly in every Opus/subagent prompt —
> subagents run in isolated context and do NOT read this file.**

Yoniq v2: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork), planned in `spec/00-spec/15`.
Legacy source: https://github.com/w0eeemst/YONIQ (not in this repo). Specs reference legacy files
assuming a local clone at `yoniq-old/YONIQ-main/` (gitignored, **never committed**) — clone it
separately to inspect.
QMSSTV reference (secondary): https://github.com/ON4QZ/QSSTV — clone separately if you need to cross-check DSP/decoding logic; not in this repo. Assuming a local cline at `QSSTV-main/` (gitignored, **never committed**) — clone it

---

## 0. Precedence (resolve conflicts in this order)

1. Saved project memories & locked architectural decisions — **non-negotiable**.
2. **Actual legacy YONIQ source behavior** (the ground truth — verify, never infer).
3. **QMSSTV** reference implementation (secondary — cross-verify tricky logic/edge cases only when YONIQ is ambiguous; inspiration, not authority; see top of file).
4. Specs in `spec/` / `docs/`.
5. General best practice / your own judgment.

Higher wins. If a lower source contradicts a higher one, say so and move on.

---

## 1. Collaboration

User has **ADHD**: stay tightly scoped — no unrequested tangents or scope creep. Note off-scope
findings in **one line** instead of chasing them. **Restate this explicitly in every Opus/subagent
prompt; don't rely on them reading this file.**

---

## 2. What "port" / "preserve behavior" means

Full rewrite (C++Builder/VCL → C#/Avalonia), **not** incremental refactor.
"Preserve behavior" / "backward compatibility" = **observable behavior, user data, feature scope**
(a decode still produces the same image; `.ini` settings still import cleanly) — **not** source/binary
compatibility with the old codebase. "Prefer refactor over rewrite" applies **within** the new C#
codebase once built, not to the initial port.

- **Port-first is scoped to DSP/codec math only** — SSTV encode/decode, filters, demodulators, CAT
  framing (`sstv.cpp`, `Fft.cpp`, `fir.cpp`, `cradio.cpp`): port the **exact** legacy algorithm
  (same filter structure/discriminator/constants), don't invent-and-tune. This does **not** extend to
  the surrounding GUI/architecture — that is intentionally being modernized.
- **Removal rule:** dropping a capability instead of porting it needs a
  [docs/removed-features.md](docs/removed-features.md) entry (legacy files, replacement if any, affected
  users). "Superseded"/"replaced" claims must state what's **not** covered — see that doc's
  OmniRig/CItems/MMlink/Loglink entries for examples of partial, not full, replacement.

---

## 3. Primary objectives

- **No assumptions** — verify against actual legacy code; don't infer from similarly-named/shaped cases
  or general domain knowledge (e.g. "Robot 72 is probably Robot 36 but slower" is **false** — it's
  grouped with a different family in `Main.cpp`). Flag anything unverified as an assumption; don't
  present it as confirmed.
- **Port first, invent second (DSP/codec math)** — see §2.
- Preserve behavior whenever practical.
- Keep commits small and reviewable.
- Never remove existing radio support unless replaced; log partial replacement in `docs/removed-features.md`.
- Write tests for all new functionality.
- Dependency injection everywhere; avoid static mutable state.
- All hardware communication must be async.
- UI never talks to radio/audio/DSP directly — only via `Yoniq.Application` service interfaces
  (`spec/01-architecture.md`).
- No hardcoded UI strings — localize everything (`spec/10-localization.md`; the legacy language `.ini`
  files are **not** a string table).
- Nullable reference types; treat warnings as errors.
- SOLID principles; composition over inheritance.

---

## 4. Legacy-source-specific rules

C++Builder/VCL conventions that **silently produce wrong results** if ported naively:

- **Encoding rule:** every legacy file read (`.ini`/`.txt`/`.DEF`/`.dfm`/`.mtm`/`.MDT`/`.bin`/`.cpp`/`.h`
  mined for strings or constants) must specify its source encoding explicitly. Assume
  **Windows-31J / CP932** for Japanese-origin content (confirmed in `Mmsstv Japanese.ini`, `MMCG.DEF`,
  inline literals) — **never** assume UTF-8. Register `System.Text.CodePagesEncodingProvider` at
  startup. No legacy text enters the codebase without a documented decode step.
- **Binary-is-bytes rule:** legacy `AnsiString`/`char[]` protocol/binary fields (e.g. `cradio.h`'s
  `CmdInit`/`CmdRx`/`CmdTx`/`cmdGNR`) must be modeled as `byte[]`/`ReadOnlyMemory<byte>`, **never**
  string/JSON — they carry raw bytes ≥0x80 and embedded nulls that a text-based migration would corrupt.
- **Behavioral-parity rule:** any DSP-core (`sstv.cpp`/`Fft.cpp`/`fir.cpp`) or CAT-layer (`cradio.cpp`)
  port needs **golden-vector tests** — real legacy-captured reference input/output with a documented
  tolerance/rounding rule (`spec/13-testing.md`). A round-trip test alone is insufficient (both halves
  can be wrong the same way); legacy is `double`, the new DSP core is `float` — that needs a **stated,
  tested tolerance**, not an assumed "close enough."
- **TX/RX are separate legacy code paths — read the matching one.** Legacy splits TX (`Main.cpp`'s
  `TMmsstv::Line*` — `LineMRT`, `LineSCT`, `LineR36`, etc.) from RX (a separate per-pixel decode switch)
  into different functions; never infer one from the other. **Real incident:** an earlier Scottie entry
  inferred TX channel order from RX branch widths — got duration right (matched `GetTiming`) but
  order/sync placement wrong (real order is separator-G-separator-B-sync-in-middle-separator-R, not
  sync-first R,G,B) — and its round-trip test passed anyway, since encoder and decoder agreed with each
  other while both were wrong about reality. **Duration match + round-trip pass are both necessary,
  neither sufficient** — always check the actual matching source function.
- **Concurrency/scheduler rule:** every cross-thread `IObservable`/event stream must state its scheduler
  and its slow-subscriber behavior (buffer/drop/block). Legacy's `PostMessage` never blocked the
  producer; a naive `Subject` replacement can — a real regression on hot paths like radio polling.
- **No Win32/COM outside an explicit optional module:** no `System.Drawing`, P/Invoke, or COM interop in
  `Yoniq.Core.*`/`Yoniq.UI`. A Windows-only integration gets its own optional project, never a hard
  dependency of cross-platform code.

---

## 5. License

**LGPL-3.0-or-later** (see [LICENSES.md](LICENSES.md), [COPYING](COPYING),
[COPYING.LESSER](COPYING.LESSER)) — matches upstream, since specs port legacy algorithms/constants, not
clean-room.
**License-audit rule:** any ported/bundled legacy asset (source, data table, bitmap, `.mtm`, prefix
table, dataset) needs a new `LICENSES.md` entry (what, source, license). When in doubt, **exclude** and
find an unambiguously licensed alternative.

---

## 6. Build / test / verify commands

> Fill with the exact CLI invocations — Claude re-guesses these every session otherwise.

- Build: dotnet build src/Yoniq.Core.Sstv -c Debug
- Run tests: dotnet test tests/Yoniq.Core.Sstv.Tests -c Debug --filter "FullyQualifiedName~<TestName>"
- Run single test / filtered subset (substring match on fully-qualified name):  dotnet test tests/Yoniq.Core.Sstv.Tests -c Debug --filter "FullyQualifiedName~EncodeThenDecode_ViaWavFile_RoundTripsWithinTolerance"

---

## 7. Delegating audits to the Opus subagent

**Setup:** a project subagent (`.claude/agents/auditor.md`, `model: opus`, `effort: high`, read-only
tools) performs equivalence/port audits. Configure it before relying on these rules. Custom subagents
DO load this file, but they do **not** receive the main conversation's history — so any rule the
auditor must honor, especially the §1 scope/ADHD rule, has to be repeated in each payload.

**When to delegate:** non-trivial ports — DSP/codec math, TX/RX path fidelity, buffer/encoding logic,
concurrency. **Skip** for mechanical 1:1 translations (delegation has context overhead).

**How to hand off** — start directly with the XML, no prose:

```
<audit_task>
  <scope>Stay tightly scoped (user has ADHD): audit ONLY this sub-task.
         Note any off-scope finding in one line; do not chase it.</scope>

  <legacy_reference file="yoniq-old/YONIQ-main/<file>" lines="<start>-<end>">
    <!-- minimal verbatim legacy snippet only; state its encoding if text -->
  </legacy_reference>

  <ported_candidate file="<path>.cs" lines="<start>-<end>">
    <!-- minimal C# snippet only -->
  </ported_candidate>

  <checklist>
    - Functional equivalence vs the legacy reference (read the MATCHING TX or RX function)
    - Golden-vector parity within the stated tolerance (double legacy -> float port)
    - Edge cases: nulls, boundaries, empty/short buffers, error states, off-by-one
    - Encoding (CP932 where applicable), binary-as-bytes (no string for byte>=0x80/nulls)
    - Integer width/sign, casting, overflow/wraparound
    - Concurrency: scheduler + slow-subscriber behavior (buffer/drop/block)
  </checklist>
</audit_task>
```

- **Isolated context:** include only the minimal snippets needed; strip conversation history, duplicate
  logs, and already-fixed code.
- **File targeting:** name target paths and focus line numbers; never dump whole files.

---

## 8. Session continuity — PROJECT_BRIEF.md

- **Write** `PROJECT_BRIEF.md` when finishing a sub-task, switching subjects, or before a `/clear`: what
  was completed, key C++→C# quirks found, legacy issues, files modified, immediate next steps.
- **Read** it at the start of a new session / after `/clear` / when picking a subject back up — before
  acting. Don't duplicate what native auto-memory already captures.

### Milestone audit

At workstream boundaries (e.g. DSP engine → UI, CAT → codec modes) or before a release tag, run the
broad three-phase systemic audit — **not** the per-function `auditor` call. It verifies the composed
chain end-to-end against legacy golden vectors, which per-function checks can't catch (see the Scottie
incident, §4). Prompt and cost throttles live in `docs/audit-playbook.md`.
Trigger: *"run the milestone audit from the playbook."*

---

## 9. Maintaining this file

Keep CLAUDE.md to **non-negotiable operational rules only**; offload detailed architecture, type-mapping
tables, and legacy specs to `spec/`/`docs/` and reference them. If this file passes **~200 lines** or
turns verbose, proactively suggest a refinement pass.
