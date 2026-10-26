using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Lumora.AppConnect;

/// <summary>
/// <see cref="IPairingPrompt"/> mit dem Kopplungsdialog (Mockup 11): öffnet ihn auf dem UI-Thread, holt ihn nach vorn (die
/// Anfrage kommt von einer anderen App) und schließt ihn, wenn die Anfrage abläuft. Nicht modal — Lumora bleibt bedienbar.
/// </summary>
public sealed class WindowPairingPrompt(Dispatcher dispatcher, Func<Window?> owner) : IPairingPrompt
{
    /// <summary>Für den Rauchtest: das gerade offene Fenster.</summary>
    internal PairingDialogWindow? Current { get; private set; }

    public Task<PairingDecision?> AskAsync(PairingPromptInfo info, CancellationToken ct)
    {
        var done = new TaskCompletionSource<PairingDecision?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Abgelaufen beendet die Frage immer — auch wenn das Fenster nie erscheint (Lumora wird gerade beendet)
        var expired = ct.Register(() => done.TrySetResult(null));
        _ = done.Task.ContinueWith(_ => expired.Dispose(), TaskScheduler.Default);
        dispatcher.BeginInvoke(() =>
        {
            if (ct.IsCancellationRequested)
            {
                done.TrySetResult(null);
                return;
            }

            var window = new PairingDialogWindow(info);
            if (owner() is { IsVisible: true } main)
            {
                window.Owner = main;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            var registration = ct.Register(() => dispatcher.BeginInvoke(() =>
            {
                if (window.IsVisible)
                {
                    window.Close(); // abgelaufen
                }
            }));
            window.Closed += (_, _) =>
            {
                registration.Dispose();
                Current = null;
                done.TrySetResult(ct.IsCancellationRequested ? null : window.Decision);
            };
            Current = window;
            window.Show();
            window.Topmost = true; // die Anfrage kommt von außen — einmal nach vorn
            window.Activate();
            window.Topmost = false;
        });
        return done.Task;
    }
}
