using System;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Imaging.Merge;
using Lumora.Shell;

namespace Lumora.Merge;

/// <summary>
/// Ende einer Katalog-Sitzung, während „Zusammenfügen“ speichert (ADR-0078, 0.5.48): Zusperren, Wechsel normal ↔ versteckt und
/// Beenden schließen die Dienste der Sitzung bzw. hängen den Container aus — ein Rechen-Prozess mit offenen Quellen darf dabei
/// nicht weiterlaufen und das fertige Ergebnis nicht still verloren gehen. Automatisch (Untätigkeit) wird abgelehnt — das
/// Zusperren verschiebt sich auf die nächste Prüfung; von Hand fragt Lumora „Warten / Abbrechen und …“. Danach räumt
/// <see cref="MergeService.DisposeAsync"/> die Sitzungen auf, bevor die Dienste entsorgt werden.
/// </summary>
public sealed class MergeSessionGuard(IMergeService merge, IDialogService? dialogs)
{
    /// <summary>Abstand beim Warten auf das Ende des Speicherns.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>true, solange gespeichert wird (auch im Hintergrund ohne Fenster).</summary>
    public bool IsSaving => merge.IsSaving;

    /// <summary>
    /// Darf die Sitzung jetzt enden? true = es läuft nichts (mehr) — nach „Warten“ erst, wenn das Ergebnis gespeichert ist, nach
    /// „Abbrechen und …“, wenn das Speichern abgebrochen ist; false = nicht jetzt (automatisch, Rückfrage geschlossen).
    /// </summary>
    /// <param name="action">Was danach geschieht, klein und als Verb: „zusperren“, „wechseln“, „beenden“.</param>
    public async Task<bool> ConfirmEndAsync(string action, bool automatic, CancellationToken ct = default)
    {
        if (!merge.IsSaving)
        {
            return true;
        }

        if (automatic || dialogs is null)
        {
            return false;
        }

        var choice = await dialogs.ChooseAsync(
            "Panorama wird noch gespeichert",
            "„Zusammenfügen“ speichert gerade ein Panorama (Fortschritt in der Aktivitäts-Anzeige).\n\n"
            + $"„Warten“ speichert es fertig und macht danach weiter. „Abbrechen und {action}“ verwirft es — die Einzelbilder bleiben unverändert.",
            "Warten",
            $"Abbrechen und {action}",
            closeText: "Zurück",
            secondaryIsDestructive: true).ConfigureAwait(true);
        switch (choice)
        {
            case DialogChoice.Primary:
                await WaitAsync(ct).ConfigureAwait(true);
                return true;
            case DialogChoice.Secondary:
                merge.CancelSaves();
                await WaitAsync(ct).ConfigureAwait(true);
                return true;
            default:
                return false;
        }
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        while (merge.IsSaving)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(true);
        }
    }
}
