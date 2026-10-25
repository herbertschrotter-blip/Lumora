using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Services.Scanning;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Wartezustände“ (0.2.22, ADR-0025): der Schritt belegt die Scan-Sperre selbst (statt eines langen
/// Scans) und prüft die echten Anzeigen — Statusleiste mit „Abbrechen“ für eine wartende Datei-Operation, Scan-Panel der
/// Bibliotheksseite für einen wartenden manuellen Scan. Läuft vor der Ordnerüberwachung (Automatik noch aus).
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string GateWaitFolderName = "Smoke-Warten";

    private readonly IScanGate _scanGate;

    private async Task GateWaitAsync()
    {
        // (1) Datei-Operation wartet auf einen „laufenden Scan“ → Statusleiste → „Abbrechen“ → nichts angelegt
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie").ConfigureAwait(true);
        var folderPath = Path.Combine(_root, GateWaitFolderName);
        Check(!Directory.Exists(folderPath), $"Ordner „{GateWaitFolderName}“ existiert noch nicht");
        var wait = _statusBar.FileOperationWait;
        using (await _scanGate.AcquireAsync(_libraryId, CancellationToken.None).ConfigureAwait(true))
        {
            var create = _fileOps.CreateFolderAsync(_libraryId, _root, GateWaitFolderName);
            await WaitUntilAsync(() => wait.IsWaiting, ShortWait, "Statusleiste: Datei-Operation wartet").ConfigureAwait(true);
            Check(wait.Text == "Wartet auf laufenden Scan…" && wait.CancelCommand.CanExecute(null), $"Wartezustand: „{wait.Text}“ mit „Abbrechen“");

            // Seit 0.4.36 (ADR-0067) zeigt die Aktivitäts-Anzeige den Wartezustand: „Datei-Vorgang — wartet“ mit Grund und Abbrechen
            await WaitUntilAsync(() => GateWaitActivityRow() is not null, ShortWait, "Aktivitäts-Anzeige: Datei-Vorgang wartet").ConfigureAwait(true);
            var waitRow = GateWaitActivityRow()!;
            Check(waitRow.Subtitle == "wartet auf laufendes Einlesen derselben Bibliothek" && waitRow.CancelCommand.CanExecute(null) && _activities!.HasActive,
                $"Liste „Wartet“: {waitRow.Subtitle}; Statusleiste „{_activities?.StatusText}“");
            await SnapshotAsync("12-statusleiste-wartet").ConfigureAwait(true);

            waitRow.CancelCommand.Execute(null); // Abbrechen über die Liste (ScanGate.CancelWait, derselbe Weg wie bisher)
            var error = await CatchAsync(() => create).ConfigureAwait(true);
            Check(error is ScanGateWaitCanceledException, $"Datei-Operation abgebrochen ({error?.GetType().Name ?? "keine Ausnahme"})");
            await WaitUntilAsync(() => !wait.IsWaiting, ShortWait, "Hinweis verschwindet").ConfigureAwait(true);
            Check(_statusBar.ScanResultText == "Wartende Datei-Operation abgebrochen", $"Zustand bestätigt: „{_statusBar.ScanResultText}“");
            await WaitUntilAsync(() => _activities!.Recent.FirstOrDefault()?.Subtitle?.Contains("Wartende Datei-Operation abgebrochen", System.StringComparison.Ordinal) == true,
                ShortWait, "„Zuletzt fertig“: Wartende Datei-Operation abgebrochen").ConfigureAwait(true);
            Check(!Directory.Exists(folderPath), "kein Ordner angelegt");
        }

        Check(!_scanGate.IsBusy(_libraryId) && _scanGate.GetWaits().Count == 0, "Sperre frei, niemand wartet");

        // (2) Manueller Scan wartet auf einen Datei-Batch → Scan-Panel „Wartet auf laufende Datei-Operation…“ → startet danach
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheksseite").ConfigureAwait(true);
        _lastScan = null;
        Task scanRun;
        using (await _scanGate.AcquireForFileOperationAsync(_libraryId, CancellationToken.None).ConfigureAwait(true))
        {
            scanRun = _library.RunSmokeScanAsync(_root);
            await WaitUntilAsync(() => _library.IsWaitingForGate, ShortWait, "Bibliotheksseite: Scan wartet").ConfigureAwait(true);
            Check(_library.IsScanning && _library.ScanPhaseText == "Wartet auf laufende Datei-Operation…", $"Scan-Panel: „{_library.ScanPhaseText}“");
            await SnapshotAsync("12-bibliothek-wartet").ConfigureAwait(true);
        }

        await scanRun.WaitAsync(LongWait).ConfigureAwait(true);
        Check(!_library.IsWaitingForGate && !_library.IsScanning && _library.ErrorMessage is null, "Scan lief nach dem Batch-Ende");
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed }, $"Incremental-Scan abgeschlossen ({_lastScan?.Outcome})");
    }
}
