# Core wire commands: libdebug (C#) client vs ps4debug-NG server

Research notes. Every block is verbatim from the file and line range in its heading.
Paths are relative to `ref/` (absolute: `/Users/egorgrigorciuc/Projectsnosync/ps4cheaterclone/ref/`).

Abbreviations: `LD` = `ref/libdebug/PS4DBG/` (client), `NG` = `ref/ps4debug-NG/debugger/` (server).

---

## 1. Packet header

### 1.1 Constants, client: `ref/libdebug/PS4DBG/PS4DBG.cs:20-44`

```csharp
        private const uint BROADCAST_MAGIC = 0xFFFFAAAA;
        private const int BROADCAST_PORT = 1010;
        // from protocol.h
        // each packet starts with the magic
        // each C# base type can translate into a packet field
        // some packets, such as write take an additional data whose length will be specified in the cmd packet data field structure specific to that cmd type
        // ushort - 2 bytes | uint - 4 bytes | ulong - 8 bytes
        private const uint CMD_PACKET_MAGIC = 0xFFAABBCC;

        //  struct cmd_packet {
        //    uint32_t magic;
        //    uint32_t cmd;
        //    uint32_t datalen;
        //    // (field not actually part of packet, comes after)
        //    uint8_t* data;
        //  }
        //  __attribute__((packed));
        //  #define CMD_PACKET_SIZE 12
        private const int CMD_PACKET_SIZE = 12;

        // some global values
        private const string LIBRARY_VERSION = "1.2";
        private const int NET_MAX_LENGTH = 8192;
        private const int PS4DBG_DEBUG_PORT = 755;
        private const int PS4DBG_PORT = 744;
```

### 1.2 Header struct, client: `ref/libdebug/PS4DBG/PS4DBG.cs:398-403`

```csharp
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct CMDPacket {
            public uint magic;
            public uint cmd;
            public uint datalen;
        }
```

### 1.3 Header send, client: `ref/libdebug/PS4DBG/PS4DBG.cs:306-311` and `:356-361`

```csharp
        private void SendCMDPacket(CMDS cmd, int length, params object[] fields) {
            // Create a new Command (CMD) Packet object and initialize its members
            CMDPacket packet = new CMDPacket();
            packet.magic = CMD_PACKET_MAGIC; // Set the magic number for the packet
            packet.cmd = (uint)cmd;          // Set the command identifier
            packet.datalen = (uint)length;   // Set the data length of the packet
```

```csharp
            // Send the Command (CMD) Packet header? to the PS4 System
            SendData(GetBytesFromObject(packet), CMD_PACKET_SIZE);

            // Check if the data byte array is not null, and in case of it not being
            // null, use it and send it's content to the PS4
            if (data != null) SendData(data, length);
        }
```

### 1.4 Field serialization, client: `ref/libdebug/PS4DBG/PS4DBG.cs:329-346`

Each argument is serialized with `BitConverter` (host endianness, little-endian on x86-64 and ARM64):

```csharp
                    switch (field) {
                        case char c: bytes = BitConverter.GetBytes(c); break;
                        case byte b: bytes = new byte[] { b }; break;
                        case short s: bytes = BitConverter.GetBytes(s); break;
                        case ushort us: bytes = BitConverter.GetBytes(us); break;
                        case int i: bytes = BitConverter.GetBytes(i); break;
                        case uint u: bytes = BitConverter.GetBytes(u); break;
                        case long l: bytes = BitConverter.GetBytes(l); break;
                        case ulong ul: bytes = BitConverter.GetBytes(ul); break;
                        case byte[] ba: bytes = ba; break;
```

Note: header and body are sent with two separate `SendData` calls (header first, then body). No padding, no `htonl`.

### 1.5 Server struct, `ref/ps4debug-NG/debugger/include/protocol.h:13-15` and `:96-102`

```c
#define PACKET_VERSION          "1.3"
#define PACKET_BRANDING         "ps4debug-NG by Pharaoh2k v" PS4DEBUG_NG_VERSION_STR
#define PACKET_MAGIC            0xFFAABBCC
```

```c
struct cmd_packet {
    uint32_t magic;
    uint32_t cmd;
    uint32_t datalen;
    void *data;
} __attribute__((packed));
#define CMD_PACKET_SIZE 12
```

### 1.6 Server header read and validation, `ref/ps4debug-NG/debugger/source/server.c:118-176`

```c
        if(FD_ISSET(fd, &sfd)) {

            memset(&packet, NULL, CMD_PACKET_SIZE);

            rsize = net_recv_all(fd, &packet, CMD_PACKET_SIZE, 0);

            if (rsize <= 0) {
                goto error;
            }

            if (errno == 54 || errno == 58 || errno == 9) {
                goto error;
            }
        } else {
```

```c
        if (packet.magic != PACKET_MAGIC) {
            continue;
        }

        if (rsize != CMD_PACKET_SIZE) {
            continue;
        }

        length = packet.datalen;
        if (length) {

            data = net_alloc_buffer(length);
            if (!data) {
                goto error;
            }

            r = net_recv_all(fd, data, length, 1);
            if (r != (int)length) {
                goto error;
            }

            packet.data = data;
        } else {
            packet.data = NULL;
        }
```

Observation: on magic mismatch the server does `continue` without consuming `datalen` body bytes, so the stream can desync. Clients must send valid headers only.

### 1.7 Byte order, `ref/ps4debug-NG/debugger/source/net.c:68-72`

```c
int net_send_int32(int fd, uint32_t status) {
    uint32_t d = status;

    return net_send_all(fd, &d, sizeof(uint32_t));
}
```

`ref/ps4debug-NG/PROTOCOL.md:29-31` (section 1.3):

```
All multi-byte integers are **little-endian** (native x86-64 layout). `__attribute__((packed))`
is used on every wire struct, so there is no implicit padding between fields.
```

No `htonl`/`htons` on the wire fields. (`sceNetHtons` is used only for the TCP port in `server.c:311`.)

---

## 2. Connect handshake and socket options

### 2.1 Client connect, `ref/libdebug/PS4DBG/PS4DBG.cs:161-176`

```csharp
        public void Connect() {
            // Before trying to open the connection between our PC and the
            // PS4 System, we first check if it's already been opened.
            if (IsConnected) {
                DebugPrintWarning("Oops, the connection is already open!");
                return;
            }

            sock.NoDelay = true;
            sock.ReceiveBufferSize = NET_MAX_LENGTH;
            sock.SendBufferSize = NET_MAX_LENGTH;
            sock.ReceiveTimeout = 1000 * 10;

            sock.Connect(enp);
            IsConnected = true;
        }
```

Port: `ref/libdebug/PS4DBG/PS4DBG.cs:44` `private const int PS4DBG_PORT = 744;`

Connect timeout: none set (blocking `sock.Connect(enp)`, OS default). `SendTimeout` not set (default infinite). `ReceiveTimeout` = 10000 ms.

No greeting, auth or broadcast is sent after TCP connect by the client. The only UDP step is optional discovery (`PS4DBG.cs:95-123`, `FindPlayStation`, UDP port 1010, 4-second receive timeout at `:99`).

### 2.2 Server accept path, `ref/ps4debug-NG/debugger/source/server.c:344-377`

```c
    while(1) {
        scePthreadYield();

        errno = NULL;
        fd = sceNetAccept(serv, (struct sockaddr *)&client, &len);
        if (fd < 0) {
            if (errno == 163) {

                break;
            }
            sceKernelUsleep(1000 * 250);
            continue;
        }
        if (!errno) {
            svc = alloc_client();
            if (!svc) {
                sceNetSocketClose(fd);
                sceKernelUsleep(1000 * 250);
                continue;
            }

            configure_socket(fd);

            svc->fd = fd;
            svc->debugging = 0;
            memcpy(&svc->client, &client, sizeof(svc->client));
            memset(&svc->dbgctx, NULL, sizeof(svc->dbgctx));

            ScePthread thread;
            scePthreadCreate(&thread, NULL, (void * (*)(void *))handle_client, (void *)svc, "clienthandler");
        }
```

No greeting is written by the server on accept. Listen backlog and port, `ref/ps4debug-NG/debugger/source/server.c:326` and `ref/ps4debug-NG/debugger/include/server.h:15-16`:

```c
    r = sceNetListen(serv, SERVER_MAXCLIENTS * 2);
```

```c
#define SERVER_PORT             744
#define SERVER_MAXCLIENTS       8
```

If all 8 slots are used (`alloc_client`, `server.c:9-18`), the new socket is closed immediately (`server.c:358-363`).

### 2.3 Server socket options, `ref/ps4debug-NG/debugger/source/server.c:207-228`

```c
void configure_socket(int fd) {
    int flag;

    flag = 1;
    sceNetSetsockopt(fd, SOL_SOCKET, SO_NBIO, (char *)&flag, sizeof(flag));

    flag = 1;
    sceNetSetsockopt(fd, IPPROTO_TCP, TCP_NODELAY, (char *)&flag, sizeof(flag));

    flag = 1;
    sceNetSetsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, (char *)&flag, sizeof(flag));

    flag = 1;
    sceNetSetsockopt(fd, SOL_SOCKET, SO_KEEPALIVE, (char *)&flag, sizeof(flag));

    flag = 1;
    sceNetSetsockopt(fd, SOL_SOCKET, SO_REUSEADDR, (char *)&flag, sizeof(flag));

    flag = 65537;
    sceNetSetsockopt(fd, SOL_SOCKET, SO_SNDBUF, (char *)&flag, sizeof(flag));
    sceNetSetsockopt(fd, SOL_SOCKET, SO_RCVBUF, (char *)&flag, sizeof(flag));
}
```

Observation: `SO_NBIO` is set to 1 on the server side. Body reads use `net_recv_all(..., force=1)`, which spins on `EWOULDBLOCK` (`net.c:37-66`), so it still works.

### 2.4 Server read loop idle behaviour, `ref/ps4debug-NG/debugger/source/server.c:102-112` and `:148`

```c
    struct timeval tv;
    memset(&tv, NULL, sizeof(tv));
    tv.tv_usec = 1000;
```

```c
            sceKernelUsleep(250);
            continue;
```

---

## 3. Status codes

### 3.1 Server, `ref/ps4debug-NG/debugger/include/protocol.h:87-94`

```c
#define CMD_SUCCESS              0x80000000
#define CMD_ERROR                0xF0000001
#define CMD_TOO_MUCH_DATA        0xF0000002
#define CMD_DATA_NULL            0xF0000003
#define CMD_ALREADY_DEBUG        0xF0000004
#define CMD_INVALID_INDEX        0xF0000005

#define CMD_FATAL_STATUS(s) ((s >> 28) == 15)
```

### 3.2 Client enum, `ref/libdebug/PS4DBG/PS4DBG.Enums.cs:5-12`

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

Values match the server (both sides checked).

### 3.3 Client status read and check, `ref/libdebug/PS4DBG/PS4DBG.cs:247-255` and `:297-301`

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
```

```csharp
        private CMD_STATUS ReceiveStatus() {
            byte[] status = new byte[4];
            sock.Receive(status, 4, SocketFlags.None);
            return (CMD_STATUS)BitConverter.ToUInt32(status, 0);
        }
```

Observation: `sock.Receive(status, 4, ...)` is a single call with no loop. A short read would leave the status wrong. The same single-call pattern is used for the count fields in list and maps (see sections 5 and 7). Any status other than `0x80000000` throws.

### 3.4 Server status send helper, `ref/ps4debug-NG/debugger/source/net.c:9-35`

Send loop, verbatim:

```c
int net_send_all(int fd, const void *data, int length) {
    const unsigned char *bytes = (const unsigned char *)data;
    int left = length;
    int offset = 0;
    int sent = 0;

    errno = NULL;

    while (left > 0) {
        if (left > 0x10000) {
            sent = write(fd, bytes + offset, 0x10000);
        } else {
            sent = write(fd, bytes + offset, left);
        }

        if (sent <= 0) {
            if(errno && errno != EWOULDBLOCK) {
                return sent;
            }
        } else {
            offset += sent;
            left -= sent;
        }
    }

    return offset;
}
```

### 3.5 Server: a non-zero handler return closes the connection, `ref/ps4debug-NG/debugger/source/server.c:178-191`

```c
        if (packet.cmd == CMD_DEBUG_ATTACH) {
            r = debug_attach_handle_svc(svc, &packet);
        } else {
            r = cmd_handler(fd, &packet, client_idx);
        }

        if (data) {
            free(data);
            data = NULL;
        }

        if (r) {
            goto error;
        }
```

`error:` label calls `free_client(svc)` (`server.c:201-204`), which closes the socket (`server.c:20-23`):

```c
void free_client(struct server_client *svc) {
    svc->id = 0;
    sceNetSocketClose(svc->fd);
```

Observation: many handlers `return 1` after sending an error status (for example `proc_read_handle` at `proc.c:323-325`, `proc_list_handle` empty list at `proc.c:316-317`, `proc_maps_handle` at `proc.c:692-694`). In those cases the server sends the status and then drops the connection. `proc_write_handle` returns 0 even when the final status is `CMD_ERROR` (`proc.c:538-542`), so the session survives a failed write.

---

## 4. CMD_VERSION (0xBD000001)

### 4.1 ID, `ref/ps4debug-NG/debugger/include/protocol.h:17`

```c
#define CMD_VERSION             0xBD000001
```

Client enum, `ref/libdebug/PS4DBG/PS4DBG.Enums.cs:15`:

```csharp
            CMD_VERSION = 0xBD000001,
```

### 4.2 Server handler, `ref/ps4debug-NG/debugger/source/server.c:35-40` and dispatch at `:52-54`

```c
int handle_version(int fd, struct cmd_packet *packet) {
    uint32_t len = strlen(PACKET_VERSION);
    net_send_all(fd, &len, sizeof(uint32_t));
    net_send_all(fd, PACKET_VERSION, len);
    return 0;
}
```

```c
        case CMD_VERSION:
            return handle_version(fd, packet);
```

Reply: **no status word**. `uint32_t len` (4 bytes), then `len` bytes of `"1.3"` with no NUL.

### 4.3 Client, `ref/libdebug/PS4DBG/PS4DBG.cs:197-212` and `:217`

```csharp
        public string GetConsoleDebugVersion() {
            CheckConnected();
            SendCMDPacket(CMDS.CMD_VERSION, 0);


            byte[] ldata = new byte[4];
            sock.Receive(ldata, 4, SocketFlags.None);

            int length = BitConverter.ToInt32(ldata, 0);

            byte[] data = new byte[length];
            sock.Receive(data, length, SocketFlags.None);

            return ConvertASCII(data, 0);
        }
```

```csharp
        public string GetLibraryDebugVersion() => LIBRARY_VERSION;
```

`LIBRARY_VERSION` is `"1.2"` (`PS4DBG.cs:41`). It is the client's own version, not the server's.

Note: the client does not call `CheckStatus()` for version. This matches the server (no status word). Sequence: send 12-byte header with `cmd=0xBD000001, datalen=0`; read 4 bytes (`len`); read `len` bytes.

### 4.4 Client string helper, `ref/libdebug/PS4DBG/PS4DBG.cs:83-90`

```csharp
        public static string ConvertASCII(byte[] data, int offset) {
            int length = Array.IndexOf<byte>(data, 0, offset) - offset;
            if (length < 0) {
                length = data.Length - offset;
            }

            return Encoding.ASCII.GetString(data, offset, length);
        }
```

Observation: `Array.IndexOf<byte>(data, 0, offset)` searches from `offset` to the end of the buffer. It does not stop at a fixed field width. For fixed-width NUL-padded fields (32-byte process name), it can run into the next field. Use a 32-byte cap in the Swift port.

---

## 5. Process list (0xBDAA0001)

### 5.1 ID, `ref/ps4debug-NG/debugger/include/protocol.h:22`

```c
#define CMD_PROC_LIST           0xBDAA0001
```

### 5.2 Entry struct, `ref/ps4debug-NG/debugger/include/kdbg.h:12-15`

```c
struct proc_list_entry {
    char p_comm[32];
    int pid;
}  __attribute__((packed));
```

Entry size 36 bytes: name at offset 0 (32 bytes, NUL-padded), pid (int32 LE) at offset 32.

### 5.3 Server handler, `ref/ps4debug-NG/debugger/source/proc.c:290-318`

```c
int proc_list_handle(int fd, struct cmd_packet *packet) {
    void *data;
    uint64_t num;
    uint32_t length;

    sys_proc_list(NULL, &num);

    if(num > 0) {
        length = sizeof(struct proc_list_entry) * num;
        data = net_alloc_buffer(length);
        if(!data) {
            net_send_int32(fd, CMD_DATA_NULL);
            return 1;
        }

        sys_proc_list(data, &num);

        net_send_int32(fd, CMD_SUCCESS);
        net_send_all(fd, &num, sizeof(uint32_t));
        net_send_all(fd, data, length);

        free(data);

        return 0;
    }

    net_send_int32(fd, CMD_DATA_NULL);
    return 1;
}
```

Reply: `CMD_SUCCESS` (u32), `num` (u32 LE), `num * 36` bytes. Empty list: `CMD_DATA_NULL` and the connection is closed (return 1).

### 5.4 Client, `ref/libdebug/PS4DBG/PS4DBG.Proc.cs:13,25` and `:37-61`

```csharp
        // receive size
        private const int PROC_LIST_ENTRY_SIZE = 36;
```

```csharp
        public ProcessList GetProcessList() {
            CheckConnected();

            SendCMDPacket(CMDS.CMD_PROC_LIST, 0);
            CheckStatus();

            // recv count
            byte[] bytes = new byte[4];
            sock.Receive(bytes, 4, SocketFlags.None);
            int number = BitConverter.ToInt32(bytes, 0);

            // recv data
            byte[] data = ReceiveData(number * PROC_LIST_ENTRY_SIZE);

            // parse data
            string[] names = new string[number];
            int[] pids = new int[number];
            for (int i = 0; i < number; i++) {
                int offset = i * PROC_LIST_ENTRY_SIZE;
                names[i] = ConvertASCII(data, offset);
                pids[i] = BitConverter.ToInt32(data, offset + 32);
            }

            return new ProcessList(number, names, pids);
        }
```

Observation: the name is parsed with `ConvertASCII` (see 4.4), not capped at 32 bytes. The pid offset (32) is correct.

---

## 6. Process info (0xBDAA000A)

### 6.1 ID, `ref/ps4debug-NG/debugger/include/protocol.h:32` and packet size `:447-448`

```c
#define CMD_PROC_INFO           0xBDAA000A
```

```c
struct cmd_proc_info_packet {
    uint32_t pid;
} __attribute__((packed));
struct cmd_proc_info_response {
    uint32_t pid;
    char name[40];
    char path[64];
    char titleid[16];
    char contentid[64];
} __attribute__((packed));
#define CMD_PROC_INFO_PACKET_SIZE 4
#define CMD_PROC_INFO_RESPONSE_SIZE 188
```

Field offsets in the 188-byte response: `pid` 0 (u32), `name` 4 (40), `path` 44 (64), `titleid` 108 (16), `contentid` 124 (64).

### 6.2 Server handler, `ref/ps4debug-NG/debugger/source/proc.c:1681-1705`

```c
int proc_info_handle(int fd, struct cmd_packet *packet) {
    struct cmd_proc_info_packet *ip;
    struct sys_proc_info_args args;
    struct cmd_proc_info_response resp;

    ip = (struct cmd_proc_info_packet *)packet->data;

    if(ip) {
        sys_proc_cmd(ip->pid, SYS_PROC_INFO, &args);

        resp.pid = args.pid;
        memcpy(resp.name, args.name, sizeof(resp.name));
        memcpy(resp.path, args.path, sizeof(resp.path));
        memcpy(resp.titleid, args.titleid, sizeof(resp.titleid));
        memcpy(resp.contentid, args.contentid, sizeof(resp.contentid));

        net_send_int32(fd, CMD_SUCCESS);
        net_send_all(fd, &resp, CMD_PROC_INFO_RESPONSE_SIZE);
        return 0;
    }

    net_send_int32(fd, CMD_DATA_NULL);

    return 0;
}
```

Reply: `CMD_SUCCESS`, then 188 bytes. The return value of `sys_proc_cmd` is not checked.

### 6.3 Client struct, `ref/libdebug/Process.cs:9-40`

```csharp
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct ProcessInfo {

        /// <summary>
        /// The Process ID (PID)
        /// </summary>
        public int pid;

        /// <summary>
        /// The Process Name
        /// </summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 40)]
        public string name;

        /// <summary>
        /// The Path of the Process Executable?
        /// </summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string path;

        /// <summary>
        /// The Process Title ID?
        /// </summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string titleid;

        /// <summary>
        /// The Process Content ID?
        /// </summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string contentid;
    }
```

### 6.4 Client call, `ref/libdebug/PS4DBG/PS4DBG.Proc.cs:21,29` and `:458-466`

```csharp
        private const int CMD_PROC_INFO_PACKET_SIZE = 4;
```

```csharp
        private const int PROC_PROC_INFO_SIZE = 188;
```

```csharp
        public ProcessInfo GetProcessInfo(int pid) {
            CheckConnected();

            SendCMDPacket(CMDS.CMD_PROC_INFO, CMD_PROC_INFO_PACKET_SIZE, pid);
            CheckStatus();

            byte[] data = ReceiveData(PROC_PROC_INFO_SIZE);
            return (ProcessInfo)GetObjectFromBytes(data, typeof(ProcessInfo));
        }
```

Field sizes match the server (4 + 40 + 64 + 16 + 64 = 188). `Pack = 1` means no padding.

---

## 7. Process maps (0xBDAA0004)

### 7.1 ID and request, `ref/ps4debug-NG/debugger/include/protocol.h:26` and `:190-193`

```c
#define CMD_PROC_MAPS           0xBDAA0004
```

```c
struct cmd_proc_maps_packet {
    uint32_t pid;
} __attribute__((packed));
#define CMD_PROC_MAPS_PACKET_SIZE 4
```

### 7.2 Entry struct, `ref/ps4debug-NG/debugger/include/proc.h:11-17`

```c
struct proc_vm_map_entry {
    char name[32];
    uint64_t start;
    uint64_t end;
    uint64_t offset;
    uint16_t prot;
} __attribute__((packed));
```

Entry size 58 bytes: name (0, 32 bytes), start (32, u64), end (40, u64), offset (48, u64), prot (56, u16).

### 7.3 Server handler, `ref/ps4debug-NG/debugger/source/proc.c:652-695`

```c
int proc_maps_handle(int fd, struct cmd_packet *packet) {
    struct cmd_proc_maps_packet *mp;
    struct sys_proc_vm_map_args args;
    uint32_t size;
    uint32_t num;

    mp = (struct cmd_proc_maps_packet *)packet->data;

    if(mp) {
        memset(&args, NULL, sizeof(args));

        if(sys_proc_cmd(mp->pid, SYS_PROC_VM_MAP, &args)) {
            net_send_int32(fd, CMD_ERROR);
            return 1;
        }

        size = args.num * sizeof(struct proc_vm_map_entry);

        args.maps = (struct proc_vm_map_entry *)net_alloc_buffer(size);
        if(!args.maps) {
            net_send_int32(fd, CMD_DATA_NULL);
            return 1;
        }

        if(sys_proc_cmd(mp->pid, SYS_PROC_VM_MAP, &args)) {
            free(args.maps);
            net_send_int32(fd, CMD_ERROR);
            return 1;
        }

        net_send_int32(fd, CMD_SUCCESS);
        num = (uint32_t)args.num;
        net_send_all(fd, &num, sizeof(uint32_t));
        net_send_all(fd, args.maps, size);

        free(args.maps);

        return 0;
    }

    net_send_int32(fd, CMD_ERROR);

    return 1;
}
```

Reply: `CMD_SUCCESS`, `num` (u32), `num * sizeof(struct proc_vm_map_entry)` bytes. Note `size` is computed from `args.num` before the second call; the second call fills `args.maps`, and `num` is then re-read from `args.num`.

### 7.4 Client, `ref/libdebug/PS4DBG/PS4DBG.Proc.cs:15,26` and `:98-127`

```csharp
        private const int CMD_PROC_MAPS_PACKET_SIZE = 4;
```

```csharp
        private const int PROC_MAP_ENTRY_SIZE = 58;
```

```csharp
        public ProcessMap GetProcessMaps(int pid) {
            CheckConnected();

            SendCMDPacket(CMDS.CMD_PROC_MAPS, CMD_PROC_MAPS_PACKET_SIZE, pid);
            CheckStatus();

            // recv count
            byte[] bnumber = new byte[4];
            sock.Receive(bnumber, 4, SocketFlags.None);
            int number = BitConverter.ToInt32(bnumber, 0);

            // recv data
            byte[] data = ReceiveData(number * PROC_MAP_ENTRY_SIZE);

            // parse data
            MemoryEntry[] entries = new MemoryEntry[number];
            for (int i = 0; i < number; i++) {
                int offset = i * PROC_MAP_ENTRY_SIZE;
                entries[i] = new MemoryEntry {
                    name = ConvertASCII(data, offset),
                    start = BitConverter.ToUInt64(data, offset + 32),
                    end = BitConverter.ToUInt64(data, offset + 40),
                    offset = BitConverter.ToUInt64(data, offset + 48),
                    prot = BitConverter.ToUInt16(data, offset + 56)
                };

            }

            return new ProcessMap(pid, entries);
        }
```

### 7.5 Client MemoryEntry, `ref/libdebug/Process.cs:68-93`

```csharp
    public class MemoryEntry {
        /// <summary>
        /// Start address of the memory region
        /// </summary>
        public ulong start;

        /// <summary>
        /// End address of the memory region
        /// </summary>
        public ulong end;

        /// <summary>
        /// Offset of the memory region
        /// </summary>
        public ulong offset;

        /// <summary>
        /// Protection attributes of the memory region
        /// </summary>
        public uint prot;

        /// <summary>
        /// Name of the memory region
        /// </summary>
        public string name;
    }
```

Observation: the same `ConvertASCII` issue applies to `name` (no 32-byte cap).

---

## 8. Read memory (0xBDAA0002)

### 8.1 ID and request struct, `ref/ps4debug-NG/debugger/include/protocol.h:23` and `:104-109`

```c
#define CMD_PROC_READ           0xBDAA0002
```

```c
struct cmd_proc_read_packet {
    uint32_t pid;
    uint64_t address;
    uint32_t length;
} __attribute__((packed));
#define CMD_PROC_READ_PACKET_SIZE 16
```

Body: pid u32 (0), address u64 (4), length u32 (12). Total 16 bytes.

### 8.2 Client, `ref/libdebug/PS4DBG/PS4DBG.Proc.cs:13` and `:70-76`

```csharp
        private const int CMD_PROC_READ_PACKET_SIZE = 16;
```

```csharp
        public byte[] ReadMemory(int pid, ulong address, int length) {
            CheckConnected();

            SendCMDPacket(CMDS.CMD_PROC_READ, CMD_PROC_READ_PACKET_SIZE, pid, address, length);
            CheckStatus();
            return ReceiveData(length);
        }
```

The client does no chunking and sets no maximum. `ReceiveData` reads until `length` bytes are collected (`PS4DBG.cs:277-295`):

```csharp
        private byte[] ReceiveData(int length) {
            MemoryStream s = new MemoryStream();

            int left = length;
            int recv = 0;
            while (left > 0) {
                byte[] b = new byte[NET_MAX_LENGTH];
                recv = sock.Receive(b, NET_MAX_LENGTH, SocketFlags.None);
                s.Write(b, 0, recv);
                left -= recv;
            }

            byte[] data = s.ToArray();

            s.Dispose();
            GC.Collect();

            return data;
        }
```

Observation: `ReceiveData` has no read timeout guard beyond `ReceiveTimeout` (10 s). If the peer closes the socket, `Receive` returns 0 and the loop spins forever (no `recv == 0` check). Handle this in the Swift port.

### 8.3 Server handler, `ref/ps4debug-NG/debugger/source/proc.c:320-357`

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

Observations:
- Reply: `CMD_SUCCESS` (u32), then exactly `length` raw bytes, sent in 0x10000 chunks, with a final tail chunk. No length prefix.
- `CMD_SUCCESS` is sent before the memory is read. `sys_proc_rw` return values are ignored, so an unreadable address gives zero-filled bytes with `CMD_SUCCESS`. The client cannot detect a failed read.
- No server-side maximum length. `length == 0` gives only `CMD_SUCCESS`.

---

## 9. Write memory (0xBDAA0003)

### 9.1 ID and request struct, `ref/ps4debug-NG/debugger/include/protocol.h:25` and `:168-173`

```c
#define CMD_PROC_WRITE          0xBDAA0003
```

```c
struct cmd_proc_write_packet {
    uint32_t pid;
    uint64_t address;
    uint32_t length;
} __attribute__((packed));
#define CMD_PROC_WRITE_PACKET_SIZE 16
```

### 9.2 Status constants, `ref/ps4debug-NG/debugger/include/protocol.h:185-188`

```c
#define PROC_WRITE_STATUS_OK             0u
#define PROC_WRITE_STATUS_INVALID        1u
#define PROC_WRITE_STATUS_VERIFY_FAILED  3u
#define PROC_WRITE_STATUS_RW_FAILED      4u
```

These are internal. The client only sees `CMD_SUCCESS` or `CMD_ERROR`.

### 9.3 Client, `ref/libdebug/PS4DBG/PS4DBG.Proc.cs:14` and `:84-91`

```csharp
        private const int CMD_PROC_WRITE_PACKET_SIZE = 16;
```

```csharp
        public void WriteMemory(int pid, ulong address, byte[] data) {
            CheckConnected();

            SendCMDPacket(CMDS.CMD_PROC_WRITE, CMD_PROC_WRITE_PACKET_SIZE, pid, address, data.Length);
            CheckStatus();
            SendData(data, data.Length);
            CheckStatus();
        }
```

Sequence: header(cmd=0xBDAA0003, datalen=16) + body(pid u32, address u64, length u32) → read status (must be `CMD_SUCCESS`, server ack) → send `length` raw bytes (`SendData` chunks at 8192) → read final status (`CMD_SUCCESS` or throw).

### 9.4 Server handler, `ref/ps4debug-NG/debugger/source/proc.c:475-543`

```c
int proc_write_handle(int fd, struct cmd_packet *packet) {
    struct cmd_proc_write_packet *wp = (struct cmd_proc_write_packet *)packet->data;

    if (!wp) {
        net_send_int32(fd, CMD_DATA_NULL);
        return 1;
    }

    void *data = net_alloc_buffer(0x10000);
    if (!data) {
        net_send_int32(fd, CMD_DATA_NULL);
        return 1;
    }

    void *readback = net_alloc_buffer(0x10000);
    if (!readback) {
        free(data);
        net_send_int32(fd, CMD_DATA_NULL);
        return 1;
    }

    net_send_int32(fd, CMD_SUCCESS);

    uint64_t length = wp->length;
    uint64_t address = wp->address;
    uint8_t status = PROC_WRITE_STATUS_OK;
    int request_valid = length == 0
        || ((int32_t)wp->pid > 0 && address + length >= address);
    if (!request_valid) {
        status = PROC_WRITE_STATUS_INVALID;
    }

    if (length > 0) {
        while (length > 0x10000) {
            if (net_recv_all(fd, data, 0x10000, 1) != 0x10000) {
                free(readback);
                free(data);
                return 1;
            }
            if (request_valid) {
                uint8_t chunk_status = proc_write_mem_status(
                    wp->pid, address, 0x10000, data, readback);
                if (status == PROC_WRITE_STATUS_OK) {
                    status = chunk_status;
                }
            }
            address += 0x10000;
            length -= 0x10000;
        }
        if (net_recv_all(fd, data, (int)length, 1) != (int)length) {
            free(readback);
            free(data);
            return 1;
        }
        if (request_valid) {
            uint8_t chunk_status = proc_write_mem_status(
                wp->pid, address, (uint32_t)length, data, readback);
            if (status == PROC_WRITE_STATUS_OK) {
                status = chunk_status;
            }
        }
    }

    net_send_int32(fd, status == PROC_WRITE_STATUS_OK
                       ? CMD_SUCCESS : CMD_ERROR);
    free(readback);
    free(data);
    return 0;
}
```

### 9.5 Server verify step, `ref/ps4debug-NG/debugger/source/proc.c:448-473`

```c
static uint8_t proc_write_mem_status(uint32_t pid, uint64_t address,
                                     uint32_t length, const void *expected,
                                     void *readback) {
    if ((int32_t)pid <= 0 || !expected || !readback || length == 0
        || address + length < address) {
        return PROC_WRITE_STATUS_INVALID;
    }

    if (sys_proc_rw(pid, address, (void *)expected, length, 1) != 0) {
        return PROC_WRITE_STATUS_RW_FAILED;
    }

    const uint8_t *wanted = (const uint8_t *)expected;
    uint8_t *actual = (uint8_t *)readback;
    for (uint32_t i = 0; i < length; i++) {
        actual[i] = wanted[i] ^ 0xFFu;
    }

    if (sys_proc_rw(pid, address, actual, length, 0) != 0) {
        return PROC_WRITE_STATUS_RW_FAILED;
    }

    return memcmp(actual, wanted, length) == 0
        ? PROC_WRITE_STATUS_OK
        : PROC_WRITE_STATUS_VERIFY_FAILED;
}
```

Observations:
- Server sequence: `CMD_SUCCESS` (ack) → receive `length` bytes → write and read-back verify → final `CMD_SUCCESS` or `CMD_ERROR`. The final status is `CMD_ERROR` on any failure.
- Write requires `pid > 0` (as int32) and `address + length` not overflowing; `length == 0` is valid and returns `CMD_SUCCESS`.
- Failed final status does not close the socket (`return 0` at `proc.c:542`).
- Write is verified (content-verified since capability 1.1, see section 11).

---

## 10. Disconnect and clean close (0xBDDD0002)

### 10.1 ID, `ref/ps4debug-NG/debugger/include/protocol.h:75-76`

```c
#define CMD_CONSOLE_REBOOT      0xBDDD0001
#define CMD_CONSOLE_END         0xBDDD0002
```

### 10.2 Client, `ref/libdebug/PS4DBG/PS4DBG.cs:181-193`

```csharp
        public void Disconnect() {
            // Before trying to close the connection we first check if
            // the connection is currently open
            if (!IsConnected) {
                DebugPrintWarning("Oops, the PS4 connection is not active!");
                return;
            }

            SendCMDPacket(CMDS.CMD_CONSOLE_END, 0);
            sock.Shutdown(SocketShutdown.Both);
            sock.Close();
            IsConnected = false;
        }
```

Client comment, `ref/libdebug/PS4DBG/PS4DBG.Console.cs:13`:

```csharp
        // note: the disconnect command actually uses the console api to end the connection
```

No reply is read. The client sends a bare 12-byte header (`cmd=0xBDDD0002`, `datalen=0`), then shuts down and closes.

### 10.3 Server, `ref/ps4debug-NG/debugger/source/console.c:205-225` (the END case)

```c
int console_handle(int fd, struct cmd_packet *packet) {
    switch(packet->cmd) {
        case CMD_CONSOLE_REBOOT:
            return console_reboot_handle(fd, packet);
        case CMD_CONSOLE_PRINT:
            return console_print_handle(fd, packet);
        case CMD_CONSOLE_NOTIFY:
            return console_notify_handle(fd, packet);
        case CMD_CONSOLE_INFO:

            net_send_int32(fd, CMD_SUCCESS);
            return 0;
        case 0xBDDD0006u:
            return console_foreground_app_handle(fd, packet);
        case CMD_CONSOLE_END:

            return 1;
    }

    return 1;
}
```

`return 1` → `handle_client` `goto error` → `free_client` (see 3.5). The server sends nothing. `free_client` also calls `debug_client_disconnect` and frees turboscan sessions for that slot (`server.c:24-31`). Disconnect releases the session on the server side.

---

## 11. NG extensions and CMD_PROC_AUTH (0xBDAACCFF)

### 11.1 Capability level via CMD_BRANDING, `ref/ps4debug-NG/debugger/source/server.c:59-66` and `ref/ps4debug-NG/debugger/include/capability.h:6-14`

```c
        case CMD_BRANDING: {

            static const char brand[] = PACKET_BRANDING "\0" PACKET_CAPABILITY_LEVEL;
            len = (uint32_t)(sizeof(brand) - 1);
            net_send_all(fd, &len, sizeof(uint32_t));
            net_send_all(fd, brand, len);
            return 0;
        }
```

```c
// Capability level reported after the NUL in the CMD_BRANDING reply.
// It is independent of PS4DEBUG_NG_VERSION_STR; bump it only for new wire features.
//   1.0 - 0xBDAACC04 bulk write
//   1.1 - content-verified process writes, truthful terminal status
//   1.2 - CMD_DEBUG_ATTACH accepts an optional debug_port (see debug_port.h)
#define PACKET_CAPABILITY_LEVEL "1.2"
```

Not used by libdebug. The `CMD_BRANDING` ID is `0xBD000501` (`protocol.h:19`).

### 11.2 Auth ID, struct and magic, `ref/ps4debug-NG/debugger/include/protocol.h:45` and `:424-429`

```c
#define CMD_PROC_AUTH           0xBDAACCFF
```

```c
struct cmd_proc_auth_packet {
    uint32_t magic;
    uint32_t flags;
} __attribute__((packed));
#define CMD_PROC_AUTH_PACKET_SIZE  8
#define CMD_PROC_AUTH_MAGIC        0xBB40E64D
```

### 11.3 Server auth handler, `ref/ps4debug-NG/debugger/source/proc.c:2098-2135`

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

Sequence:
1. Client sends header (`cmd=0xBDAACCFF`, `datalen=8`) + body `magic` u32 = `0xBB40E64D`, `flags` u32 (bit 0 → `0x1`, bit 1 → `0x2`).
2. Server replies `CMD_SUCCESS`.
3. Server sends `chlen` u16 (= 64), then 64 challenge bytes.
4. Client sends 64 response bytes. Server compares to `expected = keystream XOR challenge`.
5. Server replies `CMD_SUCCESS` (and sets bits) or `CMD_DATA_NULL` followed by a disconnect (`return 1`).

Note: the challenge comes from `rand()` (`proc.c:2113-2115`) and is not seeded in this handler; the client does not send `chlen`, the server fixes it at 64.

### 11.4 Keystream (server side, reproducible), `ref/ps4debug-NG/debugger/source/proc.c:2076-2096`

```c
uint32_t g_proc_auth_bits = 0;

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

Observation: `PROTOCOL.md:110-111` says the keystream is "derived client-side; mechanism intentionally undocumented". The keystream is in the server source above. Neither `ref/libdebug` nor `ref/PS4CheaterNeo` contains a client that sends `CMD_PROC_AUTH` (grep for `0xBB40E64D`, `CMD_PROC_AUTH`, `0xBDAACCFF`, `lfsr`: only hits are in the NG server and the NG README/PROTOCOL). The response must be `challenge[i] ^ keystream[i]`. This is derived from the server code and has not been checked against a real console.

### 11.5 What the auth bit gates (source vs docs)

Server gates in source:

`ref/ps4debug-NG/debugger/source/proc.c:2138-2139`:

```c
#define SCAN_REQUIRE_AUTH(fd) \
    do { if (!(g_proc_auth_bits & 2)) { net_send_int32((fd), CMD_DATA_NULL); return 1; } } while (0)
```

Used in `proc.c:2156` (`CMD_PROC_SCAN_START`), `proc.c:2275` (`CMD_PROC_SCAN_COUNT`), `proc.c:2442` (`CMD_PROC_SCAN_GET`), and in `scan_turbo.c:1343, 1366, 1399, 1463, 1710, 1985, 2065` (`if (!(g_proc_auth_state & 2))`, with `#define g_proc_auth_state g_proc_auth_bits` at `scan_turbo.c:20`).

Not gated in source: `proc_scan_handle` (CMD_PROC_SCAN 0xBDAA0009, `proc.c:1599`), `proc_scan_aob_handle` (0xBDAA0501, `proc.c:1774`), `proc_scan_aob_multi_handle` (0xBDAA0502, `proc.c:1893`). These handler bodies (checked at `proc.c:1599-1616`, `1774-1790`, `1893-1906`) contain no auth check. `PROTOCOL.md:357,483,490` and `README.md:179-180` say they are auth-gated. This conflicts with the source. See open questions.

### 11.6 Other NG-specific commands relevant to the core path

- `CMD_PROC_NOP = 0xBDAACC06` (keepalive, `server.c:72-74`, `protocol.h:49`): server replies `CMD_SUCCESS`.
- Bulk write `0xBDAACC04` (capability 1.0): `proc_write_multi_handle`, `proc.c:545`. Request struct at `protocol.h:175-180`. Not in libdebug.
- Read: no separate bulk read command in the core path. `CMD_PROC_READ` is 64 KiB chunked on the server (section 8.3). Larger reads are not a separate opcode.
- Turbo scan family `0xBDAACC10`-`0xBDAACC17` (`scan_turbo.c`). Not needed for core commands.

Opcode clash to watch: `ref/PS4CheaterNeo/PS4CheaterNeo/common/ps4dbg/libdebug/PS4DBG.cs:86-89` defines `CMD_PROC_SCAN_START = 0xBDAACC01`, `CMD_PROC_SCAN_NEXT = 0xBDAACC02`, `CMD_PROC_SCAN_END = 0xBDAACC06`, `CMD_PROC_SCAN_FF = 0xBDAACCFF`. In NG, `0xBDAACC06` is `CMD_PROC_NOP` (`protocol.h:49`) and `0xBDAACC02` is `CMD_PROC_SCAN_COUNT` (`protocol.h:47`). The client-side names do not match the server.

---

## 12. Client constants summary, `ref/libdebug/PS4DBG/PS4DBG.cs`

| Item | Value | Source |
|---|---|---|
| Port | 744 | `PS4DBG.cs:44` |
| Debug (interrupt) port | 755 | `PS4DBG.cs:43` |
| Discovery UDP | 1010, magic `0xFFFFAAAA` | `PS4DBG.cs:20-21` |
| Packet magic | `0xFFAABBCC` | `PS4DBG.cs:27` |
| Header size | 12 | `PS4DBG.cs:38` |
| Max chunk (send and recv buffer) | 8192 | `PS4DBG.cs:42` |
| Connect timeout | not set | `PS4DBG.cs:161-176` |
| Receive timeout | 10000 ms | `PS4DBG.cs:172` |
| Send timeout | not set | `PS4DBG.cs:161-176` |
| NoDelay | true | `PS4DBG.cs:169` |
| Library version (client) | `"1.2"` | `PS4DBG.cs:41` |

Server counterparts: `SERVER_PORT 744` (`server.h:15`), `PACKET_VERSION "1.3"` (`protocol.h:13`), `SO_SNDBUF/SO_RCVBUF 65537` (`server.c:225-227`).

---

## 13. Discrepancies between docs and source (for the port)

1. `ref/ps4debug-NG/PROTOCOL.md:319-324` (section 2.2, CMD_PROC_MAPS) says `proc_vm_map_entry` is `{uint64_t start; uint64_t end; uint64_t offset; uint64_t prot; char name[32];}` = 64 bytes. The source (`proc.h:11-17`) is `char name[32]; u64 start; u64 end; u64 offset; uint16_t prot;` = 58 bytes, matching libdebug `PROC_MAP_ENTRY_SIZE = 58`. Use the source.
2. `PROTOCOL.md:98-101` (section 1.8) says auth state is per-connection and resets on disconnect. Source: `g_proc_auth_bits` is a global (`proc.c:2076`) and is reset only in `start_server` (`server.c:333`). `free_client` (`server.c:20-33`) does not reset it.
3. `PROTOCOL.md:357` and `README.md:179-180` say CMD_PROC_SCAN, AOB and AOB_MULTI require auth. Source has no check in those handlers (section 11.5).
4. `PROTOCOL.md:101` cites `proc.c:1862-1900` for auth. Actual: `proc.c:2098-2135`. The file:line citations in PROTOCOL.md are stale (for example, PACKET_MAGIC is at `protocol.h:15`, not `:14`).
5. `PROTOCOL.md:1.4` says a magic mismatch "is silently dropped and the loop continues". The code does `continue` without consuming the body (`server.c:152-154`), so the body bytes are read as the next header.

---

## Open questions

1. Auth keystream: the server LFSR is in source (section 11.4), but no client implementation exists in `ref/`. The Swift port must implement the response from that code. This is unverified against a real console, and it runs only after a user-triggered action (it is not needed for read, write or version).
2. Auth scope: which commands really need `g_proc_auth_bits & 2`? Source shows only SCAN_START/COUNT/GET and the turbo family. PROTOCOL.md and README claim also SCAN, AOB and AOB_MULTI. Source wins for the Swift port, but this is worth checking with the researcher on a live console later (only with Egor's go-ahead).
3. Auth persistence: the source makes the auth bits global and resets them only at server start. Does a reconnect keep scan rights? Source says yes (`server.c:20-33` does not reset). Not tested.
4. libdebug partial reads: `sock.Receive` is called once for the 4-byte status (`PS4DBG.cs:299`), the 4-byte count (`PS4DBG.Proc.cs:45`, `:106`), and the version length (`PS4DBG.cs:204`). No loop. Swift port should loop until all bytes are read.
5. `ReceiveData` never checks `recv == 0`, so a peer close causes an infinite loop (`PS4DBG.cs:282-287`). Swift port should treat 0 bytes as an error.
6. `ConvertASCII` bug: uncapped string parse (section 4.4). Confirm the exact 32-byte behaviour on a process named with 32 characters.
7. Empty process list and other error paths close the connection (section 3.5). Confirm on the console that `CMD_PROC_LIST` with zero processes drops the session (not tested).
8. `CMD_PROC_READ` returns `CMD_SUCCESS` even when reads fail, and zero-fills (section 8.3). Client cannot tell a failed read from zeros. Not tested on hardware.
9. `CMD_CONSOLE_END` has no reply. Confirm the console closes cleanly (not tested).
10. Connect timeout: libdebug sets none. The Swift port should choose one. Server SO_NBIO is set (section 2.3).
11. Remaining libdebug parts not read here: `PS4DBG.Debug.cs`, `PS4DBG.Kernel.cs`, `PS4DBG.Console.cs` beyond the lines quoted, and `ScanProcess` (`PS4DBG.Proc.cs:309-435`). Not needed for the core commands.
