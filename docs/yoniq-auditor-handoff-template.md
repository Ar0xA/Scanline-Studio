# Yoniq-auditor handoff template

Per-function/port audit delegation template, referenced from `CLAUDE.md` §7. Split out to keep
`CLAUDE.md` under its own line-count target — the content is unchanged, only the location moved.

For the broader three-phase milestone audit (workstream boundaries, release tags), use
[docs/audit-playbook.md](audit-playbook.md) instead — this template is for single-function/
single-port delegation only.

## How to hand off

Start directly with the XML, no prose:

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
</audit_task>
```

- **Checklist:** don't restate it — `~/.claude/agents/yoniq-auditor.md`'s own system prompt already carries
  the full checklist (functional equivalence, golden-vector parity, encoding, binary-as-bytes,
  numeric fidelity, edge cases, concurrency, boundary hygiene) and is loaded automatically on every
  invocation. A second copy here already drifted from it once; one source of truth now.
- **Isolated context:** include only the minimal snippets needed; strip conversation history, duplicate
  logs, and already-fixed code.
- **File targeting:** name target paths and focus line numbers; never dump whole files.
