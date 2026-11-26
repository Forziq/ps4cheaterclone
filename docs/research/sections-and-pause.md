# Sections, read chunk sizes, and pausing the game

Sources (commit hashes at time of research, shallow clones in `ref/`):
- PS4Cheater (ctn123) `ref/PS4Cheater` @ 14ce736
- PS4CheaterNeo (avan06) `ref/PS4CheaterNeo` @ ca85868
- libdebug `ref/libdebug` @ 681005f (the C# client, same tree as PS4CheaterNeo/common/ps4dbg/libdebug, which is older/newer in places; both shown where they differ)
- ps4debug-NG server `ref/ps4debug-NG` @ 45d44df

Paths below are relative to `ref/` unless absolute.

---

## 1. PS4Cheater (ctn123): section filter

### 1a. Default section names and prot values (GameInfo only)

`PS4Cheater/PS4_Cheater/Util.cs:14-30`
```csharp
        const string GAME_INFO_7_02_PROCESS_NAME = "SceCdlgApp";
        const string GAME_INFO_7_02_SECTION_NAME = "libSceCdlgUtilServer.sprx";
        const int GAME_INFO_7_02_SECTION_PROT = 3;
        const int GAME_INFO_7_02_ID_OFFSET = 0XA0;
        const int GAME_INFO_7_02_VERSION_OFFSET = 0XC8;
```
(6.72 and 5.05 blocks are identical in shape: same section name, `_SECTION_PROT = 3`, offsets 0XA0 / 0XC8.)

`PS4Cheater/PS4_Cheater/Util.cs:86`
```csharp
                List<MappedSection> sectionList = mappedSectionList.GetMappedSectionList(section_name, section_prot);
```

No default name list ("anon", "exe", "Game", "dlmalloc", "SmallBlockAllocator") exists in PS4Cheater. The only name is the GameInfo lookup above. Search for those strings returned nothing.

Constant for executable sections:
`PS4Cheater/PS4_Cheater/Util.cs:109`
```csharp
        public const uint SECTION_EXECUTABLE = 0x5;
```

### 1b. How MappedSection is built from process maps

`PS4Cheater/PS4_Cheater/ProcessManager.cs:415-431` (GetMappedSectionList)
```csharp
        public List<MappedSection> GetMappedSectionList(string name, int prot)
        {
            List<MappedSection> result_list = new List<MappedSection>();
            for (int idx = 0; idx < mapped_section_list.Count; ++idx)
            {
                if (mapped_section_list[idx].Prot == prot &&
                    mapped_section_list[idx].Name.StartsWith(name))
                {
                    result_list.Add(mapped_section_list[idx]);
                }
            }
            return result_list;
        }
```

`PS4Cheater/PS4_Cheater/ProcessManager.cs:433-500` (InitMemorySectionList)
```csharp
        public void InitMemorySectionList(ProcessMap pm)
        {
            mapped_section_list.Clear();
            TotalMemorySize = 0;

            for (int i = 0; i < pm.entries.Length; i++)
            {
                MemoryEntry entry = pm.entries[i];
                if ((entry.prot & 0x1) == 0x1)
                {
                    ulong length = entry.end - entry.start;
                    ulong start = entry.start;
                    string name = entry.name;
                    int idx = 0;
                    ulong buffer_length = 1024 * 1024 * 128;

                    //Executable section
                    if ((entry.prot & 0x5) == 0x5)
                    {
                        buffer_length = length;
                    }

                    while (length != 0)
                    {
                        ulong cur_length = buffer_length;

                        if (cur_length > length)
                        {
                            cur_length = length;
                            length = 0;
                        }
                        else
                        {
                            length -= cur_length;
                        }

                        MappedSection mappedSection = new MappedSection();
                        mappedSection.Start = start;
                        mappedSection.Length = (int)cur_length;
                        mappedSection.Name = entry.name + "[" + idx + "]";
                        mappedSection.Check = false;
                        mappedSection.Prot = entry.prot;

                        mapped_section_list.Add(mappedSection);

                        start += cur_length;
                        ++idx;
                    }
                }
            }

        }
```

Findings for 1b:
- Only filter at build time: `(entry.prot & 0x1) == 0x1` (readable bit). Maps without the read bit are skipped.
- No `end <= start` check, no size cap. `length = end - start` is unsigned and would wrap if `end < start`.
- Large non-executable maps are split into 128 MB pieces (`1024 * 1024 * 128`). Executable maps (`prot & 0x5 == 0x5`) are one piece of full length.
- Name becomes `entry.name + "[" + idx + "]"`, so every piece gets an index suffix, even single-piece ones.
- `GetMappedSectionList` matches `Prot == prot` exactly (not a bit test) and `Name.StartsWith(name)`.

`PS4Cheater/PS4_Cheater/ProcessManager.cs:194` (MappedSection)
```csharp
        public uint Prot { get; set; }
```

`PS4Cheater/PS4_Cheater/ProcessManager.cs:406-412` (name shown in lists)
```csharp
            section_name.Append(sectionInfo.Name + "-");
            section_name.Append(String.Format("{0:X}", sectionInfo.Prot) + "-");
            section_name.Append(String.Format("{0:X}", sectionInfo.Start) + "-");
            section_name.Append((sectionInfo.Length / 1024).ToString() + "KB");
```

GameInfo guard: `PS4Cheater/PS4_Cheater/Util.cs:88-89`: `if (sectionList.Count != 1) return;`

---

## 2. PS4CheaterNeo: SectionTool.cs

### 2a. Default filter names and size (Settings)

`PS4CheaterNeo/PS4CheaterNeo/Properties/Settings.settings:110-112`
```xml
    <Setting Name="SectionFilterKeys" Type="OptionTreeView.Option&lt;string&gt;" Scope="User">
      <Value Profile="(Default)">libSce,libc.prx,SceShell,SceLib,SceNp,SceVoice,SceFios,libkernel,SceVdec|3_Query|3_Filter|Enter the filter value, the filter will be set here when listing Sections</Value>
    </Setting>
```

`PS4CheaterNeo/PS4CheaterNeo/Properties/Settings.settings:107-109` (FilterNameQuery, default True)
```xml
      <Value Profile="(Default)">True|3_Query|3_Filter|Determine whether to enable filtering Sections when opening the query window. \nDefault enabled</Value>
```

`PS4CheaterNeo/PS4CheaterNeo/Properties/Settings.settings:113-118`
```xml
    <Setting Name="FilterSizeQuery" Type="OptionTreeView.Option&lt;bool&gt;" Scope="User">
      <Value Profile="(Default)">False|3_Query|3_Filter|Determine whether to enable filtering Sections by size when opening the query window. \nDefault disabled</Value>
    </Setting>
    <Setting Name="SectionFilterSize" Type="OptionTreeView.Option&lt;uint&gt;" Scope="User">
      <Value Profile="(Default)">204800|3_Query|3_Filter|Filter out when section size is less than this value(unit is bytes)</Value>
    </Setting>
```

Regex-based name filter (keys are split on `,` or `;` and joined with `|`):

`PS4CheaterNeo/PS4CheaterNeo/common/SectionTool.cs:276-278`
```csharp
                string SectionFilterKeys = Properties.Settings.Default.SectionFilterKeys.Value;
                uint SectionFilterSize = Properties.Settings.Default.SectionFilterSize.Value;
                SectionFilterKeys = Regex.Replace(SectionFilterKeys, " *[,;] *", "|");
```

`PS4CheaterNeo/PS4CheaterNeo/common/SectionTool.cs:615-621`
```csharp
        public bool SectionIsFilter(string name, string sectionFilterKeys)
        {
            if (string.IsNullOrWhiteSpace(sectionFilterKeys))
                return false;

            return Regex.IsMatch(name, sectionFilterKeys);
        }
```

### 2b. Prot handling and the section-building loop

`PS4CheaterNeo/PS4CheaterNeo/common/SectionTool.cs:291-299`
```csharp
                Array.Sort(pMap.entries, CompareMemoryEntry); // Sort MemoryEntry in order to calculate SID
                for (int eIdx = 0; eIdx < pMap.entries.Length; eIdx++)
                {
                    MemoryEntry entry = pMap.entries[eIdx];
                    if ((entry.prot & 0x1) != 0x1) continue;
                    if (tmpEntry != null && tmpEntry.start == entry.start && tmpEntry.end == entry.end) continue;
                    ulong start = entry.start;
                    ulong end = entry.end;
```

`PS4CheaterNeo/PS4CheaterNeo/common/SectionTool.cs:300-321`
```csharp
                    // Prevent underflow caused by start being greater than end (which results in a massive ulong value)
                    if (start >= end) continue;

                    ulong length = end - start;

                    // Filter out invalid massive memory blocks to avoid infinite loops.
                    // Set a reasonable upper limit, such as 64GB (0x1000000000) or 32GB.
                    if (length > 0x1000000000UL)
                    {
                        // Skip abnormally large sections
                        continue;
                    }

                    bool isFilter = SectionIsFilter(entry.name, SectionFilterKeys);
                    uint idx = 0;
                    uint highBits = (uint)((start & 0xffffffff00000000) >> 32); // get higher bits value, parse higher bits into lower bits
                    uint typeCode = string.IsNullOrWhiteSpace(entry.name) ? 2u : 1u; // first code of SID, named 1, unnamed 2

                    ulong bufferLength = 1024 * 1024 * 128; // 128M
                    if ((entry.prot & 0x5) == 0x5) bufferLength = length; // Executable section
```

`PS4CheaterNeo/PS4CheaterNeo/common/SectionTool.cs:329-370` (split and Section creation)
```csharp
                    while (length != 0)
                    {
                        ulong curLength = bufferLength;

                        if (curLength <= length) length -= curLength;
                        else
                        {
                            curLength = length;
                            length = 0;
                        }
                        ...
                        Section section = new Section
                        {
                            PID    = pMap.pid,
                            SIDv1  = newSidV1,
                            SID    = newSid,
                            Start  = start,
                            Length = (int)curLength,
                            Name   = entry.name + (length == 0 && idx == 0 ? "" : "[" + idx + "]"),
                            Check  = false,
                            Prot   = entry.prot,
                            Offset = entry.offset
                        };
                        if (isFilter) section.IsFilter = true;
                        else if (section.Length < SectionFilterSize) section.IsFilterSize = true;
```

Findings for 2b:
- Prot is not compared to a value. Only the read bit (`0x1`) is required. Executable (`0x5`) gets one piece; others split at 128 MB.
- Dedup: entries with identical start and end as the previous one are skipped (`SectionTool.cs:296`).
- Size guard: `start >= end` skipped; `length > 0x1000000000UL` (64 GB) skipped (`SectionTool.cs:301-311`).
- Names are not hidden or excluded. Matching names set `IsFilter`, which hides them from the list when the filter box is checked. Small sections (< `SectionFilterSize`) set `IsFilterSize`.
- Filter use: `Query.cs:415` `if ((IsFilterBox.Checked && section.IsFilter) || (IsFilterSizeBox.Checked && section.IsFilterSize)) continue;`
- Scan skip: `Query.cs:956` `if (!currentSection.Check || isFilter && currentSection.IsFilter || isFilterSize && currentSection.IsFilterSize) isContinue = true;`

Kernel base injected as a fake section (`SectionTool.cs:401-415`), `Prot = 5`, `Length = 0x2000000`, `IsFilter = true`.

Hidden-section detection is a separate option (`SectionTool.cs:427-429`), off by default per Settings (not verified in this pass).

### 2c. Other Neo filter defaults

`PS4CheaterNeo/PS4CheaterNeo/Query.cs:775-784`
```csharp
                    uint QueryBufferSize                = Properties.Settings.Default.QueryBufferSize.Value;
                    ...
                    ulong minLength = QueryBufferSize * 1024 * 1024; //set the minimum read size in bytes
```

`PS4CheaterNeo/PS4CheaterNeo/Properties/Settings.settings:74-76`
```xml
    <Setting Name="QueryBufferSize" Type="OptionTreeView.Option&lt;uint&gt;" Scope="User">
      <Value Profile="(Default)">50|3_Query|1_Query|Set the minimum buffer size (in MB) in querying and pointerFinder, \nenter 0 to not use buffer, setting this value to 0 is better when the total number of Sections in the game is low. \nIf the game has more than a thousand Sections, Buffer must be set. \nDefault value is 50</Value>
    </Setting>
```

Contiguous sections are merged into one read range (`Query.cs:946-991`); a single read covers the whole range (`Query.cs:809-815`):
```csharp
                                    ulong bufferSize = sectionEnd.Start + (ulong)sectionEnd.Length - sectionStart.Start;
                                    if (!isCompareFirst)
                                    {
                                        if (sectionStart.IsKernel)
                                            buffer = PS4Tool.KernelReadMemory(sectionStart.Start, (int)bufferSize);
                                        else
                                            buffer = PS4Tool.ReadMemory(sectionStart.PID, sectionStart.Start, (int)bufferSize);
                                    }
```

Range merge stops at `bufferSize >= int.MaxValue` (`Query.cs:981-982`).

---

## 3. Read chunk sizes

### 3a. PS4Cheater local scan

`PS4Cheater/PS4_Cheater/Util.cs:157-158`
```csharp
        public const int MAX_PEEK_QUEUE = 4;
        public const int PEEK_BUFFER_LENGTH = 32 * 1024 * 1024;
```

`PS4Cheater/PS4_Cheater/ScanThread.cs:44-59` (PeekThread.Peek)
```csharp
                ulong address = mappedSection.Start;
                int length = mappedSection.Length;

                while (length != 0)
                {
                    int cur_length = CONSTANT.PEEK_BUFFER_LENGTH;
                    ...
                    if (cur_length > length)
                    {
                        cur_length = length;
                        length = 0;
                    }
                    else
                    {
                        length -= cur_length;
                    }
```

`PS4Cheater/PS4_Cheater/ProcessManager.cs:219` (ProcessManager section read loop, same shape)
```csharp
            const int buffer_length = 1024 * 1024 * 128;
```

`PS4Cheater/PS4_Cheater/ProcessManager.cs:235` and `:299`
```csharp
                byte[] buffer = memoryHelper.ReadMemory(address, (int)cur_length, thread_id);
```
```csharp
                byte[] buffer = memoryHelper.ReadMemory(address, (int)cur_length);
```

`PS4Cheater/PS4_Cheater/MemoryHelper.cs:141-155` (read wrapper, swallows errors and returns zeroes)
```csharp
        public byte[] ReadMemory(ulong address, int length, int thread_id = 0)
        {
            mutex[thread_id].WaitOne();
            try
            {
                byte[] buf = ps4[thread_id].ReadMemory(ProcessID, address, length);
                mutex[thread_id].ReleaseMutex();
                return buf;
            }
            catch
            {
                mutex[thread_id].ReleaseMutex();
            }
            return new byte[length];
        }
```

Summary: PS4Cheater pipeline reads 32 MB pieces (`PEEK_BUFFER_LENGTH`) into a queue of 4 (`MAX_PEEK_QUEUE`). The 128 MB value is the section split size, not the read size.

### 3b. PS4CheaterNeo

Read sizes come from section ranges, not a fixed constant (see 2c). `QueryBufferSize` default 50 MB sets the minimum range size. Kernel reads are split into 8 MB pages:

`ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/PS4Tool.cs:500-520` (KernelReadMemory)
```csharp
                byte[] result = new byte[length];
                int pageSize = 8 * 1024 * 1024;
                int offset = 0;

                while (offset < length)
                {
                    // Calculate the size for the current page
                    int currentReadSize = Math.Min(pageSize, length - offset);

                    // Execute partial read
                    byte[] buffer = ReadMemoryInternal(-1, address + (ulong)offset, currentReadSize, true);
```

The process-memory read path has no chunking in Neo: `PS4Tool.ReadMemory(processID, address, length)` calls `ReadMemoryInternal` directly (`PS4Tool.cs:328`, `:378-387`).

Client-side socket buffer (libdebug in Neo):
`ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.cs:36`
```csharp
        private const int NET_MAX_LENGTH     = 0x20000; // 128KB buffer
```

`ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.cs:271-297` (SendData and ReceiveData chunk by NET_MAX_LENGTH)
```csharp
                if (left > NET_MAX_LENGTH)
                {
                    byte[] bytes = SubArray(data, offset, NET_MAX_LENGTH);
                    sent = sock.Send(bytes, NET_MAX_LENGTH, SocketFlags.None);
                }
```
```csharp
            byte[] b = new byte[NET_MAX_LENGTH];
            ...
                recv = sock.Receive(b, Math.Min(left, NET_MAX_LENGTH), SocketFlags.None);
```

### 3c. libdebug (ref/libdebug)

`ref/libdebug/PS4DBG/PS4DBG.cs:42` and `:263-285`
```csharp
        private const int NET_MAX_LENGTH = 8192;
```
```csharp
                if (left > NET_MAX_LENGTH) {
                    byte[] bytes = SubArray(data, offset, NET_MAX_LENGTH);
                    sent = sock.Send(bytes, NET_MAX_LENGTH, SocketFlags.None);
                }
```
```csharp
            while (left > 0) {
                byte[] b = new byte[NET_MAX_LENGTH];
                recv = sock.Receive(b, NET_MAX_LENGTH, SocketFlags.None);
                s.Write(b, 0, recv);
                left -= recv;
            }
```

Note the two libdebug copies differ: `ref/libdebug` uses 8192 and `ReceiveData` reads `NET_MAX_LENGTH` per call. Neo uses 0x20000 and `Math.Min(left, ...)`.

`ref/libdebug/PS4DBG/PS4DBG.Proc.cs:72-86` (ReadMemory)
```csharp
        public byte[] ReadMemory(int pid, ulong address, int length) {
            CheckConnected();

            SendCMDPacket(CMDS.CMD_PROC_READ, CMD_PROC_READ_PACKET_SIZE, pid, address, length);
            CheckStatus();
            return ReceiveData(length);
        }
```
Packet size `ref/libdebug/PS4DBG/PS4DBG.Proc.cs:13`: `private const int CMD_PROC_READ_PACKET_SIZE = 16;`

Neither client caps `length` in `ReadMemory`. Chunking happens only in PS4Cheater's section loop.

### 3d. Server-side max for CMD_PROC_READ

No maximum request length in the server. The request is `struct cmd_proc_read_packet` (`ref/ps4debug-NG/debugger/include/protocol.h:104-108`):
```c
struct cmd_proc_read_packet {
    uint32_t pid;
    uint64_t address;
    uint32_t length;
} __attribute__((packed));
#define CMD_PROC_READ_PACKET_SIZE 16
```

Handler: `ref/ps4debug-NG/debugger/source/proc.c:320-357`
```c
int proc_read_handle(int fd, struct cmd_packet *packet) {
    struct cmd_proc_read_packet *rp = (struct cmd_proc_read_packet *)packet->data;

    if (!rp) {
        net_send_int32(fd, CMD_DATA_NULL);
        return 1;
    }

    void *data = net_alloc_buffer(0x10000);
    if (!data) {
        net_send_int32(fd, CMD_DATA_NULL);
        return 1;
    }

    net_send_int32(fd, CMD_SUCCESS);

    uint64_t length = rp->length;
    uint64_t address = rp->address;

    if (length > 0) {
        uint64_t last_aligned = address + ((length - 1) & ~(uint64_t)0xFFFF);

        for (;;) {
            memset(data, 0, 0x10000);
            if (address == last_aligned) break;
            sys_proc_rw(rp->pid, address, data, 0x10000, 0);
            net_send_all(fd, data, 0x10000);
            address += 0x10000;
        }

        uint64_t tail_size = length - ((length - 1) & ~(uint64_t)0xFFFF);
        sys_proc_rw(rp->pid, address, data, tail_size, 0);
        net_send_all(fd, data, (int)tail_size);
    }

    free(data);
    return 0;
}
```

Server reply: `CMD_SUCCESS` (u32) first, then `length` raw bytes in 0x10000 pieces (last piece = remainder). Note: `sys_proc_rw` return value is not checked, so a bad address sends zeros rather than an error.

---

## 4. Pausing the game

### 4a. Command values

`ref/libdebug/PS4DBG/PS4DBG.Enums.cs:33-47`
```csharp
            CMD_DEBUG_ATTACH = 0xBDBB0001,
            ...
            CMD_DEBUG_STOPGO = 0xBDBB0010,
```

`ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.cs:98-107`
```csharp
            CMD_DEBUG_STOPGO     = 0xBDBB0010,
            CMD_DEBUG_THRINFO    = 0xBDBB0011,
            CMD_DEBUG_SINGLESTEP = 0xBDBB0012,
            CMD_DEBUG_EXT_STOPGO = 0xBDBB0500,
```

Server: `ref/ps4debug-NG/debugger/include/protocol.h:51-69`
```c
#define CMD_DEBUG_ATTACH            0xBDBB0001
...
#define CMD_DEBUG_CONTINUE          0xBDBB0010
...
#define CMD_DEBUG_PROCESS_STOP      0xBDBB0500
```

Server dispatch: `ref/ps4debug-NG/debugger/source/debug.c:1097` and `:1114`
```c
        case CMD_DEBUG_PROCESS_STOP:     return debug_process_stop_handle(fd, packet);
...
        case CMD_DEBUG_CONTINUE:         return debug_continue_handle(fd, packet);
```

### 4b. libdebug ProcessStop / ProcessResume / ProcessKill (0xBDBB0010)

`ref/libdebug/PS4DBG/PS4DBG.Debug.cs:137-150`, `:152-166`, `:168-182`
```csharp
        public void ProcessStop() {
            CheckConnected();
            CheckDebugging();

            // Build the CMD Packet/Command Packet, and send it to the Connected
            // Remote PS4 System's running PS4Debug Payload
            SendCMDPacket(CMDS.CMD_DEBUG_STOPGO, CMD_DEBUG_STOPGO_PACKET_SIZE, 1);

            CheckStatus();
        }
```
```csharp
        public void ProcessKill() {
            ...
            SendCMDPacket(CMDS.CMD_DEBUG_STOPGO, CMD_DEBUG_STOPGO_PACKET_SIZE, 2);
```
```csharp
        public void ProcessResume() {
            ...
            SendCMDPacket(CMDS.CMD_DEBUG_STOPGO, CMD_DEBUG_STOPGO_PACKET_SIZE, 0);
```
`CMD_DEBUG_STOPGO_PACKET_SIZE = 4` (`PS4DBG.Debug.cs:22`). `CheckDebugging()` is called first, so these need an attached debugger.

Server side for 0xBDBB0010 is `debug_continue_handle` (`ref/ps4debug-NG/debugger/source/debug.c:996-1006`):
```c
int debug_continue_handle(int fd, struct cmd_packet *packet) {
    uint32_t *pp = (uint32_t *)packet->data;
    if (!g_debug_ctx.attached) { net_send_int32(fd, CMD_ERROR); return 1; }
    if (!pp) { net_send_int32(fd, CMD_DATA_NULL); return 1; }

    int signal = (*pp == 1) ? 17 : (*pp == 2) ? 9 : 0;
    int r = kern_ptrace(PT_CONTINUE, (int)g_debug_ctx.pid, (void *)1, signal);
    if (signal == 0) bp_disable_verify_sweep();
    net_send_int32(fd, ptrace_ok(r) ? CMD_SUCCESS : CMD_ERROR);
    return 0;
}
```

**Mismatch (open question):** the server treats `1` as `PT_CONTINUE` with signal 17, not as a stop. `ref/ps4debug-NG/PROTOCOL.md:729-732` says `stop == 1 → enter single-step / halted mode`, which does not match the code above. Do not rely on 0xBDBB0010 for pausing without checking.

### 4c. Extended stop/resume (0xBDBB0500), the one the server documents for pausing

Server request, from `ref/ps4debug-NG/debugger/source/debug.c:241-275`:
```c
int debug_process_stop_handle(int fd, struct cmd_packet *packet) {
    if (!packet->data) {

        net_send_int32(fd, CMD_DATA_NULL);
        return 1;
    }

    uint8_t *p = (uint8_t *)packet->data;
    uint32_t pid = *(uint32_t *)p;
    uint8_t  state = p[4];

    if (state > 2 || pid == 0) {

        net_send_int32(fd, CMD_ERROR);
        return 1;
    }

    if (g_debugging) {

        g_pending_sig_pid = (int)pid;
        if      (state == 0) g_pending_signal = 0;
        else if (state == 1) g_pending_signal = 17;
        else                 g_pending_signal = 9;
    } else {

        int sig;
        if      (state == 0) sig = 19;
        else if (state == 1) sig = 17;
        else                 sig = 9;
        kill((int)pid, sig);
    }

    net_send_int32(fd, CMD_SUCCESS);
    return 0;
}
```

- Request: 5 bytes, `u32 pid` (LE, offset 0) then `u8 state` (offset 4). No struct in protocol.h. `ref/ps4debug-NG/PROTOCOL.md:748-751` documents it the same way.
- `state`: 0 = resume, 1 = stop, 2 = kill.
- Works **without** a debugger attached: the `else` branch calls `kill(pid, sig)` (signal 19 for resume, 17 for stop, 9 for kill).
- The raw numbers 17 and 19 are used as-is. Under FreeBSD numbering, 17 is SIGSTOP and 19 is SIGCONT. That is my reading, not stated in the repo. `PROTOCOL.md:756` is garbled ("SIGSTOP(19 - FreeBSD SIGSTOP)" and "SIGTSTP(17)"), so do not copy its labels.
- Response: `CMD_SUCCESS` (u32), `CMD_DATA_NULL`, or `CMD_ERROR`.

Client, PS4CheaterNeo libdebug (`ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.Debug.cs:185-278`):
```csharp
        private const int CMD_DEBUG_EXT_STOPGO_PACKET_SIZE = 5; //ps4debug ext fw by ctn123
```
```csharp
        public void ProcessExtStop(int pid)
        {
            if (ExtFWVersion <= 0) return;
            try
            {
                CheckConnected();
                SendCMDPacket(CMDS.CMD_DEBUG_EXT_STOPGO, CMD_DEBUG_EXT_STOPGO_PACKET_SIZE, (uint)pid, (byte)1);
                CheckStatus();
            }
            ...
        }
```
Resume is `(uint)pid, (byte)0` and kill is `(uint)pid, (byte)2` (lines 242-257, 263-278). Each returns early unless `ExtFWVersion > 0`.

`ExtFWVersion` is set by `GetExtFWVersion()` (`ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.cs:495-515`), which sends `CMDS.CMD_EXT_FW_VERSION` and reads a 2-byte reply. Check that enum value (not verified in this pass).

Also note: PS4CheaterNeo's `libframe4/FRAME4.Debug.cs:29` uses `CMD_DEBUG_EXT_STOPGO_PACKET_SIZE = 8` with `(pid, 0/1)` (lines 256, 269). That is a different wire format, for FRAME4 only.

### 4d. Attach (port 755 callback) and the Neo pause button

Attach: `ref/libdebug/PS4DBG/PS4DBG.Debug.cs:85-116` starts a debug listener thread and then sends `CMD_DEBUG_ATTACH`. Server packet (`ref/ps4debug-NG/debugger/include/protocol.h:492-496`):
```c
struct cmd_debug_attach_packet {
    uint32_t pid;
    uint32_t client_ip;
} __attribute__((packed));
#define CMD_DEBUG_ATTACH_PACKET_SIZE 8
```
Extended form adds a port (`protocol.h:498-504`, `debug_port`, 0 = default 755). The 755 default is in `ref/libdebug/PS4DBG/PS4DBG.cs:43` (`PS4DBG_DEBUG_PORT = 755`).

Server attach requires no prior attach (`debug.c:283-301`, returns `CMD_ALREADY_DEBUG` if already attached).

Neo pause button:
- `PS4CheaterNeo/PS4CheaterNeo/Query.Designer.cs:648` `this.PauseBtn.Click += new System.EventHandler(this.PauseBtn_Click);`
- `PS4CheaterNeo/PS4CheaterNeo/Query.cs:1749-1756`:
```csharp
        private void PauseBtn_Click(object sender, EventArgs e) => PauseResume(ProcessStatus.Pause);

        private void PauseResume(ProcessStatus? newStatus = null)
        {
            processStatus = newStatus != null ? (ProcessStatus)newStatus : (processStatus == ProcessStatus.Pause ? ProcessStatus.Resume : ProcessStatus.Pause);
            ComboItem process = (ComboItem)ProcessesBox.SelectedItem;
            PS4Tool.AttachDebugger((int)process.Value, (string)process.Text, processStatus);
        }
```
- Dispatcher `PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/PS4Tool.cs:561-607` (`AttachDebugger`), priority order:
```csharp
            if (ps4s[0].IsConnected && ps4s[0] is PS4DBG ps4dbg && ps4dbg.ExtFWVersion > 0)
            {
                ...
                    if (newStatus == ProcessStatus.Pause) ps4dbg.ProcessExtStop(processID);
                    else ps4dbg.ProcessExtResume(processID);
            }
            else if (ps4s[0].IsConnected && ps4s[0] is FRAME4 fram4 && fram4.Version != "0" &&
                Version.TryParse(fram4.Version, out Version current) && Version.TryParse("0.2.15", out Version target) &&
                current >= target)
            {
                ...  fram4.ProcessExtStop(processID) / fram4.ProcessExtResume(processID)
            }
            else if (ps4s[0].IsConnected && ps4s[0].IsDebugging)
            {
                ...  ps4s[0].ProcessStop();  /  ps4s[0].ProcessResume();
            }
            else if (MessageBox.Show("This experimental feature requires Attach ps4 Debugging\n\n" + ...
            else
            {
                ps4s[0].AttachDebugger(processID, null);
                ps4s[0].Notify(222, "attached to " + processName);
                processStatus = ProcessStatus.Pause;
            }
```
So with a plain ps4debug that is not attached, the Pause button shows a warning, attaches (CMD_DEBUG_ATTACH), and sets state to Pause, but does **not** call ProcessStop in that branch. Open question whether this is intended.

`ProcessStatus` enum: `PS4DBG/PS4Tool.cs:645-649` has `None, Resume, Pause, ...`.

Slow-motion mode also toggles pause (`Query.cs:2457-2471`), not analysed further.

### 4e. PS4Cheater (ctn123)

No pause/stop code in PS4Cheater (`grep` for ProcessStop, STOPGO, Pause found nothing in `ref/PS4Cheater/PS4_Cheater`). Not analysed further.

---

## Open questions
1. **0xBDBB0010 vs pause.** libdebug `ProcessStop()` sends `1` on `CMD_DEBUG_STOPGO` (0xBDBB0010). The server handler (`debug.c:996`) maps `1` to `PT_CONTINUE` with signal 17, and `PROTOCOL.md:729-732` claims "halted mode". These disagree. Needs a check against the payload build that is actually running on the console.
2. **0xBDBB0500 signal numbers.** The server uses raw signal numbers 19, 17, 9 for resume, stop, kill. The meaning (FreeBSD SIGCONT 19, SIGSTOP 17) is inferred, not stated in the repo. `PROTOCOL.md:756` has garbled labels. Confirm before relying on it.
3. **Standalone pause.** `debug_process_stop_handle` does not check `g_debug_ctx.attached`. The `else` branch uses `kill()` directly, so 0xBDBB0500 looks like it works without attach. Not tested on hardware (we must not touch the console).
4. **Which payload has 0xBDBB0500.** Neo gates it on `ExtFWVersion > 0` (`CMD_EXT_FW_VERSION`, value not checked). Does the stock ps4debug-NG build support 0xBDBB0500, or only the ctn123 ext firmware? Check the `ref/ps4debug-NG` build's `debug.c` dispatch matches Neo's expectations.
5. **Two libdebug copies differ.** `ref/libdebug` has `NET_MAX_LENGTH = 8192` and `ref/PS4CheaterNeo/.../libdebug/PS4DBG.cs` has `0x20000`. Which one PS4Mem will mirror has not been decided.
6. **Section filter defaults in PS4Cheater.** Not found: no name list, no size limit, no prot filter beyond the read bit and `Prot == 3` for GameInfo. The "anon / exe / Game / dlmalloc" names are not in this source.
7. **`PROTOCOL.md` line references** (`kern.c:...`) are out of date with the source (the handlers are in `debug.c` and `proc.c`). Trust the `path:line` here, not the doc's.
8. **Neo `SectionTool` hidden-section detection** (`SectionTool.cs:427-429`) is turned on by a setting. The default of `SectionViewDetectHiddenSection` was not checked.

Saved to: `/Users/egorgrigorciuc/Projectsnosync/ps4cheaterclone/docs/research/sections-and-pause.md`
