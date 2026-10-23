namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>
/// Der erhöhte Lauf <c>Lumora.exe --container-create-vhdx …</c> (ADR-0061, Nachtrag 0.4.26). Prüft den Auftrag noch einmal
/// streng — die Befehlszeile könnte jeder Prozess des Benutzers zusammensetzen —, legt dann die VHDX an (dynamisch), hängt
/// sie dauerhaft ein, initialisiert GPT, legt eine Partition über den ganzen Platz an, formatiert NTFS und weist erst
/// danach den Buchstaben zu. Scheitert ein Schritt, hängt er die eben angelegte VHDX wieder ab und löscht sie.
/// Startet keine Oberfläche, öffnet keinen Katalog und protokolliert nichts — das Ergebnis ist nur der Exit-Code
/// (<see cref="VhdxJobExit"/>).
/// </summary>
public sealed class VhdxElevatedJob
{
    private const long Megabyte = 1024L * 1024;

    private readonly IVirtualDiskApi _api;
    private readonly Func<IReadOnlyList<string>> _cloudFolders;

    public VhdxElevatedJob(IVirtualDiskApi api, Func<IReadOnlyList<string>>? cloudFolders = null)
    {
        _api = api;
        _cloudFolders = cloudFolders ?? HiddenLibraryFolderRules.OneDriveFolders;
    }

    /// <summary>Führt den Auftrag aus; <paramref name="args"/> ohne Programmnamen. Liefert den Exit-Code.</summary>
    public int Run(IReadOnlyList<string> args)
    {
        try
        {
            return (int)Execute(args);
        }
#pragma warning disable CA1031 // Der erhöhte Lauf meldet jeden unerwarteten Fehler nur als Exit-Code.
        catch (Exception)
#pragma warning restore CA1031
        {
            return (int)VhdxJobExit.Unexpected;
        }
    }

    /// <summary>Prüft den Auftrag ohne etwas anzulegen; <see cref="VhdxJobExit.Success"/> = geeignet.</summary>
    public VhdxJobExit Validate(VhdxElevatedRequest request)
    {
        var label = request.VolumeLabel;
        if (VhdxRules.ValidateLabel(label) is not null || !string.Equals(label, label.Trim(), StringComparison.Ordinal))
        {
            return VhdxJobExit.InvalidLabel;
        }

        if (request.SizeMegabytes < VhdxRules.MinSizeGigabytes * 1024L || request.SizeMegabytes > VhdxRules.MaxSizeGigabytes * 1024L)
        {
            return VhdxJobExit.InvalidSize;
        }

        if (request.DriveLetter is < 'D' or > 'Z')
        {
            return VhdxJobExit.InvalidArguments;
        }

        var path = request.FilePath;
        if (!IsStrictLocalPath(path))
        {
            return VhdxJobExit.InvalidPath;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            return VhdxJobExit.FileExists;
        }

        if (VhdxRules.ValidatePath(path, _cloudFolders()) is not null)
        {
            return VhdxJobExit.InvalidPath;
        }

        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return VhdxJobExit.DriveNotSuitable;
        }

        if (FoldersBelowRoot(folder).Any(_api.IsReparsePoint))
        {
            return VhdxJobExit.InvalidPath;
        }

        var format = _api.GetLocalDriveFormat(Path.GetPathRoot(path)!);
        if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase) && !string.Equals(format, "ReFS", StringComparison.OrdinalIgnoreCase))
        {
            return VhdxJobExit.DriveNotSuitable;
        }

        return _api.IsDriveLetterInUse(request.DriveLetter) ? VhdxJobExit.LetterInUse : VhdxJobExit.Success;
    }

    /// <summary>
    /// Nur <c>X:\Ordner\Datei.vhdx</c>: voller Pfad mit Laufwerksbuchstaben, nur Rückstriche, keine leeren, „.“- oder
    /// „..“-Teile, keine Teile mit Punkt/Leerzeichen am Ende (Windows schneidet sie still ab), kein „:“ nach dem Laufwerk
    /// (Datenströme), kein UNC-/Geräte-Pfad (<c>\\server</c>, <c>\\?\</c>, <c>\\.\</c>), und Windows normalisiert ihn
    /// nicht um.
    /// </summary>
    public static bool IsStrictLocalPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            return false;
        }

        if (path.Contains('/', StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0 || path.Any(c => c == '"' || char.IsControl(c)))
        {
            return false;
        }

        var segments = path[3..].Split('\\');
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ') || s.StartsWith(' ')))
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(path), VhdxRules.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private VhdxJobExit Execute(IReadOnlyList<string> args)
    {
        if (!VhdxElevatedCommand.TryParse(args, out var request) || request is null)
        {
            return VhdxJobExit.InvalidArguments;
        }

        var problem = Validate(request);
        if (problem != VhdxJobExit.Success)
        {
            return problem;
        }

        if (!_api.IsElevated)
        {
            return VhdxJobExit.NotElevated;
        }

        var path = request.FilePath;
        var step = VhdxJobExit.CreateFailed;
        try
        {
            _api.CreateDynamicVhdx(path, (ulong)(request.SizeMegabytes * Megabyte));
            step = VhdxJobExit.AttachFailed;
            _api.AttachPermanently(path);
            step = VhdxJobExit.DiskNumberFailed;
            var disk = _api.GetDiskNumber(path);
            step = VhdxJobExit.InitializeFailed;
            _api.InitializeGpt(disk);
            step = VhdxJobExit.PartitionFailed;
            var partition = _api.CreateMaximumPartition(disk);
            step = VhdxJobExit.FormatFailed;
            _api.FormatNtfs(disk, partition, request.VolumeLabel);
            step = VhdxJobExit.LetterFailed;
            _api.AssignDriveLetter(disk, partition, request.DriveLetter);
            return VhdxJobExit.Success;
        }
#pragma warning disable CA1031 // Jeder Fehler eines Schritts wird zu seinem Exit-Code; vorher aufräumen.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Die Datei gab es vor dem Lauf nicht (Validate) — was jetzt dort liegt, stammt von diesem Lauf. Ausnahme: Windows
            // meldet beim Anlegen „Datei existiert“ (dann hat sie in der Zwischenzeit jemand anders angelegt).
            if (!(step == VhdxJobExit.CreateFailed && IsFileExists(ex)))
            {
                CleanUp(path);
            }

            return step;
        }
    }

    /// <summary><c>ERROR_FILE_EXISTS</c> (80) bzw. <c>ERROR_ALREADY_EXISTS</c> (183).</summary>
    private static bool IsFileExists(Exception ex)
        => ex is System.ComponentModel.Win32Exception { NativeErrorCode: 80 or 183 };

    /// <summary>
    /// Abhängen und löschen — nur, was dieser Lauf selbst angelegt hat. Ist inzwischen ein Ordner im Pfad eine Verknüpfung
    /// geworden, wird nicht gelöscht (das Löschen könnte sonst woanders landen). Bleibt die Datei, meldet das die Oberfläche.
    /// </summary>
    private void CleanUp(string path)
    {
        try
        {
            _api.Detach(path);
        }
#pragma warning disable CA1031 // Aufräumen versucht beides; die Oberfläche prüft danach, ob die Datei noch da ist.
        catch (Exception)
        {
        }

        try
        {
            if (!FoldersBelowRoot(Path.GetDirectoryName(path)!).Any(_api.IsReparsePoint))
            {
                _api.DeleteFile(path);
            }
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    /// <summary>Alle Ordner von unter dem Laufwerk bis <paramref name="folder"/> (das Laufwerk selbst nicht).</summary>
    private static IEnumerable<string> FoldersBelowRoot(string folder)
    {
        var root = Path.GetPathRoot(folder) ?? string.Empty;
        var current = root;
        foreach (var segment in folder[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }
}
