namespace Lumora.Services.Messaging;

/// <summary>
/// Lightbox → Galerie (Mockup 24): „Alle n Fotos dieser Tour“ — die Galerie setzt den Filter „Tour“ auf
/// <paramref name="TourId"/> (Zeile in <c>AppTours</c>) und zeigt sich.
/// </summary>
public sealed record ShowTourInGalleryMessage(int TourId);
