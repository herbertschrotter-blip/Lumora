using System.Collections.Generic;

namespace Lumora.Gallery.Data;

/// <summary>
/// Galerie-Filter „Tour“ (App-Verbindung Stufe 7 Teil B, ADR-0073, Mockup 24): die Fotos und Videos, die während einer Tour
/// einer verbundenen App aufgenommen wurden. Die Dateien bestimmt <c>IAppTourReader</c> beim Wählen (Zeitabgleich wie bei
/// „Ungenaue Orte“, im Bibliotheksbereich); in der SQL als Id-Liste, UND-verknüpft mit allen anderen Filtern.
/// </summary>
/// <param name="TourId">Zeile in <c>AppTours</c>.</param>
/// <param name="FileIds">Dateien der Tour im Bereich (leer = keine Treffer).</param>
/// <param name="Label">Text für Chip und „Es gilt“ („Handalm über Weinebene“).</param>
public sealed record GalleryTourFilter(int TourId, IReadOnlyCollection<int> FileIds, string Label);
