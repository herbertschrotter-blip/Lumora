using System;
using System.Collections.Generic;
using System.Windows;
using Lumora.Domain.Enums;

namespace Lumora.Lightbox;

/// <summary>Ein Eintrag in der Lightbox-Sequenz (eine Grid-Page oder ein Suchergebnis).</summary>
public sealed record LightboxEntry(
    int FileId,
    string Path,
    string Name,
    MediaType MediaType,
    bool IsFavorite,
    byte? Rating,
    long Size,
    DateTime ModifiedAtUtc);

/// <summary>
/// Öffnet die Lightbox (eigenes Fenster, Multi-Monitor-fähig) für eine Sequenz von Dateien.
/// Bilder: Zoom/Pan, Videos: LibVLC. Navigation ← → innerhalb der Sequenz.
/// Favorit/Bewertung/Löschen laufen über ICatalogUpdateService bzw. IFileOperationService.
/// </summary>
public interface ILightboxService
{
    /// <summary>Öffnet das Lightbox-Fenster (oder holt es nach vorn) und zeigt entries[startIndex].</summary>
    void Open(IReadOnlyList<LightboxEntry> entries, int startIndex, Window? owner = null);

    /// <summary>
    /// Wie <see cref="Open(IReadOnlyList{LightboxEntry}, int, Window?)"/>, mit Herkunft für die Nutzungs-Signale (ADR-0072):
    /// aus einer Story gedämpft, aus „Doppelte“ gar nicht gezählt. Ersatz-Implementierungen (Tests) ignorieren sie.
    /// </summary>
    void Open(IReadOnlyList<LightboxEntry> entries, int startIndex, Window? owner, Lumora.Services.Usage.UsageViewContext usage)
        => Open(entries, startIndex, owner);

    bool IsOpen { get; }

    /// <summary>Leiste „Verbessern“ im offenen Fenster zeigen (ADR-0071); ohne Fenster wirkungslos.</summary>
    void ShowEnhancePanel()
    {
    }
}
