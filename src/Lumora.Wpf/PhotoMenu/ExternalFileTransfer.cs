using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using Lumora.Albums;
using Lumora.Domain.Enums;
using Lumora.Services.Export;

namespace Lumora.PhotoMenu;

/// <summary>Eine Datei, die nach außen gehen könnte (aus den Kacheln bzw. der Lightbox, ohne Katalog-Abfrage).</summary>
public sealed record ExternalCandidate(int FileId, string Path, bool Missing, MediaType MediaType);

/// <summary>
/// Fotos in andere Programme kopieren (ADR-0068, Issue #25): Ziehen aus Galerie/Lightbox liefert zusätzlich zum internen Format
/// (<see cref="GalleryDragData"/>, FileIds) einen echten Datei-Drop (<see cref="DataFormats.FileDrop"/>), Strg+C bzw. Datei ›
/// „Kopieren“ legt dieselben Dateien in die Zwischenablage. <b>Nach außen nur Kopieren:</b> sobald Pfade im Datenobjekt stehen,
/// erlaubt der Ziehvorgang ausschließlich <see cref="DragDropEffects.Copy"/> (dazu „Preferred DropEffect“ = Kopieren) — OLE gibt
/// einem fremden Ziel nie mehr als die erlaubten Effekte, ein Explorer-Verschieben (Umschalt) kann die Bibliotheksdatei also
/// nicht entfernen, auch keine Verknüpfung anlegen. Interne Ziele (Ordnerleiste, Alben) entscheiden über die Tasten
/// (<see cref="GalleryDragData.IsMove"/>), nicht über den Effekt; ihr Mauszeiger zeigt dann „Kopieren“ als Platzhalter. Fehlende
/// Dateien und Archive (Fotos nur im Archiv) werden ausgelassen; in der versteckten Bibliothek gibt es keinen Weg nach außen
/// (nur das interne Format bleibt, mit den bisherigen Effekten).
/// </summary>
public static class ExternalFileTransfer
{
    /// <summary>Shell-Format, mit dem Explorer & Co. den bevorzugten Effekt lesen (4 Byte, DROPEFFECT).</summary>
    public const string PreferredDropEffectFormat = "Preferred DropEffect";

    /// <summary>Erlaubte Effekte ohne Weg nach außen (nur interne Ziele): verknüpfen bzw. verschieben (ADR-0054).</summary>
    public const DragDropEffects InternalEffects = DragDropEffects.Link | DragDropEffects.Move;

    /// <summary>Erlaubte Effekte mit Datei-Drop: ausschließlich Kopieren.</summary>
    public const DragDropEffects ExternalEffects = DragDropEffects.Copy;

    /// <summary>Dateien für den Weg nach außen aus bekannten Kachel-/Lightbox-Daten (prüft die Platte).</summary>
    public static ExternalFileSet Plan(IEnumerable<ExternalCandidate> candidates, bool blocked)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (blocked)
        {
            return ExternalFileSet.BlockedSet;
        }

        var paths = new List<string>();
        var ids = new List<int>();
        int missing = 0, archives = 0;
        foreach (var candidate in candidates.DistinctBy(c => c.FileId))
        {
            if (candidate.Missing || !File.Exists(candidate.Path))
            {
                missing++;
            }
            else if (candidate.MediaType == MediaType.Archive)
            {
                archives++;
            }
            else
            {
                paths.Add(candidate.Path);
                ids.Add(candidate.FileId);
            }
        }

        return new ExternalFileSet(paths, ids, missing, archives, false);
    }

    /// <summary>
    /// Datenobjekt zum Ziehen: immer das interne Format (FileIds), dazu — wenn Dateien nach außen dürfen — Datei-Drop und
    /// „Preferred DropEffect“ = Kopieren. Liefert die erlaubten Effekte für <c>DoDragDrop</c> mit.
    /// </summary>
    public static (DataObject Data, DragDropEffects AllowedEffects) CreateDragData(int[] fileIds, ExternalFileSet? external)
    {
        var data = GalleryDragData.Create(fileIds);
        if (external is not { Blocked: false, Paths.Count: > 0 } files)
        {
            return (data, InternalEffects);
        }

        var list = new StringCollection();
        list.AddRange(files.Paths.ToArray());
        data.SetFileDropList(list);
        data.SetData(PreferredDropEffectFormat, new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        return (data, ExternalEffects);
    }

    /// <summary>
    /// Strg+C: Dateien in die Zwischenablage (Datei-Drop + „Preferred DropEffect“ = Kopieren, damit Einfügen im Explorer kopiert,
    /// nie verschiebt). Liefert die Ergebnis-Zeile für die Statusanzeige.
    /// </summary>
    public static string CopyToClipboard(ExternalFileSet files, Action<DataObject>? setClipboard = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Blocked)
        {
            return ExternalFileSet.BlockedText;
        }

        if (files.Paths.Count == 0)
        {
            return files.SkippedText is { } skipped ? $"Nichts kopiert — {skipped}." : "Nichts zu kopieren.";
        }

        var data = new DataObject();
        var list = new StringCollection();
        list.AddRange(files.Paths.ToArray());
        data.SetFileDropList(list);
        data.SetData(PreferredDropEffectFormat, new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        (setClipboard ?? (d => Clipboard.SetDataObject(d, copy: true)))(data);
        var text = files.Paths.Count == 1 ? "1 Datei in die Zwischenablage kopiert" : $"{files.Paths.Count:N0} Dateien in die Zwischenablage kopiert";
        return files.SkippedText is { } note ? $"{text} — {note}." : text + ".";
    }
}
