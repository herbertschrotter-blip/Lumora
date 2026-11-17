namespace Lumora.Services.Messaging;

/// <summary>
/// Der Cloud-Zustand von Dateien hat sich geändert (ADR-0077): <paramref name="BecameLocal"/> = sie liegen jetzt lokal
/// (in der Lightbox geladen, „Immer auf diesem Gerät behalten“ fertig) — Gesichter, Motive, Orte, Güte, Prüfsummen und
/// Aufnahmedatum holen sie nach; sonst liegen sie jetzt nur online („Speicherplatz freigeben“ fertig). Die Galerie setzt
/// die Wolken-Marke neu. <paramref name="LibraryId"/> 0 = unbekannt (Lightbox).
/// </summary>
public sealed record CloudFilesChangedMessage(int LibraryId, IReadOnlyList<int> FileIds, bool BecameLocal);
