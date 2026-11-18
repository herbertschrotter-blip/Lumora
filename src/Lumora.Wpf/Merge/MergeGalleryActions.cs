using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Imaging.Merge;
using Lumora.Lightbox;
using Lumora.Services.Database;
using Lumora.Services.Merge;
using Lumora.Services.Operations;
using Lumora.Shell;
using Lumora.Shell.HiddenLibraries;

namespace Lumora.Merge;

/// <summary>
/// Was Galerie, Lightbox, Aktivitäts-Anzeige und Fenster für „Zusammenfügen“ brauchen (ADR-0078, Stufe 1 Teil B): Start des
/// Fensters mit Sperre bei gemischten Bibliotheken, Ergebnis und Quellen in der Lightbox (Ergebnis zuerst), „Neu berechnen…“
/// aus dem Rezept, Rückgängig, „Danach verbessern…“, „In der Galerie zeigen“. Eine Instanz je Katalog-Sitzung.
/// </summary>
public sealed class MergeGalleryActions : IMergeResultActions
{
    private readonly IServiceProvider _services;
    private readonly ILibraryContextFactory _factory;
    private readonly IMergeWindowService _windows;
    private readonly IFileOperationService? _fileOps;
    private readonly ILogger _logger;
    private MergeAvailability? _availability;

    public MergeGalleryActions(
        IServiceProvider services,
        IMergedPhotoStore store,
        ILibraryContextFactory factory,
        IMergeWindowService windows,
        IMergeService? merge = null,
        IFileOperationService? fileOps = null,
        ILogger<MergeGalleryActions>? logger = null)
    {
        _services = services;
        Store = store;
        _factory = factory;
        _windows = windows;
        Service = merge;
        _fileOps = fileOps;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public IMergedPhotoStore Store { get; }

    public IMergeService? Service { get; }

    /// <summary>Bekannte Verfügbarkeit (null = noch nicht geprüft; geprüft wird beim ersten Start).</summary>
    public MergeAvailability? KnownAvailability => _availability;

    /// <summary>Grund, warum „Zusammenfügen…“ gesperrt ist (Menü-Tooltip); null = frei.</summary>
    public string? BlockReason(IReadOnlyCollection<(int LibraryId, MediaType MediaType, bool Missing)> selection)
    {
        if (_availability is { Available: false } a)
        {
            return a.Reason ?? "„Zusammenfügen“ ist nicht verfügbar.";
        }

        if (_windows.IsSaving)
        {
            return MergeTexts.BlockedBusy;
        }

        var photos = selection.Where(s => s.MediaType == MediaType.Image && !s.Missing).ToList();
        if (photos.Count == 0)
        {
            return MergeTexts.BlockedNoPhotos;
        }

        return photos.Select(p => p.LibraryId).Distinct().Count() > 1 ? MergeTexts.BlockedMixedLibraries : null;
    }

    /// <summary>Fenster öffnen (prüft beim ersten Mal, ob der Rechen-Prozess da ist).</summary>
    public async Task StartAsync(MergeStart start, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (Service is not null && _availability is null)
        {
            try
            {
                _availability = await Service.GetAvailabilityAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Zusammenfügen: Verfügbarkeit nicht geprüft");
            }
        }

        _windows.Show(start, owner);
    }

    /// <summary>Lightbox-Einträge der Dateien in dieser Reihenfolge.</summary>
    public Task<IReadOnlyList<LightboxEntry>> LoadEntriesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => LightboxEntryLoader.LoadAsync(_factory, fileIds, ct);

    /// <summary>Ergebnis und noch vorhandene Quellen in Positionsreihenfolge (Ergebnis zuerst).</summary>
    public async Task<IReadOnlyList<int>> ResultAndSourcesAsync(int resultFileId, CancellationToken ct = default)
    {
        var sources = await Store.GetSourcesAsync(resultFileId, ct).ConfigureAwait(true);
        return [resultFileId, .. sources.Where(s => s != resultFileId)];
    }

    /// <summary>Ergebnis-Marke bzw. „Öffnen“: Ergebnis und Quellen in der Lightbox, Ergebnis zuerst.</summary>
    public Task OpenResultAsync(int resultFileId, Window? owner) => OpenResultCoreAsync(resultFileId, startAtSources: false, owner);

    /// <summary>Lightbox-Zeile „Quellen zeigen“: dieselbe Folge, Start beim ersten Einzelbild.</summary>
    public Task ShowSourcesAsync(int resultFileId, Window? owner) => OpenResultCoreAsync(resultFileId, startAtSources: true, owner);

    private async Task OpenResultCoreAsync(int resultFileId, bool startAtSources, Window? owner)
    {
        try
        {
            var ids = await ResultAndSourcesAsync(resultFileId).ConfigureAwait(true);
            var entries = await LoadEntriesAsync(ids).ConfigureAwait(true);
            if (entries.Count == 0)
            {
                return;
            }

            var start = startAtSources && entries.Count > 1 ? 1 : 0;
            _services.GetRequiredService<ILightboxService>().Open(entries, start, owner ?? Application.Current?.MainWindow);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Zusammenfügen: Ergebnis {FileId} nicht geöffnet", resultFileId);
        }
    }

    /// <summary>„Neu berechnen…“: Fenster mit den noch vorhandenen Quellen und den Optionen aus dem Rezept (speichert als weitere Datei).</summary>
    public async Task RecomputeAsync(int resultFileId, Window? owner, int? albumId = null, string? albumName = null)
    {
        var info = await Store.GetInfoAsync(resultFileId).ConfigureAwait(true);
        if (info is null)
        {
            return;
        }

        var recipe = MergeRecipe.FromJson(info.RecipeJson);
        var sources = info.Sources.Where(s => !s.Missing).OrderBy(s => s.Position).Select(s => s.FileId).ToList();
        var options = recipe?.Options is { } o ? o with { ColorBoost = recipe.ColorBoost, SphereForm = recipe.SphereForm } : null;
        await StartAsync(new MergeStart(sources, albumId, albumName, options, info.SeriesId, resultFileId), owner).ConfigureAwait(true);
    }

    public async Task ShowInGalleryAsync(int resultFileId, string name)
    {
        var gallery = _services.GetService<Gallery.ViewModels.GalleryViewModel>();
        if (gallery is null)
        {
            return;
        }

        _services.GetService<INavigationService>()?.NavigateTo(NavigationTags.Gallery);
        await gallery.ApplyRelatedSearchAsync($"zusammengefügt: {name}", [resultFileId]).ConfigureAwait(true);
        Application.Current?.MainWindow?.Activate();
    }

    public async Task<string> UndoAsync(int batchId)
    {
        if (_fileOps is null)
        {
            return "Rückgängig nicht verfügbar.";
        }

        var result = await _fileOps.UndoBatchAsync(batchId).ConfigureAwait(true);
        return result is null ? "Nichts rückgängig zu machen." : "Ergebnis in den Papierkorb — die Einzelbilder sind unverändert.";
    }

    public async Task EnhanceAsync(int resultFileId, Window? owner)
    {
        var entries = await LoadEntriesAsync([resultFileId]).ConfigureAwait(true);
        if (entries.Count == 0)
        {
            return;
        }

        var lightbox = _services.GetRequiredService<ILightboxService>();
        lightbox.Open(entries, 0, owner ?? Application.Current?.MainWindow);
        lightbox.ShowEnhancePanel();
    }

    public bool IsExplorerBlocked => HiddenSessionPrivacy.IsHiddenSessionActive;

    public string? ShowInExplorer(string path)
    {
        try
        {
            HiddenSessionPrivacy.ThrowIfShellBlocked(); // versteckte Sitzung: keine Shell-Spuren (ADR-0049, ADR-0078 Punkt 13)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return ex.Message;
        }
    }
}
