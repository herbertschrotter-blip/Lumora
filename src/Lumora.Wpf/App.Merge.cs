using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Lumora.Merge;
using Serilog;

namespace Lumora;

/// <summary>
/// „Zusammenfügen“ und das Ende einer Katalog-Sitzung (ADR-0078, 0.5.48): Zusperren, Wechsel und Beenden warten auf ein
/// laufendes Speichern oder brechen es nach Rückfrage ab (<see cref="MergeSessionGuard"/>); danach beendet der Merge-Dienst seine
/// Sitzungen und Hilfsprozesse, bevor die Dienste entsorgt bzw. der Container ausgehängt wird.
/// </summary>
public partial class App
{
    private bool _mergeExitConfirmed;
    private bool _sessionClosing;
    private int _backgroundExitPending;

    /// <inheritdoc cref="ICatalogHostSwitcher.ConfirmSessionEndAsync"/>
    public async Task<bool> ConfirmSessionEndAsync(string action, bool automatic)
    {
        var guard = _host.Services.GetService<MergeSessionGuard>();
        return guard is null || await guard.ConfirmEndAsync(action, automatic).ConfigureAwait(true);
    }

    /// <summary>true, solange „Zusammenfügen“ speichert (auch ohne Fenster) — dann wartet das automatische Zusperren.</summary>
    private bool IsMergeSaving() => _host.Services.GetService<MergeSessionGuard>()?.IsSaving == true;

    /// <summary>Offene Sitzungen von „Zusammenfügen“ beenden und ihre Hilfsprozesse abwarten (vor dem Entsorgen der Dienste).</summary>
    private static async Task StopMergeAsync(IHost host)
    {
        try
        {
            if (host.Services.GetService<Lumora.Imaging.Merge.IMergeService>() is IAsyncDisposable merge)
            {
                await merge.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Zusammenfügen ließ sich beim Schließen der Sitzung nicht sauber beenden");
        }
    }

    /// <summary>
    /// Beenden (OnExit, synchron): Sitzungen von „Zusammenfügen“ abbrechen und ihre Hilfsprozesse abwarten, bevor die Dienste
    /// entsorgt bzw. der Container ausgehängt wird — auf einem Pool-Thread, damit nichts auf den gerade blockierten UI-Thread
    /// wartet; höchstens 20 s. Normalerweise läuft nichts mehr (Rückfrage vorher).
    /// </summary>
    private void StopMergeOnExit()
    {
        try
        {
            if (_host.Services.GetService<Lumora.Imaging.Merge.IMergeService>() is IAsyncDisposable merge
                && !Task.Run(async () => await merge.DisposeAsync().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(20)))
            {
                Log.Warning("Zusammenfügen ließ sich beim Beenden nicht rechtzeitig beenden");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Zusammenfügen ließ sich beim Beenden nicht sauber beenden");
        }
    }

    /// <summary>
    /// Hauptfenster schließt (Beenden), während gespeichert wird: Schließen anhalten und „Warten / Abbrechen und beenden“ fragen.
    /// true = abgefangen.
    /// </summary>
    private bool InterceptExitWhileMerging(Window window, CancelEventArgs e)
    {
        if (_mergeExitConfirmed || _sessionClosing || !IsMergeSaving())
        {
            return false;
        }

        e.Cancel = true;
        _ = ConfirmExitWhileMergingAsync(window);
        return true;
    }

    private async Task ConfirmExitWhileMergingAsync(Window window)
    {
        try
        {
            if (!await ConfirmSessionEndAsync("beenden", automatic: false).ConfigureAwait(true))
            {
                return;
            }

            _mergeExitConfirmed = true;
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Beenden während „Zusammenfügen“ speichert fehlgeschlagen");
        }
    }

    /// <summary>
    /// Beenden aus dem Infobereich bzw. weil im Hintergrund niemand Lumora mehr braucht: von Hand nachfragen, automatisch erst
    /// beenden, wenn das Speichern fertig ist (ohne Fenster gibt es niemanden zu fragen). false = nicht beenden.
    /// </summary>
    private async Task<bool> MayExitAsync(bool automatic)
    {
        if (!IsMergeSaving())
        {
            return true;
        }

        if (!automatic)
        {
            return await ConfirmSessionEndAsync("beenden", automatic: false).ConfigureAwait(true);
        }

        // nur eine wartende Prüfung zur Zeit — weitere Anstöße der Hintergrund-Regeln währenddessen entfallen
        if (Interlocked.Exchange(ref _backgroundExitPending, 1) != 0)
        {
            return false;
        }

        try
        {
            Log.Information("Hintergrund-Ende wartet auf das Speichern von „Zusammenfügen“");
            while (IsMergeSaving())
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            }

            return true;
        }
        finally
        {
            Volatile.Write(ref _backgroundExitPending, 0);
        }
    }
}
