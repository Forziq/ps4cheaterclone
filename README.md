# PS4 Mem

PS4 Mem is PS4Cheater for macOS. It's a memory editor for your own jailbroken PS4.

It connects to the ps4debug-NG payload over your local network. It scans a running game's memory for a value such as money, ammo or health, narrows the results down, edits or locks the values, and saves a cheat list for each game.

It's for your own console and single-player games. See the disclaimer below.

## Status

- Phase 0, the command-line tool, passed its console test.
- Phase 1, the backend API, passed its console test on 26 November 2026. The test used Assassin's Creed III on a PS4 with GoldHEN and ps4debug-NG 1.3.4. It covered payload injection, the process and title ID, a console scan, next scans, a write, a lock, disconnect stopping the locks, and cheats saved per title ID.
- Phase 2, the native SwiftUI macOS app, is complete in code. It's waiting for its on-console test. Its layout hasn't been checked on a real console session yet.

Planned after Phase 2, not started:

- Export to the GoldHEN cheat JSON format.
- A hex viewer and editor with disassembly.
- "Find what writes to this address", using a hardware watchpoint on port 755.
- A pointer scanner.

## Features

The app is in `mac/`. It talks to the local backend, which runs inside the app.

### Connection

- The PS4 IP field is pre-filled from `PS4_IP`, `ps4ip.local` or the saved settings. A menu lists recent addresses.
- Connect and Disconnect.
- Inject payload… opens a sheet. Choose the `.bin` and the loader port: 9090, 9020 or 9021. If ps4debug already answers on port 744, the app doesn't send a second copy.
- The status line shows the ps4debug version.

### Processes

- The picker lists every process.
- The suggested game is highlighted. It's only a suggestion. You choose the process with Use this process.
- The picker shows the title ID and the pid. Refresh reloads the list.

### Sections

- Tick the memory sections a scan will use.
- A name filter. By default it matches `anon`, `exe`, `Game`, `dlmalloc` and `SmallBlockAllocator`, plus writable unnamed sections.
- A read-only toggle, and Select all and Select none.
- The selected and total sizes, and the number of maps left out.

### Scanner

- Value types: 1, 2, 4 and 8 bytes, signed or unsigned, float, double, string and byte arrays. Byte arrays accept `??` as a wildcard.
- Compares: exact, bigger than, smaller than, between, increased, increased by, decreased, decreased by, changed, unchanged and unknown initial value.
- Hex input, an Aligned toggle, and Pause game while scanning.
- First scan, Next scan, Undo and New scan.
- A progress bar with Cancel. The result count and the time are shown when a scan ends.

### Results

- The table shows the first 5,000 results.
- Live values for the rows on screen. A value in red has changed.
- A switch to show values in hex.
- Double-click or right-click a result to add it to the cheat list.

### Cheat list

- A lock toggle. While it's on, the value is written again about every 200 ms.
- A description, the address, the type, the value and the live value.
- Type a new value and press Return to write it. Delete asks for confirmation.
- The list is saved automatically for each game, by title ID, in `~/Library/Application Support/PS4Mem/cheats/<TITLEID>.json`.
- Import… adds the list to the current one or replaces it. Export… saves it as `<TITLEID>.json`.

The app writes to PS4 memory only when you ask it to. That means pressing Return in a value field, or turning a lock on.

## Requirements

To run the built app:

- A Mac on macOS 14 or later. The default build is for Apple silicon. Intel Macs need `RID=osx-x64` when you build.
- A jailbroken PS4 with GoldHEN and BinLoader enabled on port 9090.
- The ps4debug-NG payload `.bin`. It's a third-party binary and isn't included in this repo.
- The Mac and the PS4 on the same network.

To build the app and run the tests:

- The .NET 10 SDK.
- The Xcode Command Line Tools. Full Xcode is optional.

The built app doesn't need .NET or the Xcode tools. The backend inside it is self-contained.

## Setup

1. Clone the repo. Copy the example IP file and set your PS4's address:

   ```bash
   cp ps4ip.example ps4ip.local
   ```

   Edit `ps4ip.local` and set `PS4_IP=` to your PS4's address. The file is git-ignored, so the address stays on your machine.

2. Get the payload. Download the ps4debug-NG `.bin` from its releases and put it in `payloads/`. The default file name is `ps4debug-ng_v1.3.4_release_2026-11-05.bin`. The `payloads/` folder is git-ignored. You can also choose the file in the Inject sheet.

3. Run the tests. They don't need a console. The protocol code runs against a fake TCP server.

   ```bash
   dotnet test
   swift test --package-path mac
   ```

   There are 827 .NET tests and 103 Swift tests.

## Run the app

Build the app:

```bash
scripts/build-app.sh
```

This writes `build/PS4Mem.app`, which is about 110 MB. The script:

- Publishes the backend in Release mode as a self-contained app for this Mac's chip, into `PS4Mem.app/Contents/Resources/backend/`.
- Builds the SwiftUI app with `swift build -c release`.
- Copies `payloads/*.bin` into the app, if the file is there.
- Draws the icon and writes `Info.plist`.
- Signs the app ad hoc with `codesign -s -`.

Other builds:

```bash
RID=osx-x64 scripts/build-app.sh
OUT=/Applications scripts/build-app.sh
```

The first command builds for Intel Macs. The second puts the app in `/Applications`.

To open the app, double-click `PS4Mem.app`. It starts the backend on 127.0.0.1:5080 and stops it when you quit. If the app is force-quit or crashes, the backend stops by itself.

If an API already answers on port 5080, for example from `./run.sh` or `dotnet run`, the app uses that one. It leaves that API running when you quit.

On first launch, macOS may ask whether PS4 Mem can find devices on the local network. Click Allow. The app needs this to reach the PS4.

The app is signed ad hoc, not notarised. An app built on your own Mac opens normally. An app copied from somewhere else needs System Settings → Privacy & Security → Open Anyway. On macOS 14, right-click the app and choose Open. The ad hoc signature is fine for your own Mac, but don't hand out copies.

## Run in development

`run.sh` builds the backend and the app, then runs the app. The app starts the backend with `dotnet run`. Quitting the app or pressing Ctrl+C stops both.

```bash
./run.sh
```

To use another port for the API:

```bash
PS4MEM_API_URL=http://127.0.0.1:5081 ./run.sh
```

To run the parts yourself:

```bash
dotnet run --project src/PS4Mem.Api
swift build --package-path mac
```

Then run the app from the `swift build` output. Sample API calls for every endpoint are in [`src/PS4Mem.Api/api.http`](src/PS4Mem.Api/api.http).

## Scan walkthrough

This walks through finding the money in a game.

1. On the PS4, load GoldHEN, turn on BinLoader, start the game and load your save. Close any other debugger first, because only one can run at a time. Save in the game.
2. Open PS4 Mem and check the IP address. Click Inject payload…, then Inject. If ps4debug already runs, click Connect instead.
3. Pick the game process and click Use this process. The default sections are usually right.
4. Set the type to 4 bytes and the compare to Exact value. Type your money and click First scan. The app uses the `local` method, so the first scan reads every selected byte over the network. On Assassin's Creed III that was about 3.4 GB and took about 14 minutes over Wi-Fi. A wired connection is faster. The progress bar shows how far the scan has got, and Cancel stops it.
5. Change the money in the game, type the new amount and click Next scan. Repeat until a few results are left. Next scans only re-read the previous results, so they're quick.
6. Double-click a result to add it to the cheat list. Type a new value and press Return. Turn on the lock to keep the value there.

## Command line

The CLI is in `src/PS4Mem.Cli`. Run it with `dotnet run --project src/PS4Mem.Cli -- <command>`.

### inject

The `inject` command sends the payload for you, so you don't need to send it by hand.

```bash
dotnet run --project src/PS4Mem.Cli -- inject --ip $PS4_IP
```

The command sends the ps4debug payload to the PS4's payload loader (GoldHEN BinLoader, port 9090). It waits up to 20 seconds for ps4debug to answer on port 744. Then it connects and prints the ps4debug version.

Options:

- `--payload <file>` sends a different payload file. The default is `payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin`.
- `--loader-port <port>` sets the loader port. The default is 9090. Use 9020 or 9021 for other loaders.

The command refuses to send the payload if ps4debug already answers on port 744. A second copy can freeze the console.

If you get "Connection refused" on the loader port, BinLoader is off, or it has already been used this session. Toggle BinLoader in GoldHEN, or restart the PS4, then try again.

### Send the payload by hand

Use this only if `inject` doesn't work for you. First check whether the payload is already running:

```bash
nc -z -G 2 $PS4_IP 744 && echo "already running"
```

If it isn't running, send the payload to BinLoader:

```bash
nc -w 3 $PS4_IP 9090 < payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin
```

BinLoader takes one payload per session. Wait about 10 seconds before you connect.

### info

Prints the ps4debug version and the process list. This command only reads.

```bash
dotnet run --project src/PS4Mem.Cli -- info --ip <PS4_IP>
```

Options:

- `--ip <ip>` sets the console address.
- `--port <port>` sets the command port.
- `--debug` writes a debug log.

Options can go anywhere on the command line.

### sections

Lists the game process and its memory sections. This command only reads.

```bash
dotnet run --project src/PS4Mem.Cli -- sections --ip <PS4_IP>
```

If `ps4ip.local` holds your address, you can leave out `--ip`.

The command shows only the suggested game, marked `→ suggested:`, with the reason beside its name. Press Return to take the suggestion. To see every process, type `list`. You can also type a pid or a name. Not every game runs as `eboot.bin`. Assassin's Creed III, for example, runs as `ac3_engine_orbis_fn.elf`.

Then it prints the process's title ID and lists the readable memory sections with their name, start, end, size and protection. Unnamed sections show as `(unnamed)`. A ✓ marks the sections a scan will use. The last line gives the number of selected sections out of the total, and their size in bytes.

By default, a scan uses writable sections that are unnamed, or whose name contains `anon`, `exe`, `Game`, `dlmalloc` or `SmallBlockAllocator`. The match ignores case. Games keep most of their data in unnamed memory. In Assassin's Creed III, about 2.9 GB is unnamed heap, against about 9 MB of named sections. The default selection is only a suggestion.

Memory maps are skipped and logged if their end is at or before their start, if they are over 64 GB, if they are duplicates, or if they can't be read.

Options:

- `--pid <pid>` picks the process by its ID and skips the question.
- `--name <name>` picks the process by name and skips the question. Use the exact name, or a part that matches only one process.
- `--filter a,b,c` sets the section name filter, as a comma-separated list. Use `--filter ""` to match any name.
- `--include-readonly` also selects read-only sections.
- `--all` selects every readable section. It ignores the name filter and the writable rule.

With `--debug`, the log includes the `picker` and `maps` categories.

### scan

Scans a game's memory for a 4-byte value.

```bash
dotnet run --project src/PS4Mem.Cli -- scan --ip $PS4_IP --method local
```

`scan` is interactive. It asks which process to scan, as `sections` does, unless you pass `--pid` or `--name`. Then it uses the default sections and asks for a value. The CLI scan does exact 4-byte values only. The first value you type runs a first scan. After that, typing a new value runs a next scan on the previous results.

At the prompt:

- `l` lists the first 20 results: address, value found, current value and section.
- `w <n> <value>` writes a value to result `n`. This is the only CLI command that writes to PS4 memory.
- `u` undoes the last scan and goes back to the previous results.
- `new` starts over.
- `q` quits.

Ctrl+C cancels a running scan.

Results over 2 million are kept on disk, in `~/Library/Application Support/PS4Mem/scans`. The program deletes them when it exits. If it crashed, the leftovers are deleted the next time it starts.

Options:

- `--method local|console` sets where the comparison happens. `local` is the default. The Mac reads the memory and compares it there, the same approach as PS4Cheater. `console` makes the PS4 do the comparison. It uses ps4debug-NG's Turbo Scan when it's available, and its SCAN_START range scan otherwise. On stock ps4debug, `console` falls back to `local`.
- `--unaligned` checks every byte, not only addresses that are 4-byte aligned.
- `--all`, `--pid`, `--name`, `--filter` and `--include-readonly` work the same as they do for `sections`.

Every scan prints and logs the hit count for each section. Every write is logged with the old and new bytes, with `--debug`.

Speed depends on the method and the network. On Assassin's Creed III, a first scan of about 3.4 GB took about 90 seconds with `console`, because the PS4 does the comparison and only the matches cross the network. The same scan with `local` took about 14 minutes over Wi-Fi, at about 4 MB/s. Both found the same 36 results. A wired connection is faster.

## Local API

The local API is in `src/PS4Mem.Api`. It listens on 127.0.0.1:5080 only, and it refuses any other address. It has endpoints for settings, connection, processes, sections, payload injection, scan sessions and cheat lists.

`POST /api/inject` does the same job as the `inject` command. The request body is optional. It takes `ip`, `payloadPath` and `loaderPort`. A path and port that work are saved in the settings.

### Scans

A scan runs in the background. `POST /api/scan/first` and `POST /api/scan/next` answer at once with 202. Only one scan runs at a time.

- `GET /api/scan/progress` streams progress as server-sent events: percent, current section and hits so far. The final event gives the result count and the hits per section.
- `GET /api/scan/status` gives the same status once.
- `POST /api/scan/cancel` stops a running scan and keeps the previous results.
- `POST /api/scan/undo` goes back one scan.
- `POST /api/scan/new` drops every result.
- `GET /api/scan/results?offset=&limit=` returns a page of results, 5,000 rows by default, with the total count. Each row has the address, the value the scan found, the section and the offset in the section. Add `hex=true` to show the values in hex.
- `POST /api/scan/results/values` re-reads the current values of the visible rows from the PS4. It only reads.

The first scan takes these options:

- Value type: u8, i8, u16, i16, u32 (the default), i32, u64, i64, f32, f64, string or bytes.
- Compare: exact (the default), bigger than, smaller than, between or unknown initial value.
- Hex input: with `hex: true`, the value you type is read as hexadecimal. An integer with a `0x` prefix works without the option. The same option works for next scans.
- Sections, by start address. The default is the suggested selection.
- 4-byte alignment, on by default. You can turn it off.
- Method: local (the default) or console. If the console can't run the compare, the scan falls back to local.
- `pauseGame`, described below.

Next scans compare with increased, increased by, decreased, decreased by, changed or unchanged. The value type stays the same as the first scan.

With `pauseGame: true`, the scan stops the game process while it reads memory. It always resumes the game afterwards, including after an error, a cancel or a disconnect. This uses ps4debug-NG's process stop command. It hasn't been tested on a real console yet.

If a game is left paused, for example after the connection dropped, `POST /api/processes/{pid}/resume` resumes it.

Disconnecting, or losing the connection, cancels a running scan and drops the scan results.

### Cheats

Cheat lists are saved per game, by title ID, in `~/Library/Application Support/PS4Mem/cheats/<TITLEID>.json`.

- Add, edit and delete cheats. A cheat has a description, an address, a section and offset, a type and a value.
- Write a cheat's value to the game.
- Lock a cheat. Its value is written again about every 200 ms until you unlock it. Disconnecting stops every lock.
- Import and export a cheat list as JSON.

## Logs and data

The debug log is off by default. To turn it on:

- For the CLI and `dotnet run`, use `--debug` or set `PS4MEM_DEBUG=1`.
- For the API and the app, set the `debugLogging` setting:

  ```bash
  curl -X PUT http://127.0.0.1:5080/api/settings -H "Content-Type: application/json" -d '{"debugLogging": true}'
  ```

Where the logs go:

- The CLI and `dotnet run` write to `logs/` in the repo folder. The folder is git-ignored.
- PS4Mem.app writes to `~/Library/Logs/PS4Mem/`. The debug logs are named `ps4mem-YYYYMMDD-HHMMSS.log`. `backend.log` holds the output of the bundled backend. It's replaced at each launch.

The logs show the commands sent and the replies (in hex), the skipped maps, the result counts for each section, and every write. Attach the log when you report a problem.

App data is in `~/Library/Application Support/PS4Mem/`:

- `settings.json` holds the console IP, the recent IPs, the ports, the payload path and the debug log setting.
- `cheats/` holds the cheat lists, one file per title ID.
- `scans/` holds temporary scan files. They're deleted on exit.

## How it works

The app talks to the payload's command port (744). It uses the ps4debug wire protocol, which is documented in [`docs/protocol.md`](docs/protocol.md), with a source for each detail. The debugger port (755) is for the planned "find what writes to this address" feature, where the PS4 connects back to the Mac.

The console address is found in this order:

1. The `--ip` option, in the CLI.
2. The `PS4_IP` environment variable.
3. `ps4ip.local`, in the current folder or a parent folder.
4. The `consoleIp` setting in `~/Library/Application Support/PS4Mem/settings.json`.

There's no built-in address. If none is set, the CLI tells you how to set one.

The app never writes to PS4 memory unless you ask it to. That means a CLI write, a write or lock request to the API, or a click in the app.

Disconnecting stops every lock and cancels a running scan. The planned debugger feature allows only one debugger at a time, and disconnecting will release it too.

### Defaults

All defaults are in `src/PS4Mem.Core/PS4MemSettings.cs`. You can override each one.

- Command port: 744.
- Debugger port: 755.
- GoldHEN BinLoader port: 9090.
- Payload path: `payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin`.
- Local API address: 127.0.0.1:5080. The API accepts only loopback addresses.
- Scan method: `local`.
- Read chunk size: 32 MB.
- Section name filter: `anon`, `exe`, `Game`, `dlmalloc` and `SmallBlockAllocator`. Writable unnamed sections are selected too.
- Connect timeout: 5 seconds. Reads have no timeout, because scans can take minutes.

## Layout

| Path | What |
|---|---|
| `src/PS4Mem.Core` | Protocol client, scanning, cheats |
| `src/PS4Mem.Cli` | Command-line tool |
| `src/PS4Mem.Api` | Local backend API (ASP.NET Core), listens on 127.0.0.1 only |
| `mac/` | SwiftUI macOS app (Swift package) |
| `scripts/build-app.sh` | Builds `build/PS4Mem.app` |
| `scripts/make-icon.swift` | Draws the app icon |
| `run.sh` | Development run: backend and app |
| `tests/PS4Mem.Core.Tests` | xUnit tests (protocol code runs against a fake TCP server) |
| `tests/PS4Mem.Api.Tests` | xUnit tests for the API |
| `mac/Tests` | XCTest tests for the app |
| `docs/protocol.md` | ps4debug wire protocol, with sources |
| `payloads/` | Put the payload `.bin` here. It isn't in the repo. |
| `build/` | The built app. Git-ignored. |

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

Use PS4 Mem only on your own console, and only with your own single-player games. Don't use it to cheat online.

PS4 Mem is not affiliated with or endorsed by Sony Interactive Entertainment. The ps4debug-NG payload isn't included in this repo. Editing memory can crash the game or the console. Use it at your own risk.

## Licence

GNU GPL-3.0. See [LICENSE](LICENSE).
