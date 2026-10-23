namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>Was „Neuen BitLocker-Container erstellen“ anlegen soll (ADR-0061).</summary>
/// <param name="FilePath">Neue VHDX-Datei (voller Pfad, noch nicht vorhanden).</param>
/// <param name="SizeGigabytes">Höchstgröße; die Datei ist dynamisch erweiterbar und wächst erst mit dem Inhalt.</param>
/// <param name="DriveLetter">Freier Laufwerksbuchstabe für das neue Laufwerk.</param>
/// <param name="VolumeLabel">Neutrale Bezeichnung des Laufwerks.</param>
public sealed record VhdxPlan(string FilePath, int SizeGigabytes, char DriveLetter, string VolumeLabel);

/// <summary>
/// Regeln für eine neue BitLocker-VHDX (ADR-0061, Nachtrag 0.4.26): Speicherort, Bezeichnung, Größenvorschlag. Gelten in
/// der Oberfläche und noch einmal streng im erhöhten Lauf (<see cref="VhdxElevatedJob"/>). Nichts davon enthält ein
/// Kennwort — verschlüsselt wird danach im BitLocker-Assistenten von Windows.
/// </summary>
public static class VhdxRules
{
    public const int MinSizeGigabytes = 1;

    /// <summary>Größte VHDX laut Windows: 64 TB.</summary>
    public const int MaxSizeGigabytes = 64 * 1024;

    public const string DefaultLabel = "Daten";

    public const string DefaultFolderName = "Daten";

    public const string DefaultFileName = "sicherung";

    public const string Extension = ".vhdx";

    /// <summary>Höchstlänge einer NTFS-Bezeichnung.</summary>
    public const int MaxLabelLength = 32;

    /// <summary>Ordnernamen bekannter Cloud-Dienste; ein Container darf nie darin liegen (README, ADR-0049).</summary>
    public static readonly IReadOnlyList<string> CloudFolderMarkers =
        ["OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "My Drive", "Meine Ablage", "iCloudDrive", "iCloud Drive", "Nextcloud", "ownCloud", "pCloud", "MEGA", "Box", "MagentaCLOUD"];

    /// <summary>null = Bezeichnung geeignet, sonst die Meldung.</summary>
    public static string? ValidateLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "Bitte eine Bezeichnung für das Laufwerk eingeben (z. B. „Daten“).";
        }

        var text = label.Trim();
        if (text.Length > MaxLabelLength)
        {
            return $"Die Bezeichnung darf höchstens {MaxLabelLength} Zeichen lang sein.";
        }

        return text.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')
            ? null
            : "Die Bezeichnung darf nur Buchstaben A–Z, Ziffern, Leerzeichen und - _ . enthalten.";
    }

    /// <summary>null = Speicherort geeignet, sonst die Meldung.</summary>
    /// <param name="cloudFolders">Zusätzliche Cloud-Ordner (z. B. die OneDrive-Pfade aus den Umgebungsvariablen).</param>
    public static string? ValidatePath(string? filePath, IEnumerable<string> cloudFolders)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            return "Bitte einen Speicherort für die Containerdatei wählen (z. B. D:\\Daten\\sicherung.vhdx).";
        }

        var full = Path.GetFullPath(filePath.Trim());
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length < 2 || root[1] != ':')
        {
            return "Die Containerdatei muss auf einem lokalen Laufwerk liegen (kein Netzwerkpfad).";
        }

        if (!string.Equals(Path.GetExtension(full), Extension, StringComparison.OrdinalIgnoreCase))
        {
            return "Die Containerdatei muss auf „.vhdx“ enden.";
        }

        // Der Pfad geht als lesbares Argument an den erhöhten Lumora-Lauf — Anführungszeichen und Steuerzeichen haben dort
        // nichts zu suchen (Windows erlaubt sie in Dateinamen ohnehin nicht).
        if (full.Any(c => c == '"' || char.IsControl(c)))
        {
            return "Der Pfad der Containerdatei enthält unzulässige Zeichen.";
        }

        // Dieselbe strenge Form, die der erhöhte Lauf verlangt — hier abgelehnt statt erst nach der Windows-Rückfrage
        if (!VhdxElevatedJob.IsStrictLocalPath(full))
        {
            return "Ordner- und Dateinamen im Pfad dürfen nicht mit einem Leerzeichen beginnen oder enden, nicht mit einem Punkt "
                   + "enden und keinen Doppelpunkt enthalten.";
        }

        if (Path.GetFileNameWithoutExtension(full).Length == 0)
        {
            return "Bitte einen Dateinamen angeben.";
        }

        if (IsInCloudFolder(full, cloudFolders))
        {
            return "Der Container darf nicht in OneDrive oder einem anderen Cloud-Ordner liegen — sonst landet er (oder Teile "
                   + "davon) im Internet. Bitte einen Ordner außerhalb wählen.";
        }

        if (File.Exists(full) || Directory.Exists(full))
        {
            return "Diese Datei gibt es schon. Bitte einen anderen Namen wählen.";
        }

        return null;
    }

    /// <summary>true, wenn der Pfad unter einem der Cloud-Ordner liegt oder ein Ordner wie ein Cloud-Dienst heißt.</summary>
    public static bool IsInCloudFolder(string filePath, IEnumerable<string> cloudFolders)
    {
        var full = Path.GetFullPath(filePath);
        foreach (var cloud in cloudFolders)
        {
            if (string.IsNullOrWhiteSpace(cloud) || !Path.IsPathFullyQualified(cloud))
            {
                continue;
            }

            var folder = Path.GetFullPath(cloud).TrimEnd('\\') + "\\";
            if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var folders = Path.GetDirectoryName(full)?.Split('\\', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return folders.Skip(1).Any(segment => CloudFolderMarkers.Any(marker =>
            string.Equals(segment, marker, StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith(marker + " - ", StringComparison.OrdinalIgnoreCase) // „OneDrive - Firma“
            || (marker.Length > 4 && segment.StartsWith(marker, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>
    /// Größenvorschlag aus dem freien Platz: die Hälfte, ab 20 GB auf 10 GB abgerundet, höchstens 1024 GB, mindestens 1 GB
    /// (die Datei wächst erst mit dem Inhalt).
    /// </summary>
    public static int SuggestSizeGigabytes(long freeBytes)
    {
        var half = freeBytes / 2 / (1024L * 1024 * 1024);
        if (half >= 20)
        {
            half = half / 10 * 10;
        }

        return (int)Math.Clamp(half, MinSizeGigabytes, 1024);
    }
}
