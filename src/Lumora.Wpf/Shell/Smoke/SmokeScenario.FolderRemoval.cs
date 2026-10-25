using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Removal;
using Lumora.Services.Scanning;
using Lumora.Settings;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Ordner löschen und Fehlende entfernen“ (ADR-0065) — nur in der Kopie der Testbibliothek: Unterordner
/// <c>Smoke-Loeschen</c> mit 2 Fotos anlegen → einlesen → im Reiter „Ordner“ per Kontextmenü-Befehl in den Papierkorb (Testmodus
/// des <see cref="SmokeRecycleBin"/>: Ablage im Rauchtest-AppData, nie der echte Papierkorb) → Rückgängig (dieselben FileIds,
/// Bewertung) → eine Datei auf der Platte löschen → einlesen → „fehlt“, Zähler am Ordner, Galerie-Filter „Nur fehlende“,
/// Zahl in Einstellungen › Speicher &amp; Wartung → „Fehlende entfernen (1)“ am Ordner → Datei weg aus dem Katalog (FTS) →
/// aufräumen (Ordner in den Test-Papierkorb, der geleert wird).
/// </summary>
internal sealed partial class SmokeScenario
{
    private SmokeRecycleBin? _smokeBin;
    private SmokeRemovalDialogs? _removalDialogs;
    private MissingFilesSettingsViewModel? _missingFilesSettings;

    private string RemovalDir => Path.Combine(_root, "Smoke-Loeschen");

    private void InitFolderRemovalSmoke(IServiceProvider services)
    {
        _smokeBin = services.GetService<SmokeRecycleBin>();
        _removalDialogs = services.GetService<SmokeRemovalDialogs>();
        _missingFilesSettings = services.GetService<MissingFilesSettingsViewModel>();
    }

    private async Task FolderRemovalAsync()
    {
        Check(_smokeBin is not null && _removalDialogs is not null && _missingFilesSettings is not null, "Test-Papierkorb, Fake-Bestätigung und Einstellungs-Karte registriert");
        if (Directory.Exists(RemovalDir))
        {
            Directory.Delete(RemovalDir, recursive: true); // Rückstand eines abgebrochenen Laufs
        }

        _smokeBin!.TestMode = true;
        try
        {
            await FolderRemovalCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _smokeBin.TestMode = false;
            _smokeBin.Clear();
            _gallery.FilterState = GalleryFilterState.None;
        }
    }

    private async Task FolderRemovalCoreAsync()
    {
        // (1) Unterordner mit 2 Fotos (Kopien aus „Unsortiert“) und einlesen
        var jpgs = Directory.EnumerateFiles(Path.Combine(_root, "Unsortiert"), "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        Check(jpgs.Count == 2, "2 JPGs aus „Unsortiert“ als Material");
        var a = Path.Combine(RemovalDir, "loe-a.jpg");
        var b = Path.Combine(RemovalDir, "loe-b.jpg");
        Directory.CreateDirectory(RemovalDir);
        File.Copy(jpgs[0], a);
        File.Copy(jpgs[1], b);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 2 }, $"Unterordner eingelesen (Added={_lastScan?.Added})");
        _mediaCount += 2;
        var idA = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = 'loe-a.jpg'", ("$lib", _libraryId)).ConfigureAwait(true);
        var idB = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = 'loe-b.jpg'", ("$lib", _libraryId)).ConfigureAwait(true);

        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && !_folders.IsLoading, ShortWait, "Ordnerbaum").ConfigureAwait(true);
        await WaitUntilAsync(() => FindFolderNode(RemovalDir) is not null, ShortWait, "Smoke-Loeschen im Ordnerbaum").ConfigureAwait(true);
        var node = FindFolderNode(RemovalDir)!;
        Check(node.TotalFileCount == 2 && !node.HasMissing && _folders.HasRemovalActions, $"Ordner mit {node.TotalFileCount} Dateien, keine fehlenden, Menü „In den Papierkorb…“ vorhanden");
        await node.RateAllCommand.ExecuteAsync("4").ConfigureAwait(true);

        // (2) „In den Papierkorb…“ (Test-Papierkorb) → Katalog sauber
        await _folders.RecycleFolderCommand.ExecuteAsync(node).ConfigureAwait(true);
        var confirm = _removalDialogs!.Requests.LastOrDefault();
        Check(confirm.Title == "In den Papierkorb verschieben" && confirm.Message.Contains("2 Dateien auf der Platte, davon 2 in der Bibliothek", StringComparison.Ordinal)
              && confirm.Message.Contains("2 mit Sternen/Favorit", StringComparison.Ordinal), $"Bestätigung mit Zahlen: {confirm.Message?.Replace('\n', ' ')}");
        Check(!Directory.Exists(RemovalDir) && _smokeBin!.ParkedCount == 1, "Ordner im (Test-)Papierkorb");
        var rows = await DbScalarAsync<long>($"SELECT COUNT(*) FROM Files WHERE Id IN ({idA},{idB})").ConfigureAwait(true);
        var folderRows = await DbScalarAsync<long>("SELECT COUNT(*) FROM Folders WHERE Path = $p", ("$p", RemovalDir)).ConfigureAwait(true);
        var batch = await DbRowsAsync("SELECT OperationType, Status FROM OperationBatches ORDER BY Id DESC LIMIT 1").ConfigureAwait(true);
        Check(rows == 0 && folderRows == 0 && batch.Single() == $"{(int)OperationType.RecycleFolder} | 2", $"DB: Dateien und Ordnerzeile weg, Batch {batch.Single()}");
        Check(_folders.CanUndoRemoval && _folders.ResultMessage?.Contains("in den Papierkorb verschoben", StringComparison.Ordinal) == true, $"Ergebnis: „{_folders.ResultMessage ?? _folders.ResultWarning}“ mit Rückgängig");
        _mediaCount -= 2;

        // (3) Rückgängig → Ordner und dieselben FileIds mit Bewertung zurück
        await _folders.UndoRemovalCommand.ExecuteAsync(null).ConfigureAwait(true);
        var back = await DbScalarAsync<long>($"SELECT COUNT(*) FROM Files WHERE Id IN ({idA},{idB}) AND Rating = 4 AND Missing = 0").ConfigureAwait(true);
        Check(File.Exists(a) && File.Exists(b) && _smokeBin!.ParkedCount == 0 && back == 2, $"Rückgängig: Ordner zurück, dieselben FileIds mit Bewertung ({back}/2) — „{_folders.ResultMessage ?? _folders.ResultWarning}“");
        _mediaCount += 2;

        // (4) Datei auf der Platte löschen (Kopie) → einlesen → „fehlt“ → Zähler, Galerie-Filter, Einstellungen
        File.Delete(b);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Outcome: ScanOutcome.Completed, MarkedMissing: 1 }, $"Einlesen markiert 1 Datei als fehlend (MarkedMissing={_lastScan?.MarkedMissing})");
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(() => FindFolderNode(RemovalDir) is { HasMissing: true }, ShortWait, "Zähler „fehlt“ am Ordner").ConfigureAwait(true);
        node = FindFolderNode(RemovalDir)!;
        Check(node.MissingCount == 1 && node.RemoveMissingHeader == "Fehlende entfernen (1)", $"Kontextmenü: „{node.RemoveMissingHeader}“");

        _folders.SelectWholeLibraryCommand.Execute(null);
        _gallery.FilterState = new GalleryFilterState(Location: GalleryLocation.Missing);
        await WaitForGalleryAsync("Galerie „Nur fehlende“").ConfigureAwait(true);
        Check(_gallery.TotalCount == 1 && _gallery.CurrentPage.Single().Id == idB && _gallery.ShowRemoveMissingAction, $"Galerie „Nur fehlende“: {_gallery.TotalCount} Datei, Knopf „Aus der Bibliothek entfernen“ sichtbar");
        _gallery.FilterState = GalleryFilterState.None;
        await WaitForGalleryAsync("Galerie ohne Filter").ConfigureAwait(true);
        await _missingFilesSettings!.RefreshAsync().ConfigureAwait(true);
        Check(_missingFilesSettings.MissingCount == 1 && _missingFilesSettings.ButtonText == "Alle fehlenden entfernen (1)", $"Einstellungen › Speicher & Wartung: „{_missingFilesSettings.ButtonText}“");

        // (5) „Fehlende entfernen (1)“ am Ordner → Datei weg aus dem Katalog, die andere bleibt
        var ftsBefore = await DbScalarAsync<long>("SELECT COUNT(*) FROM FilesFts WHERE FilesFts MATCH '\"loe-b\"'").ConfigureAwait(true);
        await _folders.RemoveMissingInFolderCommand.ExecuteAsync(node).ConfigureAwait(true);
        confirm = _removalDialogs.Requests.Last();
        Check(confirm.Title == "Fehlende aus der Bibliothek entfernen" && confirm.Message.Contains("1 Datei in", StringComparison.Ordinal)
              && confirm.Message.Contains("diese Angaben gehen verloren", StringComparison.Ordinal), $"Bestätigung: {confirm.Message.Replace('\n', ' ')}");
        var rowB = await DbScalarAsync<long>($"SELECT COUNT(*) FROM Files WHERE Id = {idB}").ConfigureAwait(true);
        var rowA = await DbScalarAsync<long>($"SELECT COUNT(*) FROM Files WHERE Id = {idA} AND Missing = 0").ConfigureAwait(true);
        var thumbsB = await DbScalarAsync<long>($"SELECT COUNT(*) FROM Thumbnails WHERE FileId = {idB}").ConfigureAwait(true);
        var ftsAfter = await DbScalarAsync<long>("SELECT COUNT(*) FROM FilesFts WHERE FilesFts MATCH '\"loe-b\"'").ConfigureAwait(true);
        var removeBatch = await DbRowsAsync("SELECT OperationType, Status FROM OperationBatches ORDER BY Id DESC LIMIT 1").ConfigureAwait(true);
        Check(rowB == 0 && rowA == 1 && thumbsB == 0 && ftsBefore == 1 && ftsAfter == 0 && removeBatch.Single() == $"{(int)OperationType.RemoveFromCatalog} | 2",
            $"DB: fehlende Datei entfernt (Thumbnails, FTS {ftsBefore} → {ftsAfter}), andere bleibt, Batch {removeBatch.Single()} — „{_folders.ResultMessage ?? _folders.ResultWarning}“");
        Check(File.Exists(a), "Keine Datei auf der Platte angefasst");
        _mediaCount -= 1;
        await SnapshotAsync("21-ordner-fehlende-entfernt").ConfigureAwait(true);

        // (5b) Gewählter Ordner deutlich hervorgehoben (0.4.30) — Screenshots hell und dunkel
        await WaitUntilAsync(() => FindFolderNode(RemovalDir) is not null && !_folders.IsLoading, ShortWait, "Ordnerbaum nach dem Entfernen").ConfigureAwait(true);
        FindFolderNode(RemovalDir)!.IsSelected = true;
        await WaitUntilAsync(() => PathEquals(_folders.SelectionText, RemovalDir), ShortWait, "Ordner gewählt").ConfigureAwait(true);
        _settings.IsThemeLight = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Light && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await SnapshotAsync("22-ordner-auswahl-hell").ConfigureAwait(true);
        _settings.IsThemeDark = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("23-ordner-auswahl-dunkel").ConfigureAwait(true);
        _settings.IsThemeSystem = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);
        _folders.SelectWholeLibraryCommand.Execute(null);

        // (6) Aufräumen: Ordner in den Test-Papierkorb (Katalog bereinigt), der Test-Papierkorb wird danach geleert
        await _folders.RecycleFolderCommand.ExecuteAsync(FindFolderNode(RemovalDir)).ConfigureAwait(true);
        var left = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Name LIKE 'loe-%'").ConfigureAwait(true);
        Check(!Directory.Exists(RemovalDir) && left == 0, "Aufgeräumt: Ordner weg, keine Katalogzeile mehr");
        _mediaCount -= 1;
        _folders.DismissResultCommand.Execute(null);
        _logger.LogInformation("Rauchtest   Test-Papierkorb: {Path} wird geleert", _smokeBin!.BinDir);
    }
}
