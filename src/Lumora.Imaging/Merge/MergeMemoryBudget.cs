using System.Runtime.InteropServices;

namespace Lumora.Imaging.Merge;

/// <summary>
/// Arbeitsspeicher für volle Auflösung (ADR-0078 Punkt 5/12, Abnahme Nr. 11): frei verfügbarer physischer Speicher über
/// <c>GlobalMemoryStatusEx</c> abzüglich einer Reserve für Lumora und Windows. Der Hilfsprozess wählt damit die größte
/// passende Stufe (Spike 20: Spitzen 5–6 GB je Drohnen-Kugel).
/// </summary>
public static class MergeMemoryBudget
{
    /// <summary>Reserve für Lumora, Windows und andere Programme.</summary>
    public const long Reserve = 1_500_000_000;

    /// <summary>Mindestbudget, auch wenn kaum etwas frei ist (die kleinste Stufe braucht wenig).</summary>
    public const long Minimum = 512_000_000;

    /// <summary>(gesamt, frei) in Bytes; (0, 0), wenn Windows nichts liefert.</summary>
    public static (long Total, long Available) Query()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : (0, 0);
    }

    /// <summary>Budget für einen Lauf: frei − Reserve, mindestens <see cref="Minimum"/>; 0 = unbekannt (ohne Grenze).</summary>
    public static long Budget()
    {
        var (total, available) = Query();
        return total <= 0 ? 0 : Math.Max(Minimum, available - Reserve);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte AllowUnsafeBlocks; DllImport reicht hier (wie ShellCloudPropertyReader).
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
#pragma warning restore SYSLIB1054
}
