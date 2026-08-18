# Project Overview

## Vision

YONIQ v2 is a modern, cross-platform amateur radio application focused on stability, maintainability, extensibility and hardware interoperability.

The application shall preserve all current functionality while introducing a clean architecture suitable for long-term development.

---

## Design Goals

- Cross-platform (.NET LTS)
- Modern UI
- Runtime language switching
- CAT abstraction
- Native rigctld support
- Plugin architecture
- Dependency Injection
- Comprehensive automated testing

---

## Principles

- Preserve existing functionality
- Prefer refactoring over rewriting
- Minimize breaking changes
- Separate UI from business logic
- Separate radio protocol implementations
- All I/O asynchronous
- Configuration stored in JSON
- No hardcoded user-visible strings

---

## Non Goals

- Rewriting the application solely for aesthetic reasons
- Removing existing radio support without a documented, scoped replacement (see [docs/removed-features.md](../docs/removed-features.md))
- Vendor lock-in
- Telemetry
- Cloud dependencies

---

## License

Scanline Studio is **LGPL-3.0-or-later**, matching upstream YONIQ/MMSSTV's own stated license (`yoniq-old/YONIQ-main/README.md`: *"MMSSTV LGPL source repository"*, and per-file LGPL-3-or-later headers throughout the legacy source). See [LICENSES.md](../LICENSES.md) for the full reasoning, including why the legacy `Terms.txt` freeware/no-charge clause is treated as superseded rather than controlling, and which legacy assets (ARRL.DX, MMCG.DEF, Chilkat, FastReport) are explicitly excluded from the port.

---

## Success Criteria

- Windows, Linux and macOS builds
- Native CAT support
- Native rigctld support
- Runtime language switching
- \>80% unit test coverage
- Plugin API
- GitHub Actions CI

---

## Related documents

The remaining detail lives in `spec/01` through `spec/19`, starting with [[01-architecture]]. [[14-roadmap]] sequences delivery across all of them. [docs/removed-features.md](../docs/removed-features.md) tracks every legacy capability that is dropped or only partially replaced, per the removal rule in `CLAUDE.md`.
