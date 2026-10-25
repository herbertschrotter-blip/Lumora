using System.Collections.Generic;
using Lumora.Domain.Enums;
using Lumora.Services.Rotation;

namespace Lumora.PhotoMenu;

/// <summary>Wo das Rechtsklick-Menü geöffnet wurde (Tastenkürzel und „Öffnen“ hängen davon ab).</summary>
public enum PhotoMenuSource
{
    /// <summary>Galerie-Kachel bzw. Collage.</summary>
    Gallery = 0,

    /// <summary>Zeile der Listenansicht.</summary>
    List = 1,

    Lightbox = 2,
}

/// <summary>Das Foto, auf dem das Menü geöffnet wurde.</summary>
public sealed record PhotoMenuTarget(int FileId, string Name, string FullPath, string Extension, MediaType MediaType, bool IsMissing)
{
    public bool IsArchive => MediaType == MediaType.Archive;

    public bool IsImage => MediaType == MediaType.Image;

    /// <summary>Drehen geht nur für JPEG (ADR-0068: EXIF-Ausrichtung wie beim GPS-Schreiben).</summary>
    public bool CanRotate => !IsMissing && IPhotoRotationService.CanRotate(Extension, MediaType);
}

/// <summary>Menü-Anlass: Ziel-Foto und alle betroffenen Dateien (Auswahl bzw. das eine Foto der Lightbox).</summary>
public sealed record PhotoMenuContext(PhotoMenuSource Source, PhotoMenuTarget Primary, IReadOnlyList<int> FileIds)
{
    /// <summary>Mehrfachauswahl: nur Einträge, die für mehrere Fotos Sinn haben.</summary>
    public bool IsMulti => FileIds.Count > 1;

    public bool IsLightbox => Source == PhotoMenuSource.Lightbox;
}

/// <summary>Angezeigte Tastenkürzel je Ort (nur, wo es das Kürzel wirklich gibt).</summary>
public static class PhotoMenuGestures
{
    public static string? Open(PhotoMenuContext c) => c.IsLightbox ? null : "Enter";

    public static string? Slideshow(PhotoMenuContext c) => c.IsLightbox ? "S" : null;

    public static string? Rating(PhotoMenuContext c, int stars) => c.IsLightbox ? $"Strg+{stars}" : null;

    public static string? Favorite(PhotoMenuContext c) => c.IsLightbox ? "Strg+D" : null;

    public static string? Faces(PhotoMenuContext c) => c.IsLightbox ? "F" : null;

    public const string Tags = "Strg+T";
    public const string Categories = "Strg+K";
    public const string Album = "Strg+L";
    public const string IntoLibrary = "Strg+M";
    public const string Recycle = "Entf";
    public const string Copy = "Strg+C";
    public const string Properties = "Alt+Enter";
}
