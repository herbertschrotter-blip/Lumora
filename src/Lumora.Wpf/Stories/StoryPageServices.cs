using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows.Media;
using Lumora.Lightbox;
using Lumora.Persons;
using Lumora.Services.Places;
using Lumora.Services.Stories;
using Lumora.Shell;
using Lumora.Slideshow;

namespace Lumora.Stories;

/// <summary>Gesichts-Ausschnitte für die Personen-Bilder der Storys (Fakes in Tests).</summary>
public interface IStoryFaceSource
{
    /// <summary>Fertige Ausschnitte (UI-Thread, gebündelt).</summary>
    event Action<IReadOnlyList<(long FaceId, ImageSource Image)>>? ImagesReady;

    ImageSource? TryGet(long faceId);

    void Request(int libraryId, IReadOnlyCollection<long> faceIds);
}

/// <summary>Über den <see cref="FaceImageProvider"/> der Personen-Seite (gleicher Ausschnitt-Cache, ADR-0037), mit eigenem Speicher.</summary>
public sealed class StoryFaceSource : IStoryFaceSource, IDisposable
{
    private readonly FaceImageProvider? _provider;
    private readonly ConcurrentDictionary<long, ImageSource> _cache = new();

    public StoryFaceSource(FaceImageProvider? provider = null)
    {
        _provider = provider;
        if (_provider is not null)
        {
            _provider.ImagesReady += OnImagesReady;
        }
    }

    public event Action<IReadOnlyList<(long FaceId, ImageSource Image)>>? ImagesReady;

    public ImageSource? TryGet(long faceId) => _cache.TryGetValue(faceId, out var image) ? image : null;

    public void Request(int libraryId, IReadOnlyCollection<long> faceIds) => _provider?.Request(libraryId, faceIds);

    private void OnImagesReady(IReadOnlyList<(long FaceId, ImageSource Image)> batch)
    {
        foreach (var (faceId, image) in batch)
        {
            if (_cache.Count > 2000)
            {
                _cache.Clear();
            }

            _cache[faceId] = image;
        }

        ImagesReady?.Invoke(batch);
    }

    public void Dispose()
    {
        if (_provider is not null)
        {
            _provider.ImagesReady -= OnImagesReady;
        }
    }
}

/// <summary>
/// Bündelt, was Übersicht und Story-Ansicht brauchen (ADR-0060): Story-Dienst, Datei-Infos, Vorschaubilder, Gesichter,
/// Diashow, Lightbox, Dialoge, Navigation, Orte und die Galerie-Brücke. Ein Parameter statt zehn — Tests setzen Fakes ein.
/// </summary>
public sealed class StoryPageServices
{
    public StoryPageServices(
        IStoryService stories,
        IStoryMediaLoader media,
        IStoryImageSource images,
        IDialogService dialogs,
        INavigationService navigation,
        IStoryFaceSource? faces = null,
        ISlideshowService? slideshow = null,
        ILightboxService? lightbox = null,
        IPlaceLookup? places = null,
        StoryGalleryBridge? gallery = null,
        ISlideshowImageDecoder? decoder = null,
        Assistant.ICollageDialogService? collage = null,
        Lumora.PhotoMenu.PhotoActions? photoActions = null)
    {
        PhotoActions = photoActions; // „Als Titelbild für ▸“ auf Story-Fotos (ADR-0068)
        Stories = stories;
        Media = media;
        Images = images;
        Dialogs = dialogs;
        Navigation = navigation;
        Faces = faces;
        Slideshow = slideshow;
        Lightbox = lightbox;
        Places = places;
        Gallery = gallery;
        Decoder = decoder;
        Collage = collage;
    }

    public IStoryService Stories { get; }

    /// <summary>Gemeinsame Menü-Aktionen (ADR-0068); null in Tests ohne DI.</summary>
    public Lumora.PhotoMenu.PhotoActions? PhotoActions { get; }

    public IStoryMediaLoader Media { get; }

    public IStoryImageSource Images { get; }

    public IDialogService Dialogs { get; }

    public INavigationService Navigation { get; }

    public IStoryFaceSource? Faces { get; }

    public ISlideshowService? Slideshow { get; }

    public ILightboxService? Lightbox { get; }

    public IPlaceLookup? Places { get; }

    public StoryGalleryBridge? Gallery { get; }

    /// <summary>Scharfes Titelbild aus dem Original (wie die Diashow); ohne: Vorschaubild.</summary>
    public ISlideshowImageDecoder? Decoder { get; }

    /// <summary>„Als Collage-Bild speichern…“ (ADR-0063); ohne: Menüpunkt deaktiviert.</summary>
    public Assistant.ICollageDialogService? Collage { get; }
}
