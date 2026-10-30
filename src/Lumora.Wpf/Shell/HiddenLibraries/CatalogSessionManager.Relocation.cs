using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.Relocation;
using Lumora.Services.Operations;

namespace Lumora.Shell.HiddenLibraries;

/// <summary>Schritte beim Verschieben des Containers (Mockup 26), in dieser Reihenfolge.</summary>
public enum ContainerRelocationStep
{
    CloseLibrary,
    Dismount,
    Copy,
    Verify,
    Mount,
    Reopen,
}

/// <summary>Fortschrittsfenster beim Verschieben (Mockup 26, „Läuft“/„Fertig“/„Abgebrochen“); im Test eine Attrappe.</summary>
public interface IContainerRelocationProgress
{
    /// <summary>„Abbrechen“ im Fenster — wirkt nur beim Kopieren und Prüfen.</summary>
    CancellationToken Cancellation { get; }

    void Step(ContainerRelocationStep step);

    void Copy(ContainerCopyProgress progress);

    /// <summary>Ende: Erfolg oder Abbruch mit Erklärung; das Fenster bleibt offen, bis der Benutzer es schließt.</summary>
    void Finish(bool success, string message);
}

/// <summary>Container der versteckten Bibliothek auf ein anderes Laufwerk verschieben (Mockup 26).</summary>
public sealed partial class CatalogSessionManager
{
    /// <summary>So lange wartet Lumora nach dem Aushängen, bis Windows die Container-Datei freigibt.</summary>
    internal static TimeSpan FileReleaseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ablauf nach Mockup 26: versteckte Sitzung schließen (der versteckte Modus bleibt für das Wiederöffnen an), Container
    /// aushängen, kopieren und prüfen bzw. auf demselben Laufwerk umbenennen, am neuen Ort unter demselben Buchstaben
    /// einhängen, alte Datei behalten oder in den Papierkorb, dann nach dem Kennwort fragen und wieder öffnen. Bei jedem
    /// Fehler vor dem Einhängen am neuen Ort bleibt die alte Datei unberührt und wird wieder eingehängt. Pfade erscheinen
    /// nie im Protokoll (ADR-0049).
    /// </summary>
    /// <param name="progressFactory">Erst nach dem Schließen der Sitzung aufgerufen — der Abbau schließt alle Fenster.</param>
    /// <param name="recycleBin">Für „alte Datei in den Papierkorb“; null = Windows-Papierkorb.</param>
    public async Task<HiddenSessionResult> RelocateContainerAsync(ContainerRelocationPlan plan, Func<IContainerRelocationProgress> progressFactory, IRecycleBin? recycleBin = null)
    {
        if (ActiveLibrary is not { } library)
        {
            return HiddenSessionResult.Fail("Keine versteckte Bibliothek geöffnet.");
        }

        var closed = await CloseForRelocationAsync().ConfigureAwait(true);
        var ui = progressFactory();
        if (!closed)
        {
            return Fail(ui, "Die versteckte Bibliothek ließ sich gerade nicht schließen. Nichts wurde verändert.");
        }

        ui.Step(ContainerRelocationStep.Dismount);
        var dismount = await DismountAsync(library).ConfigureAwait(true);
        if (!dismount.Success)
        {
            return Fail(ui, $"Der Container ließ sich nicht aushängen: {dismount.Message}\nEr ist weiter als {plan.DriveLetter}: geöffnet — die versteckte Bibliothek lässt sich wie gewohnt wieder öffnen.");
        }

        if (!await WaitUntilReleasedAsync(plan.SourceFile).ConfigureAwait(true))
        {
            await MountAsync(plan.SourceFile, plan).ConfigureAwait(true);
            return Fail(ui, "Windows gab die Container-Datei nach dem Aushängen nicht frei. Nichts wurde verändert; der Container ist wieder geöffnet.");
        }

        // Kopieren und prüfen bzw. umbenennen — die alte Datei bleibt dabei unberührt
        try
        {
            if (plan.IsRename)
            {
                ui.Step(ContainerRelocationStep.Copy);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(plan.TargetFile))!);
                File.Move(plan.SourceFile, plan.TargetFile);
            }
            else
            {
                ui.Step(ContainerRelocationStep.Copy);
                var verifying = false;
                var progress = new Progress<ContainerCopyProgress>(p =>
                {
                    if (p.Phase == ContainerCopyPhase.Verify && !verifying)
                    {
                        verifying = true;
                        ui.Step(ContainerRelocationStep.Verify);
                    }

                    ui.Copy(p);
                });
                await ContainerFileCopier.CopyAndVerifyAsync(plan.SourceFile, plan.TargetFile, progress, ui.Cancellation).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogWarning("Container verschieben: Kopieren abgebrochen ({Type})", ex.GetType().Name);
            var back = await MountAsync(plan.SourceFile, plan).ConfigureAwait(true);
            var reason = ex is OperationCanceledException ? "Abgebrochen." : ex.Message;
            return Fail(ui, $"{reason}\nDie halbe Kopie wurde gelöscht, die alte Datei ist unverändert" +
                            (back.Success ? $" und wieder als {plan.DriveLetter}: geöffnet." : $". Wieder öffnen ging nicht: {back.Message}"));
        }

        // Am neuen Ort unter demselben Buchstaben öffnen
        ui.Step(ContainerRelocationStep.Mount);
        var mounted = await MountAsync(plan.TargetFile, plan).ConfigureAwait(true);
        if (!mounted.Success || HiddenLibraryStore.TryLoad(library.FolderPath) is null)
        {
            _logger.LogWarning("Container verschieben: am neuen Ort nicht als erwartetes Laufwerk geöffnet");
            if (plan.IsRename)
            {
                return Fail(ui, $"Die Datei liegt jetzt am neuen Ort, ließ sich dort aber nicht als {plan.DriveLetter}: öffnen ({mounted.Message}). " +
                                $"Bitte über „Container öffnen…“ öffnen; erscheint sie unter einem anderen Buchstaben, ihn in der Datenträgerverwaltung auf {plan.DriveLetter}: ändern.");
            }

            if (mounted.Success)
            {
                await Containers.DismountAsync($"{plan.DriveLetter}:\\").ConfigureAwait(true);
            }

            var back = await MountAsync(plan.SourceFile, plan).ConfigureAwait(true);
            return Fail(ui, $"Die Kopie ist geprüft, ließ sich aber nicht als {plan.DriveLetter}: öffnen ({mounted.Message}). " +
                            "Die alte Datei ist unverändert" + (back.Success ? $" und wieder als {plan.DriveLetter}: geöffnet." : ".") +
                            " Die Kopie bleibt liegen; Sie können sie löschen.");
        }

        var oldNote = string.Empty;
        if (!plan.IsRename && plan.OldFile == OldContainerFile.RecycleBin)
        {
            try
            {
                (recycleBin ?? new WindowsRecycleBin()).MoveToRecycleBin(plan.SourceFile);
                oldNote = " Die alte Datei liegt im Papierkorb.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                oldNote = $" Die alte Datei ließ sich nicht in den Papierkorb legen ({ex.Message}) — sie liegt weiter am alten Ort.";
            }
        }
        else if (!plan.IsRename)
        {
            oldNote = " Die alte Datei liegt weiter am alten Ort; löschen Sie sie, wenn alles passt.";
        }

        _logger.LogInformation("Container verschoben ({Mode})", plan.IsRename ? "umbenannt" : "kopiert und geprüft");
        ui.Step(ContainerRelocationStep.Reopen);
        ui.Finish(true, $"Der Container liegt am neuen Ort und ist wieder als {plan.DriveLetter}: geöffnet.{oldNote} Lumora merkt sich keinen der beiden Pfade.");
        await ReopenAfterRelocationAsync(library).ConfigureAwait(true);
        return HiddenSessionResult.Ok();
    }

    /// <summary>Wie Zusperren, aber ohne Aushängen und ohne den versteckten Modus zu beenden (wieder öffnen danach).</summary>
    private async Task<bool> CloseForRelocationAsync()
    {
        if (Interlocked.Exchange(ref _switching, 1) != 0)
        {
            return false;
        }

        try
        {
            try
            {
                await Switcher.CloseSecondaryWindowsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Fenster vor dem Verschieben nicht geschlossen ({Type})", ex.GetType().Name);
            }

            StopIdleWatch();
            await Switcher.SwitchToNormalAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("Versteckte Sitzung vor dem Verschieben nicht geschlossen ({Type})", ex.GetType().Name);
            return false;
        }
        finally
        {
            ActiveLibrary = null;
            ActiveManifest = null;
            InitialScan = null;
            HiddenSessionPrivacy.IsHiddenSessionActive = false;
            Volatile.Write(ref _switching, 0);
        }
    }

    private Task<ContainerCommandResult> MountAsync(string file, ContainerRelocationPlan plan)
        => plan.IsVirtualDisk ? Containers.MountVirtualDiskAsync(file) : Containers.MountVeraCryptAsync(file, plan.DriveLetter);

    /// <summary>Nach dem Aushängen: warten, bis niemand die Datei mehr offen hat (Windows gibt VHDX kurz verzögert frei).</summary>
    private static async Task<bool> WaitUntilReleasedAsync(string file)
    {
        var until = DateTime.UtcNow + FileReleaseTimeout;
        while (true)
        {
            try
            {
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    return true;
                }
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                await Task.Delay(500).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private HiddenSessionResult Fail(IContainerRelocationProgress ui, string message)
    {
        ui.Finish(false, message);
        return HiddenSessionResult.Fail(message);
    }

    /// <summary>Kennwort der versteckten Bibliothek fragen und öffnen; abgelehnt = in der normalen Bibliothek bleiben.</summary>
    private async Task ReopenAfterRelocationAsync(HiddenLibraryInfo library)
    {
        if (_switcher?.CurrentServices.GetService<IHiddenLibraryDialogs>() is not { } dialogs)
        {
            return;
        }

        var answer = dialogs.AskUnlock(library, canReset: false, password => VerifyPasswordAsync(library, password));
        if (answer is null)
        {
            return;
        }

        var result = await OpenAsync(library, answer.Password).ConfigureAwait(true);
        if (!result.Success && result.Message is { } message)
        {
            await ShowHintAsync("Versteckte Bibliothek öffnen", message).ConfigureAwait(true);
        }
    }
}
