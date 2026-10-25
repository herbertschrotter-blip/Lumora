using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Marks;

namespace Lumora.Lightbox;

/// <summary>
/// Leiste unten in der Lightbox (Mockup 16, 0.4.35): Tag-Chips (Farbpunkt + Name), Trenner, gesetzte Kategorien als
/// „Kategorie: Wert“ und „+ Tag / Kategorie“ (öffnet die vorhandenen Dialoge Strg+T bzw. Strg+K). Sichtbar, solange der
/// Schalter „In der Lightbox“ an ist und kein Vollbild läuft; ist er aus, zeigt die Titelleiste die Tags/Kategorien wie bis
/// 0.4.34. Die Daten kommen aus <c>LightboxViewModel.Tags.cs</c> und <c>LightboxViewModel.Categories.cs</c>.
/// </summary>
public sealed partial class LightboxViewModel
{
    private MarkSettingsState _markSettings = new();

    /// <summary>Gesetzte Kategorien der aktuellen Datei als „Kategorie: Wert“ in Anzeigereihenfolge (Chips der Leiste).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMarksSeparator))]
    private IReadOnlyList<string> _currentCategoryLabels = Array.Empty<string>();

    /// <summary>Leiste unten sichtbar (Schalter an, kein Vollbild).</summary>
    public bool ShowMarksBar => _markSettings.ShowInLightbox && !IsFullscreen;

    /// <summary>Titelleiste zeigt Tags/Kategorien nur noch, wenn die Leiste abgeschaltet ist.</summary>
    public bool ShowTitleMarks => !_markSettings.ShowInLightbox;

    /// <summary>Trenner zwischen Tags und Kategorien nur, wenn beide da sind.</summary>
    public bool ShowMarksSeparator => HasCurrentTags && CurrentCategoryLabels.Count > 0;

    private void InitMarks(MarkSettingsState? settings)
    {
        _markSettings = settings ?? new MarkSettingsState();
        // schwach angebunden: die Lightbox lebt kürzer als der Zustand der Sitzung
        PropertyChangedEventManager.AddHandler(_markSettings, OnMarkSettingsChanged, nameof(MarkSettingsState.ShowInLightbox));
    }

    partial void OnIsFullscreenChanged(bool value) => OnPropertyChanged(nameof(ShowMarksBar));

    partial void OnCurrentTagsChanged(IReadOnlyList<Services.Tags.TagInfo> value) => OnPropertyChanged(nameof(ShowMarksSeparator));

    private void OnMarkSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowMarksBar));
        OnPropertyChanged(nameof(ShowTitleMarks));
    }
}
