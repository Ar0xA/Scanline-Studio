#!/usr/bin/env python3
"""Local-model peer auditor: functional/port-equivalence first pass using a local
Ollama model, complementary to (never a replacement for) the project's real
`auditor` subagent. See README.md for usage and the negative-control validation
gate that must pass before trusting this tool's output regularly.

The model gets real, read-only, scoped file access (read_file/grep over the
legacy and candidate trees only) via a manually-driven tool loop -- Ollama's
native structured tool_calls field does not reliably populate for this model
(verified empirically: it consistently emits the right JSON but skips the
required <tool_call> wrapper tags), so tool calls are detected by parsing the
model's plain-text JSON reply instead of relying on message.tool_calls.

Standard library only -- no venv/pip install required.
"""
import argparse
import hashlib
import json
import re
import subprocess
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parent
REPO_ROOT = TOOL_DIR.parent.parent
LEGACY_ROOT = REPO_ROOT / "yoniq-old" / "YONIQ-main"
CANDIDATE_ROOTS = [REPO_ROOT / "src", REPO_ROOT / "tests"]
PROMPT_FILE = TOOL_DIR / "auditor_prompt.md"
OUT_DIR = TOOL_DIR / "out"

OLLAMA_ENDPOINT = "http://localhost:11434"
DEFAULT_MODEL = "qwen2.5-coder:14b"
NUM_CTX = 8192
CHARS_PER_TOKEN_ESTIMATE = 3.5  # conservative for dense code text
MAX_TOOL_ROUNDS = 5
MAX_READ_LINES = 150
MAX_GREP_RESULTS = 25

DIRECTIVE_RE = re.compile(r"^\s*#\s*(if|ifdef|ifndef|elif|else|endif)\b\s*(.*)$")
DEFINE_RE = re.compile(r"^\s*#\s*define\s+(\w+)\s+(.*)$")
SIGNATURE_HINT_RE = re.compile(r"::\s*\w+\s*\(")


class PeerAuditError(RuntimeError):
    """Fatal, user-facing error -- always exit non-zero, never print a fake verdict."""


def parse_line_range(spec: str) -> tuple[int, int]:
    m = re.fullmatch(r"(\d+)-(\d+)", spec.strip())
    if not m:
        raise PeerAuditError(f"Invalid line range '{spec}' -- expected START-END, e.g. 40-74")
    start, end = int(m.group(1)), int(m.group(2))
    if start < 1 or end < start:
        raise PeerAuditError(f"Invalid line range '{spec}' -- START must be >=1 and END >= START")
    return start, end


def resolve_and_contain(path_str: str, allowed_roots: list[Path], label: str) -> Path | None:
    """Returns None (not raises) on containment failure when called from a tool
    handler, so the model gets a normal error result instead of the process dying.

    Relative paths are tried against each allowed root in turn (not the repo
    root), since callers pass paths relative to the specific legacy/candidate
    tree, e.g. --legacy-file fir.cpp means yoniq-old/YONIQ-main/fir.cpp."""
    if Path(path_str).is_absolute():
        try:
            p = Path(path_str).resolve()
        except Exception:
            return None
        if p.is_file() and any(p.is_relative_to(root.resolve()) for root in allowed_roots):
            return p
        return None

    for root in allowed_roots:
        try:
            candidate = (root / path_str).resolve()
        except Exception:
            continue
        if candidate.is_file() and candidate.is_relative_to(root.resolve()):
            return candidate
    return None


def require_contained(path_str: str, allowed_roots: list[Path], label: str) -> Path:
    p = resolve_and_contain(path_str, allowed_roots, label)
    if p is None:
        allowed = " or ".join(str(r) for r in allowed_roots)
        raise PeerAuditError(f"{label} file not found or not under an allowed root ({allowed}): {path_str}")
    return p


def read_legacy_lines(path: Path) -> list[str]:
    raw = path.read_bytes()
    try:
        text = raw.decode("cp932")
    except UnicodeDecodeError:
        text = raw.decode("utf-8", errors="replace")
    return text.splitlines()


def read_candidate_lines(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8").splitlines()


def slice_lines(lines: list[str], start: int, end: int, label: str) -> list[str]:
    if end > len(lines):
        raise PeerAuditError(f"{label} line range {start}-{end} exceeds file length ({len(lines)} lines)")
    return lines[start - 1 : end]


def brace_balance_warning(snippet_lines: list[str]) -> str | None:
    text = "\n".join(snippet_lines)
    opens, closes = text.count("{"), text.count("}")
    if opens != closes:
        return f"WARNING: slice has unbalanced braces ({opens} open, {closes} close) -- likely a partial fragment, not a complete function body."
    return None


def find_enclosing_signature(all_lines: list[str], start: int, window: int = 100) -> str:
    lo = max(1, start - window)
    for i in range(start - 1, lo - 2, -1):
        if i < 0 or i >= len(all_lines):
            continue
        line = all_lines[i]
        if SIGNATURE_HINT_RE.search(line) or (
            re.match(r"^\s*(void|int|double|float|bool|char|BYTE|WORD|DWORD|AnsiString|__fastcall)\b", line)
            and "(" in line
            and not line.strip().endswith(";")
        ):
            return f"line {i + 1}: {line.strip()}"
    return f"not determined (no obvious enclosing signature found within {window} lines back -- textual heuristic only)"


def scan_preprocessor(all_lines: list[str], start: int, end: int) -> str:
    stack: list[dict] = []
    defines: dict[str, str] = {}
    frames_touching_range: list[dict] = []
    seen_frame_ids: set[int] = set()

    for i, raw in enumerate(all_lines, start=1):
        dm = DEFINE_RE.match(raw)
        if dm:
            defines[dm.group(1)] = dm.group(2).strip()

        m = DIRECTIVE_RE.match(raw)
        if m:
            kind, rest = m.group(1), m.group(2).strip()
            if kind in ("if", "ifdef", "ifndef"):
                stack.append({"kind": kind, "cond": rest, "line": i, "branch": "if"})
            elif kind == "elif":
                if stack:
                    stack[-1] = {**stack[-1], "branch": "elif", "cond": rest, "line": i}
            elif kind == "else":
                if stack:
                    stack[-1] = {**stack[-1], "branch": "else", "line": i}
            elif kind == "endif":
                if stack:
                    stack.pop()

        if start <= i <= end:
            for frame in stack:
                fid = id(frame)
                if fid not in seen_frame_ids:
                    seen_frame_ids.add(fid)
                    frames_touching_range.append(dict(frame))

    if not frames_touching_range:
        return "PREPROCESSOR CONTEXT (deterministic scan): none -- this slice is not nested inside any #if/#ifdef/#ifndef block in this file."

    lines_out = ["PREPROCESSOR CONTEXT (deterministic scan, not model-inferred):"]
    for frame in frames_touching_range:
        cond = frame["cond"].strip()
        branch = frame["branch"]
        directive_line = frame["line"]
        if branch == "if" and cond in ("0",):
            status = "DISABLED -- condition is literal 0, this branch is compiled out"
        elif branch == "else" and cond in ("0",):
            status = "note: this else-branch pairs with a preceding #if 0 further up -- likely the LIVE branch"
        elif branch in ("if", "elif") and cond in ("1",):
            status = "ACTIVE -- condition is literal 1"
        else:
            resolved_bits = []
            for token in re.findall(r"[A-Za-z_][A-Za-z0-9_]*", cond):
                if token in defines:
                    resolved_bits.append(f"{token} = {defines[token]} (from #define in this file)")
            if resolved_bits:
                status = "condition depends on macro(s): " + "; ".join(resolved_bits) + " -- verify this resolution, not exhaustively checked"
            else:
                status = "condition depends on macro(s) not #define'd in this file -- UNRESOLVED here; use grep if you need to resolve it"
        lines_out.append(f"- line {directive_line}: #{frame['kind']} {cond!r} [{branch}-branch] -- {status}")
    return "\n".join(lines_out)


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


def git_provenance() -> str:
    try:
        sha = subprocess.run(
            ["git", "-C", str(REPO_ROOT), "rev-parse", "HEAD"],
            capture_output=True, text=True, check=True, timeout=10,
        ).stdout.strip()
        dirty = subprocess.run(
            ["git", "-C", str(REPO_ROOT), "status", "--porcelain"],
            capture_output=True, text=True, check=True, timeout=10,
        ).stdout.strip()
        return f"{sha}{' (dirty working tree)' if dirty else ''}"
    except Exception as e:  # noqa: BLE001 -- provenance is best-effort, never fatal
        return f"unknown (git error: {e})"


def ollama_post(path: str, payload: dict, timeout: int = 600) -> dict:
    req = urllib.request.Request(
        f"{OLLAMA_ENDPOINT}{path}",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.URLError as e:
        raise PeerAuditError(
            f"Could not reach Ollama at {OLLAMA_ENDPOINT}{path} -- is `ollama serve` running? ({e})"
        ) from e


def get_model_digest(model: str) -> str:
    try:
        info = ollama_post("/api/show", {"name": model}, timeout=30)
        return info.get("digest") or info.get("details", {}).get("digest", "unknown")
    except PeerAuditError:
        return "unknown (could not query /api/show)"


# ---------------- Tools exposed to the model ---------------- #

def tool_read_file(args: dict) -> str:
    path_str = args.get("path", "")
    start = args.get("start_line")
    end = args.get("end_line")
    if not isinstance(start, int) or not isinstance(end, int) or start < 1 or end < start:
        return f"ERROR: invalid start_line/end_line ({start}, {end})"
    if end - start + 1 > MAX_READ_LINES:
        end = start + MAX_READ_LINES - 1

    p = resolve_and_contain(path_str, [LEGACY_ROOT] + CANDIDATE_ROOTS, "read_file")
    if p is None:
        return f"ERROR: '{path_str}' not found or outside the allowed legacy/candidate trees"

    try:
        lines = read_legacy_lines(p) if p.is_relative_to(LEGACY_ROOT) else read_candidate_lines(p)
    except Exception as e:  # noqa: BLE001
        return f"ERROR reading {path_str}: {e}"

    if end > len(lines):
        end = len(lines)
    if start > len(lines):
        return f"ERROR: start_line {start} exceeds file length ({len(lines)} lines)"

    body = "\n".join(f"{i}: {lines[i - 1]}" for i in range(start, end + 1))
    return f"{path_str}:{start}-{end}\n{body}"


def tool_grep(args: dict) -> str:
    pattern = args.get("pattern", "")
    root_name = args.get("root", "legacy")
    if not pattern:
        return "ERROR: empty pattern"
    try:
        rx = re.compile(pattern)
    except re.error as e:
        return f"ERROR: invalid regex: {e}"

    roots = [LEGACY_ROOT] if root_name == "legacy" else CANDIDATE_ROOTS if root_name == "candidate" else None
    if roots is None:
        return "ERROR: root must be 'legacy' or 'candidate'"

    results = []
    for root in roots:
        if not root.is_dir():
            continue
        for fp in sorted(root.rglob("*")):
            if not fp.is_file() or fp.suffix.lower() not in (".cpp", ".h", ".hpp", ".cs", ".cxx", ".cc"):
                continue
            try:
                lines = read_legacy_lines(fp) if root == LEGACY_ROOT else read_candidate_lines(fp)
            except Exception:  # noqa: BLE001
                continue
            for i, line in enumerate(lines, start=1):
                if rx.search(line):
                    rel = fp.relative_to(root)
                    results.append(f"{rel}:{i}: {line.strip()}")
                    if len(results) >= MAX_GREP_RESULTS:
                        results.append(f"... (truncated at {MAX_GREP_RESULTS} results)")
                        return "\n".join(results)
    return "\n".join(results) if results else "(no matches)"


TOOLS = {"read_file": tool_read_file, "grep": tool_grep}


def try_parse_tool_call(content: str) -> dict | None:
    text = content.strip()
    text = re.sub(r"^```(?:json)?\s*|\s*```$", "", text.strip())
    try:
        obj = json.loads(text)
    except json.JSONDecodeError:
        return None
    if isinstance(obj, dict) and obj.get("tool") in TOOLS:
        return obj
    return None


def estimate_tokens(messages: list[dict]) -> int:
    total_chars = sum(len(m.get("content", "")) for m in messages)
    return int(total_chars / CHARS_PER_TOKEN_ESTIMATE)


def run_agentic_loop(model: str, system_prompt: str, task_prompt: str) -> tuple[str, list[str], str]:
    """Returns (final_text, tool_call_log, done_reason)."""
    messages = [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": task_prompt},
    ]
    tool_call_log: list[str] = []

    for round_num in range(1, MAX_TOOL_ROUNDS + 1):
        est = estimate_tokens(messages)
        if est > NUM_CTX * 0.9:
            tool_call_log.append(f"[round {round_num}] ABORTED: context budget exceeded (~{est} est. tokens)")
            return (
                "VERDICT (LOCAL PEER-AUDIT -- INCOMPLETE): could not complete -- context budget exhausted mid-loop.",
                tool_call_log,
                "context_exhausted",
            )

        response = ollama_post(
            "/api/chat",
            {
                "model": model,
                "messages": messages,
                "options": {"temperature": 0, "seed": 42, "top_p": 1.0, "num_ctx": NUM_CTX},
                "stream": False,
            },
        )
        content = (response.get("message", {}).get("content") or "").strip()
        if not content:
            tool_call_log.append(f"[round {round_num}] ABORTED: empty model response")
            return ("VERDICT (LOCAL PEER-AUDIT -- INCOMPLETE): model returned an empty response.", tool_call_log, "empty_response")

        call = try_parse_tool_call(content)
        if call is None:
            return (content, tool_call_log, response.get("done_reason", "unknown"))

        tool_name = call["tool"]
        handler = TOOLS[tool_name]
        result = handler(call)
        tool_call_log.append(f"[round {round_num}] {tool_name}({ {k: v for k, v in call.items() if k != 'tool'} }) -> {len(result)} chars")

        messages.append({"role": "assistant", "content": content})
        messages.append({"role": "user", "content": f"<tool_response>\n{result}\n</tool_response>"})

    tool_call_log.append(f"ABORTED: exceeded MAX_TOOL_ROUNDS={MAX_TOOL_ROUNDS} without a final answer")
    return (
        f"VERDICT (LOCAL PEER-AUDIT -- INCOMPLETE): exceeded {MAX_TOOL_ROUNDS} tool-call rounds without a final answer.",
        tool_call_log,
        "max_rounds_exceeded",
    )


def build_task_prompt(legacy_ref: str, legacy_preprocessor: str, legacy_snippet: str,
                       legacy_brace_warning: str | None, legacy_enclosing: str,
                       candidate_ref: str, candidate_snippet: str,
                       candidate_brace_warning: str | None, note: str | None) -> str:
    legacy_block = [
        f"<legacy_reference file=\"{legacy_ref}\">",
        f"Enclosing function (best-effort): {legacy_enclosing}",
        legacy_preprocessor,
    ]
    if legacy_brace_warning:
        legacy_block.append(legacy_brace_warning)
    legacy_block.append(legacy_snippet)
    legacy_block.append("</legacy_reference>")

    candidate_block = [f"<ported_candidate file=\"{candidate_ref}\">"]
    if candidate_brace_warning:
        candidate_block.append(candidate_brace_warning)
    candidate_block.append(candidate_snippet)
    candidate_block.append("</ported_candidate>")

    parts = ["<audit_task>", "\n".join(legacy_block), "", "\n".join(candidate_block)]
    if note:
        parts.append(f"\n<note>{note}</note>")
    parts.append(f"\n(You have up to {MAX_TOOL_ROUNDS} tool-call rounds available.)")
    parts.append("</audit_task>")
    return "\n".join(parts)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--legacy-file", required=True, help="Path under yoniq-old/YONIQ-main/")
    ap.add_argument("--legacy-lines", required=True, help="START-END, 1-based inclusive")
    ap.add_argument("--candidate-file", required=True, help="Path under src/ or tests/")
    ap.add_argument("--candidate-lines", required=True, help="START-END, 1-based inclusive")
    ap.add_argument("--note", default=None, help="Extra grounding context (e.g. a constructor line elsewhere)")
    ap.add_argument("--model", default=DEFAULT_MODEL)
    ap.add_argument("--out", default=None, help="Output file path (default: tools/peer-audit/out/<timestamp>.md)")
    args = ap.parse_args()

    try:
        legacy_start, legacy_end = parse_line_range(args.legacy_lines)
        candidate_start, candidate_end = parse_line_range(args.candidate_lines)

        legacy_path = require_contained(args.legacy_file, [LEGACY_ROOT], "Legacy")
        candidate_path = require_contained(args.candidate_file, CANDIDATE_ROOTS, "Candidate")

        legacy_all_lines = read_legacy_lines(legacy_path)
        candidate_all_lines = read_candidate_lines(candidate_path)

        legacy_slice = slice_lines(legacy_all_lines, legacy_start, legacy_end, "Legacy")
        candidate_slice = slice_lines(candidate_all_lines, candidate_start, candidate_end, "Candidate")

        legacy_ref = f"yoniq-old/YONIQ-main/{legacy_path.relative_to(LEGACY_ROOT)}:{legacy_start}-{legacy_end}"
        candidate_root = next(r for r in CANDIDATE_ROOTS if candidate_path.is_relative_to(r.resolve()))
        candidate_ref = f"{candidate_root.name}/{candidate_path.relative_to(candidate_root)}:{candidate_start}-{candidate_end}"

        preprocessor_ctx = scan_preprocessor(legacy_all_lines, legacy_start, legacy_end)
        legacy_enclosing = find_enclosing_signature(legacy_all_lines, legacy_start)
        legacy_brace_warn = brace_balance_warning(legacy_slice)
        candidate_brace_warn = brace_balance_warning(candidate_slice)

        if not PROMPT_FILE.is_file():
            raise PeerAuditError(f"Vendored prompt missing: {PROMPT_FILE}")
        system_prompt = PROMPT_FILE.read_text(encoding="utf-8").replace("[MODEL]", args.model)

        task_prompt = build_task_prompt(
            legacy_ref, preprocessor_ctx, "\n".join(legacy_slice), legacy_brace_warn, legacy_enclosing,
            candidate_ref, "\n".join(candidate_slice), candidate_brace_warn, args.note,
        )

        model_digest = get_model_digest(args.model)

        final_text, tool_call_log, done_reason = run_agentic_loop(args.model, system_prompt, task_prompt)

        if not final_text.startswith("VERDICT (LOCAL PEER-AUDIT"):
            final_text = f"WARNING: model did not use the required watermarked verdict format.\n\n{final_text}"

        truncation_flag = "  *** done_reason=length -- output may have been CUT OFF ***" if done_reason == "length" else ""

        timestamp = datetime.now(timezone.utc).isoformat()
        tool_log_block = "\n".join(f"  {line}" for line in tool_call_log) if tool_call_log else "  (none -- model answered from the given snippets alone)"
        header = f"""# Local peer-audit (UNVERIFIED -- local model, not the real auditor)

- Timestamp (UTC): {timestamp}
- Model: {args.model} (digest: {model_digest})
- Sampling: temperature=0, seed=42, top_p=1.0, num_ctx={NUM_CTX}
- Repo git SHA: {git_provenance()}
- done_reason={done_reason}{truncation_flag}
- Tool calls made:
{tool_log_block}

- Legacy: {legacy_ref}, content sha256: {sha256_text(chr(10).join(legacy_slice))[:16]}
  - Enclosing function (best-effort): {legacy_enclosing}
  - {preprocessor_ctx.splitlines()[0]}
- Candidate: {candidate_ref}, content sha256: {sha256_text(chr(10).join(candidate_slice))[:16]}

---

{final_text}
"""
        print(header)

        out_path = Path(args.out) if args.out else OUT_DIR / f"{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}.md"
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(header, encoding="utf-8")
        print(f"\n(written to {out_path})", file=sys.stderr)
        return 0 if done_reason not in ("max_rounds_exceeded", "context_exhausted", "empty_response") else 1

    except PeerAuditError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
