# Build "PS4 Mem": a macOS memory editor for a jailbroken PS4 (PS4Cheater-style)

You're starting with no context, so everything you need is below. Read all of it before writing code.

## 1. What I want

A macOS app that does what **PS4Cheater / PS4CheaterNeo** do on Windows: connect to my jailbroken PS4 over the network, scan a running game's memory for values (money, ammo, health...), narrow them down, edit or lock them, and save cheat tables. It must work for **any game**, not just one. It's for my own console and single-player games only.

I'm a Next.js / TypeScript developer on an Apple Silicon MacBook Pro. I'm happy to learn some C#.

## 2. My setup (use these as defaults, but make all of them editable in the app)

| Thing | Value |
|---|---|
| PS4 firmware | 13.52, jailbroken with GoldHEN |
| PS4 IP | 192.168.0.99 (same LAN as the Mac) |
| Debugger payload | ps4debug-NG v1.3.4 by Pharaoh2k, already in this project at `payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin` (use this as the app's default payload path; don't download another copy). Wire-compatible with Ctn/SiSTRo's ps4debug v1.1.19, so existing ps4debug clients should work unchanged. Source: github.com/Pharaoh2k/ps4debug-NG (read its README for supported commands, incl. its "Turbo Scan"). |
| Payload loader | GoldHEN BinLoader on **TCP 9090**. It accepts **one payload per session**, then stops listening until it's toggled off/on in GoldHEN settings or the PS4 is restarted. |
| ps4debug command port | **TCP 744** (the client connects here for everything: processes, maps, read/write, scan) |
| ps4debug debugger port | **TCP 755**, the other way round: when a debugger session is attached, the **PS4 connects back to the Mac** on 755 to deliver breakpoint/watchpoint events. The Mac's firewall must allow it. |
| Games I'm testing with | Assassin's Creed III Remastered (process `ac3_engine_orbis_fn.elf`), and now Assassin's Creed IV Black Flag. Other games usually run as `eboot.bin`. Never hard-code process names. |

## 3. Lessons from what already failed. Read this carefully.

1. **Don't use PyPS4debug (the Python client).** I tried a script built on it. Its first scan for my 4-byte money value found only ~81 addresses, so the next scan found 0. **PS4Cheater 1.5.4.7 on Windows, with the same payload, value and game, found thousands on the first scan and worked.** PyPS4debug also crashed parsing process maps (some maps have `end = 0`) and timed out on long scans.
2. So the protocol layer must be based on the **C# libdebug library** that PS4Cheater uses (find it on GitHub; PS4Cheater by ctn123 and PS4CheaterNeo by avan06 use it or a copy of it). Port it to **.NET 8** so it runs on macOS, or reimplement the protocol from its source. Read the source; don't guess packet layouts.
3. **Section selection decides whether scans work.** Copy PS4Cheater's approach: list the process's memory maps, keep readable (+ by default writable) sections, and by default include sections whose name contains any of: `anon`, `exe`, `Game`, `dlmalloc`, `SmallBlockAllocator`. Give the user a section list with checkboxes, the name filter, and "select all". Skip broken maps (`end <= start`) instead of crashing.
4. PS4Cheater's "Console Scan" runs the comparison on the PS4 per section. Support that (and ps4debug-NG's Turbo Scan if the server reports it), with a fallback that reads memory in chunks to the Mac and compares locally. **Log result counts per section** so a bad scan is obvious.
5. No short timeouts on scans; a first scan can take a minute or more. Show progress and allow cancel.

## 4. Architecture

- **Backend:** ASP.NET Core minimal API on .NET 8, `localhost` only. Holds the ps4debug connection, scan sessions and cheat tables.
- **Frontend:** Next.js (App Router, TypeScript, Tailwind) talking to the backend. Use server-sent events or polling for scan progress.
- **One command to run it all** (e.g. `./run.sh` or `npm run dev` that starts both). Later, the backend can serve a static export of the frontend so it's a single process.
- Store data in `~/Library/Application Support/PS4Mem/`: settings, recent IPs, payload path, and **cheat tables per game, keyed by title ID** (e.g. `CUSA11711`). ps4debug's process-info command returns the title ID; use it.

## 5. Features (MVP = everything in this section)

**Connection bar**
- IP field, Connect/Disconnect, status (connected, ps4debug version).
- **"Inject payload" button:** pick the `.bin` (remember the path), choose loader port (default 9090; 9020/9021 as options), send the file over TCP, then poll port 744 every second for up to ~20 s and auto-connect when it answers. Clear errors: "Connection refused = BinLoader is off or already used this session: toggle it in GoldHEN or restart the PS4." Don't inject if 744 already answers (the payload is already running; a second copy can freeze the console).

**Process picker**
- List all processes. Auto-select the likely game: `eboot.bin`, or a process whose name ends in `.elf` and isn't a system process (names starting with `Sce`, `sys`, etc.); let me override. Show title ID and name.

**Sections panel**
- Like PS4Cheater: table of maps (name, start, end, size, prot), checkboxes, name filter with the defaults above, select all, "selected / total" count.

**Scanner**
- Value types: **1, 2, 4, 8 bytes** (signed/unsigned toggle), **float, double**, string, byte array / hex with `??` wildcards. Default is 4 bytes, since most game values like money are 32-bit integers, but some games use 8-byte or float values, so all must be available.
- Compare types: exact, bigger than, smaller than, between, increased, increased by, decreased, decreased by, changed, unchanged, **unknown initial value**.
- Options: 4-byte alignment (on by default), hex input, pause the game during scans (debugger stop/resume).
- First Scan / Next Scan / New Scan, undo last scan, progress bar, cancel, result count, scan time.
- Results table (virtualised, show first ~5,000 but keep all): address, current value (refreshes), previous value, section. Double-click to add to the cheat list. Unknown-value scans can produce millions of results, so store results compactly (address + value arrays, spill to disk if large).

**Cheat list**
- Rows: description, address, section/offset, type, value, **lock** (re-write every ~200 ms while locked), delete. Edit the value inline and write it to the PS4.
- Save/load per game (title ID), plus import/export JSON.

**Safety**
- Never write to PS4 memory unless I clicked something.
- Only one debugger at a time: if I disconnect, release everything cleanly.

## 6. After the MVP (not now)

- **Export to GoldHEN cheat JSON** (`/data/GoldHEN/cheats/json/<TITLEID>_<VERSION>.json` format; look at github.com/GoldHEN/GoldHEN_Cheat_Repository for real examples).
- Hex viewer/editor at an address, with disassembly.
- **"Find what writes to this address"** (hardware watchpoint; events come back on port 755) to build permanent code-patch cheats.
- Pointer scanner, so addresses survive a game restart.

## 7. How to work

- Go in phases and **stop after each one so I can test on the real console**:
  - **Phase 0:** .NET 8 console app. Connect to 744, print version, list processes, pick the game, list maps + filtered sections, do a 4-byte exact first scan and a next scan from values I type, and write a value. **Success = first-scan counts similar to PS4Cheater (thousands, not tens) and I can find and change my money.** Don't start Phase 1 until this works.
  - **Phase 1:** backend API wrapping Phase 0 (incl. payload injection and title-ID cheat storage).
  - **Phase 2:** Next.js UI as described above.
  - **Phase 3:** the post-MVP items, one at a time.
- Keep code simple and readable, with a README covering setup and run steps.
- If something about the protocol is unclear, read libdebug / ps4debug-NG source; don't guess.
- Before testing, I'll have the PS4 ready: GoldHEN loaded, game running, PS4Cheater closed on my other PC.

Start with Phase 0.
