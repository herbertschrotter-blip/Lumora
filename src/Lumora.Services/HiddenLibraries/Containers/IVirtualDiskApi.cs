namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>
/// Die Windows-Seite des erhöhten Schritts (ADR-0061, Nachtrag 0.4.26): virtuelle Festplatte über <c>virtdisk.dll</c>,
/// Datenträger/Partition/Volume über die Storage-Verwaltung von Windows. Jede Aktion wirft bei einem Fehler; der Ablauf
/// steht in <see cref="VhdxElevatedJob"/>. Tests ersetzen die Schnittstelle — nie echt in Tests (keine VHDX, keine UAC).
/// </summary>
public interface IVirtualDiskApi
{
    /// <summary>Läuft der Prozess mit Administratorrechten?</summary>
    bool IsElevated { get; }

    /// <summary>Dateisystem eines lokalen, festen, bereiten Laufwerks (<c>D:\</c>); null = kein lokales Laufwerk.</summary>
    string? GetLocalDriveFormat(string driveRoot);

    /// <summary>Ist der Laufwerksbuchstabe schon vergeben?</summary>
    bool IsDriveLetterInUse(char letter);

    /// <summary>Ist der vorhandene Ordner eine Verknüpfung (Junction, symbolischer Link, Bereitstellungspunkt)?</summary>
    bool IsReparsePoint(string directory);

    /// <summary>Legt eine dynamisch erweiterbare VHDX an (die Datei darf noch nicht existieren).</summary>
    void CreateDynamicVhdx(string path, ulong maximumSizeBytes);

    /// <summary>Hängt die VHDX dauerhaft ein (bleibt nach dem Ende dieses Prozesses eingehängt, bis Auswerfen/Neustart).</summary>
    void AttachPermanently(string path);

    /// <summary>Nummer des eingehängten Datenträgers (<c>\\.\PhysicalDriveN</c>).</summary>
    int GetDiskNumber(string path);

    /// <summary>Datenträger online schalten (falls nötig) und als GPT initialisieren.</summary>
    void InitializeGpt(int diskNumber);

    /// <summary>Eine Datenpartition über den ganzen Platz, noch ohne Laufwerksbuchstaben; liefert ihre Nummer.</summary>
    int CreateMaximumPartition(int diskNumber);

    /// <summary>Schnellformatierung NTFS mit Bezeichnung.</summary>
    void FormatNtfs(int diskNumber, int partitionNumber, string label);

    /// <summary>Weist den Laufwerksbuchstaben zu (erst nach dem Formatieren — sonst fragt der Explorer „Datenträger formatieren?“).</summary>
    void AssignDriveLetter(int diskNumber, int partitionNumber, char letter);

    /// <summary>Hängt die VHDX ab (Aufräumen); wirft nicht, wenn sie nicht eingehängt ist.</summary>
    void Detach(string path);

    /// <summary>Löscht die VHDX-Datei (Aufräumen).</summary>
    void DeleteFile(string path);
}
