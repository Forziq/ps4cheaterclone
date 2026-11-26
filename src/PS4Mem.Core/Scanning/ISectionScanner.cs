using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// A first-scan back end: finds every exact 4-byte match in one section.
/// <see cref="LocalSectionScanner"/> compares on the Mac; the console back end compares on the PS4.
/// </summary>
public interface ISectionScanner
{
    /// <summary>"local" or "console", for logs and results.</summary>
    string Name { get; }

    /// <summary>
    /// Appends every address in <paramref name="section"/> where the u32 (little-endian) equals
    /// <paramref name="value"/> and <c>address % alignment == 0</c>, in ascending order.
    /// Calls <paramref name="bytesDone"/> with the number of section bytes finished since the last call.
    /// </summary>
    void ScanSection(int pid, Section section, uint value, int alignment, ScanHitBuffer hits,
        Action<long> bytesDone, CancellationToken cancel);
}
