using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Lumora.Lightbox;
using Lumora.Settings;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Galerie-Filter Tour“ (ADR-0073 Stufe 7 Teil B, Mockup 24): eine Test-Tour (AppTours, wie vom Gipfelbuch
/// übernommen) eine Stunde um ein Foto mit Aufnahmedatum; die Auswahl nennt sie mit Fotozahl, der Filter zeigt genau diese
/// Fotos mit Tour-Leiste und „Es gilt“ (hell/dunkel), die Lightbox den Abschnitt „Tour“ mit Sprung zurück in die Galerie;
/// danach Filter weg, Tour gelöscht.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task TourFilterAsync()
    {
        var row = (await DbRowsAsync("""
            SELECT Id, CapturedAt FROM Files
            WHERE LibraryId = $lib AND Missing = 0 AND MediaType = 0 AND CapturedAt IS NOT NULL AND CapturedAtSource IS NOT NULL AND CapturedAtPrecision = 0
            ORDER BY Id LIMIT 1
            """, ("$lib", _libraryId)).ConfigureAwait(true)).SingleOrDefault();
        Check(row is not null, "Foto mit Aufnahmedatum für die Test-Tour");
        var cells = row!.Split(" | ");
        var fileId = int.Parse(cells[0], CultureInfo.InvariantCulture);
        var at = long.Parse(cells[1], CultureInfo.InvariantCulture);
        int tourId;
        using (var connection = _db.OpenSqliteConnection())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AppTours (AppId, ExternalId, Name, StartUtc, EndUtc, StartOffsetMinutes, Sport, Region, DistanceMeters, AscentMeters,
                    DurationSeconds, MinLongitude, MinLatitude, MaxLongitude, MaxLatitude, HasTrack, Summits, UpdatedAtUtc)
                VALUES ('rauchtest', 'tour-1', 'Rauchtest-Tour', $s, $e, 60, 'mountain_hiking', 'Testgebiet', 12400, 850, 7200,
                        15.0, 46.8, 15.1, 46.9, 0, $summits, 0);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$s", at - 3600);
            cmd.Parameters.AddWithValue("$e", at + 3600);
            cmd.Parameters.AddWithValue("$summits",
                $$"""[{"name":"Testgipfel","elevationM":1853,"arrivalUtc":"{{DateTimeOffset.FromUnixTimeSeconds(at).UtcDateTime:O}}","confirmed":true}]""");
            tourId = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        try
        {
            _navigation.NavigateTo(NavigationTags.Gallery);
            await WaitForGalleryAsync("Galerie").ConfigureAwait(true);
            await _gallery.LoadTourFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
            var entry = _gallery.TourFilterEntries.FirstOrDefault(e => e.Tour.Id == tourId);
            Check(_gallery.ShowTourFilter && entry is { HasPhotos: true } && entry.PeakLine == "▲ Testgipfel 1.853 m",
                $"Auswahl „Tour“: {entry?.Name} · {entry?.Line} · {entry?.CountText}");

            await _gallery.SelectTourCommand.ExecuteAsync(entry).ConfigureAwait(true);
            await WaitForGalleryAsync("Filter „Tour“").ConfigureAwait(true);
            Check(_gallery.HasTourFilter && _gallery.TotalCount == entry!.Tour.PhotoCount && _gallery.CurrentPage.Any(f => f.Id == fileId)
                  && _gallery.ActiveConditions.Any(c => c.Label.StartsWith("Tour „Rauchtest-Tour“", StringComparison.Ordinal))
                  && _gallery.TourTimelineMarks.Count(m => m.Label is not null) == 1 && _gallery.CanShowTourOnMap,
                $"Galerie mit Filter „Tour“: {_gallery.TotalCount} Datei(en), Leiste „{_gallery.ActiveTourFacts}“");
            await _theme.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
            await SnapshotAsync("79-tour-filter-hell").ConfigureAwait(true);
            await _theme.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
            await SnapshotAsync("80-tour-filter-dunkel").ConfigureAwait(true);

            // Lightbox: Abschnitt „Tour“ und „Alle n Fotos dieser Tour“
            _gallery.OpenInLightboxCommand.Execute(_gallery.CurrentPage.First(f => f.Id == fileId));
            await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
            var lightbox = Application.Current.Windows.OfType<LightboxWindow>().First().ViewModel;
            await WaitUntilAsync(() => lightbox.HasTour, ShortWait, "Abschnitt „Tour“").ConfigureAwait(true);
            Check(lightbox.TourName == "Rauchtest-Tour" && lightbox.TourLinkText?.EndsWith("Fotos dieser Tour", StringComparison.Ordinal) == true
                  && lightbox.TourPeaks?.StartsWith("Testgipfel 1.853 m", StringComparison.Ordinal) == true,
                $"Lightbox „Tour“: {lightbox.TourName} · {lightbox.TourDetails} · {lightbox.TourPeaks} · {lightbox.TourLinkText}");
            await _gallery.ClearTourFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
            lightbox.ShowTourInGalleryCommand.Execute(null);
            await WaitUntilAsync(() => !_lightbox.IsOpen && _gallery.HasTourFilter, ShortWait, "zurück in der Galerie mit Filter „Tour“").ConfigureAwait(true);
            await WaitForGalleryAsync("Filter „Tour“ aus der Lightbox").ConfigureAwait(true);
            Check(_gallery.TotalCount == entry.Tour.PhotoCount, $"Lightbox → Galerie: Filter „Tour“ mit {_gallery.TotalCount} Datei(en)");
        }
        finally
        {
            await _gallery.ClearTourFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
            using var connection = _db.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM AppTours WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", tourId);
            cmd.ExecuteNonQuery();
        }

        await WaitForGalleryAsync("Filter „Tour“ aufgehoben").ConfigureAwait(true);
        Check(!_gallery.HasTourFilter && !_gallery.ActiveConditions.Any(c => c.Label.StartsWith("Tour", StringComparison.Ordinal)), "Filter „Tour“ aufgehoben, Test-Tour gelöscht");
    }
}
