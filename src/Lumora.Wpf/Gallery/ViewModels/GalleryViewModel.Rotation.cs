using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Rückgängig-Taste der Galerie für „Nach links/rechts drehen“ (ADR-0068): ein Dreh-Batch wird zurückgenommen, wenn er jünger ist
/// als jeder andere rückgängig machbare Eintrag (Datei-Operationen, „Datum setzen“, „Ort setzen“) — gleiche Reihenfolge über die
/// Batch-Id wie bei „Ort setzen“.
/// </summary>
public sealed partial class GalleryViewModel
{
    private readonly Lumora.Services.Rotation.IPhotoRotationService? _rotation;

    private async Task<bool> TryUndoRotationAsync()
    {
        if (_rotation is null || await _rotation.GetUndoableBatchIdAsync() is not { } batchId)
        {
            return false;
        }

        var batches = await _fileOps.GetRecentBatchesAsync(UndoBatchWindow);
        if (batches.Where(b => b.CanUndo).Select(b => (int?)b.Id).Max() is { } newest && newest > batchId)
        {
            return false;
        }

        if (_captureDateActions?.Service is { } capture && await capture.GetUndoableBatchIdAsync() is { } captureBatch && captureBatch > batchId)
        {
            return false;
        }

        if (_placeActions?.Undo is { } geo && await geo.GetUndoableBatchIdAsync() is { } geoBatch && geoBatch > batchId)
        {
            return false;
        }

        var result = await _rotation.UndoAsync(batchId);
        _logger.LogInformation("Rückgängig: Drehen (Batch {BatchId}) — {Count} Datei(en)", batchId, result.Rotated);
        SetResultStatus(result.Failed == 0
            ? $"Rückgängig: Drehen — {result.Rotated:N0} Foto(s) zurückgedreht."
            : $"Rückgängig: Drehen — {result.Rotated:N0} zurückgedreht, {result.Failed:N0} nicht möglich.");
        if (result.Failed > 0)
        {
            await _dialogs.ShowErrorsAsync("Rückgängig: Drehen", $"{result.Failed:N0} Datei(en) wurden inzwischen verändert und bleiben, wie sie sind.", result.Errors);
        }

        return true;
    }

    private async Task<bool> HasRotationUndoAsync()
        => _rotation is not null && await _rotation.GetUndoableBatchIdAsync() is not null;
}
