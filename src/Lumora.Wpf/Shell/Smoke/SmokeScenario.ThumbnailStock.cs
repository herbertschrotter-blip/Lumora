using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lumora.Services.Infrastructure;
using Lumora.Services.Thumbnails;
using Lumora.Settings.ThumbnailStock;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Vorschaubilder auf Vorrat“ (0.5.40, vor „Versteckte Bibliothek“). Der Vorrat ist von Beginn an aus
/// (<see cref="DisableThumbnailStockForSmokeAsync"/>), damit er Zähler und Vorschaubild-Schritte nicht stört. Hier: Schalter
/// in Einstellungen › Speicher &amp; Wartung › Vorschaubilder mit seinem ViewModel; aus → ein Durchgang tut nichts; die
/// Vorschaubilder aller Fotos und Videos der Testbibliothek samt Zeile entfernt; Schalter an (Settings-Tabelle „true“) → der
/// Dienst erstellt sie im Hintergrund wieder: gleiche Cache-Datei <c>{fileId}_400_still.jpg</c> im Cache des Rauchtest-
/// AppData, Status „Generated“, nichts mehr offen; Schalter aus → „false“. Zum Schluss der Stand vor dem Rauchtest.
/// </summary>
internal sealed partial class SmokeScenario
{
    private IThumbnailStockService? _thumbnailStock;
    private ThumbnailStockSettingsViewModel? _thumbnailStockSettings;
    private bool _thumbnailStockEnabledBefore = ThumbnailStockSettingsStore.DefaultEnabled;

    private void InitThumbnailStockSmoke(IServiceProvider services)
    {
        _thumbnailStock = services.GetService<IThumbnailStockService>();
        _thumbnailStockSettings = services.GetService<ThumbnailStockSettingsViewModel>();
    }

    /// <summary>Gleich zu Beginn: Vorrat aus (Standard wäre an), erst der eigene Schritt schaltet ihn ein.</summary>
    private async Task DisableThumbnailStockForSmokeAsync()
    {
        if (_thumbnailStock is not null)
        {
            _thumbnailStockEnabledBefore = _thumbnailStock.IsEnabled;
            await _thumbnailStock.SetEnabledAsync(false).ConfigureAwait(true);
        }
    }

    private async Task ThumbnailStockAsync()
    {
        Check(_thumbnailStock is not null && _thumbnailStockSettings is not null, "Vorrat-Dienst und Schalter registriert");
        var stock = _thumbnailStock!;
        var settings = _thumbnailStockSettings!;
        try
        {
            await ThumbnailStockCoreAsync(stock, settings).ConfigureAwait(true);
        }
        finally
        {
            settings.IsEnabled = _thumbnailStockEnabledBefore;
            await settings.SavePending.ConfigureAwait(true);
        }
    }

    private async Task ThumbnailStockCoreAsync(IThumbnailStockService stock, ThumbnailStockSettingsViewModel settings)
    {
        // (1) Einstellungen › Speicher & Wartung: Schalter in der Karte „Vorschaubilder“, aus (Rauchtest)
        await OpenSettingsSectionAsync(NavigationTags.SettingsStorage, "Speicher & Wartung").ConfigureAwait(true);
        var panel = _settingsPage.FindName("StorageView") is Settings.Sections.SettingsStorageSection storage ? storage.ThumbnailStockPanel : null;
        Check(panel is not null && ReferenceEquals(panel.DataContext, settings), "Schalter „Vorschaubilder auf Vorrat erstellen“ mit seinem ViewModel");
        await WaitUntilAsync(() => panel!.IsVisible && panel.ActualHeight > 0, ShortWait, "Schalter sichtbar").ConfigureAwait(true);
        Check(!stock.IsEnabled && !settings.IsEnabled, "Vorrat im Rauchtest bis hierher aus");
        Check(await stock.RunPassAsync().ConfigureAwait(true) == ThumbnailStockReport.Empty, "ausgeschaltet: ein Durchgang tut nichts");

        // (2) Vorschaubilder aller Fotos und Videos der Testbibliothek entfernen (Zeile + Cache-Datei), damit der Vorrat Arbeit hat
        var rows = await DbRowsAsync(
            "SELECT t.FileId, t.CachePath FROM Thumbnails t JOIN Files f ON f.Id = t.FileId " +
            "WHERE f.LibraryId = $lib AND f.Missing = 0 AND f.MediaType IN (0, 1) AND t.Size = $s AND t.Kind = 0 AND t.Status = 2",
            ("$lib", _libraryId),
            ("$s", ThumbnailService.Size)).ConfigureAwait(true);
        var removed = rows.Select(r => r.Split(" | ")).Select(c => (Id: int.Parse(c[0], CultureInfo.InvariantCulture), Path: c[1])).ToList();
        Check(removed.Count >= 5, $"{removed.Count} Vorschaubilder der Testbibliothek vorhanden");
        await DbRowsAsync(
            "DELETE FROM Thumbnails WHERE Size = $s AND Kind = 0 AND FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib AND Missing = 0 AND MediaType IN (0, 1))",
            ("$lib", _libraryId),
            ("$s", ThumbnailService.Size)).ConfigureAwait(true);
        foreach (var (_, path) in removed)
        {
            TryDeleteStockThumb(path);
        }

        var ids = removed.Select(r => r.Id).ToList();
        _thumbnails.Invalidate(ids);
        Check(await RegeneratedCountAsync(ids).ConfigureAwait(true) == 0, $"{ids.Count} Fotos und Videos ohne Vorschaubild");

        // (3) Schalter an → gespeichert, der Dienst erstellt sie im Hintergrund wieder (niedrigster Vorrang, nach den übrigen Aufgaben)
        settings.IsEnabled = true;
        await settings.SavePending.ConfigureAwait(true);
        Check(stock.IsEnabled && settings.StatusText is null, "Schalter an");
        Check(await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = $k", ("$k", ThumbnailStockSettingsStore.EnabledKey)).ConfigureAwait(true) == "true",
            "Settings-Tabelle: thumbnails.stock.enabled = true");
        var deadline = DateTime.UtcNow + LongWait;
        int regenerated;
        while ((regenerated = await RegeneratedCountAsync(ids).ConfigureAwait(true)) < ids.Count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new SmokeCheckException($"Timeout nach {LongWait.TotalSeconds:N0} s: Vorrat hat erst {regenerated} von {ids.Count} Vorschaubildern erstellt (Status {stock.Status})");
            }

            await Task.Delay(250).ConfigureAwait(true);
        }

        Check(true, $"Vorrat hat alle {ids.Count} entfernten Vorschaubilder im Hintergrund erstellt");

        // (4) Gleiche Cache-Datei und gleicher Status wie die Galerie, nichts außerhalb des Caches im Rauchtest-AppData
        var thumbsRoot = _paths.GetLibraryThumbnailsPath(_libraryId);
        foreach (var (id, oldPath) in removed)
        {
            var path = await DbScalarAsync<string>(
                "SELECT CachePath FROM Thumbnails WHERE FileId = $id AND Size = $s AND Kind = 0 AND Status = 2",
                ("$id", id),
                ("$s", ThumbnailService.Size)).ConfigureAwait(true);
            if (path is null || !PathEquals(path, oldPath) || !File.Exists(path)
                || !AppDataPaths.IsSameOrInside(path, thumbsRoot)
                || !string.Equals(Path.GetFileName(path), $"{id}_{ThumbnailService.Size}_still.jpg", StringComparison.Ordinal))
            {
                Check(false, $"Vorschaubild der Datei {id} wieder erstellt (Cache-Datei {path ?? "fehlt"})");
            }
        }

        Check(true, $"{removed.Count} Vorschaubilder mit derselben Cache-Datei und Status „Generated“ wieder da");

        // (5) Schalter aus → gespeichert, ein Durchgang tut nichts
        settings.IsEnabled = false;
        await settings.SavePending.ConfigureAwait(true);
        Check(!stock.IsEnabled && await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = $k", ("$k", ThumbnailStockSettingsStore.EnabledKey)).ConfigureAwait(true) == "false",
            "Schalter aus: thumbnails.stock.enabled = false");
        Check(await stock.RunPassAsync().ConfigureAwait(true) == ThumbnailStockReport.Empty, "ausgeschaltet: kein Durchgang");
    }

    /// <summary>Wie viele dieser Dateien wieder ein gültiges Vorschaubild der aktuellen Größe haben.</summary>
    private async Task<int> RegeneratedCountAsync(IReadOnlyList<int> fileIds)
    {
        var rows = await DbRowsAsync(
            "SELECT t.FileId FROM Thumbnails t JOIN Files f ON f.Id = t.FileId " +
            "WHERE f.LibraryId = $lib AND t.Size = $s AND t.Kind = 0 AND t.Status = 2 AND t.SourceModifiedAtUtc = f.ModifiedAtUtc",
            ("$lib", _libraryId),
            ("$s", ThumbnailService.Size)).ConfigureAwait(true);
        var wanted = fileIds.ToHashSet();
        return rows.Count(r => wanted.Contains(int.Parse(r, CultureInfo.InvariantCulture)));
    }

    private void TryDeleteStockThumb(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Rauchtest   Vorschaubild nicht löschbar");
        }
    }
}
