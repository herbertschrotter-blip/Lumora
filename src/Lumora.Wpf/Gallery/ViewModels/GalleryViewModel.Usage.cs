using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Messaging;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Galerie-Filter „Lieblinge“ (ADR-0072, Mockup 20, Chip in der Zeile „Datei“ neben „Nur Favoriten“): nur Fotos, bei denen
/// Lumora aus der Nutzung eine Vorliebe gelernt hat (oft angesehen, an mehreren Tagen, verwendet, weitergegeben). UND mit
/// allen anderen Filtern, in „Es gilt“ mit eigenem ✕, „Filter zurücksetzen“ hebt ihn auf. Bei „Nutzung lernen“ aus ist die
/// Menge leer. Nach „Gelernte Nutzung löschen“ bzw. Umschalten lädt die Galerie neu, solange der Filter wirkt.
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>Umschalter „Lieblinge“.</summary>
    [ObservableProperty] private bool _lovedOnly;

    private bool _usageRegistered;

    /// <summary>Text in der Zeile „Es gilt“.</summary>
    internal const string LovedConditionLabel = "Lieblinge (oft angesehen oder verwendet)";

    /// <summary>Leertext, wenn der Filter nichts findet.</summary>
    internal const string LovedEmptyMessage = "Noch keine Lieblinge — Lumora lernt sie, wenn Sie Fotos öfter und an mehreren Tagen ansehen, verwenden oder weitergeben (Einstellungen › Storys › „Nutzung lernen“).";

    partial void OnLovedOnlyChanged(bool value)
    {
        if (value && !_usageRegistered)
        {
            _usageRegistered = true;
            WeakReferenceMessenger.Default.Register<GalleryViewModel, UsageChangedMessage>(this, static (r, _) => r.OnUsageChanged());
        }

        OnFileFilterPartChanged();
    }

    private void OnUsageChanged()
    {
        if (LovedOnly && !_suppressReload && _isInitialized)
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => FireAndForget(ChangeQueryAsync));
        }
    }
}
