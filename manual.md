# Manual steps (things only you can do)

Claude adds and updates steps here as the project goes, and refers to them by ID (M1, G0 ...). Commands are run in the project folder unless a step says otherwise.

---

## One-time setup

### M0 Install the Claude Code config (do this first)
The subagents, `/progress`, `/next` and the permission settings are in `claude-setup/`. Move them into `.claude/`:
```
mkdir -p .claude && cp -R claude-setup/. .claude/ && rm -rf claude-setup
```
Check: `ls .claude/agents .claude/commands` → `git-committer.md progress-writer.md researcher.md` and `next.md progress.md`.

### M1 Install the tools
1. **.NET 10 SDK, macOS Arm64.** Get the installer from https://dotnet.microsoft.com/download/dotnet/10.0 (the project targets .NET 10 LTS, because .NET 8 support ended on 10 Nov 2026).
   Check: `dotnet --list-sdks` → shows a `10.0.x` line.
2. **Node.js 22 LTS** (20.9+ also works). Check: `node -v`.
3. **Git and GitHub CLI.** Check: `git --version`. For `gh`, run `brew install gh`, then `gh auth login`.
4. **Netcat** is built into macOS. Check: `which nc`.

### M2 How to drive Claude Code on this project
1. `cd` into the project folder and run `claude`.
2. Type `/next`. Claude reads `progress.md`, opens only the current task and starts or resumes it.
3. When Claude says a task is done: type `/clear`, then `/next`.
4. Type `/progress` at any time for a short status summary: current task, previous tasks, next task, anything waiting on you.
5. Subagents run on Haiku automatically (research, `progress.md` updates, git). You don't need to do anything for that.

### M3 GitHub remote (do this once T0.1 has made the first commit)
1. `gh repo create ps4-mem --private --source=. --remote=origin --push`
   Expected: the repo is created and `main` is pushed. Keep it **private**.
2. After that, the git-committer subagent pushes on every milestone. Until you do this step, it commits locally and reports "push skipped".
3. Note: `payloads/*.bin` is git-ignored, so a fresh clone needs the payload copied back into `payloads/`.

---

## Every console session

### M4 Get the PS4 ready
1. PS4 on, GoldHEN loaded (firmware 13.52).
2. BinLoader enabled in the GoldHEN settings. Remember it accepts **one payload per session**: toggle it off and on, or restart the PS4, to send another.
3. Game running, past the menus and into your save.
4. PS4Cheater closed on the other PC (only one debugger at a time).
5. On the Mac: `ping -c 2 192.168.0.99` → replies.

### M5 Load the payload by hand (Phase 0 only; from Phase 1 the app does this)
1. Check whether it's already running: `nc -z -G 2 192.168.0.99 744 && echo "ps4debug already running, don't inject again"`
2. If it isn't running: `nc -w 3 192.168.0.99 9090 < payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin`
3. Wait for the ps4debug notification on the PS4, then run step 1 again. It should now say it's running.
   - "Connection refused" on 9090 means BinLoader is off or has already been used this session (see M4 step 2).

### M7 Debug log (use it for every test)
1. Add `--debug` to any CLI command, e.g. `dotnet run --project src/PS4Mem.Cli -- info --ip 192.168.0.99 --debug`.
   Or turn it on for the whole terminal session: `export PS4MEM_DEBUG=1` (turn off: `unset PS4MEM_DEBUG`).
2. The first line printed is `Debug log: <path>`. Logs are in `logs/` in the project folder, one file per run, named `ps4mem-YYYYMMDD-HHMMSS.log`.
3. The log has the settings used, every command sent to the PS4 and its reply (hex, first 256 bytes of each), skipped maps, per-section scan counts, and every write.
4. When reporting a test, attach the log files from that session, or paste `ls -t logs | head` and then the newest one: `cat "logs/$(ls -t logs | head -1)"`.
5. `logs/` is git-ignored. Delete old logs whenever you like: `rm -rf logs`.

### M6 macOS firewall for port 755 (needed from T3.3 only)
Claude will fill this in during T3.3.

---

## Test gates
Claude updates each gate with the exact commands at the end of the task before it. Paste the full output back to Claude when you're done.

### G0 Phase 0: scan and write on the real console (after T0.4)
1. M4, then M5. Then `export PS4MEM_DEBUG=1` so every command below writes a debug log (M7).
2. `dotnet run --project src/PS4Mem.Cli -- info --ip 192.168.0.99` → ps4debug version and process list.
3. `dotnet run --project src/PS4Mem.Cli -- sections --ip 192.168.0.99` → the game is auto-picked; note the selected/total section count.
4. `dotnet run --project src/PS4Mem.Cli -- scan --ip 192.168.0.99` → enter your current money.
   - Note the total hits and the per-section counts. **Expected: thousands**, like PS4Cheater.
   - Do the scan with both the console method and the local method, and note both counts and times.
5. Change your money in the game, then enter the new value. Repeat until only a few results are left.
6. `w <n> <value>` on a remaining result → check the money changed in the game.
7. Paste all the output to Claude, attach the `logs/` files from this session (M7 step 4), and say whether it worked.

### G1 Phase 1: API on the real console (after T1.6)
Claude fills this in.

### G2 Phase 2: full app (after T2.5)
Claude fills this in.

### G3.x Phase 3 features
Claude adds one gate per feature.
