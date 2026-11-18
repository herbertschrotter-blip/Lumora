using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Domain.Enums;
using Lumora.Lightbox;
using Lumora.Services.Database;
using Lumora.Services.Enhance;

namespace Lumora.Enhance;

/// <summary>
/// Was die Galerie für „Foto verbessern“ braucht (ADR-0071): Fassungs-Marken, „beste Fassung zeigen“, Stapel-Dialog und Lauf,
/// Lightbox mit allen Fassungen einer Gruppe. Eine Instanz je Katalog-Sitzung (Singleton der Sitzung).
/// </summary>
public sealed class EnhanceGalleryActions(
    IPhotoEnhanceService service,
    IPhotoVariantStore variants,
    IEnhanceSettingsStore settings,
    IEnhanceBatchDialogService dialog,
    ILibraryContextFactory factory)
{
    public IPhotoEnhanceService Service => service;

    public IPhotoVariantStore Variants => variants;

    public IEnhanceSettingsStore Settings => settings;

    public IEnhanceBatchDialogService Dialog => dialog;

    /// <summary>Lightbox-Einträge der Dateien in dieser Reihenfolge (auch Dateien außerhalb der Seite, z. B. ausgeblendete Originale).</summary>
    public Task<IReadOnlyList<LightboxEntry>> LoadEntriesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => LightboxEntryLoader.LoadAsync(factory, fileIds, ct);
}
