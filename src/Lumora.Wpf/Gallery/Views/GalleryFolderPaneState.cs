using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.ListView;

namespace Lumora.Gallery.Views;

/// <summary>
/// Ordnerleiste der Galerie (ADR-0068, Mockup 18): offen oder eingeklappt, Strg+B schaltet um, gemerkt in der Settings-Tabelle
/// des Katalogs (<see cref="SettingKey"/>; die versteckte Sitzung hat ihren eigenen Wert). Die Breite lässt sich am rechten Rand
/// ziehen (0.5.2) und wird ebenfalls gemerkt (<see cref="WidthSettingKey"/>). Singleton je Sitzung — Galerie-Seite
/// und Navigation (früher Seite „Ordner“: <c>NavigationTags.Folders</c> öffnet jetzt die Galerie mit offener Leiste) teilen ihn.
/// </summary>
public sealed partial class GalleryFolderPaneState : ObservableObject
{
    /// <summary>Schlüssel in der Settings-Tabelle: „open“ bzw. „closed“.</summary>
    public const string SettingKey = "ui.gallery.folderPane";

    /// <summary>Schlüssel in der Settings-Tabelle für die Breite der offenen Leiste (Pixel, invariant).</summary>
    public const string WidthSettingKey = "ui.gallery.folderPaneWidth";

    /// <summary>Breite der offenen Leiste, solange nichts gezogen wurde (Doppelklick auf den Rand setzt sie zurück).</summary>
    public const double DefaultWidth = 290;

    /// <summary>Schmalste bzw. breiteste Leiste beim Ziehen.</summary>
    public const double MinWidth = 200;

    public const double MaxWidth = 640;

    private readonly IGalleryViewSettingsStore? _settings;
    private readonly ILogger<GalleryFolderPaneState>? _logger;
    private bool _loaded;
    private bool _changedBeforeLoad;
    private bool _widthChangedBeforeLoad;

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

    /// <summary>Breite der offenen Leiste, zwischen <see cref="MinWidth"/> und <see cref="MaxWidth"/>.</summary>
    [ObservableProperty]
    private double _paneWidth = DefaultWidth;

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

            var width = await _settings.GetAsync(WidthSettingKey).ConfigureAwait(true);
            if (!_widthChangedBeforeLoad
                && double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out var stored))
            {
                PaneWidth = Clamp(stored);
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

    /// <summary>Rand der Leiste wird gezogen: Breite um <paramref name="delta"/> ändern (begrenzt); gespeichert erst mit
    /// <see cref="CommitWidth"/>.</summary>
    public void ResizeBy(double delta)
    {
        _widthChangedBeforeLoad |= !_loaded;
        PaneWidth = Clamp(PaneWidth + delta);
    }

    /// <summary>Ziehen beendet: Breite merken.</summary>
    public void CommitWidth() => SaveWidth();

    /// <summary>Doppelklick auf den Rand: zurück auf <see cref="DefaultWidth"/>.</summary>
    public void ResetWidth()
    {
        _widthChangedBeforeLoad |= !_loaded;
        PaneWidth = DefaultWidth;
        SaveWidth();
    }

    private static double Clamp(double width)
        => double.IsFinite(width) ? Math.Clamp(Math.Round(width), MinWidth, MaxWidth) : DefaultWidth;

    private void SaveWidth()
    {
        if (_settings is not null)
        {
            _ = SaveWidthAsync(PaneWidth.ToString(CultureInfo.InvariantCulture));
        }
    }

    private async Task SaveWidthAsync(string value)
    {
        try
        {
            await _settings!.SetAsync(WidthSettingKey, value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Breite der Ordnerleiste nicht gespeichert");
        }
    }

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
