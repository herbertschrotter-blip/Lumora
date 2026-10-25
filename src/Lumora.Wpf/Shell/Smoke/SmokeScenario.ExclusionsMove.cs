using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Exclusions;
using Lumora.Services.Scanning;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Ausschlüsse und Ordner verschieben“ (ADR-0066) — nur in der Kopie der Testbibliothek: <c>Smoke-Ausschluss</c>
/// mit 2 Fotos, einem Unterordner <c>Weg</c> (1 Foto) und <c>Tief\.thumbs</c> (1 Bild, darf nicht katalogisiert werden), dazu
/// <c>Smoke-Verschieben\Quelle</c> und <c>Smoke-Verschieben\Ziel</c> → einlesen → „.thumbs“ nicht im Katalog → <c>Weg</c> per
/// „Ordner ausschließen…“ samt Bestätigung aus dem Katalog → im Baum ausgeblendet → in den Einstellungen „Ausschluss aufheben“ →
/// wieder eingelesen → <c>Quelle</c> nach <c>Ziel</c> verschieben (FileId gleich) → Rückgängig (FileId gleich) → aufräumen
/// (Ordner in den Test-Papierkorb, der geleert wird).
/// </summary>
internal sealed partial class SmokeScenario
{
    private SmokeFolderEditDialogs? _folderEditDialogs;
    private ScanExclusionsSettingsViewModel? _exclusionSettings;
    private int _exclusionSmokeCataloged; // im Schritt katalogisierte Dateien (für _mediaCount nach dem Aufräumen)

    private string ExclusionDir => Path.Combine(_root, "Smoke-Ausschluss");

    private string MoveDir => Path.Combine(_root, "Smoke-Verschieben");

    private void InitExclusionsMoveSmoke(IServiceProvider services)
    {
        _folderEditDialogs = services.GetService<SmokeFolderEditDialogs>();
        _exclusionSettings = services.GetService<ScanExclusionsSettingsViewModel>();
    }

    private async Task ExclusionsMoveAsync()
    {
        Check(_folderEditDialogs is not null && _exclusionSettings is not null && _smokeBin is not null, "Fake-Rückfragen, Einstellungs-Karte und Test-Papierkorb registriert");
        foreach (var dir in new[] { ExclusionDir, MoveDir }.Where(Directory.Exists))
        {
            Directory.Delete(dir, recursive: true); // Rückstand eines abgebrochenen Laufs
        }

        try
        {
            await ExclusionsMoveCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _smokeBin!.TestMode = true;
            _navigation.NavigateTo(NavigationTags.Folders);
            foreach (var dir in new[] { ExclusionDir, MoveDir })
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                await WaitUntilAsync(() => !_folders.IsLoading && FindFolderNode(dir) is not null, ShortWait, "Ordner zum Aufräumen im Baum").ConfigureAwait(true);
                await _folders.RecycleFolderCommand.ExecuteAsync(FindFolderNode(dir)).ConfigureAwait(true);
            }

            _smokeBin.TestMode = false;
            _smokeBin.Clear();
            _folders.DismissResultCommand.Execute(null);
            var left = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Name LIKE 'aus-%' OR Name LIKE 'ver-%'").ConfigureAwait(true);
            Check(left == 0 && !Directory.Exists(ExclusionDir) && !Directory.Exists(MoveDir), $"Aufgeräumt: Ordner weg, keine Katalogzeile mehr ({left})");
            _mediaCount -= _exclusionSmokeCataloged;
            _exclusionSmokeCataloged = 0;
        }
    }

    private async Task ExclusionsMoveCoreAsync()
    {
        // (1) Material: Kopien aus „Unsortiert“
        var jpgs = Directory.EnumerateFiles(Path.Combine(_root, "Unsortiert"), "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        Check(jpgs.Count == 2, "2 JPGs aus „Unsortiert“ als Material");
        void Put(string relative, int source)
        {
            var target = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(jpgs[source], target);
        }

        Put(Path.Combine("Smoke-Ausschluss", "aus-a.jpg"), 0);
        Put(Path.Combine("Smoke-Ausschluss", "aus-b.jpg"), 1);
        Put(Path.Combine("Smoke-Ausschluss", "Weg", "aus-weg.jpg"), 0);
        Put(Path.Combine("Smoke-Ausschluss", "Tief", ".thumbs", "aus-thumb.jpg"), 1);
        Put(Path.Combine("Smoke-Verschieben", "Quelle", "ver-q.jpg"), 0);
        Put(Path.Combine("Smoke-Verschieben", "Ziel", "ver-z.jpg"), 1);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 5 }, $"Eingelesen: 5 neue Dateien, das Bild in „.thumbs“ nicht (Added={_lastScan?.Added})");
        _mediaCount += 5;
        _exclusionSmokeCataloged = 5;
        var thumbRows = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Name = 'aus-thumb.jpg'").ConfigureAwait(true);
        var thumbFolders = await DbScalarAsync<long>("SELECT COUNT(*) FROM Folders WHERE Path LIKE '%.thumbs%'").ConfigureAwait(true);
        Check(thumbRows == 0 && thumbFolders == 0, $"„.thumbs“ nicht im Katalog (Dateien {thumbRows}, Ordner {thumbFolders})");

        // (2) „Ordner ausschließen…“ an „Weg“ → Rückfrage mit Zahl → aus dem Katalog, im Baum ausgeblendet
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && !_folders.IsLoading, ShortWait, "Ordnerbaum").ConfigureAwait(true);
        var weg = Path.Combine(ExclusionDir, "Weg");
        await WaitUntilAsync(() => FindFolderNode(weg) is not null, ShortWait, "„Weg“ im Ordnerbaum").ConfigureAwait(true);
        Check(_folders.HasExclusionActions && _folders.HasMoveActions && FindFolderNode(weg)!.CanExclude, "Menü „Ordner ausschließen…“ und „Verschieben nach…“ vorhanden");
        await _folders.ExcludeFolderCommand.ExecuteAsync(FindFolderNode(weg)).ConfigureAwait(true);
        var ask = _folderEditDialogs!.Requests.LastOrDefault();
        Check(ask.Title == "Ordner ausschließen" && ask.Message?.Contains("1 Datei darin ist schon in der Bibliothek", StringComparison.Ordinal) == true, $"Rückfrage mit Zahl: {ask.Message?.Replace('\n', ' ')}");
        var wegRows = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Name = 'aus-weg.jpg'").ConfigureAwait(true);
        var batch = await DbRowsAsync("SELECT OperationType, Status FROM OperationBatches ORDER BY Id DESC LIMIT 1").ConfigureAwait(true);
        Check(wegRows == 0 && File.Exists(Path.Combine(weg, "aus-weg.jpg")) && batch.Single() == $"{(int)OperationType.ExcludeFromCatalog} | 2",
            $"Aus dem Katalog genommen, Datei bleibt auf der Platte, Batch {batch.Single()} — „{_folders.ResultMessage ?? _folders.ResultWarning}“");
        _mediaCount -= 1;
        _exclusionSmokeCataloged -= 1;
        await WaitUntilAsync(() => !_folders.IsLoading && FindFolderNode(weg) is null && _folders.ExcludedFoldersText is not null, ShortWait, "„Weg“ ausgeblendet, Hinweis „… ausgeschlossen“").ConfigureAwait(true);
        await SnapshotAsync("24-ordner-ausgeschlossen").ConfigureAwait(true);

        // (3) Einstellungen: Ausschluss aufheben → wieder eingelesen
        await _exclusionSettings!.RefreshAsync().ConfigureAwait(true);
        var item = _exclusionSettings.Folders.FirstOrDefault(f => PathEquals(f.FullPath, weg));
        Check(item is not null && _exclusionSettings.FolderNames.Contains(".thumbs"), $"Einstellungen: „Weg“ in der Liste der Bibliothek, allgemeine Liste mit „.thumbs“ ({string.Join(", ", _exclusionSettings.FolderNames)})");
        _lastScan = null;
        await _exclusionSettings.LiftFolderCommand.ExecuteAsync(item).ConfigureAwait(true);
        if (_exclusionSettings.Actions.LastRescan is { } rescan)
        {
            await rescan.ConfigureAwait(true);
        }

        wegRows = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Name = 'aus-weg.jpg' AND Missing = 0").ConfigureAwait(true);
        Check(wegRows == 1 && _exclusionSettings.Folders.Count == 0, $"Ausschluss aufgehoben, wieder eingelesen ({_lastScan?.Added} neu) — „{_exclusionSettings.ResultText}“");
        _mediaCount += 1;
        _exclusionSmokeCataloged += 1;

        // (4) Ordner verschieben: „Quelle“ nach „Ziel“ (FileId gleich) → Rückgängig (FileId gleich)
        var quelle = Path.Combine(MoveDir, "Quelle");
        var ziel = Path.Combine(MoveDir, "Ziel");
        var idQ = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = 'ver-q.jpg'", ("$lib", _libraryId)).ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(() => !_folders.IsLoading && FindFolderNode(quelle) is not null && FindFolderNode(ziel) is not null, ShortWait, "Quelle und Ziel im Ordnerbaum").ConfigureAwait(true);
        Check(_folders.CanDropFolder(FindFolderNode(quelle), FindFolderNode(ziel)) && !_folders.CanDropFolder(FindFolderNode(quelle), FindFolderNode(MoveDir)),
            "Ziehen: auf „Ziel“ erlaubt, auf den eigenen Elternordner nicht");
        _folderEditDialogs.PickResult = ziel;
        await _folders.MoveFolderCommand.ExecuteAsync(FindFolderNode(quelle)).ConfigureAwait(true);
        var movedPath = Path.Combine(ziel, "Quelle");
        var folderOfQ = await DbScalarAsync<string>($"SELECT d.Path FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id = {idQ}").ConfigureAwait(true);
        var moveBatch = await DbRowsAsync("SELECT OperationType, Status FROM OperationBatches ORDER BY Id DESC LIMIT 1").ConfigureAwait(true);
        Check(Directory.Exists(movedPath) && !Directory.Exists(quelle) && PathEquals(folderOfQ, movedPath) && moveBatch.Single() == $"{(int)OperationType.MoveFolder} | 2" && _folders.CanUndoRemoval,
            $"Verschoben, dieselbe FileId im neuen Ordner, Batch {moveBatch.Single()} — „{_folders.ResultMessage ?? _folders.ResultWarning}“");

        await _folders.UndoRemovalCommand.ExecuteAsync(null).ConfigureAwait(true);
        folderOfQ = await DbScalarAsync<string>($"SELECT d.Path FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id = {idQ}").ConfigureAwait(true);
        Check(Directory.Exists(quelle) && !Directory.Exists(movedPath) && PathEquals(folderOfQ, quelle), $"Rückgängig: Ordner zurück, dieselbe FileId — „{_folders.ResultMessage ?? _folders.ResultWarning}“");
        _logger.LogInformation("Rauchtest   Ausschlüsse/Verschieben: {Requests} Rückfragen", _folderEditDialogs.Requests.Count);
    }
}
