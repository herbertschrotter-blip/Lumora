using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.ListView;

namespace Lumora.Gallery.Views;

/// <summary>
/// Ordnerleiste der Galerie (ADR-0068, Mockup 18): offen oder eingeklappt, Strg+B schaltet um, gemerkt in der Settings-Tabelle
/// des Katalogs (<see cref="SettingKey"/>; die versteckte Sitzung hat ihren eigenen Wert). Singleton je Sitzung — Galerie-Seite
/// und Navigation (früher Seite „Ordner“: <c>NavigationTags.Folders</c> öffnet jetzt die Galerie mit offener Leiste) teilen ihn.
/// </summary>
public sealed partial class GalleryFolderPaneState : ObservableObject
{
    /// <summary>Schlüssel in der Settings-Tabelle: „open“ bzw. „closed“.</summary>
    public const string SettingKey = "ui.gallery.folderPane";

    /// <summary>Breite der offenen Leiste.</summary>
    public const double OpenWidth = 290;

    private readonly IGalleryViewSettingsStore? _settings;
    private readonly ILogger<GalleryFolderPaneState>? _logger;
    private bool _loaded;
    private bool _changedBeforeLoad;

    public GalleryFolderPaneState(IGalleryViewSettingsStore? settings = null, ILogger<GalleryFolderPaneState>? logger = null)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Leiste offen (Standard) bzw. eingeklappt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleToolTip))]
    private bool _isOpen = true;

    public string ToggleToolTip => IsOpen ? "Leiste einklappen (Strg+B)" : "Ordnerleiste öffnen (Strg+B)";

    /// <summary>Gespeicherten Zustand einmal lesen (die erste Galerie-Anzeige); ein früher Klick gewinnt.</summary>
    public async Task LoadAsync()
    {
        if (_loaded || _settings is null)
        {
            return;
        }

        _loaded = true;
        try
        {
            var value = await _settings.GetAsync(SettingKey).ConfigureAwait(true);
            if (!_changedBeforeLoad && value is not null)
            {
                SetOpen(!string.Equals(value, "closed", StringComparison.OrdinalIgnoreCase), save: false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Zustand der Ordnerleiste nicht lesbar — offen");
        }
    }

    /// <summary>Strg+B bzw. Knopf an der Leiste.</summary>
    [RelayCommand]
    private void Toggle() => SetOpen(!IsOpen, save: true);

    /// <summary>Öffnen (z. B. Navigation auf die frühere Seite „Ordner“).</summary>
    public void Open() => SetOpen(true, save: true);

    private void SetOpen(bool open, bool save)
    {
        if (!_loaded)
        {
            _changedBeforeLoad = true;
        }

        if (IsOpen == open)
        {
            return;
        }

        IsOpen = open;
        _logger?.LogInformation("Ordnerleiste {State}", open ? "geöffnet" : "eingeklappt");
        if (save && _settings is not null)
        {
            _ = SaveAsync(open);
        }
    }

    private async Task SaveAsync(bool open)
    {
        try
        {
            await _settings!.SetAsync(SettingKey, open ? "open" : "closed").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Zustand der Ordnerleiste nicht gespeichert");
        }
    }
}
