using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Removal;
using Lumora.Services.Removal;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Shell;

namespace Lumora.Exclusions;

/// <summary>Was „Rückgängig“ nach dem Entfernen zusätzlich zurücknimmt: den gerade gesetzten Ausschluss (ADR-0066).</summary>
public abstract record ExclusionUndo
{
    private ExclusionUndo()
    {
    }

    /// <summary>Nur die Katalogzeilen zurück (z. B. „bereits katalogisiert“ entfernt — die Regel bestand schon).</summary>
    public sealed record CatalogOnly : ExclusionUndo;

    /// <summary>Zusätzlich den Ordner-Ausschluss aufheben.</summary>
    public sealed record Folder(int LibraryId, string FolderPath) : ExclusionUndo;

    /// <summary>Zusätzlich den Namen aus der allgemeinen Liste nehmen.</summary>
    public sealed record Name(string FolderName) : ExclusionUndo;
}

/// <summary>Ergebnis eines Ausschluss-Ablaufs: Text, Warnung, Batch und was „Rückgängig“ mitnimmt.</summary>
public sealed record ExclusionActionResult(string Message, bool IsWarning, int? UndoBatchId = null, ExclusionUndo? Undo = null)
{
    public RemovalActionResult ToRemovalResult() => new(Message, IsWarning, UndoBatchId);
}

/// <summary>
/// Abläufe mit Rückfrage für die Ausschlüsse beim Einlesen (ADR-0066), gemeinsam für den Reiter „Ordner“ (Rechtsklick › „Ordner
/// ausschließen…“) und Einstellungen › Bibliotheken &amp; Eingänge: Vorschau „schon katalogisiert“ mit Zahlen wie bei „Fehlende
/// entfernen“, Wahl „Aus der Bibliothek entfernen“ oder „Nur künftig ausschließen“, Entfernen mit Rückgängig (Katalogzeilen mit
/// denselben FileIds zurück, dazu der gerade gesetzte Ausschluss wieder weg), „Ausschluss aufheben“ mit erneutem Einlesen.
/// Die Sperre in „Alle verknüpften“ prüft die aufrufende Seite.
/// </summary>
public sealed class ScanExclusionActions
{
    private readonly IScanExclusionService _service;
    private readonly IFolderEditDialogs _dialogs;
    private readonly ILogger<ScanExclusionActions> _logger;

    public ScanExclusionActions(IScanExclusionService service, IFolderEditDialogs dialogs, ILogger<ScanExclusionActions> logger)
    {
        _service = service;
        _dialogs = dialogs;
        _logger = logger;
    }

    public IScanExclusionService Service => _service;

    /// <summary>Letztes erneutes Einlesen nach „Aufheben“ (läuft im Hintergrund; Tests und Rauchtest warten darauf).</summary>
    public Task? LastRescan { get; private set; }

    /// <summary>„Ordner ausschließen…“; null bei Abbruch.</summary>
    public async Task<ExclusionActionResult?> ExcludeFolderAsync(int libraryId, string folderPath, string folderName)
    {
        var summary = await _service.SummarizeAsync(new ExclusionScope.Folder(libraryId, folderPath)).ConfigureAwait(true);
        if (summary.Count == 0)
        {
            if (!await _dialogs.ConfirmAsync("Ordner ausschließen", FolderOnlyText(folderName), "Ausschließen").ConfigureAwait(true))
            {
                return null;
            }

            return await SaveFolderAsync(libraryId, folderPath, folderName).ConfigureAwait(true)
                ?? new ExclusionActionResult($"„{folderName}“ wird künftig nicht mehr eingelesen.", false);
        }

        var choice = await _dialogs.ChooseAsync("Ordner ausschließen", FolderWithFilesText(folderName, summary), RemoveText, FutureOnlyText).ConfigureAwait(true);
        if (choice == DialogChoice.None)
        {
            return null;
        }

        if (await SaveFolderAsync(libraryId, folderPath, folderName).ConfigureAwait(true) is { } failed)
        {
            return failed;
        }

        if (choice == DialogChoice.Secondary)
        {
            return new ExclusionActionResult($"„{folderName}“ wird künftig nicht mehr eingelesen — {Files(summary.Count)} {(summary.Count == 1 ? "bleibt" : "bleiben")} in der Bibliothek.", false);
        }

        return await RemoveAsync(summary.FileIds, $"„{folderName}“ ausgeschlossen", new ExclusionUndo.Folder(libraryId, folderPath)).ConfigureAwait(true);
    }

    /// <summary>Namen zur allgemeinen Liste hinzufügen (mit Rückfrage, wenn schon Dateien in solchen Ordnern katalogisiert sind); null bei Abbruch.</summary>
    public async Task<ExclusionActionResult?> AddFolderNameAsync(string? name)
    {
        string clean;
        try
        {
            clean = ScanExclusionStore.NormalizeName(name);
        }
        catch (ArgumentException ex)
        {
            return new ExclusionActionResult(ex.Message, true);
        }

        var names = (await _service.Store.GetFolderNamesAsync().ConfigureAwait(true)).ToList();
        if (names.Contains(clean, StringComparer.OrdinalIgnoreCase))
        {
            return new ExclusionActionResult($"„{clean}“ steht schon in der Liste.", true);
        }

        var summary = await _service.SummarizeAsync(new ExclusionScope.Name(clean)).ConfigureAwait(true);
        var choice = DialogChoice.Secondary;
        if (summary.Count > 0)
        {
            choice = await _dialogs.ChooseAsync("Ordnernamen ausschließen", NameWithFilesText(clean, summary), RemoveText, FutureOnlyText).ConfigureAwait(true);
            if (choice == DialogChoice.None)
            {
                return null;
            }
        }

        names.Add(clean);
        await _service.Store.SetFolderNamesAsync(names).ConfigureAwait(true);
        if (choice == DialogChoice.Secondary)
        {
            return new ExclusionActionResult(summary.Count == 0
                ? $"Ordner „{clean}“ werden künftig nicht mehr eingelesen."
                : $"Ordner „{clean}“ werden künftig nicht mehr eingelesen — {Files(summary.Count)} {(summary.Count == 1 ? "bleibt" : "bleiben")} in der Bibliothek.", false);
        }

        return await RemoveAsync(summary.FileIds, $"„{clean}“ ausgeschlossen", new ExclusionUndo.Name(clean)).ConfigureAwait(true);
    }

    /// <summary>Namen aus der allgemeinen Liste nehmen; die Bibliothek wird danach neu eingelesen.</summary>
    public async Task<ExclusionActionResult> RemoveFolderNameAsync(string name, int? rescanLibraryId)
    {
        var names = (await _service.Store.GetFolderNamesAsync().ConfigureAwait(true)).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
        await _service.Store.SetFolderNamesAsync(names).ConfigureAwait(true);
        StartRescan(rescanLibraryId);
        return new ExclusionActionResult(rescanLibraryId is null
            ? $"Ordner „{name}“ werden beim nächsten „Aktualisieren“ wieder eingelesen."
            : $"Ordner „{name}“ werden wieder eingelesen — die aktuelle Bibliothek wird jetzt aktualisiert, andere beim nächsten „Aktualisieren“.", false);
    }

    /// <summary>„Ausschluss aufheben“ für einen Ordner der Bibliothek; danach wird sie neu eingelesen.</summary>
    public async Task<ExclusionActionResult> LiftFolderAsync(int libraryId, string folderPath)
    {
        var name = NameOf(folderPath);
        if (!await _service.Store.RemoveFolderAsync(libraryId, folderPath).ConfigureAwait(true))
        {
            return new ExclusionActionResult($"„{name}“ war nicht ausgeschlossen.", true);
        }

        StartRescan(libraryId);
        return new ExclusionActionResult($"Ausschluss aufgehoben — „{name}“ wird wieder eingelesen.", false);
    }

    /// <summary>Bereits katalogisierte Dateien in ausgeschlossenen Ordnern der Bibliothek entfernen (Einstellungen); null bei Abbruch.</summary>
    public async Task<ExclusionActionResult?> RemoveExistingAsync(int libraryId)
    {
        var summary = await _service.SummarizeAsync(new ExclusionScope.Current([libraryId])).ConfigureAwait(true);
        if (summary.Count == 0)
        {
            return new ExclusionActionResult("In ausgeschlossenen Ordnern ist nichts katalogisiert.", false);
        }

        if (!await _dialogs.ConfirmAsync("Aus der Bibliothek entfernen", ExistingText(summary), RemoveText).ConfigureAwait(true))
        {
            return null;
        }

        return await RemoveAsync(summary.FileIds, "Ausgeschlossene Ordner", new ExclusionUndo.CatalogOnly()).ConfigureAwait(true);
    }

    /// <summary>„Rückgängig“: Katalogzeilen zurück (dieselben FileIds), dazu den gerade gesetzten Ausschluss wieder weg.</summary>
    public async Task<ExclusionActionResult> UndoAsync(int batchId, ExclusionUndo? undo)
    {
        var result = await _service.UndoRemovalAsync(batchId).ConfigureAwait(true);
        var lifted = string.Empty;
        try
        {
            // Ausschluss nur aufheben, wenn wirklich Einträge zurück sind — sonst bliebe nichts in der Bibliothek und nichts ausgeschlossen
            switch (result is { Succeeded: > 0 } ? undo : null)
            {
                case ExclusionUndo.Folder folder:
                    await _service.Store.RemoveFolderAsync(folder.LibraryId, folder.FolderPath).ConfigureAwait(true);
                    lifted = " Der Ausschluss ist aufgehoben.";
                    break;
                case ExclusionUndo.Name name:
                {
                    var names = (await _service.Store.GetFolderNamesAsync().ConfigureAwait(true)).Where(n => !string.Equals(n, name.FolderName, StringComparison.OrdinalIgnoreCase)).ToList();
                    await _service.Store.SetFolderNamesAsync(names).ConfigureAwait(true);
                    lifted = $" „{name.FolderName}“ steht nicht mehr in der Liste.";
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogWarning(ex, "Rückgängig: Ausschluss konnte nicht aufgehoben werden");
        }

        if (result is null)
        {
            return new ExclusionActionResult("Nichts rückgängig zu machen." + lifted, true);
        }

        var files = result.Items.Count(i => i.Success && i.FileId != 0);
        return result.Failed == 0
            ? new ExclusionActionResult($"Rückgängig: {Files(files)} wieder in der Bibliothek.{lifted}", false)
            : new ExclusionActionResult($"Rückgängig: {Files(files)} zurück, {N(result.Failed)} nicht.{lifted}", true);
    }

    // ------------------------------------------------------------------ intern

    private async Task<ExclusionActionResult?> SaveFolderAsync(int libraryId, string folderPath, string folderName)
    {
        try
        {
            return await _service.Store.AddFolderAsync(libraryId, folderPath).ConfigureAwait(true)
                ? null
                : new ExclusionActionResult($"„{folderName}“ ist schon ausgeschlossen (selbst oder über einen Elternordner).", true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new ExclusionActionResult($"„{folderName}“: {ex.Message}", true);
        }
    }

    private async Task<ExclusionActionResult> RemoveAsync(IReadOnlyList<int> fileIds, string what, ExclusionUndo undo)
    {
        var result = await _service.RemoveFromCatalogAsync(fileIds).ConfigureAwait(true);
        _logger.LogInformation("Ausschluss: {Removed} Dateien aus der Bibliothek entfernt, {Folders} Ordner (Batch {BatchId})", result.Removed, result.FoldersRemoved, result.BatchId);
        if (result.Removed == 0)
        {
            return new ExclusionActionResult($"{what} — nichts aus der Bibliothek entfernt.", result.Failed > 0);
        }

        var text = $"{what} — {Files(result.Removed)} aus der Bibliothek entfernt (auf der Platte bleibt alles).";
        if (result.Failed > 0)
        {
            text += $" {N(result.Failed)} fehlgeschlagen.";
        }

        return new ExclusionActionResult(text, result.Failed > 0, result.CanUndo ? result.BatchId : null, undo);
    }

    private void StartRescan(int? libraryId)
    {
        if (libraryId is not { } id)
        {
            return;
        }

        LastRescan = Task.Run(async () =>
        {
            try
            {
                await _service.RescanAsync(id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erneutes Einlesen nach „Ausschluss aufheben“ fehlgeschlagen (Bibliothek {LibraryId})", id);
            }
        });
    }

    // ------------------------------------------------------------------ Texte (statisch, testbar)

    internal const string RemoveText = "Aus der Bibliothek entfernen";
    internal const string FutureOnlyText = "Nur künftig ausschließen";

    internal static string FolderOnlyText(string name)
        => $"„{name}“ samt Unterordnern künftig nicht mehr einlesen? Die Dateien bleiben auf der Platte.\n\n"
           + "Aufheben: Einstellungen › Bibliotheken & Eingänge › Ausschlüsse beim Einlesen.";

    internal static string FolderWithFilesText(string name, ExcludedFilesSummary s)
        => $"„{name}“ samt Unterordnern künftig nicht mehr einlesen?\n\n"
           + $"{Files(s.Count)} darin {(s.Count == 1 ? "ist" : "sind")} schon in der Bibliothek. „{RemoveText}“ nimmt sie heraus — "
           + "es wird keine Datei gelöscht." + UserDataLine(s) + "\n\n"
           + $"„{FutureOnlyText}“ lässt sie in der Bibliothek. „Rückgängig“ bringt die Einträge mit allen Angaben zurück.";

    internal static string NameWithFilesText(string name, ExcludedFilesSummary s)
        => $"Ordner „{name}“ (in jeder Tiefe, in allen Bibliotheken) künftig nicht mehr einlesen?\n\n"
           + $"{Files(s.Count)} in solchen Ordnern {(s.Count == 1 ? "ist" : "sind")} schon in der Bibliothek. „{RemoveText}“ nimmt sie heraus — "
           + "es wird keine Datei gelöscht." + UserDataLine(s) + "\n\n"
           + $"„{FutureOnlyText}“ lässt sie in der Bibliothek. „Rückgängig“ bringt die Einträge mit allen Angaben zurück.";

    internal static string ExistingText(ExcludedFilesSummary s)
        => $"{Files(s.Count)} in ausgeschlossenen Ordnern {(s.Count == 1 ? "ist" : "sind")} noch in der Bibliothek. Aus der Bibliothek entfernen? "
           + "Es wird keine Datei gelöscht — Lumora vergisst nur die Einträge." + UserDataLine(s) + "\n\n"
           + "„Rückgängig“ bringt die Einträge mit allen Angaben zurück.";

    private static string UserDataLine(ExcludedFilesSummary s)
        => s.UserData.HasAny ? $"\n\nDavon {RemovalActions.UserDataText(s.UserData)} — diese Angaben gehen verloren." : string.Empty;

    private static string NameOf(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    private static string Files(int n) => n == 1 ? "1 Datei" : $"{N(n)} Dateien";
}
