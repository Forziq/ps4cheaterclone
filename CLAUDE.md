# PS4 Mem: working rules for Claude Code

- **Spec:** `PROMPT.md`. Read only the sections the current task lists.
- **Plan:** `TASKS.md`. One block per task (`### T0.1 ...`).
- **Status:** `progress.md`. It's kept short on purpose, so read it first.
- **User's hands-on steps:** `manual.md`. Steps are numbered M1, M2 ... and test gates G0, G1 ...

> Target framework: **.NET 10 (LTS)**, not .NET 8. .NET 8 reached end of support on 10 Nov 2026. The code is the same; it's one `<TargetFramework>` line if Egor ever wants 8.

## 1. Start of every session (or after `/clear`)
1. Read `progress.md`.
2. Find the current task: the `Now:` line, or the `Next:` line if `Now` is done. Read **only that task's block** in `TASKS.md`: Grep for `^### T1.2`, then Read with offset/limit. Don't read the whole file.
3. Read the `PROMPT.md` sections and `docs/` files the block lists. Nothing else unless you need it.
4. If the task is a **USER TEST gate**, don't write new features. Point Egor to the `manual.md` gate, wait for his results, then fix what they show.

## 2. Delegation (required, to save usage)

| Work | Who does it |
|---|---|
| Web research, GitHub repos/READMEs, reading reference source in `ref/` (libdebug, PS4Cheater, PS4CheaterNeo, ps4debug-NG, GoldHEN), **any "where is X in this codebase?" search** | `researcher` subagent (Haiku) |
| **Any** change to `progress.md` | `progress-writer` subagent (Haiku) |
| `git init`, `git add`, `git commit`, `git push` | `git-committer` subagent (Haiku) |

You, the main agent, write the code, tests, `docs/protocol.md`, `manual.md` and the README. You never edit `progress.md` or run git add/commit/push yourself. Run independent subagent calls in parallel, for example progress-writer and git-committer at a milestone.

How to brief them (keep briefs short and concrete):
- **researcher:** "In ref/PS4CheaterNeo, find how the client sends CMD_PROC_SCAN and reads the results. Return the verbatim code with file:line and the enum values. Save notes to docs/research/console-scan.md."
- **progress-writer:** "T0.2 milestone m1 done: connect, version and process list work, 6 tests pass. T0.2 stays in progress."
- **git-committer:** "Commit: `T0.2 m1: connect, version, process list`. Push."

## 3. Protocol rule (lesson from the PyPS4debug failure)
Never guess packet layouts, command IDs, enum values, status codes or byte order. Every protocol detail must come from libdebug or ps4debug-NG source, fetched by `researcher` as **verbatim excerpts with file:line**, and be written into `docs/protocol.md` with its source. If an excerpt is ambiguous, send the researcher back for the surrounding code; don't fill gaps yourself.

## 4. Milestones and end of task
- Every task lists milestones (m1, m2 ...). When one is reached **and it builds and its tests pass**, call progress-writer (1–2 line note) and git-committer (commit and push) in parallel.
- When the task's **Done when** is met: progress-writer marks it done and sets Now/Next, git-committer commits and pushes. Then **stop** and tell Egor:
  `T0.2 done. Run /clear, then /next to start T0.3.`
  If the next task is a test gate, say which `manual.md` gate to follow instead.
- Never start the next task in the same session. Never go past a USER TEST gate without Egor's results.
- Blocked? Have progress-writer record it under `Blocked:`, then ask Egor.
- If the context is getting long mid-task, finish the current milestone, record it, and tell Egor to `/clear` then `/next`. `/next` resumes from the last milestone.

## 5. manual.md
Whenever Egor has to do something by hand (install something, a PS4 action, a macOS setting, GitHub, a console test), add or update a step in `manual.md` in the existing format: an ID, numbered steps, copy-paste commands, expected result. Refer to it by ID in your message ("see M5"). Update the gate block at the end of the task that comes before it, so the commands match the real code.

## 6. Code rules (from the spec)
- Layout: `src/PS4Mem.Core` (protocol, scanning, cheats), `src/PS4Mem.Cli`, `src/PS4Mem.Api`, `tests/PS4Mem.Core.Tests` (xUnit), `web/` (Next.js), `docs/`, and `ref/` for cloned reference repos (git-ignored).
- Simple, readable code. Defaults (IP 192.168.0.99, ports 744/755/9090, payload path, section name filter) live in one place and can all be overridden.
- Never hard-code process names. Skip maps with `end <= start`; don't crash.
- No short timeouts on scans. Show progress, support cancel, and **log result counts per section**.
- **Never write to PS4 memory unless Egor asked for it** (a CLI command or an API call from a click). Locks only re-write while the lock is on.
- Only one debugger at a time. Disconnect releases everything: locks, debugger, scans.
- Test protocol code against a fake TCP server in tests. Don't touch the real console yourself: no payload injection, no writes. Read-only commands (version, process list) only if Egor says the PS4 is ready.
- Run `dotnet build` and `dotnet test` (and `npm run build` for web) before every milestone commit.
