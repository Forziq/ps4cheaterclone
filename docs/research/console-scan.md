# Console scan research (CMD_PROC_SCAN and friends)

Project root: `/Users/egorgrigorciuc/Projectsnosync/ps4cheaterclone`. Paths below are relative to the project root (`ref/...`). Line numbers are from the shallow clones in `ref/` as they were on disk when this was written.

Note on paths: the task brief named `ref/PS4CheaterNeo/common/ScanTool.cs` and `ref/PS4CheaterNeo/common/ps4dbg/libdebug/`. The real paths have an extra `PS4CheaterNeo/` level: `ref/PS4CheaterNeo/PS4CheaterNeo/common/...`.

Note on line drift: `ref/ps4debug-NG/PROTOCOL.md` cites `proc.c:1401-1485` for `CMD_PROC_SCAN`. The code is at `proc.c:1599-1679` in the current tree. Use the code line numbers below.

---

## 1. CMD_PROC_SCAN (0xBDAA0009): request, data, response

### 1a. Common packet header (client and server)

`ref/libdebug/PS4DBG/PS4DBG.cs:27-38`
```csharp
        private const uint CMD_PACKET_MAGIC = 0xFFAABBCC;

        //  struct cmd_packet {
        //    uint32_t magic;
        //    uint32_t cmd;
        //    uint32_t datalen;
        //    // (field not actually part of packet, comes after)
        ...
        private const int CMD_PACKET_SIZE = 12;
```
(The same copy is at `ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.cs:27-38`.)

`ref/libdebug/PS4DBG/PS4DBG.cs:399-403`
```csharp
        public struct CMDPacket {
            public uint magic;
            public uint cmd;
            public uint datalen;
        }
```

`ref/ps4debug-NG/debugger/include/protocol.h:96-102`
```c
struct cmd_packet {
    uint32_t magic;
    uint32_t cmd;
    uint32_t datalen;
    void *data;
} __attribute__((packed));
#define CMD_PACKET_SIZE 12
```

`ref/ps4debug-NG/debugger/include/protocol.h:15`
```c
#define PACKET_MAGIC            0xFFAABBCC
```

Status codes:

`ref/ps4debug-NG/debugger/include/protocol.h:87-90`
```c
#define CMD_SUCCESS              0x80000000
#define CMD_ERROR                0xF0000001
#define CMD_DATA_NULL            0xF0000003
```

`ref/libdebug/PS4DBG/PS4DBG.Enums.cs:5-12`
```csharp
        public enum CMD_STATUS : uint {
            CMD_SUCCESS = 0x80000000,
            CMD_ERROR = 0xF0000001,
            CMD_TOO_MUCH_DATA = 0xF0000002,
            CMD_DATA_NULL = 0xF0000003,
            CMD_ALREADY_DEBUG = 0xF0000004,
            CMD_INVALID_INDEX = 0xF0000005
        };
```

Byte order: every field is written with `BitConverter.GetBytes` (client) and copied with `memcpy`/`net_send_all` of packed structs (server). Both assume a little-endian host (PS4 x86-64 and macOS are LE). No `htonl` anywhere. This is an inference from the code, not a documented statement.

Client header send: `ref/libdebug/PS4DBG/PS4DBG.cs:306-362` (the parts that matter):
```csharp
        private void SendCMDPacket(CMDS cmd, int length, params object[] fields) {
            CMDPacket packet = new CMDPacket();
            packet.magic = CMD_PACKET_MAGIC; // Set the magic number for the packet
            packet.cmd = (uint)cmd;          // Set the command identifier
            packet.datalen = (uint)length;   // Set the data length of the packet
            ...
            // Send the Command (CMD) Packet header? to the PS4 System
            SendData(GetBytesFromObject(packet), CMD_PACKET_SIZE);

            // Check if the data byte array is not null, and in case of it not being
            // null, use it and send it's content to the PS4
            if (data != null) SendData(data, length);
        }
```
Each field is converted with `BitConverter.GetBytes`. A `byte` becomes 1 byte, `int`/`uint` 4 bytes, `ulong` 8 bytes. The fields are concatenated in order, with no padding.

`ref/libdebug/PS4DBG/PS4DBG.cs:247-255, 297-301` (status read):
```csharp
        private void CheckStatus() {
            CMD_STATUS status = ReceiveStatus();
            if (status != CMD_STATUS.CMD_SUCCESS) {
                throw new Exception(
                    $"libdebug: Exception in CheckStatus()!\n"+
                    $"Status ({(uint)status:X})"
                );
            }
        }
        ...
        private CMD_STATUS ReceiveStatus() {
            byte[] status = new byte[4];
            sock.Receive(status, 4, SocketFlags.None);
            return (CMD_STATUS)BitConverter.ToUInt32(status, 0);
        }
```

### 1b. Request body (CMD_PROC_SCAN_PACKET_SIZE = 10)

Client constant: `ref/libdebug/PS4DBG/PS4DBG.Proc.cs:20`
```csharp
        private const int CMD_PROC_SCAN_PACKET_SIZE = 10;
```

Server struct: `ref/ps4debug-NG/debugger/include/protocol.h:278-284`
```c
struct cmd_proc_scan_packet {
   uint32_t pid;
   uint8_t valueType;
   uint8_t compareType;
   uint32_t lenData;
} __attribute__((packed));
#define CMD_PROC_SCAN_PACKET_SIZE 10
```

Layout: `pid` u32 at offset 0, `valueType` u8 at 4, `compareType` u8 at 5, `lenData` u32 at 6. Total 10 bytes, packed, no padding. Same layout as the base ps4debug at `ref/ps4debug/debugger/include/protocol.h:195` (not re-checked line by line).

### 1c. Trailing data (value bytes and the "between" second value)

Client: `ref/libdebug/PS4DBG/PS4DBG.Proc.cs:309-416` (the `ScanProcess<T>` function). The key lines are:
```csharp
        public List<ulong> ScanProcess<T>(int pid, ScanCompareType compareType, T value, T extraValue = default) {
            ...
            // send packet
            SendCMDPacket(CMDS.CMD_PROC_SCAN, CMD_PROC_SCAN_PACKET_SIZE, pid, (byte)valueType, (byte)compareType, (int)(extraValue == null ? typeLength : typeLength * 2));
            CheckStatus();

            SendData(valueBuffer, typeLength);
            if (extraValueBuffer != null) {
                SendData(extraValueBuffer, typeLength);
            }

            CheckStatus();
```
Notes:
- `lenData` = `typeLength` when `extraValue == null`, otherwise `typeLength * 2`.
- The value bytes come first, then the extra value bytes. Each one is `typeLength` long.
- Reading the code: `T` is unconstrained, so `extraValue == null` is a boxed comparison. For a value type it is always false. So for a numeric `T`, `lenData` is always `2*typeLength` and an extra value is always sent (the default 0 if none was passed). This is my reading of the code, not something I ran. Open question below.
- Byte[] path: `case byte[] ba: valueType = ScanValueType.valTypeArrBytes; valueBuffer = ba; typeLength = valueBuffer.Length;` (`PS4DBG.Proc.cs:400-404`). `extraValue` is `null` for `byte[]`, so `lenData = typeLength`.

Server reads the trailing data: `ref/ps4debug-NG/debugger/source/proc.c:1607-1620`
```c
    size_t valueLength = proc_scan_getSizeOfValueType(sp->valueType);
    if (!valueLength) {
       valueLength = sp->lenData;
    }

    unsigned char *data = (unsigned char *)net_alloc_buffer(sp->lenData);
    if (!data) {
       net_send_int32(fd, CMD_DATA_NULL);
       return 1;
    }

    net_send_int32(fd, CMD_SUCCESS);

    net_recv_all(fd, data, sp->lenData, 1);
```
And the extra value is located as: `ref/ps4debug-NG/debugger/source/proc.c:1647`
```c
    unsigned char *pExtraValue = valueLength == sp->lenData ? NULL : &data[valueLength];
```
So the server takes the first `valueLength` bytes as the scan value and the next `valueLength` bytes as the extra value (the second bound for "between", or the reference for the increased/decreased-by modes).

### 1d. Exact send / receive sequence (client, libdebug)

1. Client sends 12-byte header with `cmd=0xBDAA0009`, `datalen=10`, then 10 bytes: `pid` (i32), `valueType` (u8), `compareType` (u8), `lenData` (i32 for libdebug, `int` cast).
2. Client `CheckStatus()`: reads 4 bytes, must be `0x80000000`.
3. Client sends `valueBuffer` (`typeLength` bytes), then `extraValueBuffer` (`typeLength` bytes) if non-null.
4. Client `CheckStatus()`.
5. Client sets `sock.ReceiveTimeout = Int32.MaxValue` and loops `ReceiveData(8)` until it reads `0xFFFFFFFFFFFFFFFF`.

`ref/libdebug/PS4DBG/PS4DBG.Proc.cs:420-435`
```csharp
            // receive results
            int save = sock.ReceiveTimeout;
            sock.ReceiveTimeout = Int32.MaxValue;
            List<ulong> results = new List<ulong>();
            while (true) {
                ulong result = BitConverter.ToUInt64(ReceiveData(sizeof(ulong)), 0);
                if (result == 0xFFFFFFFFFFFFFFFF) {
                    break;
                }

                results.Add(result);
            }

            sock.ReceiveTimeout = save;
```

Server side, in order:
1. Reads the 10-byte body (`packet->data`). Returns `CMD_DATA_NULL` if `!sp`.
2. `net_send_int32(fd, CMD_SUCCESS)` (`proc.c:1618`). This matches client step 2.
3. `net_recv_all(fd, data, sp->lenData, 1)` (`proc.c:1620`). This matches client step 3.
4. `sys_proc_cmd(sp->pid, SYS_PROC_VM_MAP, &args)` twice, to get the map list (`proc.c:1622-1643`). On failure it sends `CMD_ERROR` (`0xF0000001`).
5. `net_send_int32(fd, CMD_SUCCESS)` (`proc.c:1645`). This is the client's second `CheckStatus()`.
6. Scans the maps and streams results (see 1e).
7. Sends the terminator `0xFFFFFFFFFFFFFFFF` (`proc.c:1671-1672`).

### 1e. Result stream and server loop

Server: `ref/ps4debug-NG/debugger/source/proc.c:1649-1679`
```c
    for (size_t i = 0; i < args.num; i++) {
       if ((args.maps[i].prot & PROT_READ) != PROT_READ) {
            continue;
       }

       uint64_t sectionStartAddr = args.maps[i].start;
       size_t sectionLen = args.maps[i].end - sectionStartAddr;

       for (uint64_t j = 0; j < sectionLen; j += valueLength) {
            if(j == 0 || !(j % PAGE_SIZE)) {
                sys_proc_rw(sp->pid, sectionStartAddr, scanBuffer, PAGE_SIZE, 0);
            }

            uint64_t scanOffset = j % PAGE_SIZE;
            uint64_t curAddress = sectionStartAddr + j;

            if (proc_scan_legacy_compareValues(sp->compareType, sp->valueType, valueLength, data, scanBuffer + scanOffset, pExtraValue)) {
                net_send_all(fd, &curAddress, sizeof(uint64_t));
            }
       }
    }

    uint64_t endflag = 0xFFFFFFFFFFFFFFFF;
    net_send_all(fd, &endflag, sizeof(uint64_t));
```
- Results are absolute 8-byte addresses, one per match, sent immediately. No count prefix and no batch header. The terminator is `0xFFFFFFFFFFFFFFFF`.
- Scope: the whole process. It walks every VM map from `SYS_PROC_VM_MAP`, skips maps without `PROT_READ`, and does not check `end <= start` (`sectionLen = end - start` is unsigned, so a bad map would overflow).
- Step is `valueLength` (not `alignment`). Matches are checked at every `valueLength`-byte offset from each section start.
- `proc.c:1599-1679` has no auth check (see 3c).

Conflict: `ref/ps4debug-NG/PROTOCOL.md:357-361` says:
```
#### `CMD_PROC_SCAN = 0xBDAA0009` (proc.c:1401-1485) - **requires auth bit 1**
Legacy single-pass scan; superseded by the `SCAN_START/COUNT/GET` trio.
- **Request body:** `struct cmd_proc_scan_packet` (10 bytes).
- **Trailing data:** `lenData` bytes of comparison value(s).
- **Response:** `uint32_t num`, then `num × uint64_t` addresses.
```
The code above has no `uint32_t num` prefix and no auth check. Code wins (open question Q1).

### 1f. Legacy compare function (used by CMD_PROC_SCAN)

`ref/ps4debug-NG/debugger/source/proc.c:946-955` (ExactValue):
```c
        case cmpTypeExactValue:
        {

           if (valTypeLength <= 1) return false;
           for (size_t j = 0; j < valTypeLength - 1; j++) {
              if (pScanValue[j] != pMemoryValue[j]) return false;
           }
           return true;
        }
```
Observation: the loop stops at `valTypeLength - 1`, so the last byte of an exact match is never compared. For a 4-byte value only 3 bytes are checked. The START/COUNT path uses a different compare (`proc_scan_compareValues`, `proc.c:1166`), which does not have this loop. Open question Q2.

`ref/ps4debug-NG/debugger/source/proc.c:1007-1015` (ValueBetween; bounds are exclusive and either order works):
```c
        case cmpTypeValueBetween:
        {

           #define _BETWEEN(TY) do { \
              TY mv = *(TY *)pMemoryValue; \
              TY sv = *(TY *)pScanValue; \
              TY ev = *(TY *)pExtraValue; \
              ...
              if (ev > sv) return mv > sv && mv < ev; \
              return mv < sv && mv > ev; \
```

### 1g. Size helper

`ref/ps4debug-NG/debugger/source/proc.c:854-875`
```c
size_t proc_scan_getSizeOfValueType(cmd_proc_scan_valuetype valType) {
    switch (valType) {
       case valTypeUInt8:
       case valTypeInt8:
          return 1;
       case valTypeUInt16:
       case valTypeInt16:
          return 2;
       case valTypeUInt32:
       case valTypeInt32:
       case valTypeFloat:
          return 4;
       case valTypeUInt64:
       case valTypeInt64:
       case valTypeDouble:
          return 8;
       case valTypeArrBytes:
       case valTypeString:
       default:
          return NULL;
    }
}
```

### 1h. Dispatch

`ref/ps4debug-NG/debugger/source/proc.c:2510`
```c
        case CMD_PROC_SCAN:           return proc_scan_handle(fd, packet);
```

### 1i. Use in PS4Cheater and PS4CheaterNeo

Neither UI calls `ScanProcess`. Both do the scan on the PC.

- PS4CheaterNeo: `ref/PS4CheaterNeo/PS4CheaterNeo/Query.cs:800-816` (read selection) and `ref/PS4CheaterNeo/PS4CheaterNeo/Query.cs:1083-1085` (per-address re-read):
```csharp
                                if (sectionStart != null)
                                {
                                    ulong bufferSize = sectionEnd.Start + (ulong)sectionEnd.Length - sectionStart.Start;
                                    if (!isCompareFirst)
                                    {
                                        if (sectionStart.IsKernel)
                                            buffer = PS4Tool.KernelReadMemory(sectionStart.Start, (int)bufferSize);
                                        else
                                            buffer = PS4Tool.ReadMemory(sectionStart.PID, sectionStart.Start, (int)bufferSize);
                                    }
```
  `PS4Tool.ReadMemory` is in `ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/PS4Tool.cs:328` and `:386-387` (routes to `ps4s[current].ReadMemory(processID, address, length)`, or `KernelReadMemory` for kernel sections). This is a per-range read over the wire with the `CMD_PROC_READ` path. The comparison (`ScanTool.Comparer`, `ScanTool.cs:376`) runs on the PC.
- PS4Cheater (ctn123): `ref/PS4Cheater/PS4_Cheater/ProcessManager.cs:219-235`:
```csharp
            const int buffer_length = 1024 * 1024 * 128;

            while (length != 0)
            {
                int cur_length = buffer_length;
                ...
                byte[] buffer = memoryHelper.ReadMemory(address, (int)cur_length, thread_id);
```
  Read in 128 MiB chunks per mapped section, then compared on the PC. `MemoryHelper.ReadMemory` is at `ref/PS4Cheater/PS4_Cheater/MemoryHelper.cs:141-146` and calls `ps4[thread_id].ReadMemory(ProcessID, address, length)`.

- `ref/PS4Cheater-a0zhar/` and `ref/ps4debug/`: no `ScanProcess` or `CMD_PROC_SCAN` call sites (grep for `ScanProcess|PROC_SCAN|BDAA0009|ScanValueType|ScanCompareType`).

---

## 2. Enums: ScanValueType and ScanCompareType

### 2a. libdebug (client)

`ref/libdebug/PS4DBG/PS4DBG.Proc.cs:279-307`
```csharp
        public enum ScanValueType : byte {
            valTypeUInt8 = 0,
            valTypeInt8,
            valTypeUInt16,
            valTypeInt16,
            valTypeUInt32,
            valTypeInt32,
            valTypeUInt64,
            valTypeInt64,
            valTypeFloat,
            valTypeDouble,
            valTypeArrBytes,
            valTypeString
        }

        public enum ScanCompareType : byte {
            ExactValue = 0,
            FuzzyValue,
            BiggerThan,
            SmallerThan,
            ValueBetween,
            IncreasedValue,
            IncreasedValueBy,
            DecreasedValue,
            DecreasedValueBy,
            ChangedValue,
            UnchangedValue,
            UnknownInitialValue
        }
```
Values: ScanValueType 0..11 (UInt8=0, Int8=1, UInt16=2, Int16=3, UInt32=4, Int32=5, UInt64=6, Int64=7, Float=8, Double=9, ArrBytes=10, String=11). ScanCompareType 0..11 (Exact=0, Fuzzy=1, BiggerThan=2, SmallerThan=3, ValueBetween=4, IncreasedValue=5, IncreasedValueBy=6, DecreasedValue=7, DecreasedValueBy=8, ChangedValue=9, UnchangedValue=10, UnknownInitialValue=11).

The same enums are in `ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.Proc.cs:598-628` and `ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libframe4/FRAME4.Proc.cs:637-667` (identical values).

### 2b. ps4debug-NG server

`ref/ps4debug-NG/debugger/include/protocol.h:246-276`
```c
typedef enum cmd_proc_scan_valuetype {
   valTypeUInt8 = 0,
   valTypeInt8,
   valTypeUInt16,
   valTypeInt16,
   valTypeUInt32,
   valTypeInt32,
   valTypeUInt64,
   valTypeInt64,
   valTypeFloat,
   valTypeDouble,
   valTypeArrBytes,
   valTypeString
} __attribute__((__packed__)) cmd_proc_scan_valuetype;

typedef enum cmd_proc_scan_comparetype {
   cmpTypeExactValue = 0,
   cmpTypeFuzzyValue,
   cmpTypeBiggerThan,
   cmpTypeSmallerThan,
   cmpTypeValueBetween,
   cmpTypeIncreasedValue,
   cmpTypeIncreasedValueBy,
   cmpTypeDecreasedValue,
   cmpTypeDecreasedValueBy,
   cmpTypeChangedValue,
   cmpTypeUnchangedValue,
   cmpTypeUnknownInitialValue,

   cmpTypeUnknownInitialLowValue
} __attribute__((__packed__)) cmd_proc_scan_comparetype;
```

Values: ScanValueType 0..11, same as the client. ScanCompareType 0..12. Index 12 = `cmpTypeUnknownInitialLowValue`.

Mismatch: the server has a 13th compare type (12, `cmpTypeUnknownInitialLowValue`). No C# enum has it. The server treats it like `UnknownInitialValue` (returns true; `proc.c:1156-1161`, and `proc.c:1531`). The C# client cannot request it. Other compare values match 0..11. The START/COUNT tables in the server (`proc.c:2145-2153`) have 16 entries, so 12 is in range there too.

The base ps4debug (`ref/ps4debug/debugger/include/protocol.h:165-193`) has the same enums (not re-checked line by line).

ScanType and CompareType in PS4CheaterNeo's UI are NOT wire values:
`ref/PS4CheaterNeo/PS4CheaterNeo/common/ScanTool.cs:12-34`
```csharp
    public enum ScanType
    {
        [Description("Byte")]
        Byte_   = 0,
        [Description("2 Bytes")]
        Bytes_2 = 2,
        [Description("4 Bytes")]
        Bytes_4 = 4,
        [Description("8 Bytes")]
        Bytes_8 = 6,
        [Description("Float")]
        Float_  = 8,
        [Description("Double")]
        Double_ = 9,
        Hex     = 10,
        [Description("String")]
        String_ = 11,
        Group,
        [Description("Auto determine numeric")]
        AutoNumeric,
        [Description("SCAN for Hidden Sections")]
        HiddenSections,
    }
```
Its values (0, 2, 4, 6, 8, 9, 10, 11, ...) do not match the wire enum. Do not send them to the server. `ScanTool.cs:36-55` `CompareType` is a plain UI enum with the same order as the wire enum, but it is not sent on the wire.

---

## 3. Iterative trio: CMD_PROC_SCAN_START 0xBDAACC01 / SCAN_COUNT 0xBDAACC02 / SCAN_GET 0xBDAACC03

### 3a. IDs and structs

`ref/ps4debug-NG/debugger/include/protocol.h:43-49`
```c
#define CMD_PROC_SCAN_AOB       0xBDAA0501
#define CMD_PROC_SCAN_AOB_MULTI 0xBDAA0502
#define CMD_PROC_AUTH           0xBDAACCFF
#define CMD_PROC_SCAN_START     0xBDAACC01
#define CMD_PROC_SCAN_COUNT     0xBDAACC02
#define CMD_PROC_SCAN_GET       0xBDAACC03
#define CMD_PROC_NOP            0xBDAACC06
```

`ref/ps4debug-NG/debugger/include/protocol.h:286-304`
```c
struct cmd_proc_scan_start_packet {
    uint32_t pid;
    uint64_t address;
    uint32_t length;
    uint8_t  valueType;
    uint8_t  compareType;
    uint8_t  alignment;
    uint32_t lenData;
} __attribute__((packed));
#define CMD_PROC_SCAN_START_PACKET_SIZE 23

struct cmd_proc_scan_count_packet {
    uint32_t pid;
    uint64_t base_address;
    uint8_t  valueType;
    uint8_t  compareType;
    uint32_t lenData;
} __attribute__((packed));
#define CMD_PROC_SCAN_COUNT_PACKET_SIZE 18
```

`ref/ps4debug-NG/debugger/include/protocol.h:431-435`
```c
struct cmd_proc_scan_get_packet {
    uint32_t pid;
    uint32_t count;
} __attribute__((packed));
#define CMD_PROC_SCAN_GET_PACKET_SIZE 8
```

### 3b. SCAN_START sequence (proc.c:2155-2272)

Key lines, verbatim:
```c
int proc_scan_start_handle(int fd, struct cmd_packet *packet) {
    SCAN_REQUIRE_AUTH(fd);
    struct cmd_proc_scan_start_packet *sp = (struct cmd_proc_scan_start_packet *)packet->data;
    ...
    size_t step = sp->alignment ? sp->alignment : valueLength;
    ...
    net_send_int32(fd, CMD_SUCCESS);          // proc.c:2195

    if (pattern) {
        net_recv_all(fd, pattern, sp->lenData, 1);   // proc.c:2198
    }
    if (mask) {
        net_recv_all(fd, mask, valueLength, 1);      // proc.c:2201
    }
    ...
    net_send_int32(fd, CMD_SUCCESS);          // proc.c:2222
    ...
    uint64_t addr = sp->address;
    uint32_t remaining = sp->length;
    ...
    while (remaining > 0) {
        ...
        sys_proc_rw(sp->pid, addr, readBuf, toRead, 0);
        ...
                uint32_t offset = (uint32_t)((addr + j) - sp->address);
                memcpy(resultBuf + 8 + resultLen, &offset, 4);
                memcpy(resultBuf + 8 + resultLen + 4, readBuf + j, valueLength);
                resultLen += 4 + valueLength;
        ...
    }

    if (resultLen) {
        memcpy(resultBuf, &resultLen, 8);
        net_send_all(fd, resultBuf, (int)(resultLen + 8));
    }

    uint64_t sentinel = 0xFFFFFFFFFFFFFFFFULL;   // proc.c:2262
    net_send_all(fd, &sentinel, 8);
    ...
    net_send_int32(fd, CMD_SUCCESS);             // proc.c:2270
    return 0;
}
```
Sequence:
1. Client: header (`cmd=0xBDAACC01`, `datalen=23`) + 23-byte body (`pid, address, length, valueType, compareType, alignment, lenData`).
2. Server: `CMD_SUCCESS`. Client sends `lenData` bytes (value, plus extra for "between"), and `valueLength` bytes of mask if `valueType==ArrBytes`.
3. Server: `CMD_SUCCESS`. Scans `[address, address+length)`.
4. Server streams batches. Each batch is `uint64 payloadLen` then `payloadLen` bytes of records. Each record is `uint32 offset` (relative to `sp->address`) then `valueLength` bytes of the value at that offset. Batches are flushed at about `0xFFE8 - valueLength` bytes.
5. Server sends the sentinel `0xFFFFFFFFFFFFFFFF` (8 bytes).
6. Server sends `CMD_SUCCESS`.

So the trio's START returns (offset, value) runs, not 8-byte addresses. Only the classic `CMD_PROC_SCAN` returns addresses.

### 3c. SCAN_COUNT sequence (proc.c:2274-2439)

- Body: 18 bytes `{pid, base_address, valueType, compareType, lenData}`.
- The same first steps as START: `SCAN_REQUIRE_AUTH`, `CMD_SUCCESS`, then reads `lenData` bytes (+ mask), then `CMD_SUCCESS` (`proc.c:2335`).
- Then a client-driven loop (`proc.c:2351-2354`):
```c
    while (1) {
        uint32_t chunkLen = 0;
        net_recv_all(fd, &chunkLen, 4, 1);
        if (chunkLen == 0xFFFFFFFF) break;
        if (chunkLen == 0) {

            uint64_t sentinel = 0xFFFFFFFFFFFFFFFFULL;
            net_send_all(fd, &sentinel, 8);
            continue;
        }
        if (chunkLen > 0x20000) break;
```
- Client sends `uint32 chunkLen` then `chunkLen` bytes of entries. Entry = `uint32 offset` (+ `valueLength` previous value when the compare needs it: `entrySize = includesPrevValue ? (4 + valueLength) : 4`, `proc.c:2347`). The address is `base_address + offset` (`proc.c:2382`).
- Server replies per chunk with batches (same `uint64 len` + records format as START, `proc.c:2410-2423`) and then a sentinel (`proc.c:2426-2427`).
- Client ends with `chunkLen = 0xFFFFFFFF`. Server sends `CMD_SUCCESS` (`proc.c:2437`).
- The PROTOCOL.md statement "Response: `uint32_t num_results`" (`ref/ps4debug-NG/PROTOCOL.md:512`) is NOT what the code does (open question Q3).

### 3d. SCAN_GET sequence (proc.c:2441-2484)

Verbatim, the request handling and sentinel:
```c
int proc_scan_get_handle(int fd, struct cmd_packet *packet) {
    SCAN_REQUIRE_AUTH(fd);
    struct cmd_proc_scan_get_packet *gp = (struct cmd_proc_scan_get_packet *)packet->data;
    if (!gp || gp->count == 0) {
        net_send_int32(fd, CMD_DATA_NULL);
        return 1;
    }

    size_t entriesLen = (size_t)gp->count * 12;
    ...
    net_send_int32(fd, CMD_SUCCESS);
    net_recv_all(fd, entries, (int)entriesLen, 1);

    for (uint32_t i = 0; i < gp->count; i++) {
        uint64_t addr;
        uint32_t length;
        memcpy(&addr, entries + 12 * i, 8);
        memcpy(&length, entries + 12 * i + 8, 4);

        while (length > 0) {
            uint32_t toRead = length > SCAN_IO_CHUNK ? SCAN_IO_CHUNK : length;
            memset(buf, 0, toRead);
            sys_proc_rw(gp->pid, addr, buf, toRead, 0);
            net_send_all(fd, buf, (int)toRead);
            addr += toRead;
            length -= toRead;
        }
    }

    uint64_t sentinel = 0xFFFFFFFFFFFFFFFFULL;
    net_send_all(fd, &sentinel, 8);
```
- Request: `{u32 pid; u32 count;}` (8 bytes), then `count` entries of 12 bytes each: `{u64 addr; u32 length;}`.
- Response: `CMD_SUCCESS`, then the raw bytes of each range in order, then the 8-byte sentinel. There is no `uint32 actual` and no `uint64` address list, and no trailing `CMD_SUCCESS`. This is a bulk memory read, not a survivor fetch.
- `ref/ps4debug-NG/PROTOCOL.md:514-516` says: "Response: `CMD_SUCCESS`, `uint32_t actual`, then `actual × uint64_t`". This does not match the code (open question Q3).

### 3e. Auth gate for the trio

`ref/ps4debug-NG/debugger/source/proc.c:2137-2139`
```c
extern uint32_t g_proc_auth_bits;
#define SCAN_REQUIRE_AUTH(fd) \
    do { if (!(g_proc_auth_bits & 2)) { net_send_int32((fd), CMD_DATA_NULL); return 1; } } while (0)
```
Used at `proc.c:2156` (START), `:2275` (COUNT), `:2442` (GET).

### 3f. Is any C# client using the trio?

No. `grep` for `BDAACC` across all `ref/` `.cs` files finds only one hit:

`ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.cs:86-89`
```csharp
            CMD_PROC_SCAN_START = 0xBDAACC01,
            CMD_PROC_SCAN_NEXT  = 0xBDAACC02,
            CMD_PROC_SCAN_END   = 0xBDAACC06,
            CMD_PROC_SCAN_FF    = 0xBDAACCFF,
```
These names are defined and never used. `0xBDAACC06` is `CMD_PROC_NOP` in the server (`protocol.h:49`), not a scan end. The official `ref/libdebug/PS4DBG/PS4DBG.Enums.cs:14-64` has no `BDAACC` entries at all.

`ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libframe4/FRAME4.cs:79-80` uses different IDs for a different protocol:
```csharp
            CMD_PROC_SCAN_GET_RESULTS = 0xBDAA000D,
            CMD_PROC_SCAN_COUNT_RESULTS = 0xBDAA000E,
```
In ps4debug-NG, `0xBDAA000E` is `CMD_PROC_ALLOC_HINTED` (`protocol.h`, see the list at `PROTOCOL.md:895`), and `0xBDAA000D` is not defined. Do not reuse FRAME4's IDs against ps4debug-NG. FRAME4 also uses a 14-byte `CMD_PROC_SCAN` body (`FRAME4.Proc.cs:27`, `:774`):
```csharp
        private const int CMD_PROC_SCAN_PACKET_SIZE = 14;
        ...
            SendCMDPacket(CMDS.CMD_PROC_SCAN, CMD_PROC_SCAN_PACKET_SIZE, pid, firstScan, (byte)valueType, (byte)compareType, (uint)(extraValue == null ? typeLength : typeLength * 2));
```
This is incompatible with the 10-byte struct in ps4debug-NG (`protocol.h:278-284`).

---

## 4. Turbo Scan (0xBDAACC10 to 0xBDAACC17)

### 4a. IDs and where they are defined

No macros exist. The IDs are raw literals in the dispatcher, `ref/ps4debug-NG/debugger/source/proc.c:2493-2500`:
```c
        case 0xBDAACC10u:             return proc_turboscan_caps_handle(fd, packet);
        case 0xBDAACC11u:             return proc_turboscan_start_handle(fd, packet, client_idx);
        case 0xBDAACC12u:             return proc_turboscan_count_handle(fd, packet, client_idx);
        case 0xBDAACC13u:             return proc_turboscan_get_handle(fd, packet, client_idx);
        case 0xBDAACC14u:             return proc_turboscan_end_handle(fd, packet, client_idx);
        case 0xBDAACC15u:             return proc_turboscan_config_handle(fd, packet);
        case 0xBDAACC16u:             return proc_turboscan_regions_handle(fd, packet);
        case 0xBDAACC17u:             return proc_turboscan_cancel_handle(fd, packet);
```
`ref/ps4debug-NG/debugger/include/protocol.h:305-316` has the TSE_ engine bits, not the IDs. The turbo prototypes are in `ref/ps4debug-NG/debugger/include/scan_turbo.h:48-55`. `scan_turbo.h` does not define the IDs.

| ID | Name (from PROTOCOL.md / README) | Handler | Auth |
|---|---|---|---|
| 0xBDAACC10 | CAPS | `proc_turboscan_caps_handle` (scan_turbo.c:1377) | no |
| 0xBDAACC11 | START | `proc_turboscan_start_handle` (scan_turbo.c:1462) | yes |
| 0xBDAACC12 | COUNT (rescan) | `proc_turboscan_count_handle` (scan_turbo.c:1709) | yes |
| 0xBDAACC13 | GET | `proc_turboscan_get_handle` (scan_turbo.c:1984) | yes |
| 0xBDAACC14 | END | `proc_turboscan_end_handle` (scan_turbo.c:2063) | yes |
| 0xBDAACC15 | CONFIG | `proc_turboscan_config_handle` (scan_turbo.c:1342) | yes |
| 0xBDAACC16 | REGIONS (classify) | `proc_turboscan_regions_handle` (scan_turbo.c:1398) | yes |
| 0xBDAACC17 | CANCEL | `proc_turboscan_cancel_handle` (scan_turbo.c:1365) | yes |

Names come from `ref/ps4debug-NG/PROTOCOL.md:541-596` and the table at `PROTOCOL.md:909-916`. The README uses `CMD_PROC_TURBOSCAN_CAPS` and `CMD_PROC_TURBOSCAN_GET` (`README.md:185, 196`), but those macros are not in the code.

### 4b. CAPS (detection)

`ref/ps4debug-NG/debugger/source/scan_turbo.c:1377-1391`
```c
int proc_turboscan_caps_handle(int fd, struct cmd_packet *packet) {
    (void)packet;
    struct cmd_proc_turboscan_caps_response resp;
    memset(&resp, 0, sizeof(resp));
    resp.version     = 1;
    resp.engines     = TSE_SIMD_COMPARE | TSE_SERVER_RESIDENT | TSE_SNAPSHOT
                     | TSE_SNAPSHOT_SEGMENTS | TSE_SNAPSHOT_CONFIG | TSE_SNAPSHOT_FIRST
                     | TSE_SNAPSHOT_PREVIOUS | TSE_ALIASING | TSE_PARALLEL_COMPARE
                     | TSE_RESCAN_ALIASING | TSE_FLOAT_POLICY
                     | TSE_COMPACT_SIMPLE_SNAPSHOT;
    resp.max_threads = TS_WORKER_THREADS;
    net_send_int32(fd, CMD_SUCCESS);
    net_send_all(fd, &resp, sizeof(resp));
    return 0;
}
```
`ref/ps4debug-NG/debugger/source/scan_turbo.c:78`: `#define TS_WORKER_THREADS 4`.

Request: no body. Response: `CMD_SUCCESS` (4 bytes) then the 16-byte struct.

`ref/ps4debug-NG/debugger/include/protocol.h:349-354`
```c
struct cmd_proc_turboscan_caps_response {
    uint32_t version;
    uint32_t engines;
    uint32_t max_threads;
    uint32_t reserved;
} __attribute__((packed));
```

TSE engine bits, `ref/ps4debug-NG/debugger/include/protocol.h:306-317`:
```c
#define TSE_SIMD_COMPARE      0x00000001u
#define TSE_ALIASING          0x00000002u
#define TSE_SERVER_RESIDENT   0x00000004u
#define TSE_SNAPSHOT          0x00000008u
#define TSE_SNAPSHOT_SEGMENTS 0x00000010u
#define TSE_SNAPSHOT_CONFIG   0x00000020u
#define TSE_SNAPSHOT_FIRST    0x00000040u
#define TSE_SNAPSHOT_PREVIOUS 0x00000080u
#define TSE_PARALLEL_COMPARE  0x00000100u
#define TSE_RESCAN_ALIASING   0x00000200u
#define TSE_FLOAT_POLICY      0x00000400u
#define TSE_COMPACT_SIMPLE_SNAPSHOT 0x00000800u
```

Mismatch: the code sets all 12 bits, so `engines = 0xFFF`. `ref/ps4debug-NG/PROTOCOL.md:545-546` says "A release build reports `engines = 0x7FF`". The doc is stale, or the code changed (open question Q4).

Turbo TS_ request flags, `ref/ps4debug-NG/debugger/include/protocol.h:319-330`:
```c
#define TS_USE_ALIASING           0x00000001u
#define TS_SERVER_RESIDENT        0x00000002u
#define TS_SNAPSHOT               0x00000004u
#define TS_SNAPSHOT_INCLUDE_ZEROS 0x00000008u
#define TS_SNAPSHOT_SEGMENTS      0x00000010u
#define TS_SNAPSHOT_KEEP_FIRST    0x00000020u
#define TS_SNAPSHOT_KEEP_PREVIOUS 0x00000040u
#define TS_PARALLEL_COMPARE       0x00000080u
#define TS_RESCAN_ALIASING        0x00000100u
#define TS_FLOAT_SIMPLE           0x00000200u
#define TS_FLOAT_EXACT            0x00000400u
#define TS_FLOAT_EXPONENT_SHIFT   16u
#define TS_FLOAT_EXPONENT_MASK    0x007F0000u
```

### 4c. Turbo request structs (protocol.h)

`ref/ps4debug-NG/debugger/include/protocol.h:333-342` (START):
```c
struct cmd_proc_turboscan_start_packet {
    uint32_t pid;
    uint64_t address;
    uint32_t length;
    uint8_t  valueType;
    uint8_t  compareType;
    uint8_t  alignment;
    uint32_t lenData;
    uint32_t flags;
} __attribute__((packed));
```
Size 27 bytes (classic START is 23, plus `flags`). This is my arithmetic from the packed layout.

`protocol.h:372-379` (COUNT): `{u32 pid; u64 base_address; u8 valueType; u8 compareType; u32 lenData; u32 flags;}`, 22 bytes.

`protocol.h:386-390` (GET):
```c
struct cmd_proc_turboscan_get_packet {
    uint32_t start_index;
    uint32_t count;
    uint32_t flags;
} __attribute__((packed));
```
12 bytes.

`protocol.h:381-384` (resident summary):
```c
struct cmd_proc_turboscan_resident_summary {
    uint32_t resident_stored;
    uint64_t count;
} __attribute__((packed));
```
`protocol.h:344-347` (CONFIG): `{u32 ram_thresh_mb; u32 spill_path_len;}` then path bytes. `protocol.h:356-361` (REGIONS request, 16 bytes): `{u32 pid; u32 max; u32 probe_bytes; u32 reserved;}`. `protocol.h:363-370` (region info, 32 bytes): `{u64 start; u64 end; u32 prot; u32 flags; u32 mbps; u32 reserved;}`.

### 4d. Wire sequence for START (scan_turbo.c:1462-1707, key lines)

- Auth gate: `scan_turbo.c:1463`: `if (!(g_proc_auth_state & 2)) { net_send_int32(fd, CMD_DATA_NULL); return 1; }`
- `scan_turbo.c:1489`: `net_send_int32(fd, CMD_SUCCESS);` then `:1490` `if (pattern) net_recv_all(fd, pattern, (int)sp->lenData, 1);`
- Snapshot branch (`want_snapshot`) at `scan_turbo.c:1528-1537`. `scan_turbo.c:1528`: `net_send_int32(fd, CMD_SUCCESS);`. It reads `nseg` at `:1537` only when `TS_SNAPSHOT_SEGMENTS` is set.
- Known-value path. `scan_turbo.c:1602-1609`:
```c
    int resident = (sp->flags & TS_SERVER_RESIDENT) != 0;
    ...
    int use_segments = resident && (sp->flags & TS_SNAPSHOT_SEGMENTS) != 0;
    ...
    if (use_segments) {
        net_recv_all(fd, &nseg, 4, 1);
```
- Resident success, `scan_turbo.c:1652-1664`:
```c
        if (stored) {
            struct cmd_proc_turboscan_resident_summary sum;
            sum.resident_stored = 1;
            sum.count = s->count;
            net_send_all(fd, &sum, sizeof(sum));
            ...
            net_send_int32(fd, CMD_SUCCESS);
            return 0;
        }
```
  No survivors are streamed. Fetch them with GET.
- Resident failed, `scan_turbo.c:1666-1670`: `turboscan_session_free_idx(client_idx);` then `sum.resident_stored = 0; sum.count = 0; net_send_all(fd, &sum, sizeof(sum));`. If `use_segments` is set, it sends a sentinel and `CMD_SUCCESS` and returns (`:1672-1683`). Otherwise it falls through to the streaming path.
- Streaming path (non-resident, or resident fallback), `scan_turbo.c:1687-1705`: runs the scan and streams the same `(offset, value)` runs as classic START, then `0xFFFF...` sentinel (`:1696-1697`), then `net_send_int32(fd, CMD_SUCCESS)` (`:1705`).
- Documented wire, `ref/ps4debug-NG/PROTOCOL.md:553-558`: "if `TS_SERVER_RESIDENT` was requested, `CMD_SUCCESS` then `struct cmd_proc_turboscan_resident_summary`". The code has no `CMD_SUCCESS` before the summary. It does send a summary even when `resident_stored == 0`, and then a run stream. A client must always read the summary first when `TS_SERVER_RESIDENT` is set (open question Q5).

### 4e. GET (scan_turbo.c:1984-2061)

Verbatim key lines:
```c
    struct turboscan_session *s = fs_session_get(client_idx);
    if (!s || !s->in_use || (s->mode == TS_MODE_LIST && !s->buf)) {
        net_send_int32(fd, CMD_SUCCESS);
        uint32_t zero = 0;
        net_send_all(fd, &zero, 4);
        net_send_int32(fd, CMD_SUCCESS);
        return 0;
    }
    ...
    uint64_t start        = gp->start_index;
    uint64_t want         = gp->count;
    if (start > total) start = total;
    uint64_t end = start + want;
    if (end > total || end < start) end = total;
    uint32_t actual = (uint32_t)(end - start);
    ...
    net_send_int32(fd, CMD_SUCCESS);
    uint32_t hdr = actual | (has_first ? 0x80000000u : 0u);
    net_send_all(fd, &hdr, 4);
    ...
        memcpy(o,     rec,                  8);
        memcpy(o + 8, rec + 8, value_length);
        memcpy(o + 8 + value_length, rec + rec_prev_off, value_length);
        if (has_first)
            memcpy(o + 8 + 2 * value_length, rec + rec_first_off, value_length);
    ...
    net_send_int32(fd, CMD_SUCCESS);
```
Response: `CMD_SUCCESS`, `u32 hdr` (low 31 bits = record count, bit 31 = has_first), then records of `8 + value_length*(has_first?3:2)` bytes (`{u64 addr; value current; value previous; [value first]}`), then trailing `CMD_SUCCESS`. No sentinel. This matches `PROTOCOL.md:570-574`.

### 4f. COUNT, END, CANCEL, REGIONS, CONFIG

- COUNT (scan_turbo.c:1709-1820+): same request header pattern (22-byte body + pattern/mask). The `CMD_SUCCESS` at `:1775` precedes the pattern read at `:1776-1777`. It narrows the resident set when `TS_SERVER_RESIDENT` is set (`:1726`). Response shape as START.
- END (scan_turbo.c:2063-2070): auth gate, then `CMD_SUCCESS`. Frees this connection's survivor set.
- CANCEL (scan_turbo.c:1365-1375): `u32 pid` body, `CMD_SUCCESS`. Must be sent on a second connection (`PROTOCOL.md:596-604`).
- REGIONS (scan_turbo.c:1398-1455): `CMD_SUCCESS` (`:1453`), then `u32 num` and records (PROTOCOL.md:590-591).
- CONFIG (scan_turbo.c:1342-1362): `CMD_SUCCESS` (`:1361`).

### 4g. Auth gate for turbo

Each handler except CAPS starts with `if (!(g_proc_auth_state & 2)) { net_send_int32(fd, CMD_DATA_NULL); return 1; }`. `g_proc_auth_state` is `#define g_proc_auth_state g_proc_auth_bits` (`scan_turbo.c:20`). Examples: `scan_turbo.c:1343, 1366, 1399, 1463, 1985, 2065`.

### 4h. Fallback rule (documented)

`ref/ps4debug-NG/README.md:182-188`:
```
### Turbo Scan family (v1.3.0, additive + capability-gated)
A faster, opt-in scan path (`0xBDAACC10`-`0xBDAACC17`) that runs alongside the
unchanged classic / iterative / AOB scanners. A client detects it via
`CMD_PROC_TURBOSCAN_CAPS` (`0xBDAACC10`) and falls back to the iterative trio when
it (or a specific engine) is absent. Result format mirrors the iterative scan, so
clients reuse one parser. Raw-literal opcodes (no `CMD_*` macro); see
[PROTOCOL.md](PROTOCOL.md) §2.2 / §7.4.
```
PROTOCOL.md:547: "Clients call this first and gate their UI on the bits they see."

Capability level, `ref/ps4debug-NG/debugger/include/capability.h:6-11`: the turbo family is NOT in the capability list (levels 1.0, 1.1, 1.2 only). Detection is by CAPS, not by the capability string.

### 4i. Turbo vs classic CMD_PROC_SCAN, in short

- Classic `CMD_PROC_SCAN`: one request, whole process, streams all matching addresses. No session, no paging.
- Iterative trio: range scan (`SCAN_START`), rescan via client-supplied chunks (`SCAN_COUNT`), bulk read (`SCAN_GET`). Session is per-pid in the server.
- Turbo: START/COUNT can keep survivors server-side (`TS_SERVER_RESIDENT`). The client then pages through them with GET `{start_index, count}`. Snapshot mode (`TS_SNAPSHOT`) handles unknown-initial-value scans. CANCEL and REGIONS are extra. Filtering by "console" (region classification) is REGIONS (`PROTOCOL.md:587-594`): the server does not drop regions, the client chooses.

---

## 5. Auth: CMD_PROC_AUTH 0xBDAACCFF

`ref/ps4debug-NG/debugger/include/protocol.h:424-429`
```c
struct cmd_proc_auth_packet {
    uint32_t magic;
    uint32_t flags;
} __attribute__((packed));
#define CMD_PROC_AUTH_PACKET_SIZE  8
#define CMD_PROC_AUTH_MAGIC        0xBB40E64D
```

Handler, `ref/ps4debug-NG/debugger/source/proc.c:2098-2135`:
```c
int proc_auth_handle(int fd, struct cmd_packet *packet) {
    struct cmd_proc_auth_packet *ap = (struct cmd_proc_auth_packet *)packet->data;
    if (!ap || ap->magic != CMD_PROC_AUTH_MAGIC) {
        net_send_int32(fd, CMD_DATA_NULL);
        return 1;
    }

    net_send_int32(fd, CMD_SUCCESS);

    uint16_t chlen = 64;
    uint8_t challenge[64];
    uint8_t expected[64];
    uint8_t response[64];

    for (uint16_t i = 0; i < chlen; i++) {
        int r = rand();
        challenge[i] = (uint8_t)(r - r / -255);
    }

    proc_auth_xor_keystream(challenge, expected, chlen);

    net_send_all(fd, &chlen, sizeof(chlen));
    net_send_all(fd, challenge, chlen);
    net_recv_all(fd, response, chlen, 1);

    for (uint16_t i = 0; i < chlen; i++) {
        if (response[i] != expected[i]) {
            net_send_int32(fd, CMD_DATA_NULL);
            return 1;
        }
    }

    if (ap->flags & 1) g_proc_auth_bits |= 1;
    if (ap->flags & 2) g_proc_auth_bits |= 2;

    net_send_int32(fd, CMD_SUCCESS);
    return 0;
}
```

Keystream, `ref/ps4debug-NG/debugger/source/proc.c:2078-2096` (verbatim):
```c
static struct { uint32_t s1, s2, s3, s4; } g_auth_lfsr;

static uint32_t auth_lfsr_next(void) {
    g_auth_lfsr.s1 = ((g_auth_lfsr.s1 << 18) & 0xFFF80000u) ^ ((g_auth_lfsr.s1 ^ (g_auth_lfsr.s1 << 6))  >> 13);
    g_auth_lfsr.s2 = ((g_auth_lfsr.s2 <<  2) & 0xFFFFFFE0u) ^ ((g_auth_lfsr.s2 ^ (g_auth_lfsr.s2 << 2))  >> 27);
    g_auth_lfsr.s3 = ((g_auth_lfsr.s3 <<  7) & 0xFFFFF800u) ^ ((g_auth_lfsr.s3 ^ (g_auth_lfsr.s3 << 13)) >> 21);
    g_auth_lfsr.s4 = ((g_auth_lfsr.s4 << 13) & 0xFFF00000u) ^ ((g_auth_lfsr.s4 ^ (g_auth_lfsr.s4 << 3))  >> 12);
    return g_auth_lfsr.s1 ^ g_auth_lfsr.s2 ^ g_auth_lfsr.s3 ^ g_auth_lfsr.s4;
}

static void proc_auth_xor_keystream(const uint8_t *in, uint8_t *out, uint16_t len) {
    g_auth_lfsr.s1 = 200;
    g_auth_lfsr.s2 = 300;
    g_auth_lfsr.s3 = 400;
    g_auth_lfsr.s4 = 500;
    for (uint16_t i = 0; i < len; i++) {
        out[i] = in[i] ^ (uint8_t)auth_lfsr_next();
    }
}
```
(Lines 2088-2096. The LFSR is reset on every call, so the keystream is the same for every connection.)

Client sequence:
1. Send header `cmd=0xBDAACCFF, datalen=8`, body `magic=0xBB40E64D` (u32), `flags` (u32). Set bit `0x2` for the scan family (`proc.c:2131`, `SCAN_REQUIRE_AUTH` tests `& 2`).
2. Read `CMD_SUCCESS` (4 bytes).
3. Read `uint16 chlen` (2 bytes, LE), should be 64.
4. Read 64 challenge bytes.
5. Send 64 bytes: `response[i] = challenge[i] ^ keystream[i]`, where the keystream is the LFSR above, seeded fresh with 200/300/400/500 for this exchange.
6. Read `CMD_SUCCESS`, or `CMD_DATA_NULL` on mismatch.

Note: `proc_auth_xor_keystream` is the same function for both directions. The server computes `expected = challenge ^ keystream` and compares it with the client's response. So the client response is `challenge[i] ^ keystream[i]`.

Scope of the auth bits, `ref/ps4debug-NG/PROTOCOL.md:101-121`:
```
State is per-connection: disconnecting resets it. There is *no* server-wide
authentication on other command families - reads/writes/debug/kernel RW are
unauthenticated.
```
Code (`proc.c:2076`, `server.c:333`, `server.c:20-30`): `g_proc_auth_bits` is one global. It is reset only at server start (`server.c:333`). `free_client` (`server.c:20-30`) does not clear it. So in the code, auth is global until the server restarts (open question Q6).

Other notes:
- Auth gates the classic `CMD_PROC_SCAN_START/COUNT/GET` (`proc.c:2156, 2275, 2442`), the turbo family except CAPS, and the AOB scans (`PROTOCOL.md:483-497`).
- `CMD_PROC_SCAN` (0xBDAA0009, `proc.c:1599-1679`) has no `SCAN_REQUIRE_AUTH` in the code. The docs say it requires auth (open question Q1).

---

## 6. PS4CheaterNeo ScanTool.cs: value to bytes, and console vs local

### 6a. ValueStringToByte

`ref/PS4CheaterNeo/PS4CheaterNeo/common/ScanTool.cs:87-140`
```csharp
        public static byte[] ValueStringToByte(ScanType scanType, string value)
        {
            byte[] bytes = null;

            if (value == null || value.Length == 0) return null;

            switch (scanType)
            {
                case ScanType.Bytes_8:
                case ScanType.HiddenSections:
                    bytes = BitConverter.GetBytes(value.StartsWith("-") ? (ulong)long.Parse(value) : ulong.Parse(value));
                    break;
                case ScanType.Bytes_4:
                    bytes = BitConverter.GetBytes(value.StartsWith("-") ? (uint)int.Parse(value) : uint.Parse(value));
                    break;
                case ScanType.Bytes_2:
                    bytes = BitConverter.GetBytes(value.StartsWith("-") ? (UInt16)Int16.Parse(value) : UInt16.Parse(value));
                    break;
                case ScanType.Byte_:
                    bytes = BitConverter.GetBytes(value.StartsWith("-") ? (byte)sbyte.Parse(value) : byte.Parse(value));
                    Array.Resize(ref bytes, 1);
                    break;
                case ScanType.Double_:
                    bytes = BitConverter.GetBytes(double.Parse(value));
                    break;
                case ScanType.Float_:
                    bytes = BitConverter.GetBytes(float.Parse(value));
                    break;
                case ScanType.Hex:
                    value = value.Replace(" ", "").Replace("-", "").Replace("_", "");
                    if (value.Length % 2 == 1) value = "0" + value;
                    bytes = new byte[value.Length / 2];
                    for (int idx = 0; idx < bytes.Length; idx++) bytes[idx] = Convert.ToByte(value.Substring(idx * 2, 2), 16);
                    break;
                case ScanType.String_:
                    bytes = Encoding.Default.GetBytes(value);
                    break;
                case ScanType.Group:
                    break;
                case ScanType.AutoNumeric:
                    if (value.StartsWith("-"))
                    {
                        if (sbyte.TryParse(value, out sbyte resultSByte)) bytes = BitConverter.GetBytes(resultSByte);
                        else if (Int16.TryParse(value, out Int16 resultInt16)) bytes = BitConverter.GetBytes(resultInt16);
                        else if (Int32.TryParse(value, out Int32 resultInt32)) bytes = BitConverter.GetBytes(resultInt32);
                        else if (Int64.TryParse(value, out Int64 resultInt64)) bytes = BitConverter.GetBytes(resultInt64);
                    }
                    if (bytes == null) bytes = BitConverter.GetBytes(ulong.Parse(value));
                    break;
                default:
                    throw new Exception("ScanType verification failed");
            }
            return bytes;
        }
```
Notes:
- Output is little-endian via `BitConverter` (host order, LE on the target platforms).
- Negative input: `"-"` prefix goes through signed parse, then the unsigned cast (two's complement bytes).
- `Byte_` is 1 byte (`Array.Resize`). `Bytes_8` is 8 bytes. `Hex` is the raw byte sequence, with `-`, space and `_` stripped.
- Length table, `ScanTool.cs:68-78`: `Byte_=1, Bytes_2=2, Bytes_4=4, Bytes_8=8, Float_=4, Double_=8, AutoNumeric=8, HiddenSections=8` (the table lists the values; Hex/String/Group are not in it).
- AutoNumeric: `-` prefix tries sbyte, then short, int, long; otherwise (and for non-negative) `ulong.Parse`, which is always 8 bytes (`ScanTool.cs:134`). The `ComparerExact` path (`ScanTool.cs:404-507`) then picks width by size.
- `ValueStringToULong` (`ScanTool.cs:166`) and `BytesToULong` (`ScanTool.cs:147-158`) pad to 8 bytes.

### 6b. Console vs local

No call to `ScanProcess` or `GetScanResults` from any UI in `ref/PS4CheaterNeo`. `grep` for `ScanProcess` finds only the definitions and commented-out lines in `IPS4DBG.cs:143,164` (and `:158-159` for GetScanResults). The only scanning code that runs is the local `ScanTool.Comparer` / `ComparerExact` (`ScanTool.cs:288-507`), called from `Query.cs:1040, 1047, 1092, 1099, 1152, 1231`.

So the choice is not "console vs local" at the scan step. Memory is always read from the console section by section (`Query.cs:800-816`, see §1i), and matching is done on the PC. Kernel sections use `KernelReadMemory` (`Query.cs:812-813`, `PS4Tool.cs:386`), others use `ReadMemory`.

Kernel vs process routing is `IsKernel` on the section (`Query.cs:812`). The FRAME4 version check (`PS4Tool.cs:572-574` and `:619-621`, `fram4.Version` >= `0.2.15`) only controls process pause/resume, not scanning:
```csharp
            else if (ps4s[0].IsConnected && ps4s[0] is FRAME4 fram4 && fram4.Version != "0" &&
                Version.TryParse(fram4.Version, out Version current) && Version.TryParse("0.2.15", out Version target) &&
                current >= target)
```

PS4Cheater (ctn123) does the same: `ref/PS4Cheater/PS4_Cheater/ProcessManager.cs:219-235` reads in 128 MiB chunks per mapped section and compares on the PC.

---

## Open questions

- **Q1. CMD_PROC_SCAN auth and response.** `PROTOCOL.md:357-361` says "requires auth bit 1" and "Response: `uint32_t num`, then `num × uint64_t`". The code (`proc.c:1599-1679`) has no auth check and no count prefix; results are streamed with a `0xFFFF...` terminator. I followed the code. Confirm on a real console before relying on either.
- **Q2. ExactValue compares N-1 bytes in the legacy path.** `proc.c:950-953` loops to `valTypeLength - 1`. Check whether the console behaves that way, or whether the newer `proc_scan_compareValues` is what actually runs.
- **Q3. COUNT and GET responses.** `PROTOCOL.md:512` (COUNT returns `uint32_t num_results`) and `PROTOCOL.md:514-516` (GET returns `CMD_SUCCESS, uint32 actual, uint64 list`) do not match `proc.c:2437` and `proc.c:2459-2483`. Code above is what I quoted.
- **Q4. CAPS engines value.** Code sets all 12 bits (`0xFFF`), doc says `0x7FF`. Check the live `CAPS` reply.
- **Q5. Turbo START resident reply.** Doc says `CMD_SUCCESS` before the summary; code does not send one. The summary is always sent when `TS_SERVER_RESIDENT` is set (even with `resident_stored=0`, then a run stream follows). Verify on a live console.
- **Q6. Auth scope.** Doc says per-connection and reset on disconnect. Code uses one global `g_proc_auth_bits`, reset only at server start (`server.c:333`). Check whether a reconnect still sees scan auth.
- **Q7. `extraValue == null` in libdebug (`PS4DBG.Proc.cs:410`).** My reading: for unconstrained generic `T`, the check is always false for value types, so `lenData` is always `2*typeLength`. Not run; verify with a unit test if the new code relies on the single-value form.
- **Q8. Which FRAME4 / ps4debug version the Neo UI targets.** Neither UI calls console scan. FRAME4's scan IDs and packet (14 bytes, `0xBDAA000D/E`) do not match ps4debug-NG. Do not mix them.
- **Q9. Read overrun in CMD_PROC_SCAN.** For sizes that do not divide `PAGE_SIZE` (e.g. ArrBytes or strings of arbitrary length), the server reads only one page window (`proc.c:1657-1665`). Matches crossing a page boundary may read past the window. Not verified.
- **Q10. Not found:** no `scan_turbo.h` macros for the turbo IDs; no `cmd_proc_turboscan_*` C# struct anywhere in `ref/`; no C# client implements CAPS, turbo, or the trio.

## 7. Follow-up checks

### 7.1 proc.c scan loop (CMD_PROC_SCAN map loop), verbatim

`ref/ps4debug-NG/debugger/source/proc.c:1640-1680`
```c
1640        free(data);
1641        net_send_int32(fd, CMD_ERROR);
1642        return 1;
1643    }
1644
1645    net_send_int32(fd, CMD_SUCCESS);
1646
1647    unsigned char *pExtraValue = valueLength == sp->lenData ? NULL : &data[valueLength];
1648    unsigned char *scanBuffer = (unsigned char *)net_alloc_buffer(PAGE_SIZE);
1649    for (size_t i = 0; i < args.num; i++) {
1650       if ((args.maps[i].prot & PROT_READ) != PROT_READ) {
1651            continue;
1652       }
1653
1654       uint64_t sectionStartAddr = args.maps[i].start;
1655       size_t sectionLen = args.maps[i].end - sectionStartAddr;
1656
1657       for (uint64_t j = 0; j < sectionLen; j += valueLength) {
1658            if(j == 0 || !(j % PAGE_SIZE)) {
1659                sys_proc_rw(sp->pid, sectionStartAddr, scanBuffer, PAGE_SIZE, 0);
1660            }
1661
1662            uint64_t scanOffset = j % PAGE_SIZE;
1663            uint64_t curAddress = sectionStartAddr + j;
1664
1665            if (proc_scan_legacy_compareValues(sp->compareType, sp->valueType, valueLength, data, scanBuffer + scanOffset, pExtraValue)) {
1666                net_send_all(fd, &curAddress, sizeof(uint64_t));
1667            }
1668       }
1669    }
1670
1671    uint64_t endflag = 0xFFFFFFFFFFFFFFFF;
1672    net_send_all(fd, &endflag, sizeof(uint64_t));
1673
1674    free(scanBuffer);
1675    free(args.maps);
1676    free(data);
1677
1678    return 0;
1679}
```

Address argument: the read is `sys_proc_rw(sp->pid, sectionStartAddr, ...)` at line 1659. It is **not** `sectionStartAddr + j`. The read address is always the section start. The only `+ j` is `curAddress = sectionStartAddr + j` at line 1663, which is the address reported to the client.

Reading the code as written:
- j=0 reads page `[sectionStartAddr, +PAGE_SIZE)`.
- At j=PAGE_SIZE (PAGE_SIZE is 0x4000 per kdebugger/include/proc.h:10, see 7.6), the condition at 1658 is true again, and the code re-reads from `sectionStartAddr`, not `sectionStartAddr + PAGE_SIZE`.
- `scanOffset = j % PAGE_SIZE` (line 1662) then indexes into that re-read first page.
- So for every section larger than one page, the bytes compared for j >= PAGE_SIZE are the first page's bytes, while the reported address is `sectionStartAddr + j`. This looks like a bug, not an intended design. Treat it as a probable bug and confirm before relying on it.

Clamp: no. There is no clamp of the `sys_proc_rw` length to `sectionLen` or to `end`. The read is always `PAGE_SIZE` bytes from `sectionStartAddr` (line 1659), and the return value of `sys_proc_rw` is ignored. `sectionLen` only bounds the j loop (line 1657). Also, if `valueLength` does not divide PAGE_SIZE, `j % PAGE_SIZE == 0` is never hit exactly and `scanOffset + valueLength` can exceed PAGE_SIZE, reading past `scanBuffer` (line 1665). For 1/2/4/8-byte types this does not happen.

### 7.2 Legacy exact compare, and which function CMD_PROC_SCAN calls

`ref/ps4debug-NG/debugger/source/proc.c:940-955`
```c
940 bool proc_scan_legacy_compareValues(cmd_proc_scan_comparetype cmpType,
941                                     cmd_proc_scan_valuetype valType,
942                                     size_t valTypeLength,
943                                     unsigned char *pScanValue,
944                                     unsigned char *pMemoryValue,
945                                     unsigned char *pExtraValue) {
946    switch (cmpType) {
947       case cmpTypeExactValue:
948       {
949
950          if (valTypeLength <= 1) return false;
951          for (size_t j = 0; j < valTypeLength - 1; j++) {
952             if (pScanValue[j] != pMemoryValue[j]) return false;
953          }
954          return true;
955       }
```

The CMD_PROC_SCAN handler calls `proc_scan_legacy_compareValues` (line 1665). It does not call `proc_scan_compareValues`. The other exact case, `proc.c:1170` (`case cmpTypeExactValue:` inside `bool proc_scan_compareValues(...)` starting at about line 1166), belongs to the newer scanner and is used by the iterative/turbo path, not the legacy one.

Quirk: the legacy exact compare checks only `valTypeLength - 1` bytes (line 951), so the last byte of the value is never compared. A 4-byte exact value compares 3 bytes. Verify this before depending on it. It is important for any client that relies on the legacy scan for exact matches.

### 7.3 PROT_READ / PROT_WRITE / PROT_EXEC values

ps4debug-NG, kernel side (what proc.c uses, since it includes the kernel headers):
- `ref/ps4debug-NG/ps4-ksdk/include/ksdk_bsd.h:14-16`
```c
14 #define VM_PROT_READ		0x01
15 #define VM_PROT_WRITE		0x02
16 #define VM_PROT_EXECUTE		0x04
```
- `ref/ps4debug-NG/ps4-ksdk/include/ksdk_bsd.h:23-25`
```c
23 #define PROT_READ			VM_PROT_READ
24 #define PROT_WRITE			VM_PROT_WRITE
25 #define PROT_EXEC			VM_PROT_EXECUTE
```
- Also `ref/ps4debug-NG/ps4-payload-sdk/libPS4/include/memory.h:10-12` (userland SDK, not the kernel path): `#define PROT_CPU_READ 1`, `#define PROT_CPU_WRITE 2`, `#define PROT_CPU_EXEC 4`. Lines 18-20 map `PROT_READ PROT_CPU_READ`, and so on. Same numeric values.

ps4debug (ref/ps4debug, the original):
- `ref/ps4debug/ps4-ksdk/include/ksdk_bsd.h:18-20`: `#define VM_PROT_READ 0x01`, `#define VM_PROT_WRITE 0x02`, `#define VM_PROT_EXECUTE 0x04`.
- `ref/ps4debug/ps4-ksdk/include/ksdk_bsd.h:27-29`: `#define PROT_READ  VM_PROT_READ`, `#define PROT_WRITE VM_PROT_WRITE`, `#define PROT_EXEC  VM_PROT_EXECUTE`.
- `ref/ps4debug/ps4-payload-sdk/include/memory.h:10-12`: `PROT_CPU_READ 1`, `PROT_CPU_WRITE 2`, `PROT_CPU_EXEC 4`.

Result: READ = 0x01, WRITE = 0x02, EXEC = 0x04 in both repos. The scan filter at proc.c:1650 is `(args.maps[i].prot & PROT_READ) != PROT_READ`. Note: no `vm/vm.h` value conflicts. `ref/ps4debug-NG/debugger/third_party/cxxrt/cinc/vm/vm.h:76-78` has the same values.

### 7.4 PS4Cheater (ctn123): console-side scan options and the first-scan path

Console-side hits: `grep -rniI "console|ScanProcess|CMD_PROC_SCAN|0xBDAA0009|Remote"` over `ref/PS4Cheater/PS4_Cheater/*.cs` (22 .cs files, PS4Cheater's source is under `PS4_Cheater/`) returns **no .cs hits**. The only hits are in the README:
- `ref/PS4Cheater/README.md:21`: `  - Console scanner which does the scanning on the ps4 console.`
- `ref/PS4Cheater/README.md:30-31`: `  - Console scanner` / `    - Fast but not always faster than the non-console scanner.`
- `ref/PS4Cheater/README.md:36`: `  - You can enable console scanner for the first scan, then disable for the next scan etc.`

Therefore: the README advertises a console scanner, but no code in the .cs files of this checkout implements it. The first scan calls `memoryHelper.ReadMemory` for every section and compares on the PC. The only ps4debug call is a read.

First-scan code path (all PC side):
1. `ref/PS4Cheater/PS4_Cheater/main.cs:479-500`: `new_scan_worker_DoWork` splits the work over `num_threads` tasks, each running `new_scan_thread`.
2. `ref/PS4Cheater/PS4_Cheater/main.cs:503-518`:
```csharp
void new_scan_thread(string value_0, string value_1, int thread_id, int num_threads, ref long processed_memory_len, ulong total_memory_size)
{
    for (int section_idx = 0; section_idx < processManager.MappedSectionList.Count; ++section_idx)
    {
        // Split work up between threads.
        if (section_idx % num_threads != thread_id) continue;

        if (new_scan_worker.CancellationPending) break;
        MappedSection mappedSection = processManager.MappedSectionList[section_idx];
        mappedSection.UpdateResultList(processManager, memoryHelper, value_0, value_1, hex_box.Checked, true, thread_id);
```
(line 512 is the `UpdateResultList(..., true, thread_id)` call.)
3. `ref/PS4Cheater/PS4_Cheater/ProcessManager.cs:219-235`: `UpdateResultList` reads each section in chunks of at most 128 MB:
```csharp
const int buffer_length = 1024 * 1024 * 128;   // line 219
...
byte[] buffer = memoryHelper.ReadMemory(address, (int)cur_length, thread_id);   // line 235
```
4. `ref/PS4Cheater/PS4_Cheater/ProcessManager.cs:265`: `memoryHelper.CompareWithMemoryBufferNewScanner(default_value_0, default_value_1, buffer, new_result_list, base_address);` (newScan == true).
5. Memory read: `ref/PS4Cheater/PS4_Cheater/MemoryHelper.cs:141-146`: `public byte[] ReadMemory(ulong address, int length, int thread_id = 0)` → `ps4[thread_id].ReadMemory(ProcessID, address, length);`. `ps4` is `PS4DBG[]` (`MemoryHelper.cs:48`). The C# client is libdebug's `PS4DBG.ReadMemory(int pid, ulong address, int length)`, at `ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.Proc.cs:79`. The PS4Cheater checkout does not contain PS4DBG source.
6. Compare: `ref/PS4Cheater/PS4_Cheater/MemoryHelper.cs:383-398`:
```csharp
public void CompareWithMemoryBufferNewScanner(byte[] default_value_0, byte[] default_value_1, byte[] buffer,
    ResultList new_result_list, uint base_address)
{
    int alignment = Alignment;
    int length = Length;

    Byte[] new_value = new byte[length];
    Byte[] dummy_value = new byte[length];
    for (int i = 0; i + length < buffer.LongLength; i += alignment)
    {
        Buffer.BlockCopy(buffer, i, new_value, 0, length);
        if (Comparer(default_value_0, default_value_1, dummy_value, new_value))
        {
            new_result_list.Add((uint)i + base_address, new_value);
```
7. The exact-uint comparer: `MemoryHelper.cs:1064` (`Comparer = scan_type_equal_uint;` for `UINT_TYPE` under `EXACT_VALUE`), and `MemoryHelper.cs:558-561`:
```csharp
bool scan_type_equal_uint(Byte[] default_value_0, Byte[] default_value_1, Byte[] old_value, Byte[] new_value)
{
    return BitConverter.ToUInt32(default_value_0, 0) == BitConverter.ToUInt32(new_value, 0);
}
```
8. Alignment: `MemoryHelper.cs:968-969` (in `InitMemoryHandler`, `MemoryHelper.cs:932`):
```csharp
Length = sizeof(uint);
Alignment = (is_alignment) ? 4 : 1;
```
   `is_alignment` comes from `main.cs:286`: `alignment_box.Checked`. So the 4-byte exact scan steps **every 4 bytes when the alignment box is ticked, and every byte when it is not**. It compares all 4 bytes of the value (`ToUInt32` of 4 bytes, not `Length-1`).

Note the contrast with ps4debug-NG legacy: PS4Cheater compares all bytes of the value and uses step = alignment (1 or 4). The legacy ps4debug-NG handler steps by `valueLength` and compares `valueLength - 1` bytes (see 7.2).

### 7.5 ps4debug-NG docs on the legacy scan

- `ref/ps4debug-NG/PROTOCOL.md:357-358`:
```
357 #### `CMD_PROC_SCAN = 0xBDAA0009` (proc.c:1401-1485) - **requires auth bit 1**
358 Legacy single-pass scan; superseded by the `SCAN_START/COUNT/GET` trio.
```
- `ref/ps4debug-NG/PROTOCOL.md:891`: table row `| `0xBDAA0009` | `CMD_PROC_SCAN` | `proc_scan_handle` | bit 1 |`.
- `ref/ps4debug-NG/README.md:171`: `- **Value scan** (`CMD_PROC_SCAN`) - single-pass, 12 value types × 13 compare`. The README does not use "legacy", "deprecated" or "buggy" for this command. Its `### Memory scanning` section at lines 170-180 describes the iterative trio as the other option.
- `grep -n -i "legacy|deprecat|buggy"` in `ref/ps4debug-NG/README.md`, `PROTOCOL.md` and `debugger/PROTOCOL.md` finds nothing beyond the lines above. Also `PROTOCOL.md:136`, `:144`, `:199` refer to a "legacy 8-byte body" for the **older protocol version**, not to CMD_PROC_SCAN.

Stale line references: `PROTOCOL.md:357` says `proc.c:1401-1485`, but the handler is at `proc.c:1599-1679`. The existing section 1 of this file already notes this.

### 7.6 Open questions
- Is the "same first page" read at proc.c:1659 really a bug, or is there a hidden override? I only read the loop. Check whether `sys_proc_rw` in the ps4debug-NG kernel side offsets the address. Not verified. Ask the researcher to confirm with a test against the fake server or the ps4debug (original) implementation in `ref/ps4debug/debugger/source/proc.c`.
- `PAGE_SIZE` is `0x4000` at `ref/ps4debug-NG/kdebugger/include/proc.h:10`. It is not defined in `ref/ps4debug-NG/debugger/include/`, so the debugger's value is not confirmed.
- The PS4Cheater README's "console scanner" feature has no .cs implementation in this checkout. Check the `PS4Cheater-a0zhar` tree in `ref/` before concluding it does not exist anywhere.
- PS4DBG (C# libdebug) `ReadMemory` wire format at `ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.Proc.cs:79` is not yet fetched.
