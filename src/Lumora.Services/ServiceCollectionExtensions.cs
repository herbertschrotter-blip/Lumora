using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Data.RawSql;
using Lumora.Services.Albums;
using Lumora.Services.Archive;
using Lumora.Services.Catalog;
using Lumora.Services.Categories;
using Lumora.Services.Database;
using Lumora.Services.Database.Relocation;
using Lumora.Services.Duplicates;
using Lumora.Services.Faces;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Erase;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Media;
using Lumora.Services.Metadata;
using Lumora.Services.Motifs;
using Lumora.Services.Operations;
using Lumora.Services.Places;
using Lumora.Services.Quality;
using Lumora.Services.Scanning;
using Lumora.Services.Sorter;
using Lumora.Services.Stories;
using Lumora.Services.Tags;
using Lumora.Services.Thumbnails;
using Lumora.Services.Watch;

namespace Lumora.Services;

/// <summary>
/// DI-Helper für die Services-Schicht. Erlaubt der Wpf-App eine einzige Zeile zur
/// Registrierung aller produktiven Services (Database, …) statt Service-by-Service.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLumoraServices(this IServiceCollection services)
    {
        // Infrastructure — TryAdd damit eine bereits registrierte Instanz nicht überschrieben wird
        services.TryAddSingleton<IAppDataPaths, AppDataPaths>();
        services.AddCatalogContext(); // aktiver Katalog (normal/versteckt), Cache-/Temp-Ordner (ADR-0049)

        // Database
        services.TryAddSingleton<SqlitePragmaInterceptor>();
        services.TryAddSingleton<ILibraryContextFactory, LibraryContextFactory>();
        services.TryAddSingleton<DatabaseInitializer>();

        // Medienformate (Scanner, Renderer, Settings) — Factory, weil Microsoft.DI sonst den Konstruktor
        // (IEnumerable<string>, IEnumerable<string>) mit zwei LEEREN Listen wählt und kein Format erkannt wird.
        services.TryAddSingleton<IMediaFormatRegistry>(_ => new MediaFormatRegistry());

        // Bibliotheken (Services/Library)
        services.AddLibrary();
        services.AddLibraryScoping(); // Bibliotheks-Trennung: Bereich, Gruppen, Verknüpfen (ADR-0048)

        // Scanning (Data/RawSql + Services/Scanning)
        services.TryAddSingleton<BulkScanWriter>();
        services.TryAddSingleton<FtsIndexer>();
        services.TryAddSingleton<FolderEnumerator>();
        services.TryAddSingleton<IScanGate, ScanGate>(); // nie zwei Scans derselben Bibliothek, nie Scan neben Datei-Batch
        services.TryAddSingleton<IOwnChangeRegistry>(_ => new OwnChangeRegistry()); // eigene Schreibvorgänge für die Ordnerüberwachung
        services.TryAddSingleton<IScanService, ScanService>();
        // Ausschlüsse beim Einlesen: allgemeine Liste + je Bibliothek, Entfernen mit Rückgängig (Services/Scanning/Exclusions, ADR-0066)
        Scanning.Exclusions.ScanExclusionServiceCollectionExtensions.AddScanExclusions(services);

        // Thumbnails — Video-Renderer (FFmpeg) ist WPF-frei und lebt hier; derselbe FFmpeg-Wrapper
        // dient als Fallback für Bildformate ohne WIC-Codec (WebP/HEIC/AVIF/RAW).
        // Der Bild-Renderer (WIC/IImageThumbnailRenderer) wird vom Wpf-Projekt registriert.
        services.TryAddSingleton<FfmpegVideoThumbnailer>();
        services.TryAddSingleton<IVideoThumbnailRenderer>(sp => sp.GetRequiredService<FfmpegVideoThumbnailer>());
        services.TryAddSingleton<IFallbackImageThumbnailRenderer>(sp => sp.GetRequiredService<FfmpegVideoThumbnailer>());
        services.TryAddSingleton<IThumbnailService, ThumbnailService>();

        // Aktivitäts-Anzeige: Zentrale aller Hintergrund-Vorgänge, Brücke der Fortschrittsquellen, Datei-Vorgänge als Dekorierer
        // (Services/Activity, ADR-0067) — vor AddOperations, damit IFileOperationService der Dekorierer ist
        Activity.ActivityServiceCollectionExtensions.AddActivities(services);

        // Datei-Operationen (Papierkorb/Move/Rename/Undo) + Katalog-Metadaten (Favorit/Bewertung)
        services.AddOperations();
        services.AddCatalog();
        services.AddSorter();
        services.AddArchives();
        services.AddTags();
        services.AddCategories(); // frei konfigurierbare Kategorien mit Werten (ADR-0027)
        services.AddFolderAdoption(); // Ordner in die Bibliothek übernehmen, Ordner bewerten (ADR-0034)
        services.AddCaptureDates(); // Aufnahmedatum: EXIF/Video/Dateiname, Nachtragen, „Datum setzen…“ (ADR-0046)
        services.AddPlaces(); // Orte: GPS lesen/schreiben, Ortsnamen offline (GeoNames), Karte/Filter (ADR-0050)

        // Ordnerüberwachung (Services/Watch): FileSystemWatcher je Wurzel → Incremental-Scan nach Ruhezeit
        services.AddWatch();

        // Speicherort von Katalog-DB und Thumbnails ändern (Services/Database/Relocation, ADR-0026)
        services.AddDatabaseRelocation();

        // Gesichtserkennung Teil 1: Modell, ONNX-Analysator, Hintergrund-Index (Services/Faces, ADR-0035)
        services.AddFaces();
        services.AddFaceDataEraser(); // „Gesichtsdaten löschen“ je Bibliothek (Services/Faces/Erase, 0.2.38)

        // Gesichtserkennung Teil 3: Zuordnung, Vorschläge, Gruppen, Gesichts-Ausschnitte (Services/Faces/Assignment, ADR-0037)
        services.AddFaceRecognition();

        // Motive: SigLIP 2 lokal, Begriffe, Treffer, Hintergrund-Index, GPU-Sperre mit den Gesichtern (Services/Motifs, ADR-0052)
        services.AddMotifs();

        // Doppelte erkennen: Prüfsummen, Bild-Fingerabdrücke, Gruppen, Aufräumen mit Rückgängig (Services/Duplicates, ADR-0053)
        services.AddDuplicates();
        // Alben und Ordner-Verknüpfungen: Fotos verknüpfen statt kopieren (Services/Albums, ADR-0054)
        services.AddAlbums();
        // Foto-Güte und Highlights: Schärfe, Belichtung, Bildwirkung, Auswahl für Storys (Services/Quality, ADR-0057)
        services.AddQuality();
        // Storys: Ereignisse, Reisen, Rückblicke, Personen, Orte automatisch; Highlights über IHighlightSelector (Services/Stories, ADR-0058)
        services.AddStories();
        // Story-Assistent: Satz → lokale Sprach-KI (Ollama, nur Absicht) → Filter + Aktion, Collage-Bild (Services/Assistant, ADR-0063)
        Assistant.StoryAssistantServiceCollectionExtensions.AddStoryAssistant(services);
        // Ordner in den Papierkorb, Fehlende aus der Bibliothek entfernen (Services/Removal, ADR-0065)
        Removal.RemovalServiceCollectionExtensions.AddRemoval(services);
        // Tag- und Kategorie-Symbole: Schalter in der Settings-Tabelle, Zahlen je Ordner (Services/Marks, Mockup 16, 0.4.35)
        Marks.MarksServiceCollectionExtensions.AddMarks(services);

        return services;
    }
}
