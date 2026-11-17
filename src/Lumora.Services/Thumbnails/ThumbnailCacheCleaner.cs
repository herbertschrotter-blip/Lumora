using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;

namespace Lumora.Services.Thumbnails;

/// <summary>Ergebnis von „Cache leeren“: gelöschte Cache-Dateien, übersprungene (in Benutzung) und entfernte DB-Zeilen.</summary>
public sealed record ThumbnailCacheClearResult(int DeletedFiles, int SkippedFiles, int Rows);

/// <summary>
/// „Cache leeren“ (Einstellungen › Speicher &amp; Wartung): alle Zeilen der Tabelle <c>Thumbnails</c> und alle Dateien unter
/// <see cref="IAppDataPaths.ThumbnailsRoot"/> entfernen — samt Ablage <c>_undo</c> (ADR-0065 Nachtrag 0.4.39) —, den
/// In-Memory-Zustand des Vorschaubild-Dienstes verwerfen und <see cref="ThumbnailCacheClearedMessage"/> senden: die
/// Vorschaubilder auf Vorrat füllen den Cache wieder auf (0.5.40, ADR-0074 Nachtrag 0.5.40).
/// </summary>
public interface IThumbnailCacheCleaner
{
    Task<ThumbnailCacheClearResult> ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// <inheritdoc cref="IThumbnailCacheCleaner"/>
/// <para>
/// Ablauf (Nachbesserung 0.5.40): zuerst die Dateien — das dauert bei 500 000 Vorschaubildern Minuten; Galerie und Vorrat
/// arbeiten dabei weiter (ein Vorschaubild ohne Datei erzeugt die Galerie neu). Dann ruht der Vorschaubild-Dienst kurz
/// (<see cref="IThumbnailService.PauseAsync"/> wartet auf laufende Aufträge von Galerie, Pflege und Vorrat): alle Zeilen
/// löschen, die inzwischen entstandenen Dateien löschen, Zustand verwerfen, fortsetzen. So bleibt keine Zeile „Generated“
/// ohne Cache-Datei zurück, die der Vorrat für erledigt hielte, und die Galerie steht nur Sekunden still. Läuft ein Auftrag
/// länger als <see cref="PauseTimeout"/>, geht es ohne Pause weiter (Warnung im Protokoll) — besser als Zeilen ohne Dateien
/// stehen zu lassen oder die Galerie unbegrenzt anzuhalten.
/// </para>
/// </summary>
public sealed class ThumbnailCacheCleaner : IThumbnailCacheCleaner
{
    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly IThumbnailService _thumbnails;
    private readonly IMessenger _messenger;
    private readonly ILogger<ThumbnailCacheCleaner> _logger;

    /// <param name="messenger">Optional (Tests); produktiv derselbe wie beim Vorrat, sonst WeakReferenceMessenger.Default.</param>
    public ThumbnailCacheCleaner(
        ILibraryContextFactory factory,
        IAppDataPaths paths,
        IThumbnailService thumbnails,
        IMessenger? messenger = null,
        ILogger<ThumbnailCacheCleaner>? logger = null)
    {
        _factory = factory;
        _paths = paths;
        _thumbnails = thumbnails;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _logger = logger ?? NullLogger<ThumbnailCacheCleaner>.Instance;
    }

    /// <summary>So lange wird höchstens auf laufende Vorschaubild-Aufträge gewartet (FFmpeg bricht nach 8 s ab).</summary>
    public TimeSpan PauseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<ThumbnailCacheClearResult> ClearAsync(CancellationToken ct = default)
    {
        var root = _paths.ThumbnailsRoot;

        // 1) die Dateien, ohne den Dienst anzuhalten (lang); in Benutzung befindliche versucht Schritt 2 noch einmal
        var (deletedFirst, _) = await Task.Run(() => DeleteFiles(root), ct).ConfigureAwait(false);

        // 2) kurz angehalten: Zeilen, inzwischen entstandene Dateien, Zustand
        ThumbnailCacheClearResult result;
        var paused = await TryPauseAsync(ct).ConfigureAwait(false);
        try
        {
            result = await Task.Run(
                () =>
                {
                    var rows = DeleteRows();
                    var (deleted, skipped) = DeleteFiles(root);
                    return new ThumbnailCacheClearResult(deletedFirst + deleted, skipped, rows);
                },
                CancellationToken.None).ConfigureAwait(false);

            // Noch angehalten: als fehlgeschlagen gemerkte Dateien (Codec fehlte, kaputte Datei) werden mit frischem
            // AttemptCount erneut versucht; erledigte ohne Cache-Datei prüft der Dienst ohnehin neu.
            _thumbnails.InvalidateAll();
        }
        finally
        {
            if (paused)
            {
                _thumbnails.Resume(); // zurückgestellte Galerie-Anforderungen kommen sofort dran
            }
        }

        _messenger.Send(new ThumbnailCacheClearedMessage()); // erst nach dem Fortsetzen: der Vorrat beginnt nicht pausiert
        return result;
    }

    /// <summary>false = ein Auftrag lief länger als <see cref="PauseTimeout"/>; die Pause ist dann schon zurückgenommen.</summary>
    private async Task<bool> TryPauseAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PauseTimeout);
        try
        {
            await _thumbnails.PauseAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Cache leeren: ein Vorschaubild-Auftrag läuft seit über {Seconds:0} s — Zeilen werden ohne Pause des Dienstes gelöscht",
                PauseTimeout.TotalSeconds);
            return false;
        }
    }

    private int DeleteRows()
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Thumbnails;";
        return cmd.ExecuteNonQuery();
    }

    private static (int Deleted, int Skipped) DeleteFiles(string root)
    {
        var deleted = 0;
        var skipped = 0;
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return (deleted, skipped);
    }
}
