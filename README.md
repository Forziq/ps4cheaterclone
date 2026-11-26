# PS4 Mem

PS4 Mem is PS4Cheater for macOS: a memory editor for a jailbroken PS4. It talks to the ps4debug-NG payload over your local network to list processes, scan a game's memory and edit values.

It's for your own console and single-player games. See the disclaimer below.

## Status

Phase 0 has passed its first real-console test. It's a .NET console app that checks scanning works before the Phase 2 app is built.

Working now:

- Connecting to the payload and reading its version.
- Listing processes with the `info` command. Tested on a real console running ps4debug-NG v1.3, with 66 processes listed.
- A process picker. It shows only the suggested game, marked `→ suggested:`. Press Return to take it. Type `list` to see every process, or type a pid or a name.
- Section selection with the `sections` command. It lists the memory sections a scan will use, and only reads.
- Sending the payload with the `inject` command, and with the API's `POST /api/inject` endpoint.

The `scan` command passed its first real-console test on Assassin's Creed III (PS4, GoldHEN, ps4debug-NG 1.3.4). It found an exact 4-byte value, the in-game money. A next scan narrowed the results to one address, and a write changed it. The new value showed in the game. The command has:

- A first scan and next scans.
- Undo with `u`, which goes back to the previous results.
- A result store that keeps large result sets on disk.
- Writing a value with `w`. This is the only command that writes to PS4 memory.

Only exact 4-byte values work in the `scan` command for now.

Phase 1 is in progress. The core scanning library now has more value and compare types, unknown initial value snapshots, the result store, undo and wider console support. The API (next step) and then the Mac app will use them. The backend API (`src/PS4Mem.Api`) listens on 127.0.0.1:5080 only. It has endpoints for settings, connection, processes, sections and payload injection. More value types and the cheat list come later.

## Planned features

Phase 1 is a local API (ASP.NET Core, listening on localhost only). It injects the payload over the GoldHEN BinLoader, and it will save cheat tables per game, keyed by title ID.

Phase 2 is a native macOS app written in SwiftUI, in the `mac/` folder. It talks to the local backend API. The finished app will be a double-clickable `PS4Mem.app` that contains the backend and starts and stops it itself, so you won't need a .NET install to run it.

It will have:

- A connection bar and a process picker that suggests the game.
- A sections panel with a name filter.
- A scanner for 1, 2, 4 and 8-byte signed and unsigned values, float, double, strings and byte arrays with `??` wildcards. Scan types include exact, bigger, smaller, between, increased, decreased, changed, unchanged and unknown initial value. It will support undo, progress and cancel.
- A results table and a cheat list with lock, and JSON import and export.

After that, the plan includes export to the GoldHEN cheat JSON format, a hex viewer and editor with disassembly, a "find what writes to this address" tool using a hardware watchpoint, and a pointer scanner.

## Requirements

- A Mac with the .NET 10 SDK, to build and run the current CLI and API. The finished app won't need it.
- Swift, to build the Phase 2 app in `mac/`. The Xcode Command Line Tools are enough. Full Xcode is optional.
- A jailbroken PS4 running GoldHEN, with BinLoader enabled on port 9090.
- The ps4debug-NG payload `.bin`. It's a third-party binary and isn't included in this repo. Get it from the ps4debug-NG releases and put it in `payloads/`. The default file name is `ps4debug-ng_v1.3.4_release_2026-11-05.bin`.
- The Mac and the PS4 on the same network.

## Setup

1. Clone the repo, then copy the example IP file and set your PS4's address:

   ```bash
   cp ps4ip.example ps4ip.local
   ```

   Edit `ps4ip.local` and set `PS4_IP=` to your PS4's IP address. The file is git-ignored, so the address stays on your machine.

2. Build and run the tests:

   ```bash
   dotnet build
   dotnet test
   ```

The tests don't need a console. The protocol code runs against a fake TCP server.

## Usage

The `inject` command sends the payload for you, so you don't need to send it by hand.

1. Send the payload and print the ps4debug version. Replace `$PS4_IP` with your PS4's address, or set `PS4_IP` in your shell first:

   ```bash
   dotnet run --project src/PS4Mem.Cli -- inject --ip $PS4_IP
   ```

   The command sends the ps4debug payload to the PS4's payload loader (GoldHEN BinLoader, port 9090). It waits up to 20 seconds for ps4debug to answer on port 744. Then it connects and prints the ps4debug version.

   Options:

   - `--payload <file>` sends a different payload file. The default is `payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin`.
   - `--loader-port <port>` sets the loader port. The default is 9090. Use 9020 or 9021 for other loaders.

   The command refuses to send the payload if ps4debug already answers on port 744. A second copy can freeze the console.

   If you get "Connection refused" on the loader port, BinLoader is off, or it has already been used this session. Toggle BinLoader in GoldHEN, or restart the PS4, then try again.

2. Fallback: send the payload by hand. Use this only if `inject` doesn't work for you. First check whether the payload is already running:

   ```bash
   nc -z -G 2 $PS4_IP 744 && echo "already running"
   ```

   If it isn't running, send the payload to BinLoader:

   ```bash
   nc -w 3 $PS4_IP 9090 < payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin
   ```

   BinLoader takes one payload per session. Wait about 10 seconds before you connect.

3. Print the ps4debug version and the process list. This command only reads:

   ```bash
   dotnet run --project src/PS4Mem.Cli -- info
   ```

   Options:

   - `--ip <ip>` sets the console address.
   - `--port <port>` sets the command port.
   - `--debug` writes a debug log.

   Options can go anywhere on the command line, for example:

   ```bash
   dotnet run --project src/PS4Mem.Cli -- info --ip <PS4_IP> --debug
   ```

4. List the game process and its memory sections. This command only reads:

   ```bash
   dotnet run --project src/PS4Mem.Cli -- sections --ip <PS4_IP>
   ```

   If `ps4ip.local` holds your address, you can leave out `--ip`.

   The command shows only the suggested game, marked `→ suggested:`, with the reason beside its name. Press Return to take the suggestion. To see every process, type `list`. You can also type a pid or a name. Not every game runs as `eboot.bin`. Assassin's Creed III, for example, runs as `ac3_engine_orbis_fn.elf`.

   Then it prints the process's title ID and lists the readable memory sections with their name, start, end, size and protection. Unnamed sections show as `(unnamed)`. A ✓ marks the sections a scan will use. The last line gives the number of selected sections out of the total, and their size in bytes.

   By default, a scan uses writable sections that are unnamed, or whose name contains `anon`, `exe`, `Game`, `dlmalloc` or `SmallBlockAllocator`. The match ignores case. Unnamed sections are anonymous memory, and games keep most of their data there. In Assassin's Creed III, about 2.9 GB is unnamed heap, against about 9 MB of named sections. The default selection is only a suggestion. The options below change it.

   Memory maps are skipped and logged if their end is at or before their start, if they are over 64 GB, if they are duplicates, or if they can't be read.

   Options:

   - `--pid <pid>` picks the process by its ID and skips the question.
   - `--name <name>` picks the process by name and skips the question. Use the exact name or a part that matches only one process.
   - `--filter a,b,c` sets the section name filter, as a comma-separated list. Use `--filter ""` to match any name.
   - `--include-readonly` also selects read-only sections.
   - `--all` selects every readable section. It ignores the name filter and the writable rule.

   With `--debug`, the log includes the `picker` and `maps` categories.

5. Scan a game's memory for a 4-byte value:

   ```bash
   dotnet run --project src/PS4Mem.Cli -- scan --ip $PS4_IP --method local
   ```

   `scan` is interactive. It asks which process to scan, as `sections` does, unless you pass `--pid` or `--name`. Then it uses the default sections and asks for a 4-byte value. The first value you type runs a first scan. After that, typing a new value runs a next scan on the previous results.

   At the prompt:

   - `l` lists the first 20 results: address, value found, current value and section.
   - `w <n> <value>` writes a value to result `n`. This is the only command that writes to PS4 memory.
   - `u` undoes the last scan and goes back to the previous results.
   - `new` starts over.
   - `q` quits.

   Ctrl+C cancels a running scan.

   Results over 2 million are kept on disk, in `~/Library/Application Support/PS4Mem/scans`. The program deletes them when it exits. If it crashed, the leftovers are deleted the next time it starts.

   Options:

   - `--method local|console` sets where the comparison happens. `local` is the default. The Mac reads the memory and compares it there, the same approach as PS4Cheater. `console` makes the PS4 do the comparison. It uses ps4debug-NG's Turbo Scan when it's available, and its SCAN_START range scan otherwise. On stock ps4debug, `console` falls back to `local`.
   - `--unaligned` checks every byte, not only addresses that are 4-byte aligned.
   - `--pid`, `--name`, `--filter`, `--include-readonly` and `--all` work the same as they do for `sections`.

   Every scan prints and logs the hit count for each section. Every write is logged with the old and new bytes (with `--debug`).

   Speed depends on the method and the network. On Assassin's Creed III, a first scan of about 3.4 GB took about 90 seconds with `console`. The PS4 does the comparison, so only the matches cross the network. The same scan with `local` took about 14 minutes over Wi-Fi, at about 4 MB/s. Both found the same 36 results. A wired connection is faster.

To write a debug log without the flag, set `PS4MEM_DEBUG=1`. Logs go to `logs/`, which is git-ignored. Attach the log when you report a problem.

### Local API

The local API (`src/PS4Mem.Api`) has a `POST /api/inject` endpoint that does the same job as the `inject` command. The request body is optional. It takes `ip`, `payloadPath` and `loaderPort`. A path and port that work are saved in the settings. A sample request is in [`src/PS4Mem.Api/api.http`](src/PS4Mem.Api/api.http).

## How it works

The app talks to the payload's command port (744). It uses the ps4debug wire protocol, which is documented in [`docs/protocol.md`](docs/protocol.md), with sources for each detail. The debugger port (755) is for the planned "find what writes to this address" feature, where the PS4 connects back to the Mac.

The console address is found in this order:

1. The `--ip` option.
2. The `PS4_IP` environment variable.
3. `ps4ip.local`, in the current folder or a parent folder.
4. The `consoleIp` setting in `~/Library/Application Support/PS4Mem/settings.json`.

There's no built-in address. If none is set, the CLI tells you how to set one.

Safety: the app never writes to PS4 memory unless you ask it to, with a CLI command or a click in the interface.

The planned design allows only one debugger at a time, and disconnecting will release locks, the debugger and any scans. These rules apply once those features arrive.

### Defaults

All defaults are in `src/PS4Mem.Core/PS4MemSettings.cs`, and you can override each one:

- Command port: 744.
- Debugger port: 755.
- GoldHEN BinLoader port: 9090.
- Payload path: `payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin`.
- Scan method: `local`.
- Read chunk size: 32 MB.
- Section name filter: `anon`, `exe`, `Game`, `dlmalloc` and `SmallBlockAllocator`. Writable unnamed sections are selected too.
- Connect timeout: 5 seconds. Reads have no timeout, because scans can take minutes.

### Layout

| Path | What |
|---|---|
| `src/PS4Mem.Core` | Protocol client, scanning, cheats |
| `src/PS4Mem.Cli` | Console app |
| `src/PS4Mem.Api` | Local backend API (ASP.NET Core), listens on 127.0.0.1:5080 only |
| `mac/` | Planned SwiftUI macOS app (Phase 2) |
| `tests/PS4Mem.Core.Tests` | xUnit tests (protocol code runs against a fake TCP server) |
| `docs/protocol.md` | ps4debug wire protocol, with sources |

## Credits

- [ps4debug-NG](https://github.com/Pharaoh2k/ps4debug-NG) by Pharaoh2k (GPL-3.0). The debugger payload this app talks to. Its source is the reference for the wire protocol.
- [ps4debug](https://github.com/jogolden/ps4debug) by golden (jogolden). The original payload.
- [libdebug](https://github.com/a0zhar/libdebug) by a0zhar. A C# ps4debug client, used as a protocol reference.
- [PS4Cheater](https://github.com/ctn123/PS4_Cheater) by ctn123. The tool this project is modelled on.
- [PS4CheaterNeo](https://github.com/avan06/PS4CheaterNeo) by avan06. A protocol and scanning reference.
- [GoldHEN](https://github.com/GoldHEN/GoldHEN). The homebrew enabler, with BinLoader.
- [GoldHEN Cheat Repository](https://github.com/GoldHEN/GoldHEN_Cheat_Repository). The cheat format for the planned export.

No code is copied from libdebug, PS4Cheater or PS4CheaterNeo, because they don't have a licence. The client is written from the documented protocol.

## Disclaimer

Use PS4 Mem only on your own console, and only with offline or single-player games. Don't use it to cheat online.

PS4 Mem is not affiliated with or endorsed by Sony Interactive Entertainment. Editing memory can crash the game or the console. Use it at your own risk.

## Licence

GNU GPL-3.0. See [LICENSE](LICENSE).
