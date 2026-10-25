using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Places;
using Lumora.Places.Map.Tiles;
using Lumora.Places.Styles;
using Lumora.Places.ViewModels;
using Lumora.Settings;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Erweiterung des Rauchtest-Schritts „Orte-Karte“ (0.4.1, ADR-0055) — Kachelquelle weiter abgeschaltet (kein Abruf):
/// der „Kartenausschnitt“ aus „In der Galerie zeigen“ gilt nur in der Galerie, der Filter „Ort: Wien“ aus der Filterkarte
/// gilt auf der Karte und bleibt beim Seitenwechsel Orte → Ordner → Orte erhalten; „Filter zurücksetzen“ auf der Karte wirkt überall; Kartenstil
/// umschalten (ohne Schlüssel Hinweis + OSM, mit Schlüssel CARTO-Namensnennung, „Automatisch“ folgt Dunkel) ohne Abruf;
/// Screenshots hell/dunkel und Einstellungen › Darstellung › Karte.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task SharedFilterAndMapStylesAsync(PlacesViewModel vm, PlacesPage page, int viennaFileId, int locatedTotal)
    {
        var services = ((App)Application.Current).Services;
        var styles = services.GetRequiredService<IMapStyleService>();
        var shared = services.GetRequiredService<ISharedGalleryFilter>();
        Check(_gallery.HasMapAreaFilter && shared.IsActive && shared.Current?.Filter.Geo is not null, "Gemeinsamer Filter: Kartenausschnitt aus der Galerie");

        // (6a) Galerie → Orte: der Kartenausschnitt stammt von der Karte selbst und gilt dort nicht (sonst schrumpfte sie)
        _navigation.NavigateTo(NavigationTags.Places);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Places, ShortWait, "zurück auf „Orte“").ConfigureAwait(true);
        await WaitUntilAsync(() => !vm.IsLoading && !vm.IsFiltered && vm.LocatedCount == locatedTotal, ShortWait, $"Karte ohne Kartenausschnitt-Filter ({locatedTotal})").ConfigureAwait(true);

        // (6b) Filter „Ort: Wien“ in der Filterkarte → die Karte zeigt nur die Treffer des Galerie-Filters
        var placeId = await DbScalarAsync<long>("SELECT COALESCE(PlaceId, -1) FROM Files WHERE Id = $f", ("$f", viennaFileId)).ConfigureAwait(true);
        Check(placeId > 0, $"Wien-Foto hat einen Ort ({placeId})");
        var groupCount = await DbScalarAsync<long>(
            "SELECT COUNT(*) FROM Files WHERE LibraryId = $lib AND PlaceId = $p AND Latitude IS NOT NULL AND Missing = 0 AND MediaType IN (0, 1)",
            ("$lib", _libraryId), ("$p", placeId)).ConfigureAwait(true);
        _gallery.FilterState = new GalleryFilterState { Place = new Lumora.Gallery.Data.GalleryPlaceFilter([(int)placeId], false, Label: "Wien") };
        await WaitUntilAsync(() => !vm.IsLoading && vm.IsFiltered && vm.LocatedCount == groupCount, ShortWait, $"Karte gefiltert: {groupCount} Fotos in Wien").ConfigureAwait(true);
        Check(vm.UnlocatedCount == 0 && vm.SummaryText.EndsWith("gefiltert", StringComparison.Ordinal), $"Trefferzeile „{vm.SummaryText}“");
        Check(ReferenceEquals(page.FilterCard.DataContext, _gallery) && page.FilterCard.IsVisible && page.GallerySearch.IsVisible,
            "Orte zeigen DIESELBE Filterkarte und Suche (DataContext = Galerie-ViewModel)");
        Check(_gallery.ActiveConditions.Any(c => c.Kind == GalleryFilterConditionKind.Place), "„Es gilt: Ort: Wien“ auf der Karte");
        vm.ShowAllPhotosCommand.Execute(null);
        Check(vm.VisiblePhotoCount == groupCount, $"„Im Ausschnitt“ zählt nur Treffer ({vm.VisiblePhotoCount})");
        await SnapshotAsync("40b-orte-filter-hell").ConfigureAwait(true);

        // (7) Ordner-Seite: Filter bleibt, Ordnerauswahl ist seitengebunden
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading,
            ShortWait, "Ordner-Seite").ConfigureAwait(true);
        Check(_gallery.HasPlaceFilter && _gallery.TotalCount <= groupCount, $"Ordner-Seite behält den Filter ({_gallery.TotalCount} Treffer)");
        _navigation.NavigateTo(NavigationTags.Places);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Places && !vm.IsLoading && vm.LocatedCount == groupCount, ShortWait, "Orte unverändert gefiltert").ConfigureAwait(true);

        // (8) Kartenstil ohne Schlüssel: Hinweis, Rückfall auf OSM — kein Abruf
        var keysBefore = styles.Keys;
        var styleBefore = styles.Style;
        Check(page.BuildMapStyleMenu().Items.Count == MapStyleCatalog.Options.Count, "Stil-Menü mit allen Stilen");
        await vm.SetMapStyleAsync(MapStyle.Voyager).ConfigureAwait(true);
        await WaitUntilAsync(() => vm.HasStyleNotice, ShortWait, "Hinweis „API-Schlüssel nötig“").ConfigureAwait(true);
        Check(vm.Attribution == OsmTileSource.OsmAttribution && vm.StyleNotice == MapStyleCatalog.KeyMissingNotice && page.MapView.LastDrawnTileImages == 0,
            $"Voyager ohne Schlüssel: OSM-Zuordnung, Hinweis „{vm.StyleNotice}“, keine Kacheln");
        Check(vm.MapStyleEntries().Single(e => e.Option.Style == MapStyle.Voyager) is { IsCurrent: true, NeedsKey: true }, "Stil-Menü: Voyager gewählt, Schlüssel nötig");
        await SnapshotAsync("40c-orte-stil-hinweis").ConfigureAwait(true);

        // (9) Mit (Rauchtest-)Schlüssel: CARTO-Namensnennung; „Automatisch“ folgt Dunkel — weiterhin kein Abruf
        await styles.SetKeysAsync(new MapApiKeys("smoke-test-key", null)).ConfigureAwait(true);
        await WaitUntilAsync(() => !vm.HasStyleNotice && vm.Attribution == MapStyleCatalog.CartoAttribution, ShortWait, "Voyager mit Schlüssel").ConfigureAwait(true);
        await vm.SetMapStyleAsync(MapStyle.Automatic).ConfigureAwait(true);
        _settings.IsThemeDark = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await WaitUntilAsync(
            () => (vm.Tiles?.Source as StyledMapTileSource)?.Resolution.Definition == MapStyleCatalog.DarkMatter,
            ShortWait,
            "Automatisch → Dark Matter").ConfigureAwait(true);
        Check(vm.Tiles!.State == MapTileSourceState.Disabled && page.MapView.LastDrawnTileImages == 0, "Stilwechsel ohne Kachelabruf");
        await SnapshotAsync("40d-orte-filter-dunkel").ConfigureAwait(true);

        // Einstellungen › Darstellung › Karte (dunkel)
        _navigation.NavigateTo(NavigationTags.SettingsAppearance);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Settings, ShortWait, "Einstellungen › Darstellung").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        var mapSettings = services.GetRequiredService<MapStyleSettingsViewModel>();
        await mapSettings.LoadAsync().ConfigureAwait(true);
        Check(mapSettings.Carto.Key == "smoke-test-key" && mapSettings.Carto.Status == MapServiceStatus.NotVerified
              && mapSettings.MapTiler.Status == MapServiceStatus.NotSetUp && mapSettings.MapTiler.RequestKeyCommand.CanExecute(null),
            $"Kartendienste vorgeschlagen: CARTO „{mapSettings.Carto.StatusText}“, MapTiler „{mapSettings.MapTiler.StatusText}“");
        await mapSettings.Carto.CheckKeyCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(mapSettings.Carto.CheckOk is null && mapSettings.Carto.Status != MapServiceStatus.Ready,
            $"„Schlüssel prüfen“ im Rauchtest ohne Abruf: „{mapSettings.Carto.CheckText}“");
        Check(mapSettings.Carto.UsageText.StartsWith("Diesen Monat von Lumora abgerufen: 0 Kacheln", StringComparison.Ordinal),
            $"Lumoras Zählung: „{mapSettings.Carto.UsageText}“");
        await SnapshotAsync("40e-einstellungen-karte-dunkel").ConfigureAwait(true);
        _settings.IsThemeSystem = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);
        await SnapshotAsync("40f-einstellungen-karte-hell").ConfigureAwait(true);
        await styles.SetKeysAsync(keysBefore).ConfigureAwait(true);
        await styles.SetStyleAsync(styleBefore).ConfigureAwait(true);
        Check(vm.Attribution == OsmTileSource.OsmAttribution, "Stil und Schlüssel zurückgesetzt (OSM)");

        // (10) „Filter zurücksetzen“ in der Filterkarte der Karte wirkt überall
        _navigation.NavigateTo(NavigationTags.Places);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Places, ShortWait, "Orte").ConfigureAwait(true);
        await _gallery.ResetFiltersCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !vm.IsLoading && !vm.IsFiltered && vm.LocatedCount == locatedTotal, ShortWait, $"Karte ungefiltert ({locatedTotal})").ConfigureAwait(true);
        Check(!shared.IsActive && !_gallery.HasActiveFilters, "Filter überall zurückgesetzt");
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie").ConfigureAwait(true);
    }
}
