using Lumora.Domain.Enums;

namespace Lumora.Services.Rotation;

/// <summary>Ergebnis von Drehen bzw. dessen Rückgängig.</summary>
/// <param name="BatchId">Protokoll-Batch (0, wenn nichts gedreht wurde).</param>
/// <param name="Skipped">Kein JPEG, Video, Archiv, fehlt — nicht drehbar.</param>
/// <param name="Errors">„Name: Grund“ je gescheiterter Datei.</param>
public sealed record RotationResult(int BatchId, int Rotated, int Skipped, int Failed, IReadOnlyList<string> Errors)
{
    public static RotationResult Empty { get; } = new(0, 0, 0, 0, []);

    public bool CanUndo => BatchId > 0 && Rotated > 0;

    public string Summary
    {
        get
        {
            var text = Rotated == 1 ? "1 Foto gedreht" : $"{Rotated:N0} Fotos gedreht";
            if (Skipped > 0)
            {
                text += $" · {Skipped:N0} nicht drehbar (nur JPEG)";
            }

            return Failed > 0 ? text + $" · {Failed:N0} fehlgeschlagen" : text;
        }
    }
}

/// <summary>
/// „Nach links/rechts drehen“ (ADR-0068): setzt die EXIF-Ausrichtung im JPEG nach demselben Prinzip wie das GPS-Schreiben
/// (nur das APP1-Segment, Temp-Datei + Prüfung, alte Bytes im Rückgängig-Speicher), dreht Maße und Gesichtsrahmen im Katalog
/// mit und erneuert Vorschaubild und Gesichts-Ausschnitte. Nur JPEG; alles andere wird übersprungen. Rückgängig über
/// <see cref="UndoAsync"/> (auch aus der Aktivitäts-Anzeige).
/// </summary>
public interface IPhotoRotationService
{
    /// <summary>true, wenn die Datei gedreht werden kann (Bild mit JPEG-Endung; der Dateikopf wird beim Drehen geprüft).</summary>
    static bool CanRotate(string extension, MediaType mediaType)
        => mediaType == MediaType.Image && extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif";

    Task<RotationResult> RotateAsync(IReadOnlyList<int> fileIds, bool clockwise, CancellationToken ct = default);

    Task<RotationResult> UndoAsync(int batchId, CancellationToken ct = default);

    /// <summary>Jüngster noch rückgängig machbarer Dreh-Batch (für die Rückgängig-Taste der Galerie) oder null.</summary>
    Task<int?> GetUndoableBatchIdAsync(CancellationToken ct = default) => Task.FromResult<int?>(null);
}
