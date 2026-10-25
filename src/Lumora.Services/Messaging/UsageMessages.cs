using Lumora.Services.Usage;

namespace Lumora.Services.Messaging;

/// <summary>
/// Meldung „Datei(en) verwendet bzw. weitergegeben“ für die Nutzungs-Signale (ADR-0072), für Aktionen ohne Zugriff auf
/// <see cref="IUsageSignals"/>: z. B. Kopieren nach außen (Ziehen/Strg+C), Exportieren, Drucken, Titelbild setzen.
/// Der Dienst der aktuellen Katalog-Sitzung empfängt sie; ist „Nutzung lernen“ aus, passiert nichts.
/// </summary>
public sealed record UsageSignalMessage(IReadOnlyList<int> FileIds, UsageKind Kind);

/// <summary>Einstellungen der Nutzungs-Signale geändert oder gelernte Nutzung gelöscht (Galerie-Filter, Lightbox, Storys neu).</summary>
public sealed record UsageChangedMessage(UsageSettings Settings, bool Erased = false);
