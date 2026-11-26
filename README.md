# PS4 Mem

A macOS memory editor for a jailbroken PS4, in the style of PS4Cheater. It talks to the
ps4debug (ps4debug-NG) payload over the LAN to list processes, scan memory, and edit values.

> Work in progress: Phase 0 (a .NET console app that proves scanning works).

## Layout

| Path | What |
|---|---|
| `src/PS4Mem.Core` | Protocol client, scanning, cheats |
| `src/PS4Mem.Cli` | Console app |
| `tests/PS4Mem.Core.Tests` | xUnit tests (protocol code runs against a fake TCP server) |
| `docs/protocol.md` | ps4debug wire protocol, with sources |

## Build

Needs the .NET 10 SDK.

```bash
dotnet build
dotnet test
```

## Defaults

All defaults live in `src/PS4Mem.Core/PS4MemSettings.cs` and can be overridden:
console IP `192.168.0.99`, command port 744, debugger port 755, GoldHEN BinLoader port 9090.

The ps4debug-NG payload `.bin` is a third-party binary and is not committed.
