using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Exclusions;
using Lumora.Removal;
using Lumora.Services.Operations;

namespace Lumora.Folders;

/// <summary>
/// „Ordner verschieben“ im Reiter „Ordner“ (ADR-0066): Zielauswahl (Windows-Ordnerdialog, beginnt in der Wurzel des Ordners;
/// geprüft wird im Dienst), Bestätigung beim Ziehen, Ausführung über <see cref="IFileOperationService.MoveFolderAsync"/>
/// (ablehnen mit klarer Meldung) und Rückgängig über <see cref="IFileOperationService.UndoBatchAsync"/>.
/// </summary>
public sealed class FolderMoveActions
{
    private readonly IFileOperationService _fileOps;
    private readonly IFolderEditDialogs _dialogs;
    private readonly ILogger<FolderMoveActions> _logger;

    public FolderMoveActions(IFileOperationService fileOps, IFolderEditDialogs dialogs, ILogger<FolderMoveActions> logger)
    {
        _fileOps = fileOps;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>Zielordner wählen; null bei Abbruch.</summary>
    public string? PickTarget(string folderName, string? initialDirectory)
        => _dialogs.PickFolder($"„{folderName}“ verschieben nach …", initialDirectory);

    /// <summary>Verschieben (beim Ziehen mit Rückfrage); null bei Abbruch.</summary>
    public async Task<RemovalActionResult?> MoveAsync(int folderId, string folderName, string targetPath, bool confirm)
    {
        var targetName = NameOf(targetPath);
        if (confirm && !await _dialogs.ConfirmAsync("Ordner verschieben", ConfirmText(folderName, targetName), "Verschieben").ConfigureAwait(true))
        {
            return null;
        }

        var result = await _fileOps.MoveFolderAsync(folderId, targetPath).ConfigureAwait(true);
        _logger.LogInformation("Ordner verschieben: {State} (Batch {BatchId})", result.Success ? "ok" : "nicht verschoben", result.BatchId);
        if (result.Success)
        {
            return new RemovalActionResult($"„{folderName}“ nach „{targetName}“ verschoben.", false, result.BatchId);
        }

        return new RemovalActionResult(result.BatchId == 0
            ? result.Error ?? "Der Ordner wurde nicht verschoben."
            : $"„{folderName}“ wurde nicht verschoben: {result.Error}", true);
    }

    /// <summary>„Rückgängig“: Ordner zurück an den alten Platz.</summary>
    public async Task<RemovalActionResult> UndoAsync(int batchId)
    {
        var result = await _fileOps.UndoBatchAsync(batchId).ConfigureAwait(true);
        if (result is null)
        {
            return new RemovalActionResult("Nichts rückgängig zu machen.", true);
        }

        var folder = result.Items.FirstOrDefault(i => i.FileId == 0);
        return folder is { Success: true }
            ? new RemovalActionResult($"Rückgängig: „{NameOf(folder.SourcePath)}“ ist wieder am alten Platz.", false)
            : new RemovalActionResult($"Rückgängig nicht möglich: {folder?.Error ?? result.Items.FirstOrDefault(i => !i.Success)?.Error ?? "unbekannter Fehler"}", true);
    }

    internal static string ConfirmText(string folderName, string targetName)
        => $"„{folderName}“ samt Inhalt nach „{targetName}“ verschieben?\n\nAlle Angaben (Sterne, Personen, Alben, Tags) bleiben erhalten. "
           + "„Rückgängig“ verschiebt den Ordner zurück.";

    private static string NameOf(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
