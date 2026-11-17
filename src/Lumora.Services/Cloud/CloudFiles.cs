namespace Lumora.Services.Cloud;

/// <summary>
/// Zustand einer Datei in einem Cloud-Ordner mit „Dateien bei Bedarf“ (OneDrive, ADR-0077), nur aus den Datei-Attributen
/// gelesen — ohne Download.
/// </summary>
/// <param name="IsCloud">Liegt in einem Cloud-Ordner (Cloud-Attribut gesetzt oder unter einer bekannten Sync-Wurzel).</param>
/// <param name="IsOnlineOnly">Platzhalter: der Inhalt liegt nur online; wer ihn liest, löst einen Download aus.</param>
/// <param name="IsPinned">„Immer auf diesem Gerät behalten“ (<see cref="CloudFiles.Pinned"/>).</param>
/// <param name="IsUnpinned">„Speicherplatz freigeben“ angefordert (<see cref="CloudFiles.Unpinned"/>).</param>
public readonly record struct CloudFileState(bool IsCloud, bool IsOnlineOnly, bool IsPinned, bool IsUnpinned)
{
    /// <summary>Keine Cloud-Datei (oder nicht lesbar).</summary>
    public static readonly CloudFileState NotCloud = default;
}

/// <summary>
/// Zentrale Platzhalter-Erkennung (ADR-0077): eine Stelle für die Cloud-Attribute, die alle Dienste nutzen
/// (<c>ThumbnailService.IsLocallyAvailable</c> leitet hierher weiter). Liest nur Attribute, nie den Inhalt.
/// <list type="bullet">
/// <item><b>Nur online</b> (Platzhalter): <c>RecallOnDataAccess</c> (0x00400000), <c>RecallOnOpen</c> (0x00040000) oder
/// <c>Offline</c> (0x1000).</item>
/// <item><b>Immer behalten</b>: <c>Pinned</c> (0x00080000); <b>Speicherplatz freigeben</b>: <c>Unpinned</c> (0x00100000) —
/// Windows bzw. OneDrive lädt bzw. entfernt den Inhalt danach selbst.</item>
/// </list>
/// Cloud-Dateien und -Ordner tragen zusätzlich <c>ReparsePoint</c>; anders als eine Verknüpfung haben sie kein Linkziel —
/// der Scanner liest sie deshalb ein (<see cref="IsLink"/>).
/// </summary>
public static class CloudFiles
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN: Inhalt (bzw. Ordnerinhalt) liegt nicht lokal.</summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: Lesen des Inhalts lädt die Datei herunter.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>FILE_ATTRIBUTE_PINNED: „Immer auf diesem Gerät behalten“.</summary>
    public const FileAttributes Pinned = (FileAttributes)0x00080000;

    /// <summary>FILE_ATTRIBUTE_UNPINNED: „Speicherplatz freigeben“.</summary>
    public const FileAttributes Unpinned = (FileAttributes)0x00100000;

    /// <summary>Platzhalter, deren Inhalt nicht lokal liegt (Lesen löst einen Download aus).</summary>
    public const FileAttributes OnlineOnlyAttributes = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    /// <summary>Alle Attribute, die nur Cloud-Dateien tragen.</summary>
    public const FileAttributes CloudAttributes = OnlineOnlyAttributes | Pinned | Unpinned;

    /// <summary>Was <c>SetFileAttributes</c> annimmt — alles andere (Reparse, Sparse, Recall …) verwaltet Windows selbst.</summary>
    internal const FileAttributes SettableAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.Temporary
        | FileAttributes.Offline | FileAttributes.NotContentIndexed | Pinned | Unpinned;

    /// <summary>Nur online (Platzhalter)?</summary>
    public static bool IsOnlineOnly(FileAttributes attributes)
        => (attributes & FileAttributes.Directory) == 0 && (attributes & OnlineOnlyAttributes) != 0;

    /// <summary>Zustand aus Attributen; <paramref name="underSyncRoot"/>: liegt unter einer bekannten Sync-Wurzel.</summary>
    public static CloudFileState Classify(FileAttributes attributes, bool underSyncRoot = false)
    {
        var cloud = underSyncRoot || (attributes & CloudAttributes) != 0;
        return cloud
            ? new CloudFileState(true, IsOnlineOnly(attributes), (attributes & Pinned) != 0, (attributes & Unpinned) != 0)
            : CloudFileState.NotCloud;
    }

    /// <summary>Attribute oder null (fehlt, kein Zugriff, ungültiger Pfad) — nur Metadaten, kein Download.</summary>
    public static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Datei vorhanden und nur online (Platzhalter).</summary>
    public static bool IsOnlineOnly(string path) => TryGetAttributes(path) is { } attributes && IsOnlineOnly(attributes);

    /// <summary>Datei vorhanden und lokal (kein Ordner, kein Platzhalter, dessen Lesen einen Download auslöste).</summary>
    public static bool IsLocallyAvailable(string path)
        => TryGetAttributes(path) is { } attributes && (attributes & (OnlineOnlyAttributes | FileAttributes.Directory)) == 0;

    /// <summary>
    /// Neue Attribute für „Immer auf diesem Gerät behalten“ (+P −U) bzw. „Speicherplatz freigeben“ (+U −P): nur, was
    /// <c>SetFileAttributes</c> annimmt, die übrigen setzbaren bleiben.
    /// </summary>
    public static FileAttributes WithPin(FileAttributes current, bool keepOnDevice)
        => (current & SettableAttributes & ~(Pinned | Unpinned)) | (keepOnDevice ? Pinned : Unpinned);

    /// <summary>
    /// Verknüpfung (Symlink, Junction) statt Cloud-Datei bzw. -Ordner? Cloud-Einträge tragen <c>ReparsePoint</c>, haben aber
    /// kein Linkziel. Mit Cloud-Attribut gilt ein Eintrag nie als Verknüpfung (ohne Dateizugriff); sonst entscheidet
    /// <see cref="FileSystemInfo.LinkTarget"/> (liest nur den Reparse-Punkt, kein Download). Unlesbar (z. B. OneDrive nicht
    /// erreichbar) → keine Verknüpfung: der Eintrag wird eingelesen bzw. sein Ordner meldet beim Auflisten einen IO-Fehler
    /// und gilt dann als nicht lesbar — nie werden seine Dateien still als „fehlt“ markiert.
    /// </summary>
    public static bool IsLink(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var attributes = info.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        if ((attributes & CloudAttributes) != 0)
        {
            return false;
        }

        try
        {
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
