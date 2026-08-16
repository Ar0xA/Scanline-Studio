#!/usr/bin/env python3
"""CI coverage gate (spec/18-path-to-1.0.md Medium item; spec/13-testing.md:25,56).

Parses every coverage.cobertura.xml under a results directory (produced by
`dotnet test ... --collect:"XPlat Code Coverage"`), takes the best (max) observed
per-package line-rate across all reports -- a package instrumented by more than one
test project (e.g. ScanlineStudio.Core.Sstv, incidentally touched by several) is
judged on its best-observed coverage, never its worst; this can only under-report a
project's true merged coverage, never mask a real violation -- and compares each
named threshold in coverage-thresholds.json against that value.

Fails (non-zero exit) if:
  - a thresholded package's measured coverage is below its threshold,
  - a thresholded package has NO coverage data in any report at all (missing data
    must fail, not silently pass; "excluded" packages are, by definition, exempt
    from this -- that's the whole point of excluding them from the coverage run),
  - a ScanlineStudio.Core.*/ScanlineStudio.Application source project on disk has
    no corresponding entry in either "thresholds" or "excluded" (guards against a
    future new project joining the codebase silently ungated).

Usage: check-coverage.py <results-directory> [--thresholds-file PATH] [--repo-root PATH]
"""

from __future__ import annotations

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def collect_max_line_rates(results_dir: Path) -> dict[str, float]:
    best: dict[str, float] = {}
    reports = sorted(results_dir.glob("**/coverage.cobertura.xml"))
    if not reports:
        print(f"WARNING: no coverage.cobertura.xml files found under {results_dir}", file=sys.stderr)
    for report in reports:
        tree = ET.parse(report)
        for package in tree.getroot().findall(".//package"):
            name = package.get("name")
            line_rate = float(package.get("line-rate", "0"))
            if name is None:
                continue
            if name not in best or line_rate > best[name]:
                best[name] = line_rate
    return best


def find_in_scope_projects(repo_root: Path) -> set[str]:
    src = repo_root / "src"
    projects: set[str] = set()
    for entry in src.iterdir():
        if not entry.is_dir():
            continue
        name = entry.name
        if name != "ScanlineStudio.Application" and not name.startswith("ScanlineStudio.Core."):
            continue
        # Code-review finding: a directory match alone isn't enough -- a stray/half-deleted
        # directory with no .csproj isn't a real project and shouldn't trip the "ungated
        # project" guard below.
        if any(entry.glob("*.csproj")):
            projects.add(name)
    return projects


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("results_dir", type=Path, help="Directory containing coverage.cobertura.xml files (searched recursively)")
    parser.add_argument("--thresholds-file", type=Path, default=None, help="Path to coverage-thresholds.json (default: <repo-root>/coverage-thresholds.json)")
    parser.add_argument("--repo-root", type=Path, default=None, help="Repo root, for locating src/ and the default thresholds file (default: this script's grandparent directory)")
    args = parser.parse_args()

    repo_root = args.repo_root or Path(__file__).resolve().parent.parent
    thresholds_file = args.thresholds_file or (repo_root / "coverage-thresholds.json")

    with thresholds_file.open() as f:
        config = json.load(f)
    thresholds: dict[str, int] = config["thresholds"]
    excluded: dict[str, str] = config.get("excluded", {})

    measured = collect_max_line_rates(args.results_dir)
    in_scope = find_in_scope_projects(repo_root)

    failures: list[str] = []

    # Every in-scope project must be accounted for, gated or explicitly excluded.
    unaccounted = in_scope - set(thresholds) - set(excluded)
    for name in sorted(unaccounted):
        failures.append(
            f"{name}: no coverage-thresholds.json entry (add a numeric threshold under "
            f'"thresholds", or an "excluded" entry with a stated reason)'
        )

    for name, threshold in sorted(thresholds.items()):
        if name not in measured:
            failures.append(f"{name}: no coverage data found in any report (missing data must fail, not pass)")
            continue
        actual_pct = measured[name] * 100
        if actual_pct < threshold:
            failures.append(f"{name}: {actual_pct:.1f}% is below the {threshold}% threshold")

    print("Coverage gate results")
    print("======================")
    for name, threshold in sorted(thresholds.items()):
        actual = measured.get(name)
        status = "OK" if actual is not None and actual * 100 >= threshold else "FAIL"
        actual_display = f"{actual * 100:.1f}%" if actual is not None else "MISSING"
        print(f"  [{status}] {name}: {actual_display} (threshold {threshold}%)")
    for name, reason in sorted(excluded.items()):
        print(f"  [SKIP] {name}: excluded -- {reason}")

    if failures:
        print("\nFAILED:")
        for failure in failures:
            print(f"  - {failure}")
        return 1

    print("\nAll gated projects meet their coverage threshold.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
