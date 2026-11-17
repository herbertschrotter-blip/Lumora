using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Lumora.Services.Media;

namespace Lumora.Services.Scanning;

/// <summary>
/// Durchläuft das Filesystem rekursiv unterhalb eines RootPath und liefert DiscoveredFile-Einträge
/// für alle Endungen aus dem <see cref="IMediaFormatRegistry"/>. Permission-/IO-Fehler werden
/// geloggt und übersprungen (kein Abbruch des Scans).
/// <para>
/// Verknüpfungen (Symlinks, Junctions) werden übersprungen, Cloud-Dateien und -Ordner nicht (ADR-0077): OneDrive markiert
/// jede Datei und jeden Ordner als Reparse-Punkt — bis 0.5.40 fand der Scan in einem OneDrive-Ordner deshalb nichts.
/// <see cref="Cloud.CloudFiles.IsLink"/> unterscheidet beides, ohne den Inhalt zu lesen (Platzhalter bleiben online).
/// </para>
/// </summary>
public sealed class FolderEnumerator
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false, // wir machen den Rekurs manuell, um Permission-Fehler pro Folder zu behandeln
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System, // Reparse-Punkte: Cloud ja, Verknüpfung nein (IsLink, ADR-0077)
    };

    private readonly IMediaFormatRegistry _formats;
    private readonly ILogger<FolderEnumerator> _logger;

    public FolderEnumerator(IMediaFormatRegistry formats, ILogger<FolderEnumerator> logger)
    {
        _formats = formats;
        _logger = logger;
    }

    /// <summary>
    /// Yield-Stream über alle gefundenen MediaFiles unterhalb von rootPath.
    /// Berücksichtigt Cancellation pro Folder.
    /// </summary>
    /// <param name="onIoError">
    /// Optionaler Hook für IO-Fehler (nicht für fehlende Berechtigungen): wird mit Pfad und Ausnahme aufgerufen,
    /// bevor der Ordner bzw. die Datei übersprungen wird. Der Incremental-Scan prüft darin den Root erneut
    /// (PLAN Kap. 12: IOException → Root-Recheck) und darf den Scan durch eine Ausnahme beenden.
    /// </param>
    /// <param name="exclusions">
    /// Ausschlüsse beim Einlesen (ADR-0066): ausgeschlossene Unterordner (Name aus der allgemeinen Liste in jeder Tiefe bzw.
    /// Ordner der Bibliothek) werden samt Inhalt nicht betreten. Null = keine.
    /// </param>
    public IEnumerable<DiscoveredFile> Enumerate(string rootPath, CancellationToken ct = default, Action<string, Exception>? onIoError = null, Exclusions.ScanExclusionSet? exclusions = null)
    {
        if (!Directory.Exists(rootPath))
        {
            _logger.LogWarning("RootPath existiert nicht: {Path}", rootPath);
            yield break;
        }

        var queue = new Queue<string>();
        queue.Enqueue(rootPath);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = queue.Dequeue();

            // Files in diesem Folder
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", Options);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Keine Berechtigung auf Folder (übersprungen): {Path}", folder);
                continue;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO-Fehler auf Folder: {Path}", folder);
                onIoError?.Invoke(folder, ex);
                continue;
            }

            using var fileEnumerator = files.GetEnumerator();
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Auflisten kann mittendrin scheitern (z. B. OneDrive-Ordner, Verbindung weg): Ordner als nicht lesbar
                // melden (UnreadablePaths, ADR-0077) statt den ganzen Scan abzubrechen
                string filePath;
                try
                {
                    if (!fileEnumerator.MoveNext())
                    {
                        break;
                    }

                    filePath = fileEnumerator.Current;
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "IO-Fehler beim Auflisten von Folder: {Path}", folder);
                    onIoError?.Invoke(folder, ex);
                    break;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Keine Berechtigung beim Auflisten von Folder (Rest übersprungen): {Path}", folder);
                    break;
                }

                DiscoveredFile? df = null;
                try
                {
                    var ext = Path.GetExtension(filePath);
                    if (!_formats.TryClassify(ext, out var mediaType))
                    {
                        continue;
                    }

                    var info = new FileInfo(filePath);
                    if (Cloud.CloudFiles.IsLink(info))
                    {
                        continue; // Verknüpfung auf eine Datei — wie bisher nicht einlesen
                    }

                    df = new DiscoveredFile(
                        FullPath: info.FullName,
                        FolderPath: folder,
                        Name: info.Name,
                        Extension: ext.ToLowerInvariant(),
                        Size: info.Length,
                        CreatedAtUtc: info.CreationTimeUtc,
                        ModifiedAtUtc: info.LastWriteTimeUtc,
                        MediaType: mediaType);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Skip File: {Path}", filePath);
                    if (ex is IOException)
                    {
                        onIoError?.Invoke(filePath, ex);
                    }
                }

                if (df is not null)
                {
                    yield return df;
                }
            }

            // Subfolder enqueuen
            try
            {
                foreach (var subInfo in new DirectoryInfo(folder).EnumerateDirectories("*", Options))
                {
                    var sub = subInfo.FullName;
                    // Katalog einer versteckten Bibliothek (.lumora, ADR-0049) liegt in der Bibliothek — nie mitscannen
                    if (!Infrastructure.CatalogFolders.IsCatalogFolder(sub)
                        && exclusions?.IsExcludedChildDirectory(sub) != true // ADR-0066: ausgeschlossen → nicht betreten
                        && !Cloud.CloudFiles.IsLink(subInfo)) // Junction/Symlink: keine Schleifen; OneDrive-Ordner ja (ADR-0077)
                    {
                        queue.Enqueue(sub);
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Keine Berechtigung auf Subfolder-Listing (übersprungen): {Path}", folder);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO-Fehler auf Subfolder-Listing: {Path}", folder);
                onIoError?.Invoke(folder, ex);
            }
        }
    }
}
