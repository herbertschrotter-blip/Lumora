using System.Windows;

namespace Lumora.Albums;

/// <summary>
/// Ziehen von Galerie-Dateien auf Ordner (Ordnerbaum) und Alben (ADR-0054). Inhalt: <c>int[]</c> der FileIds.
/// Tasten-Konvention beim Ablegen auf einen Ordner (wie im Explorer): ohne Taste oder mit Alt = <b>Verknüpfen</b>
/// (Standard, nichts wird bewegt), mit Umschalt = <b>Verschieben</b> (mit Rückfrage, protokolliert, rückgängig machbar).
/// Strg (im Explorer „Kopieren“) verknüpft ebenfalls — Lumora kopiert nie in die Bibliothek. Seit 0.4.44 (ADR-0068) trägt der
/// Ziehvorgang zusätzlich einen Datei-Drop für andere Programme und erlaubt dann nur „Kopieren“ (<c>ExternalFileTransfer</c>);
/// interne Ziele entscheiden deshalb über die Tasten, nicht über den Effekt.
/// </summary>
public static class GalleryDragData
{
    public const string Format = "Lumora.GalleryFileIds";

    public static DataObject Create(int[] fileIds) => new(Format, fileIds);

    public static int[]? Read(IDataObject data)
        => data.GetDataPresent(Format) ? data.GetData(Format) as int[] : null;

    /// <summary>Mauszeiger für Ziele, die immer verknüpfen (Alben): „Verknüpfen“, mit Datei-Drop nach außen „Kopieren“ (ADR-0068).</summary>
    public static DragDropEffects LinkEffect(DragDropEffects allowed)
        => (allowed & DragDropEffects.Link) != 0 ? DragDropEffects.Link
            : (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
            : DragDropEffects.None;

    /// <summary>Umschalt gedrückt → Verschieben, sonst Verknüpfen.</summary>
    public static bool IsMove(DragDropKeyStates keys) => (keys & DragDropKeyStates.ShiftKey) != 0;

    /// <summary>
    /// Wirkung für den Mauszeiger: Verschieben bzw. Verknüpfen. Enthält der Ziehvorgang auch einen Datei-Drop nach außen (ADR-0068),
    /// erlaubt er nur „Kopieren“ — dann steht „Kopieren“ als Platzhalter im Mauszeiger; was passiert, entscheiden die Tasten.
    /// </summary>
    public static DragDropEffects EffectFor(DragDropKeyStates keys, DragDropEffects allowed)
    {
        var wanted = IsMove(keys) ? DragDropEffects.Move : DragDropEffects.Link;
        if ((allowed & wanted) != 0)
        {
            return wanted;
        }

        return (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }
}
