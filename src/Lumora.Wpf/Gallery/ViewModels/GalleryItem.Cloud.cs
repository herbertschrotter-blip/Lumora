using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Services.Cloud;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// OneDrive-Zustand der Kachel (ADR-0077, Mockup 27): <see cref="IsCloudOnly"/> zeigt die Wolken-Marke („nur online“),
/// <see cref="IsCloudFile"/> schaltet im Rechtsklick-Menü „Immer auf diesem Gerät behalten“ und „Speicherplatz freigeben“
/// frei. Gesetzt von der Galerie nach den Datei-Attributen (kein Download).
/// </summary>
public sealed partial class GalleryItem
{
    public const string CloudOnlyToolTip = "Nur online (OneDrive) — Vorschaubild von OneDrive, das Original wird beim Öffnen geladen";

    /// <summary>Nur online: Wolken-Marke auf der Kachel; ein „fehlt“ gibt es dafür nie.</summary>
    [ObservableProperty] private bool _isCloudOnly;

    /// <summary>Liegt in einem OneDrive-Ordner (lokal oder nur online).</summary>
    [ObservableProperty] private bool _isCloudFile;

    internal void ApplyCloudState(CloudFileState state)
    {
        IsCloudFile = state.IsCloud;
        IsCloudOnly = state.IsOnlineOnly;
    }
}
