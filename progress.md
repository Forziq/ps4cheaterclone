# Progress
<!-- Only the progress-writer subagent edits this file. Keep it under 60 lines. -->
Now: T0.2b Public repo prep (licence and README) (not started)
Next: T0.3 Process picker and section selection
Waiting on Egor: M0c untrack Claude setup files (now), M3 make the repo public (after T0.2b). Optional: M2b git email.
Blocked: none

## Tasks  ([ ] todo · [~] in progress · [x] done · [T] waiting for Egor's test)
[x] T0.1 Protocol research and scaffold
[x] T0.2 Protocol client (libdebug port)
[ ] T0.2b Public repo prep (licence and README)
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
- 2026-11-26 T0.2 done: PS4DebugClient (connect, version, process list/info, maps, chunked read, write, clean disconnect), 30 fake-server tests, `info` CLI command.
- 2026-11-26 T0.2 m2: process info (title ID), maps (skips end<=start), chunked 32 MB read with cancel, write; 30 tests pass.
- 2026-11-26 T0.2 m1: PS4DebugClient connects, gets version and process list, disconnects cleanly; 20 tests pass (10 new, fake TCP server).
- 2026-11-26 M3 done (GitHub remote). T0.2 started.
- 2026-11-26 T0.1 done: refs cloned, protocol.md written from source, scaffold + enableable debug log (--debug / PS4MEM_DEBUG, logs/), first commit.
- 2026-11-26 Project plan set up (CLAUDE.md, TASKS.md, agents, /progress, /next, manual.md).

## Decisions and gotchas (max 15 lines)
- Claude setup (.claude/, CLAUDE.md, PROMPT.md, TASKS.md, progress.md, manual.md, docs/research/) is git-ignored; GitHub gets project files only.
- PS4 IP is private: only in git-ignored .env.local (PS4_IP) and app settings; never in tracked files or README. Cleanup of code/tests/README is in T0.2b.
- Repo is PUBLIC. README by readme-writer, commit messages by git-committer (both Haiku, uk-writing). Claude's Co-Authored-By trailer is fine.
- Target .NET 10 LTS rather than 8 (.NET 8 support ended 10 Nov 2026).
- Payload .bin is git-ignored (third-party binary). ref/ (cloned reference repos) is git-ignored.
- Classic CMD_PROC_SCAN 0xBDAA0009 is broken in ps4debug-NG (re-reads each section's first page, compares N-1 bytes; protocol.md §10). PS4Cheater never used it: LocalScan over CMD_PROC_READ is the reference; ConsoleScan = Turbo/trio after auth.
- PS4Cheater has no default section-name list; the anon/exe/Game/dlmalloc/SmallBlockAllocator list comes from PROMPT §3.3 (Egor).
- Debug log (Core/Logging/DebugLog) is required in every new component; G0 needs logs attached (manual M7).
- `info` has not been run on the real console yet; Egor can try it any time the payload is loaded (read-only).
