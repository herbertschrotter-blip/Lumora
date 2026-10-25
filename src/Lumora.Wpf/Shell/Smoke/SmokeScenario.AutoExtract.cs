using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Archive;
using Lumora.Services.Scanning;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Auto-Entpacken“ (0.2.31, ADR-0032/0033): zweite Wurzel als Eingang (wie „Eingang → Archiv“), ein
/// Archiv vor dem Einschalten (bleibt manuell), Schalter der aktiven Bibliothek auf der Einstellungsseite, ZIP in den
/// Eingang kopieren → Aktualisieren → automatisch entpackt → Folge-Scan → Katalog/Galerie/Statusleiste, danach
/// „Vorhandene Archive jetzt entpacken“ für das ältere Archiv. Am Ende Schalter aus, Eingang entfernt (nur Katalog), Ordner
/// gelöscht. Läuft vor der Ordnerüberwachung (Automatik noch aus — kein zusätzlicher Scan).
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string AutoExtractOldZip = "smoke-auto-alt.zip";
    private const string AutoExtractNewZip = "smoke-auto-neu.zip";

    private readonly IArchiveAutoExtractService _autoExtract;

    /// <summary>Eingangs-Ordner des Schritts — Geschwisterordner der Testbibliothek, eigener Name.</summary>
    private string AutoExtractInboxDir => Path.Combine(Path.GetDirectoryName(_root) ?? _root, Path.GetFileName(_root) + "-Auto-Eingang");

    private async Task AutoExtractAsync()
    {
        var inbox = AutoExtractInboxDir;
        if (Directory.Exists(inbox))
        {
            Directory.Delete(inbox, recursive: true);
            _logger.LogInformation("Rauchtest   Eingangs-Ordner eines früheren Laufs entfernt: {Path}", inbox);
        }

        Directory.CreateDirectory(inbox);
        var unsorted = Path.Combine(_root, "Unsortiert");
        var jpgs = Directory.EnumerateFiles(unsorted, "*.jpg")
            .Where(f => !PathEquals(Path.GetFileName(f), AddedFileName) && !PathEquals(Path.GetFileName(f), WatchedFileName))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        Check(jpgs.Count == 2, "2 JPGs aus „Unsortiert“ als Archiv-Inhalt");
        var oldZip = WriteSmokeZip(Path.Combine(inbox, AutoExtractOldZip), ("auto-alt.jpg", jpgs[0]));
        var oldTarget = Path.Combine(inbox, Path.GetFileNameWithoutExtension(AutoExtractOldZip));
        var newTarget = Path.Combine(inbox, Path.GetFileNameWithoutExtension(AutoExtractNewZip));
        Check(!_autoExtract.GetSettings(_libraryId).Enabled, "Standard für die Bibliothek: aus");

        // (1) Eingang hinzufügen, Aktualisieren mit Einstellung aus → Archiv im Katalog, nicht entpackt
        var inboxRoot = await _libraries.AddRootAsync(_libraryId, inbox, LibraryRootRole.Inbox).ConfigureAwait(true);
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheksseite").ConfigureAwait(true);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 1 }, $"Aktualisieren nimmt das vorhandene ZIP auf (Added={_lastScan?.Added})");
        _mediaCount++;
        await _autoExtract.WhenIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        Check(!Directory.Exists(oldTarget) && File.Exists(oldZip), "Einstellung aus: nicht entpackt");

        // (2) Einstellungsseite: Schalter der aktiven Bibliothek einschalten
        await OpenSettingsSectionAsync(NavigationTags.SettingsArchives, "Einstellungen").ConfigureAwait(true);
        await _settings.LoadAutoExtractSettingsAsync().ConfigureAwait(true);
        var library = await _libraries.GetAsync(_libraryId).ConfigureAwait(true);
        Check(_settings.IsAutoExtractAvailable && _settings.AutoExtractLibraryId == _libraryId
              && library is not null && _settings.AutoExtractLibraryText.Contains(library.Name, StringComparison.Ordinal),
            $"Abschnitt „Archive“ gilt für die aktive Bibliothek: {_settings.AutoExtractLibraryText}");
        Check(!_settings.IsAutoExtractEnabled && !_settings.ExtractExistingArchivesCommand.CanExecute(null), "Schalter aus, „Vorhandene Archive …“ erst nach dem Einschalten");
        _settings.IsAutoExtractEnabled = true;
        await WaitUntilAsync(() => _autoExtract.GetSettings(_libraryId).Enabled, ShortWait, "Schalter gespeichert").ConfigureAwait(true);
        var stored = await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = $k", ("$k", ArchiveAutoExtractSettings.EnabledKeyFor(_libraryId))).ConfigureAwait(true);
        var legacy = await DbScalarAsync<int>("SELECT COUNT(*) FROM Settings WHERE Key IN ($a, $b)",
            ("$a", ArchiveAutoExtractSettings.LegacyEnabledKey), ("$b", ArchiveAutoExtractSettings.LegacyRecycleKey)).ConfigureAwait(true);
        Check(stored == "true" && legacy == 0, $"Settings {ArchiveAutoExtractSettings.EnabledKeyFor(_libraryId)} = {stored}, kein globaler Schlüssel");
        Check(_settings.ExtractExistingArchivesCommand.CanExecute(null), "„Vorhandene Archive in Eingängen jetzt entpacken…“ verfügbar");
        // Abschnitt „Archive“ steht unten auf der Seite: für den Screenshot den Knopf „Vorhandene Archive …“ ins Bild holen
        Button? button = null;
        await WaitUntilAsync(
            () => (button = FindVisual<Button>(Application.Current.MainWindow,
                b => AutomationProperties.GetName(b) == "Vorhandene Archive in Eingängen jetzt entpacken")) is not null,
            ShortWait,
            "Knopf „Vorhandene Archive in Eingängen jetzt entpacken…“ auf der Einstellungsseite").ConfigureAwait(true);
        await WaitUntilAsync(() => button!.IsEnabled, ShortWait, "Knopf „Vorhandene Archive …“ aktiv").ConfigureAwait(true);
        button!.BringIntoView();
        await Task.Delay(300).ConfigureAwait(true);
        await SnapshotAsync("15-einstellungen-auto-entpacken").ConfigureAwait(true);

        // (3) ZIP in den Eingang kopieren → Aktualisieren → automatisch entpackt → Folge-Scan → Katalog
        var staged = WriteSmokeZip(Path.Combine(_paths.AppDataRoot, AutoExtractNewZip), ("auto-a.jpg", jpgs[0]), ("sub/auto-b.jpg", jpgs[1]));
        var newZip = Path.Combine(inbox, AutoExtractNewZip);
        File.Copy(staged, newZip, overwrite: true);
        File.Delete(staged);
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheksseite").ConfigureAwait(true);
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_library.ErrorMessage is null, $"Aktualisieren: {_library.ScanSummary}");
        _mediaCount++;
        await WaitUntilAsync(() => _autoExtract.LastSummary?.Items.Any(i => i.Name == AutoExtractNewZip) == true, LongWait, "automatisches Entpacken gelaufen").ConfigureAwait(true);
        await _autoExtract.WhenIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        var summary = _autoExtract.LastSummary!;
        Check(summary.LibraryId == _libraryId && summary.Items.Count == 1 && summary.Items[0].State == ArchiveAutoExtractItemState.Extracted
              && summary.FilesExtracted == 2 && !summary.HasProblems,
            $"Automatisch entpackt: {summary.ExtractedCount} Archiv, {summary.FilesExtracted} Dateien, Probleme {summary.Problems.Count}");
        Check(summary.Scan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 2 }, $"Folge-Scan: Added={summary.Scan?.Added}");
        Check(File.Exists(Path.Combine(newTarget, "auto-a.jpg")) && File.Exists(Path.Combine(newTarget, "sub", "auto-b.jpg")) && File.Exists(newZip),
            "Dateien im Ordner neben dem Archiv, Archiv bleibt liegen (Papierkorb aus)");
        Check(!Directory.Exists(oldTarget), "Archiv von vor dem Einschalten bleibt unberührt");
        _mediaCount += 2;
        var cataloged = await DbScalarAsync<int>(
            "SELECT COUNT(*) FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Name IN ('auto-a.jpg', 'auto-b.jpg') AND d.RootId = $r",
            ("$r", inboxRoot.Id)).ConfigureAwait(true);
        Check(cataloged == 2, $"DB: {cataloged} entpackte Dateien unter der Eingangs-Wurzel");
        await WaitUntilAsync(() => _statusBar.ArchiveText?.StartsWith("Automatisch entpackt: 1 Archiv · 2 Dateien", StringComparison.Ordinal) == true,
            ShortWait, "Ergebnis des Entpackens").ConfigureAwait(true);
        // Seit 0.4.36 (ADR-0067) steht das Ergebnis in der Aktivitäts-Anzeige unter „Zuletzt fertig“
        await WaitUntilAsync(() => _activities?.Recent.Any(r => r.Title == "Archive entpacken" && r.Subtitle?.Contains("1 Archiv entpackt · 2 Dateien", StringComparison.Ordinal) == true) == true,
            ShortWait, "Aktivitäts-Anzeige: Archive entpacken unter „Zuletzt fertig“").ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount,
            ShortWait, $"Galerie zeigt {_mediaCount} Dateien inkl. entpackter").ConfigureAwait(true);
        await SnapshotAsync("15-galerie-auto-entpackt").ConfigureAwait(true);

        // (4) „Vorhandene Archive in Eingängen jetzt entpacken“ holt das ältere Archiv nach
        var existing = await _autoExtract.CountExistingAsync(_libraryId).ConfigureAwait(true);
        Check(existing == 1, $"Vorhandene, noch nicht entpackte Archive: {existing} (das neue hat schon seinen Ordner)");
        var queued = await _settings.StartExtractExistingAsync().ConfigureAwait(true);
        Check(queued == 1, $"Nachholen eingereiht: {queued}");
        await WaitUntilAsync(() => _autoExtract.LastSummary?.Items.Any(i => i.Name == AutoExtractOldZip) == true, LongWait, "Nachholen gelaufen").ConfigureAwait(true);
        await _autoExtract.WhenIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        summary = _autoExtract.LastSummary!;
        Check(summary.Items.Count == 1 && summary.Items[0].State == ArchiveAutoExtractItemState.Extracted && summary.Scan is { Added: 1 }
              && File.Exists(Path.Combine(oldTarget, "auto-alt.jpg")),
            $"Vorhandenes Archiv entpackt: {summary.FilesExtracted} Datei, Folge-Scan Added={summary.Scan?.Added}");
        _mediaCount += 1;
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == _mediaCount, ShortWait, $"Galerie zeigt {_mediaCount} Dateien").ConfigureAwait(true);

        // (5) Einstellung zurücksetzen, Eingang entfernen (nur Katalog), Ordner löschen
        _settings.IsAutoExtractEnabled = false;
        await WaitUntilAsync(() => !_autoExtract.GetSettings(_libraryId).Enabled, ShortWait, "Schalter wieder aus").ConfigureAwait(true);
        await _libraries.RemoveRootAsync(inboxRoot.Id).ConfigureAwait(true);
        _mediaCount -= 5; // 2 Archive + 3 entpackte Dateien
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == _mediaCount, ShortWait, $"Galerie nach Entfernen des Eingangs: {_mediaCount}").ConfigureAwait(true);
        var rootsLeft = await DbScalarAsync<int>("SELECT COUNT(*) FROM LibraryRoots WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(rootsLeft == 1 && _settings.ErrorMessage is null, "nur noch die Archiv-Wurzel, Einstellungen ohne Fehler");
        Directory.Delete(inbox, recursive: true);
    }

    /// <summary>Erstes Element vom Typ <typeparamref name="T"/> im visuellen Baum, das <paramref name="match"/> erfüllt.</summary>
    private static T? FindVisual<T>(DependencyObject? root, Func<T, bool> match)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T candidate && match(candidate))
            {
                return candidate;
            }

            if (FindVisual(child, match) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static string WriteSmokeZip(string path, params (string Entry, string Source)[] entries)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, source) in entries)
        {
            zip.CreateEntryFromFile(source, entry);
        }

        return path;
    }
}
