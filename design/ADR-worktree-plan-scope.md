# ADR: Worktree plan scope (monorepo / submodule comfort)

**Status:** accepted  
**Date:** 2026-07-22  
**Context:** `cdp_csx_run_plan` used bare `git worktree add HEAD` + promote only when primary porcelain was empty. Dogfood on dirty monorepos / submodules failed (wrong GitRoot, sandbox ≠ screen, promote refuse).

## Decision

Three agent-visible terms:

| Term | Meaning |
|------|---------|
| **GitRoot** | `git rev-parse --show-toplevel` from entry/session path (submodule → submodule, not parent) |
| **PlanScope** | Relative dir under GitRoot (default: `cdp_open` project). Empty = whole repo |
| **WorkRoot** | Temp worktree sandbox |

Flow:

1. Resolve GitRoot + PlanScope (session ProjectRoot / optional `workspace_path` + `scope`).
2. `worktree add -b … HEAD`.
3. Fail-fast if PlanScope populated on primary but empty in worktree (wrong root / unpopulated submodule).
4. **Overlay** primary dirty/untracked files under PlanScope into WorkRoot.
5. **BaseTree** = `git write-tree` after overlay (plan delta base — not raw HEAD WIP).
6. Run CSX with `Plan.Resolve` remapping.
7. **Promote** default **`overlap_safe`**: apply BaseTree→final patch; ignore dirty outside patch paths; refuse if primary diverged on overlapping paths after plan start. Escape **`strict_clean`**: refuse any primary dirty (CI).

## Consequences

- Agents need not guess the submodule git root after `cdp_open`.
- Promote no longer blocked by unrelated WIP.
- Raw `File.*` in CSX still bypasses remap — use `Fs.*` / `Plan.Resolve` (follow-up).
