using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lumora.Imaging.Merge;
using Lumora.Shell;

namespace Lumora.Merge;

/// <summary>Öffnet das Fenster „Zusammenfügen“ (nicht modal, eines je Sitzung).</summary>
public interface IMergeWindowService
{
    /// <summary>Fenster mit dieser Auswahl öffnen; ein offenes ohne laufendes Speichern wird ersetzt.</summary>
    void Show(MergeStart start, Window? owner = null);

    /// <summary>
    /// Ein Speichern läuft im offenen Fenster (dann aktiviert <see cref="Show"/> es statt es zu ersetzen). Nach „Im Hintergrund
    /// weiterrechnen“ ist das Fenster zu: ein neues darf öffnen, seine Vorschau wartet auf den einen schweren Lauf zur Zeit
    /// (Aktivität „wartet — eine andere Berechnung läuft“).
    /// </summary>
    bool IsSaving { get; }

    /// <summary>Offenes Fenster (Tests, Rauchtest).</summary>
    MergeWindow? Current { get; }
}

/// <summary>Produktiv: ein Fenster je Sitzung, Owner = Hauptfenster (bleibt offen, wenn die Lightbox schließt).</summary>
public sealed class MergeWindowService(IServiceProvider services) : IMergeWindowService
{
    private MergeWindow? _window;

    public bool IsSaving => _window?.ViewModel.IsSaving == true;

    public MergeWindow? Current => _window;

    public void Show(MergeStart start, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (_window is { } open)
        {
            if (open.ViewModel.IsSaving)
            {
                open.Activate(); // ein Speichern läuft: nicht ersetzen
                return;
            }

            open.Close();
        }

        var viewModel = new MergeViewModel(
            services.GetRequiredService<IMergeService>(),
            start,
            services.GetService<IDialogService>(),
            services.GetService<IMergeThumbnailLoader>(),
            services.GetService<MergeGalleryActions>(),
            services.GetService<ILogger<MergeViewModel>>());
        var window = new MergeWindow(viewModel);
        var main = Application.Current?.MainWindow;
        if (main is not null && !ReferenceEquals(main, window) && main.IsVisible && new WindowInteropHelper(main).Handle != IntPtr.Zero)
        {
            window.Owner = main;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _window = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        };
        window.Show();
        window.Activate();
        _ = owner; // Lightbox oder Galerie: das Fenster gehört dem Hauptfenster, damit es die Lightbox überlebt
    }
}
