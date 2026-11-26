using System.Buffers.Binary;
using PS4Mem.Core.Scanning;

namespace PS4Mem.Core.Tests.Fakes;

public static class ScanResultsExtensions
{
    /// <summary>Every result's value as u32 (results of a 4-byte scan).</summary>
    public static uint[] U32s(this ScanResults r) =>
        Enumerable.Range(0, checked((int)r.Count)).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(r.ValueAt(i))).ToArray();

    /// <summary>Every result's value as text.</summary>
    public static string[] Texts(this ScanResults r) =>
        Enumerable.Range(0, checked((int)r.Count)).Select(i => r.FormatValue(i)).ToArray();
}
