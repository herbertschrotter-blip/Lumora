using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Marks;

namespace Lumora.Marks;

/// <summary>
/// Schalter „Symbole für Tags und Kategorien“ (Einstellungen › Darstellung, Mockup 16) als beobachtbarer Zustand der Sitzung:
/// Galerie (Kacheln/Collage), Lightbox und Ordnerbaum binden daran, die Einstellungs-Karte schreibt hinein. Lädt beim ersten
/// Abruf aus der Settings-Tabelle des aktiven Katalogs (versteckte Sitzung = eigener Katalog und eigener DI-Container) und
/// speichert jede Änderung. Ohne Speicher (Tests) bleibt alles an.
/// </summary>
public sealed partial class MarkSettingsState : ObservableObject
{
    private readonly IMarkSettingsStore? _store;
    private readonly ILogger _logger;
    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1); // Speichern in Reihenfolge der Klicks
    private bool _loading;
    private bool _changedByUser; // vor dem Ende des ersten Ladens umgelegt → Laden überschreibt das nicht

    /// <summary>Auf den Galerie-Kacheln (oben links; Collage nur beim Darüberfahren).</summary>
    [ObservableProperty] private bool _showOnTiles = true;

    /// <summary>Leiste unten in der Lightbox mit Tag-Chips und Kategorien.</summary>
    [ObservableProperty] private bool _showInLightbox = true;

    /// <summary>Zahl der Fotos mit Tags/Kategorien neben dem Ordnernamen.</summary>
    [ObservableProperty] private bool _showInFolderTree = true;

    /// <summary>Ohne Speicher (Tests, Vorschau): alles an, nichts wird gespeichert.</summary>
    public MarkSettingsState()
        : this(null, null)
    {
    }

    public MarkSettingsState(IMarkSettingsStore? store, ILogger<MarkSettingsState>? logger = null)
    {
        _store = store;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Loaded = store is null ? Task.CompletedTask : LoadAsync();
    }

    /// <summary>Erstes Laden (Tests, Rauchtest warten darauf).</summary>
    public Task Loaded { get; }

    /// <summary>Zuletzt angestoßenes Speichern (Tests, Rauchtest).</summary>
    public Task LastSave { get; private set; } = Task.CompletedTask;

    public MarkSettings Current => new(ShowOnTiles, ShowInLightbox, ShowInFolderTree);

    private async Task LoadAsync()
    {
        try
        {
            var settings = await _store!.LoadAsync().ConfigureAwait(true);
            if (_changedByUser)
            {
                return; // der Benutzer war schneller; sein Wert ist schon gespeichert
            }

            _loading = true;
            try
            {
                ShowOnTiles = settings.ShowOnTiles;
                ShowInLightbox = settings.ShowInLightbox;
                ShowInFolderTree = settings.ShowInFolderTree;
            }
            finally
            {
                _loading = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einstellungen „Symbole für Tags und Kategorien“ konnten nicht geladen werden — Standard (an)");
        }
    }

    partial void OnShowOnTilesChanged(bool value) => Save();

    partial void OnShowInLightboxChanged(bool value) => Save();

    partial void OnShowInFolderTreeChanged(bool value) => Save();

    private void Save()
    {
        if (_loading || _store is null)
        {
            return;
        }

        _changedByUser = true;

        LastSave = SaveAsync(Current);
    }

    private async Task SaveAsync(MarkSettings settings)
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _store!.SaveAsync(settings).ConfigureAwait(false);
            _logger.LogInformation(
                "Symbole für Tags und Kategorien: Kacheln={Tiles} Lightbox={Lightbox} Ordnerbaum={Tree}",
                settings.ShowOnTiles, settings.ShowInLightbox, settings.ShowInFolderTree);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einstellungen „Symbole für Tags und Kategorien“ konnten nicht gespeichert werden");
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
