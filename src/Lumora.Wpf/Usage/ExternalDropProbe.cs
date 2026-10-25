using System;
using System.Runtime.InteropServices;

namespace Lumora.Usage;

/// <summary>
/// Nach einem Ziehen mit Datei-Drop (ADR-0068): Liegt der Mauszeiger über einem Fenster eines anderen Programms? Nur dann
/// zählt ein „Kopieren“-Ergebnis als Weitergabe (Nutzungs-Signale, ADR-0072) — Lumoras eigene Ziele (Alben, Ordnerleiste,
/// Karte) melden mangels anderer erlaubter Effekte ebenfalls „Kopieren“.
/// </summary>
internal static class ExternalDropProbe
{
    public static bool CursorOverOtherProcess()
    {
        try
        {
            if (!GetCursorPos(out var point))
            {
                return false;
            }

            var window = WindowFromPoint(point);
            if (window == IntPtr.Zero)
            {
                return false;
            }

            _ = GetWindowThreadProcessId(window, out var processId);
            return processId != 0 && processId != (uint)Environment.ProcessId;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
