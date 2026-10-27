using Lumora.Services.Places;

namespace Lumora.Services.Messaging;

/// <summary>
/// Galerie → Seite „Orte“ (Mockup 24, Tour-Leiste „Auf der Karte“): die Karte auf <paramref name="Bounds"/> stellen, z. B. den
/// Rahmen einer Tour.
/// </summary>
public sealed record ShowAreaOnMapMessage(GeoBounds Bounds);
