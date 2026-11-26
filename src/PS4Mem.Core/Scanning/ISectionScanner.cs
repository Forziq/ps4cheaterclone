using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// A first-scan back end: finds every match of a query in one section.
/// <see cref="LocalSectionScanner"/> compares on the Mac and supports every query; the console
/// back end compares on the PS4 and supports only some (<see cref="Supports"/>).
/// </summary>
public interface ISectionScanner
{
    /// <summary>"local" or "console", for logs and results.</summary>
    string Name { get; }

    /// <summary>True when this back end can run <paramref name="query"/> as a first scan.</summary>
    bool Supports(ScanQuery query);

    /// <summary>
    /// Appends every address in <paramref name="section"/> whose value matches <paramref name="query"/>
    /// and where <c>address % alignment == 0</c>, in ascending order, with the bytes found.
    /// Calls <paramref name="bytesDone"/> with the number of section bytes finished since the last call.
    /// </summary>
    void ScanSection(int pid, Section section, ScanQuery query, int alignment, ScanHitBuffer hits,
        Action<long> bytesDone, CancellationToken cancel);
}
