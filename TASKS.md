# Tasks

Each task is sized to fit one Claude Code session. Work milestone by milestone (m1, m2 ...); each milestone gets a progress note and a commit. `/clear` after every task. Gates marked **USER TEST** are Egor's: follow the matching `manual.md` gate and report back.

---

## Phase 0: .NET console app (prove scanning works)

### T0.1 Protocol research and scaffold
- **Read:** PROMPT §2, §3. Pass the repo links in §2 and §3.2 to the researcher.
- **Do:**
  - researcher: `git clone --depth 1` into `ref/`: Pharaoh2k/ps4debug-NG, and PS4Cheater (ctn123) and PS4CheaterNeo (avan06), whichever contain the libdebug C# source (find them on GitHub; don't invent URLs).
  - researcher, in batches by topic: verbatim excerpts with file:line for: packet header and magic, byte order, connect handshake, status codes; the commands for version, process list, process info (title ID), process maps, read, write, **console scan** (value-type and compare-type enums, how results stream back, end marker); ps4debug-NG **Turbo Scan** (how to detect it, how it differs); process stop/continue (for "pause game"); PS4Cheater's section filter (name list, prot flags) and read chunk sizes. Notes go in `docs/research/*.md`.
  - You: write `docs/protocol.md` from those excerpts, one section per command, each with its source.
  - Scaffold: `PS4Mem.sln`, `src/PS4Mem.Core` (classlib), `src/PS4Mem.Cli` (console), `tests/PS4Mem.Core.Tests` (xUnit), all `net10.0`. Write a README stub.
  - git-committer: `git init -b main` and the first commit (`.gitignore` already exists).
- **Milestones:** m1 reference repos cloned and research notes saved · m2 `docs/protocol.md` complete · m3 solution builds and the first commit is made.
- **Done when:** every command above is documented with a source, `dotnet build` passes, and the first commit exists. Remind Egor about M3 (GitHub remote).

### T0.2 Protocol client (libdebug port)
- **Read:** `docs/protocol.md`, PROMPT §3.1–3.2.
- **Do:** In Core, write a `PS4DebugClient`: connect (connect timeout only, no read timeout), version, process list, process info (title ID), maps (skip `end <= start`, keep names and prot), read (chunked), write, clean disconnect. One lock around the socket. Port the logic from libdebug; don't redesign it. Tests use a fake TCP server that replays the byte layouts from `docs/protocol.md`. CLI command: `info --ip <ip>` (version and process list). **Debug log:** the client takes a `DebugLog` (`Core/Logging`); log every message sent and received with `Wire` and every command and its result or error with `Info`/`Error` (category `proto`).
- **Milestones:** m1 connect, version, process list with tests · m2 info, maps, read, write with tests · m3 `info` CLI command.
- **Done when:** all tests pass and `info` builds.

### T0.3 Process picker and section selection
- **Read:** PROMPT §3.3, §5 "Process picker" and "Sections panel", `docs/research` notes on PS4Cheater's section filter.
- **Do:** `GameProcessPicker`: `eboot.bin`, or a `*.elf` that isn't a system process (system prefixes are configurable: `Sce`, `sys` ...); can be overridden with `--pid` or `--name`. `SectionFilter`: readable, writable by default, name contains any of `anon, exe, Game, dlmalloc, SmallBlockAllocator` (from PROMPT §3.3; PS4Cheater itself has no such list, see `docs/protocol.md` §14). Prot bits: 1 read, 2 write, 4 exec (`docs/protocol.md` §7). Prints selected/total count and bytes. CLI command: `sections` (maps table with a ✓ column). **Debug log:** every map with kept/skipped and why (category `maps`), and the picked process and why (category `picker`). Tests with sample maps, including `end = 0`.
- **Milestones:** m1 picker and filter with tests · m2 `sections` CLI command.
- **Done when:** tests pass and `sections` prints the table.

### T0.4 First scan, next scan and write (4-byte exact)
- **Read:** PROMPT §3.4–3.5, §5 "Scanner" (4-byte part only), `docs/protocol.md` (scan sections).
- **Do:**
  - `ScanEngine` with two back ends: **LocalScan** (the default and the reference: read with `CMD_PROC_READ` in chunks of about 1–4 MB, compare on the Mac, 4-byte alignment; this is what PS4Cheater does) and **ConsoleScan** (comparison runs on the PS4, per section: Turbo Scan if CAPS answers, else the SCAN_START/COUNT trio, both after `CMD_PROC_AUTH`; **never the classic `0xBDAA0009`**, which is broken, see `docs/protocol.md` §10–12).
  - First scan and next scan for exact 4-byte values. A next scan re-reads previous hits, batching nearby addresses into one read.
  - Results stored as compact arrays (`ulong[]` addresses, `uint[]` values).
  - Per-section log: name, start, size, hits, ms, on screen and in the debug log (category `scan`). Every write goes to the debug log with pid, address, old and new bytes (category `write`). Progress callback and CancellationToken (Ctrl+C cancels).
  - Interactive CLI command `scan`: pick the process, show the section summary, choose console or local, enter a value. Then a loop: new value = next scan, `l` lists the first 20 results, `w <n> <value>` writes (only on this explicit command), `q` quits.
- **Milestones:** m1 LocalScan with tests against fake memory · m2 ConsoleScan (+ Turbo Scan) · m3 interactive CLI and write.
- **Done when:** builds and tests pass. **Update manual.md G0** with the exact commands.

### T0.5 USER TEST: Phase 0 on the real console (gate G0)
- Egor runs G0 and pastes the output. You fix what it shows (each fix is a milestone with a commit).
- If first-scan counts are low: compare the section list with PS4Cheater's, the per-section counts, alignment, and console vs local results; check `docs/protocol.md` against the source again.
- **Done when:** Egor confirms first-scan counts are in the thousands (similar to PS4Cheater) and he found and changed his money. **Don't start Phase 1 before this.**

---

## Phase 1: backend API (ASP.NET Core minimal API, 127.0.0.1 only)

### T1.1 API skeleton, settings and connection
- **Read:** PROMPT §4, §5 "Connection bar" (not injection), "Process picker", "Sections panel".
- **Do:** `src/PS4Mem.Api` on `127.0.0.1:5080` with CORS for `localhost:3000`. A settings store in `~/Library/Application Support/PS4Mem/settings.json` (debug logging on/off, IP, recent IPs, payload path defaulting to `payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin`, loader port). A `ConnectionManager` singleton. Endpoints: `GET/PUT /api/settings`, `POST /api/connect`, `POST /api/disconnect`, `GET /api/status`, `GET /api/processes` (with the suggested game), `GET /api/processes/{pid}` (title ID), `GET /api/processes/{pid}/maps` (with the default selection). Add `api.http` with sample calls.
- **Milestones:** m1 skeleton and settings · m2 connection, process and map endpoints.
- **Done when:** builds, and `api.http` calls work against the fake server in tests.

### T1.2 Payload injection
- **Read:** PROMPT §2 (BinLoader rows), §5 "Inject payload".
- **Do:** Core `PayloadInjector`: if 744 already answers, refuse ("payload already running"). Otherwise send the `.bin` to the loader port (9090 by default, 9020/9021 optional), poll 744 every second for up to 20 s, then connect automatically. Clear error messages (connection refused → the BinLoader message in the spec). Add `POST /api/inject` and a CLI `inject` command, and remember the path in settings. Tests use fake listeners.
- **Milestones:** m1 injector with tests · m2 endpoint and CLI command.
- **Done when:** tests cover refused, already running, success and timeout.

### T1.3 Value types and compare types (Core, no console needed)
- **Read:** PROMPT §5 "Scanner".
- **Do:** Value types 1/2/4/8 bytes (signed/unsigned), float, double, string, byte array with `??` wildcards; hex input parsing. Compare types: exact, bigger than, smaller than, between, increased, increased by, decreased, decreased by, changed, unchanged, unknown initial value. Optional alignment. Float handling follows PS4Cheater (researcher checks). LocalScan supports them all. Thorough unit tests.
- **Milestones:** m1 value types and parsing · m2 all compares in LocalScan.
- **Done when:** tests pass for every type × compare that's valid.

### T1.4 Result store, unknown initial value, undo, console mapping
- **Read:** PROMPT §3.4, §5 "Scanner" (results), `docs/protocol.md` (scan enums).
- **Do:** Results stored as struct-of-arrays that spill to disk in `~/Library/Application Support/PS4Mem/scans/` above a threshold (about 2M rows); clean up on new scan and on exit. Unknown initial value takes a snapshot of the section memory (to disk), and the next scan compares against it. Undo keeps the previous result set. Map each compare and type to the console-scan enum where the server supports it, and fall back to local where it doesn't.
- **Milestones:** m1 result store and spill · m2 unknown initial value and undo · m3 console mapping.
- **Done when:** tests pass, including a scan with millions of results.

### T1.5 Scan session API
- **Read:** PROMPT §5 "Scanner", §3.5.
- **Do:** `POST /api/scan/first|next|new|undo|cancel`, `GET /api/scan/progress` (SSE: percent, current section, hits so far), `GET /api/scan/results?offset&limit` (first ~5,000 by default, total count), `POST /api/scan/results/values` (re-reads current values for the visible rows). "Pause game during scan" uses process stop/continue and always resumes, even on error or cancel. Log scan time and per-section counts.
- **Milestones:** m1 scan endpoints and SSE · m2 results paging and refresh · m3 pause game.
- **Done when:** builds and tests pass.

### T1.6 Cheat list, locks and title-ID storage
- **Read:** PROMPT §5 "Cheat list" and "Safety", §4 (storage).
- **Do:** Cheat model (description, address, section and offset, type, value, locked). `~/Library/Application Support/PS4Mem/cheats/<TITLEID>.json`. Endpoints for list/add/edit/delete, write a value, lock/unlock (a background loop re-writes every ~200 ms while locked), import/export JSON. Disconnect stops all locks.
- **Milestones:** m1 storage and CRUD · m2 write and lock loop · m3 import/export.
- **Done when:** tests pass. **Update manual.md G1.**

### T1.7 USER TEST: API on the real console (gate G1)
- Egor runs G1 (inject, connect, scan, write, lock, save) using `api.http`. Fix what it shows.
- **Done when:** Egor confirms G1 passes.

---

## Phase 2: Next.js UI (`web/`, App Router, TypeScript, Tailwind)

### T2.1 Scaffold, one-command run, connection bar
- **Read:** PROMPT §4, §5 "Connection bar".
- **Do:** `web/` with a typed API client. `./run.sh` (or a root `npm run dev`) starts the API and the web app together and stops both on Ctrl+C. Connection bar: IP field with recent IPs, Connect/Disconnect, status and version. Inject dialog: file path, port select, progress and the clear error messages.
- **Milestones:** m1 scaffold and run script · m2 connection bar · m3 inject dialog.
- **Done when:** `npm run build` passes and `./run.sh` starts both.

### T2.2 Process picker and sections panel
- **Read:** PROMPT §5 "Process picker", "Sections panel".
- **Do:** Process dropdown with the auto-selected game, title ID and name. Sections table (name, start, end, size, prot) with checkboxes, the name filter (defaults pre-filled), select all, and the "selected / total" count.
- **Milestones:** m1 picker · m2 sections panel.
- **Done when:** builds.

### T2.3 Scanner and results table
- **Read:** PROMPT §5 "Scanner".
- **Do:** Type and compare selectors (the value inputs change with the compare type), signed/unsigned, hex, alignment and pause options. First/Next/New/Undo, progress bar over SSE, cancel, result count and scan time. Virtualised results table (TanStack Virtual) showing address, current value (refreshes visible rows), previous value and section. Double-click a row to add it to the cheat list.
- **Milestones:** m1 scan controls and progress · m2 results table with live refresh.
- **Done when:** builds.

### T2.4 Cheat list
- **Read:** PROMPT §5 "Cheat list", "Safety".
- **Do:** Table with description, address, section+offset, type, inline value edit (writes on Enter only), lock toggle, delete. Save/load per title ID, import/export JSON.
- **Milestones:** m1 table and editing · m2 locks and save/load/import/export.
- **Done when:** builds.

### T2.5 Single-process build and README
- **Read:** PROMPT §4 (static export), §7 (README).
- **Do:** `next build` as a static export that the API serves from `wwwroot`. A one-command production run. Full README with setup, dev run and production run. **Update manual.md G2.**
- **Milestones:** m1 static export served by the API · m2 README.
- **Done when:** one process serves the whole app.

### T2.6 USER TEST: full app on the real console (gate G2)
- **Done when:** Egor confirms G2 passes. MVP complete.

---

## Phase 3: after the MVP (one task at a time, each ends with its own gate G3.x)

### T3.1 Export to GoldHEN cheat JSON
- researcher: real examples from GoldHEN/GoldHEN_Cheat_Repository → `docs/research/goldhen-json.md`. Export the cheat list to `<TITLEID>_<VERSION>.json`. Write gate G3.1.

### T3.2 Hex viewer/editor with disassembly
- Hex view at an address (paged reads, edit writes on confirm). x86-64 disassembly (researcher picks a .NET library, for example Iced). Write gate G3.2.

### T3.3 "Find what writes to this address"
- Debugger attach, hardware watchpoint, and a listener on **TCP 755** for events coming back from the PS4. Always detach cleanly. Add the macOS firewall step to manual.md (M6). Write gate G3.3.

### T3.4 Pointer scanner
- Find pointer paths to an address so cheats survive a game restart. Store pointer cheats in the cheat table. Write gate G3.4.
