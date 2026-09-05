# Yoniq-principal handoff template

Delegation template for the `yoniq-principal` subagent (`~/.claude/agents/yoniq-principal.md`), referenced from
`CLAUDE.md` §7. `yoniq-principal` reviews an `yoniq-auditor` review — it never audits a plan/code cold. This
template's shape reflects that: it drops the legacy/candidate snippet pairs the yoniq-auditor template
carries (yoniq-principal reads the same files itself, but its job is verifying specific claims, not scanning
for issues) and adds the three fields a review-of-a-review actually needs.

## When to use this (not every round)

See `yoniq-principal.md`'s own frontmatter for the concrete trigger rule. Short version: two contradicting/
drifting yoniq-auditor rounds, a yoniq-auditor blocker with high cost-of-being-wrong, the last gate before a
CLAUDE.md §7 full-weight change ships, or a fix resting on an asserted legacy-behavior claim. Never a
standing habit, never for the lighter single-pass tier, never for nit-only findings. Cap: once per
artifact per phase — this model has real weekly usage limits, spend it deliberately.

## How to hand off

Start directly with the XML, no prose:

```
<principal_review_task>
  <scope>Stay tightly scoped (user has ADHD): review ONLY the yoniq-auditor's findings below against this
         artifact. Note any off-scope finding in one line under "Off-scope notes"; do not chase it. Do
         not re-audit the artifact from scratch — verify the yoniq-auditor's specific claims.</scope>

  <artifact type="plan | code" ref="<path or inline, whichever is shorter>">
    <!-- the plan document or the ported C# candidate the yoniq-auditor reviewed -->
  </artifact>

  <yoniq_auditor_findings>
    <!-- VERBATIM, unsummarized. This is the single field that keeps yoniq-principal from degenerating into
         a second cold audit -- if you don't have the yoniq-auditor's actual findings text, don't paraphrase
         it, go get it. -->
  </yoniq_auditor_findings>

  <claims_to_reverify>
    <!-- Numbered list of the specific yoniq-auditor claims that most need independent re-checking -- usually
         the blockers, and any claim resting on an assumed legacy-behavior fact. Cite the yoniq-auditor's own
         file:line for each. -->
    1. ...
    2. ...
  </claims_to_reverify>

  <do_not_reverify>
    <!-- Explicitly out of scope: findings already independently confirmed elsewhere, nits, or claims
         not worth this model's budget. Keeps the review bounded. -->
  </do_not_reverify>

  <decision_at_stake>
    <!-- One sentence: what happens if this holds up vs. doesn't -- e.g. "we build Step A as designed
         if this holds; if not, the plan needs another revision before any code is written." This is
         what keeps the review anchored to a real go/no-go, not abstract commentary. -->
  </decision_at_stake>
</principal_review_task>
```

- **Checklist:** don't restate it — `yoniq-principal.md`'s own system prompt already carries the full
  ground-truth/verification rules and is loaded automatically on every invocation.
- **Isolated context:** yoniq-principal starts fresh, same as the yoniq-auditor — no conversation history. Include
  only what's needed to verify the specific claims; strip anything already resolved.
- **File targeting:** name target paths and focus line numbers where possible, mirroring the yoniq-auditor
  template's own rule — yoniq-principal is read-only and token-expensive, don't make it hunt.
