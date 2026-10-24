using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumora.Services.Operations;

/// <summary>
/// Papierkorb über <c>SHFileOperationW</c> (FO_DELETE + FOF_ALLOWUNDO) ohne jede Shell-UI:
/// FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI, Fehler kommen als Rückgabecode und werden als
/// <see cref="IOException"/> gemeldet — kein modaler Dialog pro gesperrter Datei im Batch.
/// Wichtig: Die Shell löscht eine Datei <b>endgültig</b> (ohne Rückfrage, weil FOF_NOCONFIRMATION),
/// wenn das Laufwerk keinen Papierkorb hat — Netzlaufwerke, UNC-Pfade, Wechseldatenträger. Deshalb
/// wird das vor dem Aufruf geprüft (<see cref="GetUnavailableReason"/>: Laufwerkstyp +
/// <c>SHQueryRecycleBin</c>) und bei fehlendem Papierkorb eine Ausnahme geworfen, statt hart zu löschen
/// (Repo-Regel: Papierkorb statt Hard-Delete). Nach dem Aufruf wird zusätzlich geprüft, dass die
/// Datei wirklich weg ist.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_NO_CONNECTED_ELEMENTS = 0x2000;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    public void MoveToRecycleBin(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);

        // Erst die Verfügbarkeit prüfen (reine Pfad-/Laufwerkslogik), dann die Datei — File.Exists auf
        // einem nicht erreichbaren Netzpfad kann Sekunden dauern.
        var reason = GetUnavailableReason(fullPath);
        if (reason is not null)
        {
            throw new IOException($"Papierkorb nicht verfügbar — {reason} Die Datei wurde nicht gelöscht: {fullPath}");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Die Datei wurde nicht gefunden.", fullPath);
        }

        var result = ShellDelete(fullPath, out var aborted);
        if (result != 0 || aborted)
        {
            throw new IOException(
                $"Verschieben in den Papierkorb fehlgeschlagen ({DescribeShellError(result, aborted)}): {fullPath}");
        }

        if (File.Exists(fullPath))
        {
            throw new IOException($"Die Shell hat die Datei nicht in den Papierkorb verschoben: {fullPath}");
        }
    }

    /// <summary>
    /// Ordner samt Inhalt in den Papierkorb (ADR-0034) — dieselben Schutzregeln wie für Dateien: ohne Papierkorb auf dem
    /// Laufwerk wird nichts gelöscht, Laufwerkswurzeln werden abgelehnt, danach muss der Ordner weg sein.
    /// </summary>
    public void MoveDirectoryToRecycleBin(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        if (string.Equals(Path.GetPathRoot(fullPath)?.TrimEnd('\\', '/'), fullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Eine Laufwerkswurzel kann nicht in den Papierkorb verschoben werden: {fullPath}");
        }

        var reason = GetUnavailableReason(fullPath);
        if (reason is not null)
        {
            throw new IOException($"Papierkorb nicht verfügbar — {reason} Der Ordner wurde nicht gelöscht: {fullPath}");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Der Ordner wurde nicht gefunden: {fullPath}");
        }

        // FOF_WANTNUKEWARNING: passt der Ordner nicht in den Papierkorb, fragt die Shell, statt ihn endgültig zu löschen.
        var result = ShellDelete(fullPath, out var aborted, FOF_WANTNUKEWARNING);
        if (result != 0 || aborted)
        {
            throw new IOException(
                $"Verschieben in den Papierkorb fehlgeschlagen ({DescribeShellError(result, aborted)}): {fullPath}");
        }

        if (Directory.Exists(fullPath))
        {
            throw new IOException($"Die Shell hat den Ordner nicht in den Papierkorb verschoben: {fullPath}");
        }
    }

    /// <inheritdoc />
    string? IRecycleBin.GetUnavailableReasonFor(string path) => GetUnavailableReason(Path.GetFullPath(path)); // ADR-0065

    /// <summary>
    /// Liefert den Grund, warum für <paramref name="fullPath"/> kein Papierkorb verfügbar ist, oder
    /// <c>null</c>, wenn die Shell die Datei in den Papierkorb legen kann. Ohne Dateizugriff.
    /// </summary>
    public static string? GetUnavailableReason(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        if (fullPath.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "Netzwerkpfade (UNC) haben keinen Papierkorb.";
        }

        var path = fullPath.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
            ? fullPath[ExtendedPrefix.Length..]
            : fullPath;

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return "Netzwerkpfade (UNC) haben keinen Papierkorb.";
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
        {
            return "Der Pfad liegt auf keinem Laufwerk mit Papierkorb.";
        }

        DriveType driveType;
        try
        {
            driveType = new DriveInfo(root).DriveType;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return $"Laufwerk {root} ist nicht lesbar ({ex.Message}).";
        }

        switch (driveType)
        {
            case DriveType.Network:
                return $"Netzlaufwerk {root} hat keinen Papierkorb.";
            case DriveType.CDRom:
            case DriveType.NoRootDirectory:
            case DriveType.Unknown:
                return $"Laufwerk {root} ({driveType}) hat keinen Papierkorb.";
        }

        // Shell fragen: scheitert u. a. bei Wechseldatenträgern (USB-Sticks), die Windows ohne Papierkorb führt.
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        var hr = SHQueryRecycleBinW(root, ref info);
        return hr == 0 ? null : $"Laufwerk {root} ({driveType}) hat keinen Papierkorb (HRESULT 0x{hr:X8}).";
    }

    private static int ShellDelete(string fullPath, out bool aborted, ushort extraFlags = 0)
    {
        var flags = (ushort)(FOF_ALLOWUNDO | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NO_CONNECTED_ELEMENTS | extraFlags);
        var from = fullPath + "\0"; // pFrom ist doppelt nullterminiert (Liste); der Marshaller hängt das zweite \0 an

        if (IntPtr.Size == 8)
        {
            var op = new SHFILEOPSTRUCTW64 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
            var rc = SHFileOperationW(ref op);
            aborted = op.fAnyOperationsAborted;
            return rc;
        }
        else
        {
            var op = new SHFILEOPSTRUCTW32 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
            var rc = SHFileOperationW(ref op);
            aborted = op.fAnyOperationsAborted;
            return rc;
        }
    }

    /// <summary>Shell-spezifische DE_*-Codes in Klartext, sonst Win32-Fehlertext.</summary>
    private static string DescribeShellError(int code, bool aborted)
    {
        if (code == 0 && aborted)
        {
            return "Vorgang von der Shell abgebrochen";
        }

        var text = code switch
        {
            0x71 => "Quelle und Ziel sind dieselbe Datei",
            0x74 => "Stammverzeichnis kann nicht gelöscht werden",
            0x75 => "Vorgang abgebrochen",
            0x78 => "Zugriff auf die Quelldatei verweigert",
            0x79 => "Pfad zu tief verschachtelt",
            0x7C => "Ungültiger Dateiname oder Datei nicht gefunden",
            0x81 => "Dateiname zu lang",
            0x85 => "Datei zu groß für den Papierkorb",
            0x86 or 0x87 or 0x88 => "Quelle liegt auf einem optischen Datenträger",
            0x402 => "Unbekannter Shell-Fehler",
            _ => null,
        };

        if (text is not null)
        {
            return $"{text}, Code 0x{code:X}";
        }

        var win32 = new Win32Exception(code).Message;
        return $"{win32}, Code 0x{code:X}";
    }

    // ------------------------------------------------------------------ P/Invoke
    // SHFILEOPSTRUCTW ist auf x86 mit Pack=1 definiert (pshpack1.h), auf x64 natürlich ausgerichtet —
    // deshalb zwei Layouts, Auswahl über IntPtr.Size. SHQUERYRBINFO analog (cbSize + zwei __int64).

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte AllowUnsafeBlocks; DllImport reicht hier.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW64
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct SHFILEOPSTRUCTW32
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW64 lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW32 lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
#pragma warning restore SYSLIB1054
}
