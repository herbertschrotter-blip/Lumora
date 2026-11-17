namespace Lumora.Services.Cloud;

/// <summary>Eine Datei des Katalogs mit Pfad (für Pin-Aktionen und Nachrichten).</summary>
public sealed record CloudFileRef(int LibraryId, int FileId, string Path);

/// <summary>Was „Immer auf diesem Gerät behalten“ bzw. „Speicherplatz freigeben“ tun soll.</summary>
public enum CloudPinAction
{
    /// <summary>+P −U: Windows lädt die Datei herunter und behält sie.</summary>
    KeepOnDevice = 0,

    /// <summary>+U −P: Windows entfernt den lokalen Inhalt, die Datei liegt danach nur online.</summary>
    FreeUpSpace = 1,
}

/// <summary>Ergebnis einer Pin-Aktion.</summary>
/// <param name="Changed">Attribut gesetzt.</param>
/// <param name="NotCloud">Keine Cloud-Datei (oder nicht mehr vorhanden) — nichts geändert.</param>
/// <param name="Failed">Windows hat das Attribut abgelehnt.</param>
public sealed record CloudPinResult(CloudPinAction Action, int Changed, int NotCloud, int Failed)
{
    /// <summary>Statusmeldung für Galerie und Lightbox.</summary>
    public string StatusText => CloudPinText.Format(this);
}

/// <summary>Dateiattribut lesen und setzen — austauschbar für Tests (keine echte Cloud in der Suite).</summary>
public interface ICloudFileSystem
{
    FileAttributes? TryGetAttributes(string path);

    /// <summary>Setzt die Attribute (wirft bei Ablehnung durch Windows).</summary>
    void SetAttributes(string path, FileAttributes attributes);
}

/// <summary>Echtes Dateisystem.</summary>
public sealed class WindowsCloudFileSystem : ICloudFileSystem
{
    public FileAttributes? TryGetAttributes(string path) => CloudFiles.TryGetAttributes(path);

    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);
}

/// <summary>
/// OneDrive-Bibliothek (ADR-0077): Zustand je Datei (nur Attribute, kein Download) sowie „Immer auf diesem Gerät behalten“
/// und „Speicherplatz freigeben“ über <c>SetFileAttributes</c>. Nach einer Pin-Aktion beobachtet der Dienst die Dateien,
/// bis Windows sie geladen bzw. freigegeben hat, und meldet das als <see cref="Messaging.CloudFilesChangedMessage"/>.
/// </summary>
public interface ICloudFileService
{
    /// <summary>Zustand der Datei (Attribute plus bekannte Sync-Wurzeln).</summary>
    CloudFileState GetState(string path);

    /// <summary>
    /// Schnelle Vorprüfung ohne Dateizugriff: liegt die Datei unter einer bekannten Cloud-Wurzel? Die Galerie ordnet solche
    /// Dateien vor dem Anfordern der Vorschaubilder ein, alle übrigen erst danach (Seiten mit lauter lokalen Dateien warten
    /// so auf keinen zusätzlichen Zugriff).
    /// </summary>
    bool MayBeCloud(string path);

    /// <summary>Setzt +P −U bzw. +U −P für alle Cloud-Dateien der Liste (andere bleiben unberührt) und beobachtet sie danach.</summary>
    CloudPinResult SetPinState(IReadOnlyList<CloudFileRef> files, CloudPinAction action);

    /// <summary>Lumora hat die Datei selbst geladen (Lightbox): Hintergrund-Analysen holen sie nach.</summary>
    void NotifyBecameLocal(CloudFileRef file);
}
