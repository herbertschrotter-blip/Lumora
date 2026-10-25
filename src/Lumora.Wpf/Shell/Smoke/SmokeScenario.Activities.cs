using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lumora.Services.Activity;
using Lumora.Services.Scanning;
using Lumora.Settings;
using Lumora.Shell.Activity;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Aktivitäten“ (ADR-0067, Mockup 15) — nur in der Kopie der Testbibliothek: <c>Smoke-Aktivitaet\Quelle</c>
/// mit 1 Foto einlesen → Scan-Sperre selbst halten → Verschieben nach <c>Ziel</c> starten → Statusleiste zeigt den Vorgang
/// („Verschieben — wartet“) → Liste aufklappen (Screenshot hell) → Sperre frei → fertig unter „Zuletzt fertig“ mit Rückgängig,
/// kurze Meldung mit Rückgängig (Screenshot dunkel) → Rückgängig über die Liste → Datei wieder in <c>Quelle</c>, dieselbe FileId
/// → aufräumen (Test-Papierkorb).
/// </summary>
internal sealed partial class SmokeScenario
{
    private ActivityStatusViewModel? _activities;
    private IActivityCenter? _activityCenter;
    private int _activitySmokeCataloged;

    private string ActivityDir => Path.Combine(_root, "Smoke-Aktivitaet");

    /// <summary>Zeile „Gesichter erkennen“ unter „Läuft“ (Schritt „Gesichter“, Pause/Fortsetzen über die Liste).</summary>
    private ActivityItemViewModel? FacesActivityRow() => _activities?.Running.FirstOrDefault(r => r.Snapshot.Kind == ActivityKind.Faces);

    /// <summary>Zeile einer wartenden Datei-Operation ohne eigenen Vorgang (Schritt „Wartezustände“).</summary>
    private ActivityItemViewModel? GateWaitActivityRow() => _activities?.Waiting.FirstOrDefault(r => r.Title == "Datei-Vorgang — wartet");

    private void InitActivitiesSmoke(IServiceProvider services)
    {
        _activities = services.GetService<ActivityStatusViewModel>();
        _activityCenter = services.GetService<IActivityCenter>();
    }

    private async Task ActivitiesAsync()
    {
        Check(_activities is not null && _activityCenter is not null && _statusBar.Activities == _activities && _smokeBin is not null,
            "Aktivitäts-Anzeige in der Statusleiste, Zentrale und Test-Papierkorb registriert");
        if (Directory.Exists(ActivityDir))
        {
            Directory.Delete(ActivityDir, recursive: true); // Rückstand eines abgebrochenen Laufs
        }

        var themeBefore = _theme.Mode;
        try
        {
            await ActivitiesCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            await _theme.SetModeAsync(themeBefore).ConfigureAwait(true);
            _activities!.IsFlyoutOpen = false;
            _activities.DismissToastCommand.Execute(null);
            _smokeBin!.TestMode = true;
            _navigation.NavigateTo(NavigationTags.Folders);
            if (Directory.Exists(ActivityDir))
            {
                await WaitUntilAsync(() => !_folders.IsLoading && FindFolderNode(ActivityDir) is not null, ShortWait, "Ordner zum Aufräumen im Baum").ConfigureAwait(true);
                await _folders.RecycleFolderCommand.ExecuteAsync(FindFolderNode(ActivityDir)).ConfigureAwait(true);
            }

            _smokeBin.TestMode = false;
            _smokeBin.Clear();
            _folders.DismissResultCommand.Execute(null);
            var left = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Name LIKE 'akt-%'").ConfigureAwait(true);
            Check(left == 0 && !Directory.Exists(ActivityDir), $"Aufgeräumt: Ordner weg, keine Katalogzeile mehr ({left})");
            _mediaCount -= _activitySmokeCataloged;
            _activitySmokeCataloged = 0;
            _navigation.NavigateTo(NavigationTags.Gallery);
        }
    }

    private async Task ActivitiesCoreAsync()
    {
        var vm = _activities!;

        // (1) Material: ein Foto in „Quelle“, eingelesen
        var jpg = Directory.EnumerateFiles(Path.Combine(_root, "Unsortiert"), "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First();
        var quelle = Path.Combine(ActivityDir, "Quelle");
        var ziel = Path.Combine(ActivityDir, "Ziel");
        Directory.CreateDirectory(quelle);
        Directory.CreateDirectory(ziel);
        File.Copy(jpg, Path.Combine(quelle, "akt-1.jpg"));
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Added: 1 }, $"Eingelesen: 1 neue Datei (Added={_lastScan?.Added})");
        _mediaCount += 1;
        _activitySmokeCataloged = 1;
        var fileId = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = 'akt-1.jpg'", ("$lib", _libraryId)).ConfigureAwait(true);
        await WaitUntilAsync(() => vm.Recent.Any(r => r.Snapshot.Kind == ActivityKind.Scan), ShortWait, "Einlesen unter „Zuletzt fertig“").ConfigureAwait(true);

        // (2) Verschieben startet, wartet auf die (vom Schritt gehaltene) Scan-Sperre → Statusleiste und Liste
        _navigation.NavigateTo(NavigationTags.Gallery);
        Task<Services.Operations.FileOperationBatchResult> move;
        using (await _scanGate.AcquireAsync(_libraryId, CancellationToken.None).ConfigureAwait(true))
        {
            move = _fileOps.MoveAsync([fileId], ziel);
            await WaitUntilAsync(() => vm.StatusText == "Verschieben — wartet" || vm.Waiting.Any(w => w.Title == "Verschieben — wartet"), ShortWait, "Statusleiste: Verschieben").ConfigureAwait(true);
            var row = vm.Waiting.Single(w => w.Title == "Verschieben — wartet");
            Check(row.Subtitle == "wartet auf laufendes Einlesen derselben Bibliothek" && row.CanCancel, $"Liste „Wartet“: {row.Subtitle}, Abbrechen möglich");
            Check(vm.HasActive && !vm.IsIdle, $"Statusleiste: „{vm.StatusText}“ {vm.OthersText}");
            vm.IsFlyoutOpen = true;
            await _theme.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
            await SnapshotAsync("25-aktivitaeten-hell").ConfigureAwait(true);
        }

        // (3) Sperre frei → fertig, „Zuletzt fertig“ mit Rückgängig, kurze Meldung mit Rückgängig
        var result = await move.WaitAsync(LongWait).ConfigureAwait(true);
        Check(result.Succeeded == 1 && File.Exists(Path.Combine(ziel, "akt-1.jpg")), "Verschoben");
        await WaitUntilAsync(() => vm.Recent.FirstOrDefault() is { Title: "Verschieben", HasUndo: true }, ShortWait, "„Zuletzt fertig“: Verschieben mit Rückgängig").ConfigureAwait(true);
        var done = vm.Recent.First();
        Check(done.Subtitle?.Contains("1 Datei verschoben", StringComparison.Ordinal) == true && done.Subtitle.Contains("nach „Ziel“", StringComparison.Ordinal),
            $"Ergebnis nennt nur den Ordnernamen: {done.Subtitle}");
        await WaitUntilAsync(() => vm.IsToastVisible, ShortWait, "kurze Meldung").ConfigureAwait(true);
        Check(vm.ToastTitle == "1 Datei verschoben" && vm.ToastHasUndo, $"Meldung „{vm.ToastTitle}“ mit Rückgängig");
        await _theme.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("25-aktivitaeten-dunkel").ConfigureAwait(true);

        // (4) Rückgängig über die Liste: derselbe Weg (UndoBatchAsync), Datei zurück, dieselbe FileId
        await done.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
        var folderOf = await DbScalarAsync<string>($"SELECT d.Path FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id = {fileId}").ConfigureAwait(true);
        Check(File.Exists(Path.Combine(quelle, "akt-1.jpg")) && !File.Exists(Path.Combine(ziel, "akt-1.jpg")) && PathEquals(folderOf, quelle),
            "Rückgängig über die Liste: Datei zurück in „Quelle“, dieselbe FileId");
        await WaitUntilAsync(() => vm.IsToastVisible && vm.ToastTitle == "Rückgängig: 1 Datei zurückverschoben", ShortWait, "kurze Meldung „Rückgängig: 1 Datei zurückverschoben“").ConfigureAwait(true);
        var original = vm.Recent.First(r => r.Id == done.Id);
        Check(!original.HasUndo && original.Subtitle?.Contains("rückgängig gemacht", StringComparison.Ordinal) == true, $"Ursprünglicher Eintrag ohne Rückgängig: {original.Subtitle}");
        vm.IsFlyoutOpen = false;
        _logger.LogInformation("Rauchtest   Aktivitäten: {Recent} Einträge unter „Zuletzt fertig“", vm.Recent.Count);
    }
}
