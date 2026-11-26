# Progress
<!-- Only the progress-writer subagent edits this file. Keep it under 60 lines. -->
Now: T0.1 Protocol research and scaffold (not started)
Next: T0.2 Protocol client (libdebug port)
Waiting on Egor: M0 copy claude-setup/ into .claude/, M1 git/gh check (.NET 10.0.404 and Node done), M2 Claude Code workflow (manual.md). M3 GitHub remote after T0.1.
Blocked: none

## Tasks  ([ ] todo · [~] in progress · [x] done · [T] waiting for Egor's test)
[ ] T0.1 Protocol research and scaffold
[ ] T0.2 Protocol client (libdebug port)
[ ] T0.3 Process picker and section selection
[ ] T0.4 First/next scan and write (4-byte)
[ ] T0.5 USER TEST G0: Phase 0 on console
[ ] T1.1 API skeleton, settings, connection
[ ] T1.2 Payload injection
[ ] T1.3 Value types and compare types
[ ] T1.4 Result store, unknown initial, undo
[ ] T1.5 Scan session API
[ ] T1.6 Cheat list, locks, title-ID storage
[ ] T1.7 USER TEST G1: API on console
[ ] T2.1 Web scaffold, run script, connection bar
[ ] T2.2 Process picker and sections panel
[ ] T2.3 Scanner and results table
[ ] T2.4 Cheat list UI
[ ] T2.5 Single-process build and README
[ ] T2.6 USER TEST G2: full app (MVP done)
[ ] T3.1 GoldHEN cheat JSON export
[ ] T3.2 Hex viewer/editor and disassembly
[ ] T3.3 Find what writes (watchpoint, port 755)
[ ] T3.4 Pointer scanner

## Log (newest first, max 12 lines)
- 2026-11-26 Project plan set up (CLAUDE.md, TASKS.md, agents, /progress, /next, manual.md).

## Decisions and gotchas (max 15 lines)
- Target .NET 10 LTS rather than 8 (.NET 8 support ended 10 Nov 2026).
- Payload .bin is git-ignored (third-party binary). ref/ (cloned reference repos) is git-ignored.
