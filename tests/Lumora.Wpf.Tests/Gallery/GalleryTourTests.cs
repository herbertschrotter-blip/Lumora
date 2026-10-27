using System.Windows;
using Lumora.Gallery.Tours;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Places;
using Lumora.Services.Places.Tracks;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;
using Wpf.Ui.Appearance;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Galerie-Filter „Tour“ (ADR-0073 Stufe 7, Mockup 24): Texte der Auswahl (Sportart, Datum, Länge, Höhenmeter, Region,
/// Gipfel, Fotozahl, Jahres-Überschrift), Tour-Leiste und Zeitstrahl (Fotos und Gipfel als Anteil der Tourdauer); der
/// Zeitstrahl zeichnet hell und dunkel.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GalleryTourTests
{
    private static readonly DateTime Start = new(2020, 3, 15, 7, 12, 0, DateTimeKind.Utc);

    private static AppTourInfo Tour(int photos = 38, string sport = "mountain_hiking") => new(
        1, "gipfelbuch", "Handalm über Weinebene", Start, Start.AddMinutes(388), 60, sport, "Koralpe", 14200, 912,
        [new AppTourSummit("Handalm", 1853, Start.AddMinutes(192), true)], photos, new GeoBounds(46.8, 15.0, 46.9, 15.1));

    [Fact]
    public void Eintrag_der_Auswahl_wie_im_Mockup()
    {
        var entry = new TourFilterEntry(Tour(), firstOfYear: true);

        entry.YearText.Should().Be("2020");
        entry.Line.Should().Be("Bergtour · So, 15.03.2020 · 14,2 km · ↑ 912 m · Koralpe");
        entry.PeakLine.Should().Be("▲ Handalm 1.853 m");
        entry.CountText.Should().Be("38 Fotos");
        new TourFilterEntry(Tour(photos: 0), firstOfYear: false).Should().Match<TourFilterEntry>(e => e.CountText == "keine Fotos" && e.YearText == null && !e.HasPhotos);
        new TourFilterEntry(Tour(sport: "road_cycling"), false).Symbol.Should().Be(global::Wpf.Ui.Controls.SymbolRegular.VehicleBicycle24);
        TourFilterEntry.Sport("unbekannt").Should().Be("Tour");
    }

    [Fact]
    public void Tour_Leiste_und_Zeitstrahl()
    {
        GalleryViewModel.TourFacts(Tour()).Should().Be("Bergtour · So, 15.03.2020 · 08:12–14:40 · 14,2 km · ↑ 912 m · ▲ Handalm 1.853 m · Koralpe");

        var start = new DateTimeOffset(Start).ToUnixTimeSeconds();
        var marks = GalleryViewModel.Marks(Tour(), [new AppTourPhoto(1, start), new AppTourPhoto(2, start + (388 * 30)), new AppTourPhoto(3, start + 99999)]);

        marks.Where(m => m.Label is null).Select(m => m.Fraction).Should().Equal(0, 0.5, 1);
        marks.Single(m => m.Label is not null).Should().Match<TourTimelineMark>(m => m.Label == "▲ Handalm 11:24" && Math.Abs(m.Fraction - (192.0 / 388)) < 1e-9);
    }

    [Fact]
    public Task Zeitstrahl_zeichnet_hell_und_dunkel() => TestUiThread.Dispatcher.InvokeAsync(() =>
    {
        var start = new DateTimeOffset(Start).ToUnixTimeSeconds();
        var timeline = new TourTimeline { Marks = GalleryViewModel.Marks(Tour(), [new AppTourPhoto(1, start + 600)]), Width = 600, Height = 30 };
        var window = new Window { Content = timeline, Width = 640, Height = 120, ShowActivated = false, Left = -10000 };
        window.Show();
        try
        {
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                ApplicationThemeManager.Apply(theme);
                window.UpdateLayout();
            }

            timeline.ActualWidth.Should().Be(600, "zeichnet ohne Ausnahme in beiden Farbschemata");
        }
        finally
        {
            window.Close();
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }
    }).Task;
}
