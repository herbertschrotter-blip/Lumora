using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Services.Operations;
using Lumora.Services.Removal;
using Lumora.Shell;

namespace Lumora.Removal;

/// <summary>Bestätigungen der Lösch-/Entfernen-Abläufe (ADR-0065) — austauschbar für Tests und den Rauchtest.</summary>
public interface IRemovalDialogs
{
    /// <summary>Rückfrage mit zerstörerischem Hauptknopf; true = bestätigt.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

/// <summary>Produktiv: eigenes Dialogfenster über <see cref="IDialogService"/>.</summary>
internal sealed class RemovalDialogs(IDialogService dialogs) : IRemovalDialogs
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => dialogs.ConfirmAsync(title, message, confirmText, isDestructive: true);
}

/// <summary>Ergebnis einer Lösch-/Entfernen-Aktion für die Anzeige; <paramref name="UndoBatchId"/> = Batch für „Rückgängig“.</summary>
public sealed record RemovalActionResult(string Message, bool IsWarning, int? UndoBatchId = null);

/// <summary>
/// Abläufe mit Bestätigung für „Ordner in den Papierkorb…“ und „Fehlende aus der Bibliothek entfernen“ (ADR-0065), gemeinsam für
/// den Reiter „Ordner“, die Galerie und Einstellungen › Speicher &amp; Wartung: Vorschau holen, Sperrgrund oder Zahlen zeigen
/// („Diese Angaben gehen verloren“), bei Laufwerken ohne Papierkorb die eigene Rückfrage „endgültig löschen?“, ausführen,
/// Ergebnis in Worten. Die Sperre in „Alle verknüpften“ prüft die aufrufende Seite (sie kennt den Bereich).
/// </summary>
public sealed class RemovalActions
{
    /// <summary>Hinweis in „Alle verknüpften“ (wie die anderen Datei-Aktionen, ADR-0048).</summary>
    public const string AllLinkedHint = "In der Ansicht „Alle verknüpften“ nicht möglich — bitte eine einzelne Bibliothek wählen.";

    private readonly IRemovalService _removal;
    private readonly IRemovalDialogs _dialogs;
    private readonly ILogger<RemovalActions> _logger;

    public RemovalActions(IRemovalService removal, IRemovalDialogs dialogs, ILogger<RemovalActions> logger)
    {
        _removal = removal;
        _dialogs = dialogs;
        _logger = logger;
    }

    public IRemovalService Service => _removal;

    /// <summary>„In den Papierkorb…“ für einen Ordner; null bei Abbruch.</summary>
    public async Task<RemovalActionResult?> RecycleFolderAsync(int folderId)
    {
        var preview = await _removal.PreviewFolderRecycleAsync(folderId).ConfigureAwait(true);
        if (preview is null)
        {
            return new RemovalActionResult("Der Ordner ist nicht (mehr) im Katalog.", true);
        }

        if (preview.BlockedReason is { } blocked)
        {
            return new RemovalActionResult(blocked, true);
        }

        var permanent = preview.NoRecycleBinReason is not null;
        if (!await ConfirmFolderAsync(preview, permanent).ConfigureAwait(true))
        {
            return null;
        }

        var result = await _removal.RecycleFolderAsync(folderId, permanent).ConfigureAwait(true);
        if (result.NeedsPermanentConsent)
        {
            // Papierkorb erst bei der Ausführung weg (Laufwerk gewechselt): eigene Rückfrage, sonst Abbruch
            if (!await ConfirmFolderAsync(preview with { NoRecycleBinReason = result.NoRecycleBinReason }, permanent: true).ConfigureAwait(true))
            {
                return null;
            }

            result = await _removal.RecycleFolderAsync(folderId, allowPermanentDelete: true).ConfigureAwait(true);
        }

        return DescribeFolderResult(preview.Name, result);
    }

    /// <summary>„Fehlende entfernen“ für Ordner, Auswahl oder Bibliothek; null bei Abbruch.</summary>
    /// <param name="where">Ortsangabe im Satz („in „2020““, „in der Auswahl“, „in dieser Bibliothek“).</param>
    public async Task<RemovalActionResult?> RemoveMissingAsync(MissingTarget target, string where)
    {
        var summary = await _removal.SummarizeMissingAsync(target).ConfigureAwait(true);
        if (target is MissingTarget.Selection && summary.Present > 0)
        {
            return new RemovalActionResult(MixedSelectionText(summary.Present), true);
        }

        if (summary.OnlyOffline)
        {
            return new RemovalActionResult(OfflineText, true);
        }

        if (summary.Count == 0)
        {
            return new RemovalActionResult("Keine fehlenden Dateien — nichts zu entfernen.", false);
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "Fehlende aus der Bibliothek entfernen",
            MissingConfirmationText(summary, where),
            "Aus der Bibliothek entfernen").ConfigureAwait(true);
        if (!confirmed)
        {
            return null;
        }

        var result = await _removal.RemoveMissingAsync(target).ConfigureAwait(true);
        _logger.LogInformation("Fehlende entfernen: {Removed} entfernt, {Folders} Ordner (Batch {BatchId})", result.Removed, result.FoldersRemoved, result.BatchId);
        return DescribeMissingResult(result);
    }

    /// <summary>„Rückgängig“ eines Batches dieser Aktionen.</summary>
    public async Task<RemovalActionResult> UndoAsync(int batchId)
    {
        var result = await _removal.UndoAsync(batchId).ConfigureAwait(true);
        if (result is null)
        {
            return new RemovalActionResult("Nichts rückgängig zu machen.", true);
        }

        var files = result.Items.Count(i => i.Success && i.FileId != 0);
        return result.Failed == 0
            ? new RemovalActionResult($"Rückgängig: {Files(files)} wieder in der Bibliothek.", false)
            : new RemovalActionResult($"Rückgängig: {Files(files)} zurück, {result.Failed.ToString("N0", CultureInfo.CurrentCulture)} nicht ({FirstError(result)}).", true);
    }

    private Task<bool> ConfirmFolderAsync(FolderRecyclePreview preview, bool permanent)
        => permanent
            ? _dialogs.ConfirmAsync("Endgültig löschen?", FolderConfirmationText(preview, permanent: true), "Endgültig löschen")
            : _dialogs.ConfirmAsync("In den Papierkorb verschieben", FolderConfirmationText(preview, permanent: false), "In den Papierkorb");

    // ------------------------------------------------------------------ Texte (statisch, testbar)

    internal const string OfflineText = "Laufwerk nicht erreichbar — Dateien gelten als offline, nicht als fehlend. Es wurde nichts entfernt.";

    internal static string MixedSelectionText(int present)
        => $"Nur fehlende Dateien lassen sich aus der Bibliothek entfernen — {Files(present)} der Auswahl {(present == 1 ? "ist" : "sind")} vorhanden. "
           + "Tipp: Filter „Ablage: Nur fehlende“ zeigt nur die fehlenden.";

    internal static string FolderConfirmationText(FolderRecyclePreview p, bool permanent)
    {
        var lines = new List<string>
        {
            permanent
                ? $"Dieses Laufwerk hat keinen Papierkorb — „{p.Name}“ endgültig löschen?"
                : $"„{p.Name}“ samt Inhalt in den Windows-Papierkorb verschieben?",
        };
        if (permanent && p.NoRecycleBinReason is { } reason)
        {
            lines.Add(reason);
        }

        lines.Add(string.Empty);
        lines.Add($"• {Files(p.FilesOnDisk)} auf der Platte, davon {p.CatalogFiles.ToString("N0", CultureInfo.CurrentCulture)} in der Bibliothek; {(p.SubfoldersOnDisk == 1 ? "1 Unterordner" : N(p.SubfoldersOnDisk) + " Unterordner")}");
        if (p.UserData.HasAny)
        {
            lines.Add("• " + UserDataText(p.UserData));
        }

        lines.Add(string.Empty);
        lines.Add(permanent
            ? "Das lässt sich nicht rückgängig machen."
            : "„Rückgängig“ holt Ordner, Dateien und alle Angaben zurück.");
        return string.Join("\n", lines);
    }

    internal static string MissingConfirmationText(MissingSummary s, string where)
    {
        var lines = new List<string>
        {
            $"{Files(s.Count)} {where} gibt es auf der Platte nicht mehr. Aus der Bibliothek entfernen? Es wird keine Datei gelöscht — Lumora vergisst nur die Einträge.",
        };
        if (s.UserData.HasAny)
        {
            lines.Add(string.Empty);
            lines.Add($"Davon {UserDataText(s.UserData)} — diese Angaben gehen verloren.");
        }

        if (s.Offline > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{Files(s.Offline)} unter nicht erreichbaren Laufwerken {(s.Offline == 1 ? "bleibt" : "bleiben")} (gelten als offline, nicht als fehlend).");
        }

        lines.Add(string.Empty);
        lines.Add("„Rückgängig“ bringt die Einträge mit allen Angaben zurück.");
        return string.Join("\n", lines);
    }

    internal static string UserDataText(UserDataCounts u)
        => $"{N(u.RatedOrFavorite)} mit Sternen/Favorit, {N(u.WithPersons)} mit benannten Personen, {N(u.InAlbums)} in Alben, {N(u.WithTagsOrCategories)} mit Tags/Kategorien";

    internal static RemovalActionResult DescribeFolderResult(string name, FolderRecycleResult r)
    {
        if (r.BatchId == 0 && r.Error is { } rejected)
        {
            return new RemovalActionResult(rejected, true);
        }

        var verb = r.Permanent ? "endgültig gelöscht" : "in den Papierkorb verschoben";
        if (r.Success)
        {
            return new RemovalActionResult($"„{name}“ {verb} — {Files(r.FilesRemoved)} aus der Bibliothek, {Folders(r.FoldersRemoved)} aus dem Baum.", false, r.CanUndo ? r.BatchId : null);
        }

        var text = r.FilesRemoved > 0
            ? $"„{name}“ nur teilweise {verb}: {Files(r.FilesRemoved)} entfernt, {Files(r.FilesFailed)} geblieben ({r.Error})."
            : $"„{name}“ wurde nicht {verb}: {r.Error}";
        if (r.FilesRemoved == 0 && r.FolderGone)
        {
            text = $"„{name}“ {verb}, aber der Katalog wurde nicht nachgeführt ({r.Error}) — „Rückgängig“ holt den Ordner zurück.";
        }

        return new RemovalActionResult(text, true, r.CanUndoPartially ? r.BatchId : null);
    }

    internal static RemovalActionResult DescribeMissingResult(MissingRemovalBatchResult r)
    {
        if (r.Removed == 0)
        {
            return r.SkippedOffline > 0 && r.Reappeared == 0
                ? new RemovalActionResult(OfflineText, true)
                : new RemovalActionResult(r.Reappeared > 0 ? $"Nichts entfernt — {Files(r.Reappeared)} {(r.Reappeared == 1 ? "war" : "waren")} wieder da („fehlt“ zurückgesetzt)." : "Nichts entfernt.", r.Failed > 0);
        }

        var parts = new List<string> { $"{Files(r.Removed)} aus der Bibliothek entfernt" };
        if (r.FoldersRemoved > 0)
        {
            parts.Add($"{Folders(r.FoldersRemoved)} aus dem Baum");
        }

        if (r.Reappeared > 0)
        {
            parts.Add($"{N(r.Reappeared)} wieder da („fehlt“ zurückgesetzt)");
        }

        if (r.SkippedOffline > 0)
        {
            parts.Add($"{N(r.SkippedOffline)} offline übersprungen");
        }

        if (r.Failed > 0)
        {
            parts.Add($"{N(r.Failed)} fehlgeschlagen");
        }

        return new RemovalActionResult(string.Join(", ", parts) + ".", r.Failed > 0 || r.SkippedOffline > 0, r.CanUndo ? r.BatchId : null);
    }

    private static string FirstError(FileOperationBatchResult r)
    {
        foreach (var item in r.Items)
        {
            if (!item.Success && item.Error is { } e)
            {
                return e;
            }
        }

        return "unbekannter Fehler";
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    private static string Files(int n) => n == 1 ? "1 Datei" : $"{N(n)} Dateien";

    private static string Folders(int n) => n == 1 ? "1 Ordner" : $"{N(n)} Ordner";
}
