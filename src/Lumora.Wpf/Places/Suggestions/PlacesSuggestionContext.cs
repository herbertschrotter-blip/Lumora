using Lumora.Gallery.Places;
using Lumora.Services.Library;
using Lumora.Services.Places;
using Lumora.Services.Places.Suggestions;
using Lumora.Services.Places.Tracks;

namespace Lumora.Places.Suggestions;

/// <summary>
/// Dienste der Vorschläge für die Seite „Orte“ (ADR-0062) als ein Paket — damit der Konstruktor des
/// <see cref="ViewModels.PlacesViewModel"/> nur einen optionalen Parameter mehr bekommt (ohne Paket: Bereich „Ungenau“ aus).
/// </summary>
public sealed class PlacesSuggestionContext(
    IGeoSuggestionService suggestions,
    IGeoTagUndoService undo,
    IGeoTrackSettingsStore settings,
    IGpsTrackStore tracks,
    ILibraryScope scope,
    IReverseGeocoder geocoder,
    PlaceActions? placeActions = null,
    IExternalTrackSync? externalTracks = null)
{
    /// <summary>Spuren aus verbundenen Apps nachfragen (App-Verbindung Stufe 7); null in Tests.</summary>
    public IExternalTrackSync? ExternalTracks { get; } = externalTracks;

    public IGeoSuggestionService Suggestions { get; } = suggestions;

    public IGeoTagUndoService Undo { get; } = undo;

    public IGeoTrackSettingsStore Settings { get; } = settings;

    public IGpsTrackStore Tracks { get; } = tracks;

    public ILibraryScope Scope { get; } = scope;

    public IReverseGeocoder Geocoder { get; } = geocoder;

    /// <summary>„Ort setzen…“ für Fotos ohne Vorschlag (Dialog der Galerie); null in Tests.</summary>
    public PlaceActions? PlaceActions { get; } = placeActions;
}
