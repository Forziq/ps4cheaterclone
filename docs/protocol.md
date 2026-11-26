# ps4debug wire protocol (as used by PS4 Mem)

Every layout here comes from reference source (ps4debug-NG v1.3.4 server, libdebug client), never from guesswork (CLAUDE.md §3).
Each section names its source as `path:line`, relative to `ref/`. The verbatim
excerpts behind each claim are in `docs/research/`:

- `core-commands.md`: header, status, version, process list/info/maps, read, write, disconnect, auth
- `console-scan.md`: CMD_PROC_SCAN, enums, the iterative scan trio, Turbo Scan
- `sections-and-pause.md`: section filtering, read chunk sizes, stop/continue

**Precedence rule.** When the ps4debug-NG docs (`PROTOCOL.md`, `README.md`) disagree with
its C source, the **source wins**. Known disagreements are listed in §15.

## Reference sources

| Name | Repo | Commit | Role |
|---|---|---|---|
| `ps4debug-NG` | github.com/Pharaoh2k/ps4debug-NG | 14ce736 | Server (the payload Egor runs, v1.3.4) |
| `libdebug` | github.com/a0zhar/libdebug | 681005f | C# client (`PS4DBG`) |
| `PS4CheaterNeo` | github.com/avan06/PS4CheaterNeo | 45d44df | C# app with its own libdebug copy |
| `PS4Cheater` | github.com/ctn123/PS4_Cheater | ca85868 | C# app (the one that worked for Egor) |
| `ps4debug` | github.com/a0zhar/ps4debug | 0107eec | Fork of the original server (reference only) |

Abbreviations: `LD` = `libdebug/PS4DBG/`, `NG` = `ps4debug-NG/debugger/`.

---

## 1. Transport and packet header

- TCP, client connects to the console on **port 744** (`NG/include/server.h:15`, `LD/PS4DBG.cs:44`).
- **Little-endian** for every integer. All wire structs are `__attribute__((packed))`, no padding.
  No `htonl` anywhere on the wire (`ps4debug-NG/PROTOCOL.md:29-31`; `NG/source/net.c:68-72`;
  client uses `BitConverter`, `LD/PS4DBG.cs:329-346`).
- Every request starts with a 12-byte header (`NG/include/protocol.h:96-102`, `LD/PS4DBG.cs:398-403`):

| Offset | Type | Field | Value |
|---|---|---|---|
| 0 | u32 | magic | `0xFFAABBCC` (`NG/include/protocol.h:15`, `LD/PS4DBG.cs:27`) |
| 4 | u32 | cmd | command ID |
| 8 | u32 | datalen | length of the body that follows |

- The body (`datalen` bytes) follows immediately. libdebug sends header and body as two
  separate sends (`LD/PS4DBG.cs:356-361`); the server reads the header, then exactly
  `datalen` body bytes (`NG/source/server.c:118-156`).
- **A bad magic is skipped without consuming the body** (`NG/source/server.c:131-133`), so the
  stream desyncs. Always send a valid header.
- Replies have **no header**. Each command defines its own reply bytes (status word, counts, data).

## 2. Connecting and disconnecting

**Connect.** No greeting, banner or auth in either direction after TCP accept
(`NG/source/server.c:344-377`, `LD/PS4DBG.cs:161-176`). The server allows 8 clients and closes
extra connections immediately (`NG/include/server.h:16`, `NG/source/server.c:358-363`).

libdebug socket setup (`LD/PS4DBG.cs:161-176`): `NoDelay = true`, send/receive buffers 8192,
`ReceiveTimeout = 10 s`, no connect or send timeout.

**Disconnect: `CMD_CONSOLE_END` `0xBDDD0002`** (`NG/include/protocol.h:76`).
Request: header only, `datalen = 0`. **No reply.** The client then shuts down and closes the
socket (`LD/PS4DBG.cs:181-193`). The server's handler returns 1, which closes the client slot
(`NG/source/console.c:219-221`), and `free_client` also detaches the debugger and frees that
slot's Turbo Scan sessions (`NG/source/server.c:20-33`).

**Keepalive: `CMD_PROC_NOP` `0xBDAACC06`** (`NG/include/protocol.h:49`). Header only; reply
`CMD_SUCCESS` (`NG/source/server.c:72-74`).

**Any handler that returns non-zero closes the connection** after sending its status
(`NG/source/server.c:178-191`). Examples: empty process list, failed maps, read with a null body.
A failed *write* does not close it (§8).

### PS4 Mem implementation notes (not protocol facts)
These fix known libdebug client bugs; the bytes on the wire are unchanged.
- Loop every receive until the full count arrives. libdebug calls `Receive` once for 4-byte
  status/count/length fields (`LD/PS4DBG.cs:299`, `LD/PS4DBG.Proc.cs:45`, `:106`, `LD/PS4DBG.cs:204`).
- Treat a 0-byte receive as "connection closed" and throw. libdebug's `ReceiveData`
  spins forever on that (`LD/PS4DBG.cs:282-287`).
- Cap fixed-width string fields at their width. libdebug's `ConvertASCII` searches to the end of
  the buffer (`LD/PS4DBG.cs:83-90`), so a 32-char name would pick up the pid bytes.
- Use a connect timeout only; **no read timeout** (scans can run for minutes; CLAUDE.md §6).

## 3. Status codes

A status is a u32 LE (`NG/include/protocol.h:87-94`, matches `LD/PS4DBG.Enums.cs:5-12`):

| Name | Value |
|---|---|
| `CMD_SUCCESS` | `0x80000000` |
| `CMD_ERROR` | `0xF0000001` |
| `CMD_TOO_MUCH_DATA` | `0xF0000002` |
| `CMD_DATA_NULL` | `0xF0000003` |
| `CMD_ALREADY_DEBUG` | `0xF0000004` |
| `CMD_INVALID_INDEX` | `0xF0000005` |

`CMD_FATAL_STATUS(s)` is `(s >> 28) == 15` (`NG/include/protocol.h:94`). libdebug's
`CheckStatus` throws on anything other than `CMD_SUCCESS` (`LD/PS4DBG.cs:247-255`).

## 4. `CMD_VERSION` `0xBD000001`

Source: `NG/include/protocol.h:17`, `NG/source/server.c:35-40`, `LD/PS4DBG.cs:197-212`.

- Request: header only, `datalen = 0`.
- Reply: **no status word.** `u32 len`, then `len` ASCII bytes, no NUL. ps4debug-NG sends `"1.3"`
  (`PACKET_VERSION`, `NG/include/protocol.h:13`).
- libdebug's `GetLibraryDebugVersion()` returns the *client's* constant `"1.2"`; it sends nothing.

**`CMD_BRANDING` `0xBD000501`** (NG only, `NG/include/protocol.h:19`, `NG/source/server.c:59-66`):
same reply shape (`u32 len` + bytes, no status). The bytes are `"ps4debug-NG by Pharaoh2k v<ver>"`,
a NUL, then the capability level (currently `"1.2"`, `NG/include/capability.h:6-14`). Use it to tell
NG from stock ps4debug.

## 5. `CMD_PROC_LIST` `0xBDAA0001`

Source: `NG/include/protocol.h:22`, `NG/include/kdbg.h:12-15`, `NG/source/proc.c:290-318`,
`LD/PS4DBG.Proc.cs:37-61`.

- Request: header only.
- Reply: `u32 status`, `u32 count`, then `count` × 36-byte entries:

| Offset | Type | Field |
|---|---|---|
| 0 | char[32] | name (NUL-padded) |
| 32 | i32 | pid |

- No processes: `CMD_DATA_NULL`, and the server closes the connection.

## 6. `CMD_PROC_INFO` `0xBDAA000A` (title ID)

Source: `NG/include/protocol.h:32`, `:440-448`, `NG/source/proc.c:1681-1705`,
`LD/PS4DBG.Proc.cs:458-466`, `libdebug/Process.cs:9-40`.

- Request body (4 bytes): `u32 pid`.
- Reply: `u32 status`, then a 188-byte struct:

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | pid |
| 4 | char[40] | name |
| 44 | char[64] | path |
| 108 | char[16] | titleid (e.g. `CUSA12345`) |
| 124 | char[64] | contentid |

- Null body: `CMD_DATA_NULL` (connection stays open). The server doesn't check whether the pid
  exists, so a bad pid returns `CMD_SUCCESS` with whatever the kernel call left in the struct.

## 7. `CMD_PROC_MAPS` `0xBDAA0004`

Source: `NG/include/protocol.h:26`, `:190-193`, `NG/include/proc.h:11-17`,
`NG/source/proc.c:652-695`, `LD/PS4DBG.Proc.cs:98-127`.

- Request body (4 bytes): `u32 pid`.
- Reply: `u32 status`, `u32 count`, then `count` × **58-byte** entries:

| Offset | Type | Field |
|---|---|---|
| 0 | char[32] | name |
| 32 | u64 | start |
| 40 | u64 | end |
| 48 | u64 | offset |
| 56 | u16 | prot |

- Errors: `CMD_ERROR` or `CMD_DATA_NULL`, and the connection closes.
- Some maps have `end <= start` (PROMPT §3.1). Skip them (PS4CheaterNeo does the same,
  `PS4CheaterNeo/PS4CheaterNeo/common/SectionTool.cs:301-302`).
- `ps4debug-NG/PROTOCOL.md:319-324` documents a 64-byte entry with a different field order.
  That is wrong; the source and libdebug both use 58 bytes.

Prot bits (`ps4debug-NG/ps4-ksdk/include/ksdk_bsd.h:14-16`, `PROT_*` aliases at `:23-29`):
`0x1` read, `0x2` write, `0x4` execute. Clients test `prot & 0x1` for readable
(`PS4Cheater/PS4_Cheater/ProcessManager.cs:69`) and `prot & 0x5 == 0x5` for executable code
(`PS4Cheater/PS4_Cheater/Util.cs:109`).

## 8. `CMD_PROC_READ` `0xBDAA0002`

Source: `NG/include/protocol.h:23`, `:104-109`, `NG/source/proc.c:320-357`, `LD/PS4DBG.Proc.cs:70-76`.

- Request body (16 bytes):

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | pid |
| 4 | u64 | address |
| 12 | u32 | length |

- Reply: `u32 status` (`CMD_SUCCESS`), then **exactly `length` raw bytes**, no length prefix.
  The server streams them in 0x10000 pieces.
- `CMD_SUCCESS` is sent **before** the memory is read, and read errors are ignored: unreadable
  memory comes back as zeros (`NG/source/proc.c:949-967`). The client can't tell zeros from a failure.
- No server-side maximum length. Null body: `CMD_DATA_NULL`, and the connection closes.

**Chunk sizes used by clients** (`sections-and-pause.md` §3):
- PS4Cheater local scan reads **32 MB** per call (`PEEK_BUFFER_LENGTH`, `PS4Cheater/PS4_Cheater/Util.cs:157-158`), 4 reads queued.
- PS4Cheater/Neo split sections into **128 MB** pieces, executables kept whole
  (`PS4Cheater/PS4_Cheater/ProcessManager.cs:75-81`).
- libdebug receive buffer: 8192 (`LD/PS4DBG.cs:42`); Neo's copy uses 0x20000.
  This is only a local buffer size, not a wire limit.

## 9. `CMD_PROC_WRITE` `0xBDAA0003`

Source: `NG/include/protocol.h:25`, `:168-173`, `NG/source/proc.c:448-543`, `LD/PS4DBG.Proc.cs:84-91`.

Sequence:
1. Send header (`datalen = 16`) and body: `u32 pid`, `u64 address`, `u32 length` (same layout as read).
2. Read `u32 status`: the server's ack, `CMD_SUCCESS`.
3. Send `length` raw bytes.
4. Read the final `u32 status`: `CMD_SUCCESS`, or `CMD_ERROR` if the request was invalid, the
   write failed, or the server's read-back check didn't match (capability 1.1,
   `NG/include/capability.h`).

- The final `CMD_ERROR` **keeps the connection open** (`NG/source/proc.c:538-542`).
- Valid request: `length == 0`, or `pid > 0` and `address + length` doesn't overflow.
- Null body or allocation failure: `CMD_DATA_NULL` at step 2, and the connection closes.

## 10. Console scan: classic `CMD_PROC_SCAN` `0xBDAA0009` (do not trust its results)

Source: `NG/include/protocol.h:278-284`, `NG/source/proc.c:1599-1679`, `LD/PS4DBG.Proc.cs:309-435`.
Verbatim excerpts: `console-scan.md` §1 and §7.

Sequence:
1. Send header (`datalen = 10`) and body:

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | pid |
| 4 | u8 | valueType (§10.1) |
| 5 | u8 | compareType (§10.1) |
| 6 | u32 | lenData |

2. Read `u32 status` (`CMD_SUCCESS`).
3. Send `lenData` bytes: the value, then (for two-value compares like "between") the second value.
   For a fixed-size type, the server takes the first `size` bytes as the value and the rest as the
   extra value (`NG/source/proc.c:1607-1647`). libdebug always sends both for numeric types
   (`lenData = 2 × size`, `LD/PS4DBG.Proc.cs:410`).
4. Read `u32 status` (`CMD_SUCCESS`; `CMD_ERROR` if the maps can't be read).
5. Read 8-byte addresses (u64) until `0xFFFFFFFFFFFFFFFF`. No count prefix
   (`NG/source/proc.c:1649-1679`). This can take minutes, so no read timeout.

Scope: the **whole process**, every map with the read bit. You can't limit it to a section.

**Why PS4 Mem doesn't rely on it:**
- Each page step reads `sys_proc_rw(sp->pid, sectionStartAddr, ...)`, i.e. **always the section's first
  page**, while reporting `sectionStartAddr + j` (`NG/source/proc.c:1659`, `:1663`). Anything past the
  first page of each map is compared against the wrong bytes. Read errors are ignored and nothing
  clamps the read at the end of a section.
- The ExactValue compare checks only `size - 1` bytes (`NG/source/proc.c:950-954`,
  `proc_scan_legacy_compareValues`).
- It steps by `size` from each map's start, not by an alignment.
- `PROTOCOL.md:357` calls it "legacy ... superseded by the SCAN_START/COUNT/GET trio".

This fits PROMPT §3.1 (PyPS4debug found only ~81 hits). **PS4Cheater never uses console scan:**
its first scan reads 128 MB chunks with `CMD_PROC_READ` and compares on the PC
(`PS4Cheater/PS4_Cheater/main.cs:503-512` → `ProcessManager.cs:219-235` → `MemoryHelper.cs:383-398`),
stepping by 4 when "alignment" is ticked and by 1 otherwise (`main.cs:286`, `MemoryHelper.cs:968-969`).
PS4CheaterNeo does the same (`PS4CheaterNeo/PS4CheaterNeo/Query.cs:800-816`). So **local scan over
`CMD_PROC_READ` is the proven path.**

### 10.1 Value and compare types

Same values on both sides (`NG/include/protocol.h:246-276`, `LD/PS4DBG.Proc.cs:279-307`):

| Value | valueType | Size | | Value | compareType |
|---|---|---|---|---|---|
| 0 | UInt8 | 1 | | 0 | ExactValue |
| 1 | Int8 | 1 | | 1 | FuzzyValue |
| 2 | UInt16 | 2 | | 2 | BiggerThan |
| 3 | Int16 | 2 | | 3 | SmallerThan |
| 4 | UInt32 | 4 | | 4 | ValueBetween (exclusive, either order) |
| 5 | Int32 | 4 | | 5 | IncreasedValue |
| 6 | UInt64 | 8 | | 6 | IncreasedValueBy |
| 7 | Int64 | 8 | | 7 | DecreasedValue |
| 8 | Float | 4 | | 8 | DecreasedValueBy |
| 9 | Double | 8 | | 9 | ChangedValue |
| 10 | ArrBytes | lenData | | 10 | UnchangedValue |
| 11 | String | lenData | | 11 | UnknownInitialValue |
| | | | | 12 | UnknownInitialLowValue (NG only, no C# client) |

Sizes: `NG/source/proc.c:854-875`. Values are little-endian. PS4CheaterNeo's UI `ScanType` enum
(0, 2, 4, 6, 8 ...) is **not** the wire enum (`PS4CheaterNeo/PS4CheaterNeo/common/ScanTool.cs:12-34`).

## 11. Range scans on the console (NG only, need auth §12)

These scan **one address range** on the console, so they fit "per section". Both need auth bit `0x2`.
Neither has a C# client in `ref/`, so both are **untested on a console** (PS4 Mem's client is
tested against a fake server only). Verbatim excerpts for the details below: `console-scan.md` §8.

### 11.1 Iterative trio
Source: `NG/include/protocol.h:43-49`, `:286-304`, `:431-435`, `NG/source/proc.c:2137-2484`.
They compare with `proc_scan_compareValues` (`NG/source/proc.c:1166`), not the buggy legacy function.

**`CMD_PROC_SCAN_START` `0xBDAACC01`**: body 23 bytes
`{u32 pid; u64 address; u32 length; u8 valueType; u8 compareType; u8 alignment; u32 lenData}`.
1. Read `CMD_SUCCESS`. Send `lenData` value bytes (plus a mask of `size` bytes for ArrBytes).
2. Read `CMD_SUCCESS`. The server scans `[address, address+length)` with step `alignment` (or `size` if 0).
3. Read batches until the 8-byte sentinel `0xFFFFFFFFFFFFFFFF`. Each batch is `u64 payloadLen`, then
   records of `u32 offset` (from `address`) + `size` value bytes.
4. Read a final `CMD_SUCCESS`.

Details (`NG/source/proc.c:2155-2272`):
- `lenData` is **not** checked against the value size; only a zero value size is rejected
  (`:2165-2168`). The pattern buffer is `lenData` bytes (`:2180`) and the compare reads `size` bytes,
  so send `lenData = size` (4 for UInt32). `lenData = 0` is not guarded; never send it.
- For ExactValue the extra value is NULL (`:2204`); `proc_scan_compareValues` compares all `size`
  bytes (`:1212-1218`), unlike the legacy compare (§10).
- No maximum `length` (`:2225`), but the field is u32. The server reads `0x8000`-byte chunks
  (`:2206-2209`, `:2231`) and tests offsets that are multiples of the step from `address`, so an
  aligned 4-byte value is never split across its chunks.
- Batches are flushed at `0xFFE8 - size` bytes (`:2228`): payload at most `0xFFEC`. `payloadLen`
  doesn't count its own 8 bytes.
- Every error before streaming is `CMD_DATA_NULL` and the connection closes.

**`CMD_PROC_SCAN_COUNT` `0xBDAACC02`** (next scan): body 18 bytes
`{u32 pid; u64 base_address; u8 valueType; u8 compareType; u32 lenData}`. Same two acks as START,
then a client-driven loop: send `u32 chunkLen` + entries (`u32 offset` [+ previous value]), the server
answers with batches + sentinel; `chunkLen = 0xFFFFFFFF` ends it, and the server sends `CMD_SUCCESS`
(`NG/source/proc.c:2274-2439`). Max chunk 0x20000.

**`CMD_PROC_SCAN_GET` `0xBDAACC03`**: bulk read, not a result fetch. Body `{u32 pid; u32 count}`,
read `CMD_SUCCESS`, send `count` × `{u64 addr; u32 length}`, then read the raw bytes of each range
in order and an 8-byte sentinel (`NG/source/proc.c:2441-2484`).

`ps4debug-NG/PROTOCOL.md:512-516` describes COUNT and GET replies differently. The source above wins.

### 11.2 Turbo Scan `0xBDAACC10`–`0xBDAACC17`
Source: raw literals in the dispatcher (`NG/source/proc.c:2493-2500`, no macros), handlers in
`NG/source/scan_turbo.c`, structs `NG/include/protocol.h:305-390`. Details in `console-scan.md` §4.

| ID | Name | Auth |
|---|---|---|
| `0xBDAACC10` | CAPS | no |
| `0xBDAACC11` | START | yes |
| `0xBDAACC12` | COUNT (rescan) | yes |
| `0xBDAACC13` | GET (page through results) | yes |
| `0xBDAACC14` | END (free results) | yes |
| `0xBDAACC15` | CONFIG | yes |
| `0xBDAACC16` | REGIONS | yes |
| `0xBDAACC17` | CANCEL (send on a second connection) | yes |

**Detection: CAPS.** Header only. Reply `CMD_SUCCESS` + 16 bytes
`{u32 version; u32 engines; u32 max_threads; u32 reserved}` (`NG/source/scan_turbo.c:1377-1391`).
v1.3.4 source sets `version = 1` and all 12 `TSE_*` engine bits (`0xFFF`; `PROTOCOL.md:545` says
`0x7FF`). Rule (`ps4debug-NG/README.md:182-188`): use Turbo if CAPS answers, otherwise fall back to
the trio.

How it differs from the trio: START has a 27-byte body (the trio's 23 + `u32 flags`). With
`TS_SERVER_RESIDENT` the results **stay on the console**: START replies with a 12-byte summary
`{u32 resident_stored; u64 count}` (no leading `CMD_SUCCESS`, `NG/source/scan_turbo.c:1652-1664`),
and GET `{u32 start_index; u32 count; u32 flags}` pages through them as
`CMD_SUCCESS`, `u32 hdr` (count, bit 31 = has "first" value), records
`{u64 addr; current; previous; [first]}`, `CMD_SUCCESS` (`NG/source/scan_turbo.c:1984-2061`).
Without resident mode it streams the same batches as the trio's START.

**START with `flags = 0`** (what PS4 Mem sends; `NG/source/scan_turbo.c:1462-1707`): the same wire
sequence as the trio's START. `CMD_SUCCESS` (`:1489`), receive `lenData` pattern bytes (`:1490`),
`CMD_SUCCESS` (`:1528`, outside the snapshot branch), batches in the trio's format
(`:1146-1154`, `:1196-1199`), the sentinel (`:1696-1697`), `CMD_SUCCESS` (`:1705`). Batches flush at
`0x3FFE8 - size` bytes (`:1110`): payload at most `0x3FFEC`. Errors before streaming are all
`CMD_DATA_NULL` followed by a close (`:1463`, `:1467`, `:1471`, `:1481`, `:1486`, `:1495`, `:1525`;
`NG/source/server.c:189-190`).

**Unknown commands.** ps4debug-NG answers `CMD_ERROR` and closes the connection
(`NG/source/proc.c:2524-2525`). Stock ps4debug closes it **without any status**
(`ps4debug/debugger/source/proc.c:810-815`, `server.c:296-301`). So CAPS (or AUTH) on a server
without them ends the connection either way: the client must reconnect before carrying on.

Turbo results are freed per connection when the client disconnects (`NG/source/server.c:24-31`).

### 11.3 What PS4 Mem does
- **LocalScan** (`CMD_PROC_READ` + compare on the Mac) is the default and the reference.
- **ConsoleScan** = Turbo if CAPS answers, otherwise the trio, both after auth.
  The classic `0xBDAA0009` is not used. Sequence: AUTH (flags `0x2`); if that fails, reconnect and
  use LocalScan (stock ps4debug). Then CAPS; if it fails, reconnect and use the trio. Each section
  is sent as pieces of at most 1 GB (the length is u32), each extended by `size - 1` bytes so a value
  on a piece edge is found once. A streaming piece can't be interrupted on this connection
  (CANCEL needs a second one), so cancel takes effect between pieces.
- **Next scan** always re-reads the previous hits with `CMD_PROC_READ` on the Mac, for both back
  ends (nearby hits share one read).
- G0 compares LocalScan and ConsoleScan counts on the same value. A mismatch means
  ConsoleScan is wrong.

## 12. `CMD_PROC_AUTH` `0xBDAACCFF` (NG only)

Source: `NG/include/protocol.h:45`, `:424-429`, `NG/source/proc.c:2076-2139`.

Sequence:
1. Send header (`datalen = 8`), body `u32 magic = 0xBB40E64D`, `u32 flags` (bits `0x1`, `0x2`).
   Wrong magic: `CMD_DATA_NULL`, and the connection closes.
2. Read `u32 status` (`CMD_SUCCESS`).
3. Read `u16 chlen` (always 64), then `chlen` challenge bytes.
4. Send `chlen` response bytes: `challenge[i] ^ keystream[i]`.
5. Read `u32 status`: `CMD_SUCCESS` sets the requested bits; a mismatch gives `CMD_DATA_NULL`,
   and the connection closes.

The keystream is a 4-register LFSR, reset to `s1=200, s2=300, s3=400, s4=500` on every auth;
each byte is the low 8 bits of `auth_lfsr_next()`. The exact shifts and masks are in
`core-commands.md` §11.4 (`NG/source/proc.c:2078-2096`). Port that function exactly.

What it gates (source): bit `0x2` is required by `CMD_PROC_SCAN_START/COUNT/GET` and the whole
Turbo Scan family (`NG/source/proc.c:2138-2139`, `:2156`, `:2275`, `:2442`; `NG/source/scan_turbo.c`).
`CMD_PROC_SCAN` (`0xBDAA0009`) and AOB scans are **not** gated in source.

The auth bits are a **global**, reset only when the server starts (`NG/source/server.c:333`),
not per connection. No client in `ref/` implements auth, so this has not been tested.

## 13. Pausing the game (process stop/continue)

Two commands exist. **PS4 Mem uses `0xBDBB0500`.**

### `CMD_DEBUG_PROCESS_STOP` `0xBDBB0500` (preferred)
Source: `NG/include/protocol.h:69`, dispatch `NG/source/debug.c:1097`, handler `NG/source/debug.c:241-275`.

- Request body (**5 bytes**, no struct in protocol.h): `u32 pid`, `u8 state`.
  `state`: 0 = resume, 1 = stop, 2 = kill.
- Reply: `u32 status`: `CMD_SUCCESS`; `CMD_ERROR` if `state > 2` or `pid == 0`; `CMD_DATA_NULL` for a
  null body. Both errors close the connection.
- **Works without an attached debugger**: it calls `kill(pid, sig)` with signals 19 / 17 / 9
  for resume / stop / kill. With a debugger attached, it queues the signal instead.
- The signal numbers are raw in the source; reading 17 = SIGSTOP and 19 = SIGCONT is FreeBSD
  numbering, our inference. `ps4debug-NG/PROTOCOL.md:756` labels them inconsistently.
- Client: PS4CheaterNeo `ProcessExtStop/Resume` sends `(uint)pid, (byte)1/0`
  (`PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.Debug.cs:185-278`).

### `CMD_DEBUG_STOPGO` / `CMD_DEBUG_CONTINUE` `0xBDBB0010` (avoid for pausing)
- libdebug's `ProcessStop/Resume/Kill` send a 4-byte `u32` of 1 / 0 / 2 and require an attached
  debugger (`LD/PS4DBG.Debug.cs:137-182`).
- ps4debug-NG's handler is `debug_continue_handle` (`NG/source/debug.c:996-1006`): it returns
  `CMD_ERROR` and closes the connection if no debugger is attached, and maps 1 to `PT_CONTINUE`
  with signal 17. Whether that actually pauses is unclear (§15), so PS4 Mem doesn't use it to pause.

Both are **untested on the console**. G0/G1 must check that stop and resume work on the game.

## 14. Section selection defaults

- **Build the list** from `CMD_PROC_MAPS`: keep entries with the read bit (`prot & 0x1`), skip
  `end <= start` (`PS4CheaterNeo/PS4CheaterNeo/common/SectionTool.cs:293-302`).
- **Default checked**: writable as well, and either **unnamed** or the name contains any of `anon`,
  `exe`, `Game`, `dlmalloc`, `SmallBlockAllocator`. The name list is PS4 Mem's own default, not
  PS4Cheater's: PS4Cheater has no default list and starts with nothing ticked
  (`PS4Cheater/PS4_Cheater/ProcessManager.cs:433-500`, `mappedSection.Check = false`).
- **Unnamed maps** are anonymous memory, where games keep most of their data. On the console,
  Assassin's Creed III had 61 unnamed rw maps (2.9 GB) and only 9 MB of named, matching sections;
  a name-only filter found nothing. So unnamed writable maps are checked by default.
- The default is only a suggestion: the user can change the selection. The process picker is a
  suggestion too: the `scan` command lists every process and asks.
- For reference, PS4CheaterNeo **hides** sections matching
  `libSce,libc.prx,SceShell,SceLib,SceNp,SceVoice,SceFios,libkernel,SceVdec` by default
  (`PS4CheaterNeo/PS4CheaterNeo/Properties/Settings.settings:110-112`) and skips maps over 64 GB
  (`SectionTool.cs:308`).

## 15. Docs vs source: known disagreements

| # | Docs say | Source says (we follow this) |
|---|---|---|
| 1 | Map entry is 64 bytes, different field order (`ps4debug-NG/PROTOCOL.md:319-324`) | 58 bytes (`NG/include/proc.h:11-17`) |
| 2 | Auth is per connection and resets on disconnect (`PROTOCOL.md:98-101`) | Global, reset only at server start (`NG/source/proc.c:2076`, `NG/source/server.c:333`) |
| 3 | `CMD_PROC_SCAN` and AOB need auth (`PROTOCOL.md:357`, `README.md:179-180`) | Not gated (`NG/source/proc.c:1599-1616`) |
| 4 | Bad magic is "silently dropped" (`PROTOCOL.md` §1.4) | Body isn't consumed, so the stream desyncs (`NG/source/server.c:131-133`) |
| 5 | `0xBDBB0010` value 1 means halted mode (`PROTOCOL.md:729-732`) | `PT_CONTINUE` with signal 17 (`NG/source/debug.c:996-1006`) |
| 6 | `CMD_PROC_SCAN` replies `u32 num` + addresses (`PROTOCOL.md:357-361`) | Addresses + `0xFFFF…` terminator, no count (`NG/source/proc.c:1649-1679`) |
| 7 | SCAN_COUNT/GET reply shapes (`PROTOCOL.md:512-516`) | See §11.1 (`NG/source/proc.c:2274-2484`) |
| 8 | CAPS `engines = 0x7FF` (`PROTOCOL.md:545`) | `0xFFF` (`NG/source/scan_turbo.c:1377-1391`) |
| 9 | PROTOCOL.md file:line citations | Stale; use the citations in this file |

PS4CheaterNeo's libdebug copy names `0xBDAACC02` `SCAN_NEXT` and `0xBDAACC06` `SCAN_END`
(`PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.cs:86-89`). In NG they are
`CMD_PROC_SCAN_COUNT` and `CMD_PROC_NOP` (`NG/include/protocol.h:47`, `:49`). Use NG's meanings.

## 16. Open questions (to settle at test gates, never by guessing)

1. Pause via `0xBDBB0500` (§13): confirm stop and resume on the game.
2. Auth keystream (§12): needed for ConsoleScan (§11); untested. Auth bits are global, so a
   reconnect may keep them (§12).
3. Turbo START resident reply has no leading `CMD_SUCCESS` in the source (§11.2); docs say it does.
4. `PAGE_SIZE` for the debugger build: 0x4000 in `ps4debug-NG/kdebugger/include/proc.h:10`; no define
   found under `NG/include`. Only matters for the legacy scan.
5. `CMD_PROC_INFO` with a bad pid returns `CMD_SUCCESS` and garbage: treat an empty title ID as "unknown".
