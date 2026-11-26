namespace PS4Mem.Core.Protocol;

/// <summary>
/// CMD_PROC_AUTH challenge response (protocol.md §12). The server XORs its 64-byte challenge with
/// a keystream from a 4-register LFSR that is reset to 200/300/400/500 on every auth, and expects
/// that result back. Exact port of <c>auth_lfsr_next</c> and <c>proc_auth_xor_keystream</c>
/// (ps4debug-NG debugger/source/proc.c:2076-2096). Untested on a console until G0.
/// </summary>
public static class Ps4Auth
{
    /// <summary>CMD_PROC_AUTH_MAGIC (NG/include/protocol.h:424-429).</summary>
    public const uint Magic = 0xBB40E64D;

    /// <summary>Auth flag bit that unlocks the range scans and Turbo Scan (NG/source/proc.c:2138-2139).</summary>
    public const uint ScanBit = 0x2;

    /// <summary>The response the server expects: <c>challenge[i] ^ keystream[i]</c>.</summary>
    public static byte[] Respond(ReadOnlySpan<byte> challenge)
    {
        uint s1 = 200, s2 = 300, s3 = 400, s4 = 500;
        var response = new byte[challenge.Length];
        for (var i = 0; i < challenge.Length; i++)
        {
            s1 = ((s1 << 18) & 0xFFF80000u) ^ ((s1 ^ (s1 << 6)) >> 13);
            s2 = ((s2 << 2) & 0xFFFFFFE0u) ^ ((s2 ^ (s2 << 2)) >> 27);
            s3 = ((s3 << 7) & 0xFFFFF800u) ^ ((s3 ^ (s3 << 13)) >> 21);
            s4 = ((s4 << 13) & 0xFFF00000u) ^ ((s4 ^ (s4 << 3)) >> 12);
            response[i] = (byte)(challenge[i] ^ (byte)(s1 ^ s2 ^ s3 ^ s4));
        }
        return response;
    }
}
