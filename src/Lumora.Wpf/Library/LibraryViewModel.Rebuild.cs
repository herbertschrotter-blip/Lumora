using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Removal;
using Lumora.Services.Scanning;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Shell;

namespace Lumora.Libraries;

/// <summary>
/// Rückfrage von „Neu aufbauen“ (#22, Nachtrag ADR-0066): Nach „Nur künftig ausschließen“ liegen Einträge in Ordnern, die der Scan
/// nicht mehr betritt — ein Neuaufbau nähme sie heraus. Die Rückfrage nennt dann ihre Zahl samt Benutzerdaten (wie „Fehlende
/// entfernen“) und bietet „Einträge behalten“ (Ausschluss für genau diese Einträge nicht anwenden,
/// <see cref="ScanOptions.KeepCatalogedInExcludedFolders"/>; Benutzerdaten über <c>UserDataCarryOver</c>) oder „Einträge
/// herausnehmen“ (bisheriges Verhalten). Ohne solche Einträge bleibt es bei der bekannten Bestätigung.
/// </summary>
public sealed partial class LibraryViewModel
{
    internal const string RebuildTitle = "Bibliothek neu aufbauen?";
    internal const string RebuildConfirmText = "Neu aufbauen";
    internal const string RebuildKeepText = "Neu aufbauen, Einträge behalten";
    internal const string RebuildDropText = "Neu aufbauen, Einträge herausnehmen";

    internal const string RebuildIntroText =
        "Der Katalog dieser Bibliothek wird neu aufgebaut; Vorschaubilder werden neu erzeugt. " +
        "Favoriten, Bewertungen, Tags und Kategorien bleiben für Dateien erhalten, die noch am selben Ort liegen. " +
        "Bis der neue Katalog fertig ist, bleibt der bisherige bestehen — ein Abbruch ändert nichts.";

    internal const string RebuildUncountedText =
        "Ob Einträge in ausgeschlossenen Ordnern in der Bibliothek liegen, ließ sich gerade nicht prüfen — solche Einträge werden behalten.";

    internal const string RebuildTipText = "Tipp: „Aktualisieren“ übernimmt nur Änderungen und ist deutlich schneller.";

    private readonly IScanExclusionService? _exclusions;

    /// <summary>Bestätigung für „Neu aufbauen“; null bei Abbruch, sonst die Scan-Optionen (behalten oder nicht).</summary>
    private async Task<ScanOptions?> ConfirmRebuildAsync(LibraryListItem item)
    {
        var summary = ExcludedFilesSummary.Empty;
        var summaryFailed = false;
        if (_exclusions is not null)
        {
            try
            {
                summary = await _exclusions.SummarizeAsync(new ExclusionScope.Current([item.Id])).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                summaryFailed = true;
                _logger.LogWarning(ex, "Neu aufbauen: Einträge in ausgeschlossenen Ordnern konnten nicht gezählt werden — sie werden behalten");
            }
        }

        if (summary.Count == 0)
        {
            var message = RebuildIntroText + "\n\n" + (summaryFailed ? RebuildUncountedText + "\n\n" : string.Empty) + RebuildTipText;
            var ok = await _dialogs.ConfirmAsync(RebuildTitle, message, RebuildConfirmText, isDestructive: true).ConfigureAwait(true);
            if (!ok)
            {
                return null;
            }

            // Zählen gescheitert: im Zweifel nichts herausnehmen, was der Benutzer nicht gesehen hat
            return summaryFailed ? new ScanOptions { KeepCatalogedInExcludedFolders = true } : ScanOptions.Default;
        }

        var choice = await _dialogs.ChooseAsync(RebuildTitle, RebuildWithExcludedText(summary), RebuildKeepText, RebuildDropText, secondaryIsDestructive: true).ConfigureAwait(true);
        _logger.LogInformation("Neu aufbauen: {Count} Einträge in ausgeschlossenen Ordnern, Wahl {Choice}", summary.Count, choice);
        return choice switch
        {
            DialogChoice.Primary => new ScanOptions { KeepCatalogedInExcludedFolders = true },
            DialogChoice.Secondary => ScanOptions.Default,
            _ => null,
        };
    }

    /// <summary>Text der Rückfrage mit Einträgen in ausgeschlossenen Ordnern (statisch, testbar).</summary>
    internal static string RebuildWithExcludedText(ExcludedFilesSummary s)
    {
        var text = RebuildIntroText + "\n\n"
            + $"{FilesText(s.Count)} in ausgeschlossenen Ordnern {(s.Count == 1 ? "ist" : "sind")} noch in der Bibliothek (nach „Nur künftig ausschließen“). "
            + "Beim Neuaufbau fielen sie heraus — es wird keine Datei gelöscht.";
        if (s.UserData.HasAny)
        {
            text += $"\n\nDavon {RemovalActions.UserDataText(s.UserData)} — diese Angaben gingen beim Herausnehmen verloren.";
        }

        return text + "\n\n"
            + $"„{RebuildKeepText}“ nimmt genau diese Einträge mit ihren Angaben wieder auf (was es auf der Platte nicht mehr gibt, fällt wie "
            + "sonst beim Neuaufbau weg); neue Dateien in diesen Ordnern bleiben ausgeschlossen. "
            + $"„{RebuildDropText}“ nimmt sie heraus.\n\n" + RebuildTipText;
    }

    /// <summary>Satz für die Zusammenfassung nach dem Neuaufbau, wenn Einträge behalten wurden.</summary>
    private static string KeptExcludedText(ScanResult result)
        => result.KeptExcluded > 0 ? $" Davon {FilesText(result.KeptExcluded)} in ausgeschlossenen Ordnern behalten." : string.Empty;

    private static string FilesText(int n) => n == 1 ? "1 Datei" : $"{n.ToString("N0", CultureInfo.CurrentCulture)} Dateien";
}
