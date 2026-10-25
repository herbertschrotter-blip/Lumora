using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Lumora.Lightbox;
using Lumora.Services.Usage;
using Lumora.Settings;
using Lumora.Stories;
using Lumora.Usage;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Nutzungs-Signale“ (ADR-0072, Mockup 20) mit simulierter Zeit: „Nutzung lernen“ ist im Rauchtest bis
/// hierher aus (<see cref="DisableUsageForSmokeAsync"/>, damit frühere Schritte keine Highlights verschieben); hier an →
/// Lightbox aus der Galerie, eigene Uhr für die Messung: 5 s aktiv + Zoom zählt, 0,4 s (Schnelllauf) nicht, Zeit mit
/// Fenster im Hintergrund nicht, aus „Doppelte“ gar nicht; Kopieren nach außen meldet Weitergabe; Tageszeilen (nur Summen)
/// im Katalog, Zeile „Nutzung“ in der Lightbox; zwei frühere Tage nachgetragen → Galerie-Filter „Lieblinge“ zeigt genau das
/// Foto (Screenshot hell); Einstellungen › Storys › „Nutzung lernen“ mit Zahlen (Screenshot dunkel); „Gelernte Nutzung
/// löschen“ → leer; aus → nichts mehr gespeichert. Aufräumen: Filter zurück, Lernen wieder aus.
/// </summary>
internal sealed partial class SmokeScenario
{
    private UsageSignalService? _usageService;
    private UsageSettingsViewModel? _usageSettings;

    private void InitUsageSmoke(IServiceProvider services)
    {
        _usageService = services.GetService<UsageSignalService>();
        _usageSettings = services.GetService<UsageSettingsViewModel>();
    }

    /// <summary>Gleich zu Beginn: „Nutzung lernen“ aus (Standard wäre an) — erst dieser Schritt schaltet es ein.</summary>
    private async Task DisableUsageForSmokeAsync()
    {
        if (_usageService is not null)
        {
            await _usageService.SaveSettingsAsync(new UsageSettings(Enabled: false)).ConfigureAwait(true);
        }
    }

    private async Task UsageSignalsAsync()
    {
        Check(_usageService is not null && _usageSettings is not null, "Nutzungs-Signale: Dienst und Einstellungen registriert");
        var themeBefore = _theme.Mode;
        try
        {
            await UsageSignalsCoreAsync(_usageService!, _usageSettings!).ConfigureAwait(true);
        }
        finally
        {
            if (_lightbox.IsOpen)
            {
                Application.Current.Windows.OfType<LightboxWindow>().FirstOrDefault()?.ViewModel.CloseCommand.Execute(null);
            }

            if (_gallery.LovedOnly)
            {
                _gallery.LovedOnly = false;
                await WaitForGalleryAsync("Filter „Lieblinge“ aufgehoben").ConfigureAwait(true);
            }

            await _usageService!.EraseAsync().ConfigureAwait(true);
            await _usageService.SaveSettingsAsync(new UsageSettings(Enabled: false)).ConfigureAwait(true);
            await _theme.SetModeAsync(themeBefore).ConfigureAwait(true);
            _navigation.NavigateTo(NavigationTags.Gallery);
        }
    }

    private async Task UsageSignalsCoreAsync(UsageSignalService usage, UsageSettingsViewModel settings)
    {
        // (1) Bis hierher aus: nichts gespeichert; jetzt an
        Check(Rows().Count == 0 && !usage.IsEnabled, "Bis hierher „Nutzung lernen“ aus, keine Tageszeilen");
        await usage.SaveSettingsAsync(new UsageSettings()).ConfigureAwait(true);
        Check(usage.IsEnabled, "„Nutzung lernen“ an");

        // (2) Lightbox aus der Galerie mit simulierter Uhr
        _navigation.NavigateTo(NavigationTags.Gallery);
        await _gallery.ResetFiltersCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Galerie ohne Filter").ConfigureAwait(true);
        var images = _gallery.CurrentPage.Where(i => !i.IsVideo && !i.IsArchive && !i.Missing).Take(3).ToList();
        Check(images.Count == 3, $"Drei Bilder auf der ersten Seite ({images.Count})");
        _gallery.OpenInLightboxCommand.Execute(images[0]);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().First();
        var vm = window.ViewModel;
        await WaitUntilAsync(() => vm.CurrentItem?.FileId == images[0].Id, ShortWait, "erstes Bild in der Lightbox").ConfigureAwait(true);
        var tracker = vm.UsageTracker;
        Check(tracker is not null && vm.UsageContext == UsageViewContext.Normal, "Lightbox misst (aus der Galerie: zählt voll)");
        // Ab hier ohne await: die Uhr läuft nur über Advance, Fenster-Ereignisse kommen nicht dazwischen
        var clock = new SmokeClock();
        tracker!.Context = UsageViewContext.None; // die echte Zeit seit dem Öffnen verwerfen
        tracker.UseTime(clock);
        tracker.Context = UsageViewContext.Normal;
        vm.SetUsageActive(false);
        tracker.Show(images[0].Id);
        clock.Advance(TimeSpan.FromSeconds(40)); // Fenster nicht vorne → zählt nicht
        vm.SetUsageActive(true);
        clock.Advance(TimeSpan.FromSeconds(5));
        vm.IsZoomFit = false; // hineingezoomt
        var first = images[0].Id;
        var second = vm.Items.Count > 1 ? vm.Items[vm.CurrentIndex + 1].FileId : 0;
        vm.NextCommand.Execute(null);
        clock.Advance(TimeSpan.FromMilliseconds(400)); // Schnelllauf
        vm.NextCommand.Execute(null);
        var third = vm.CurrentItem!.FileId;
        clock.Advance(TimeSpan.FromSeconds(3));
        vm.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        // (3) „Doppelte“: gar nicht gezählt
        _lightbox.Open([Entry(images[1])], 0, Application.Current.MainWindow, UsageViewContext.None);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox wie aus „Doppelte“").ConfigureAwait(true);
        var dupWindow = Application.Current.Windows.OfType<LightboxWindow>().First();
        Check(dupWindow.ViewModel.UsageContext == UsageViewContext.None, "Lightbox aus „Doppelte“: zählt nicht");
        dupWindow.ViewModel.UsageTracker!.UseTime(clock);
        dupWindow.ViewModel.UsageTracker.Show(images[1].Id);
        dupWindow.ViewModel.SetUsageActive(true);
        clock.Advance(TimeSpan.FromSeconds(30));
        dupWindow.ViewModel.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        // (4) Weitergabe: dieselbe Meldung, die Strg+C, Ziehen nach außen und Exportieren senden
        UsageReport.Shared([first]);
        await usage.FlushAsync().ConfigureAwait(true);
        var rows = Rows();
        var today = UsageScoring.DayNumber(DateOnly.FromDateTime(DateTime.Now));
        Check(rows.Count(r => r.FileId == first) == 1 && rows.Single(r => r.FileId == first) is var a
              && a.Opens == 1 && a.Seconds == 5 && a.Zooms == 1 && a.Shares == 1 && a.Day == today,
            $"Tageszeile des ersten Fotos: {Describe(rows.Where(r => r.FileId == first))}");
        Check(rows.All(r => r.FileId != second), $"Schnelllauf (0,4 s) nicht gezählt: {Describe(rows.Where(r => r.FileId == second))}");
        Check(rows.Any(r => r.FileId == third && r.Opens == 1 && r.Seconds == 3), $"Letzte Ansicht beim Schließen gezählt: {Describe(rows.Where(r => r.FileId == third))}");
        Check(rows.Count == 2, $"Aus „Doppelte“ und im Hintergrund nichts gezählt — nur zwei Tageszeilen: {Describe(rows)}");
        Check(_db.OpenSqliteConnection() is var c && Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('FileUsageDays') WHERE name LIKE '%Utc%' OR name LIKE '%Time%'") == 0,
            "Nur Tagessummen, keine Zeitpunkte einzelner Ansichten");

        // (5) Wiederkehr an zwei früheren Tagen (nachgetragen) → Liebling; Galerie-Filter „Lieblinge“ (hell)
        using (var connection = _db.OpenSqliteConnection())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO FileUsageDays (FileId, Day, Opens, Seconds) VALUES ($f, $d1, 2, 90), ($f, $d2, 1, 40);";
            cmd.Parameters.AddWithValue("$f", first);
            cmd.Parameters.AddWithValue("$d1", today - 3);
            cmd.Parameters.AddWithValue("$d2", today - 9);
            cmd.ExecuteNonQuery();
        }

        var totals = await usage.GetTotalsAsync(first).ConfigureAwait(true);
        Check(totals is not null && UsageScoring.IsLoved(totals) && UsageScoring.Reason(totals) == "oft angesehen",
            $"Gelernt: {UsageScoring.Describe(totals)} → Liebling, Grund „{UsageScoring.Reason(totals)}“");
        _gallery.LovedOnly = true;
        await WaitForGalleryAsync("Filter „Lieblinge“").ConfigureAwait(true);
        Check(_gallery.TotalCount == 1 && _gallery.CurrentPage.Single().Id == first && _gallery.ActiveConditions.Any(x => x.Label.StartsWith("Lieblinge", StringComparison.Ordinal)),
            $"Galerie „Lieblinge“: {_gallery.TotalCount} Datei(en)");
        await _theme.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await SnapshotAsync("32-nutzung-hell").ConfigureAwait(true);

        // Lightbox: Zeile „Nutzung“
        _gallery.OpenInLightboxCommand.Execute(_gallery.CurrentPage.Single());
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var lightbox = Application.Current.Windows.OfType<LightboxWindow>().First().ViewModel;
        await WaitUntilAsync(() => lightbox.FileInfoRows.Any(r => r.Label == "Nutzung"), ShortWait, "Zeile „Nutzung“").ConfigureAwait(true);
        Check(lightbox.FileInfoRows.Single(r => r.Label == "Nutzung").Value.Contains("an 3 Tagen", StringComparison.Ordinal),
            $"Lightbox „Nutzung“: {lightbox.FileInfoRows.Single(r => r.Label == "Nutzung").Value}");
        lightbox.UsageContext = UsageViewContext.None; // diese Kontroll-Ansicht nicht mitzählen
        lightbox.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        // (6) Einstellungen › Storys › „Nutzung lernen“ (dunkel)
        await OpenSettingsSectionAsync(NavigationTags.SettingsStories, "Reiter „Storys“").ConfigureAwait(true);
        await WaitUntilAsync(() => settings.IsLoaded && settings.Summary.Files >= 2, ShortWait, "Karte „Nutzung lernen“ gelesen").ConfigureAwait(true);
        Check(settings.IsEnabled && settings.ForgottenEnabled && settings.Summary.Loved == 1 && settings.HasData,
            $"Karte: {settings.FilesText} Fotos, {settings.DaysText} Tage, {settings.LovedText} Liebling(e), seit {settings.SinceText}");
        if (_settingsPage.FindName("StoriesView") is SettingsStoriesSection section && section.FindName("UsageCard") is FrameworkElement card)
        {
            card.BringIntoView();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            Check(card.IsVisible, "Karte „Nutzung lernen“ sichtbar");
        }

        await _theme.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("32-nutzung-dunkel").ConfigureAwait(true);
        Check(UsageSettingsViewModel.EraseMessage(settings.Summary).Contains("nicht rückgängig", StringComparison.Ordinal), "Rückfrage vor dem Löschen nennt die Folgen");

        // (7) Löschen → leer; aus → nichts mehr gespeichert
        await settings.EraseConfirmedAsync().ConfigureAwait(true);
        Check(Rows().Count == 0 && !settings.HasData && settings.IsEnabled, $"Gelernte Nutzung gelöscht: {settings.StatusText}");
        settings.IsEnabled = false;
        await WaitUntilAsync(() => !usage.IsEnabled, ShortWait, "„Nutzung lernen“ aus").ConfigureAwait(true);
        usage.RecordView(first, TimeSpan.FromSeconds(30), zoomed: false, UsageViewContext.Normal);
        UsageReport.Used([first]);
        await usage.FlushAsync().ConfigureAwait(true);
        Check(Rows().Count == 0, "Ausgeschaltet: nichts gespeichert");
    }

    private static LightboxEntry Entry(Lumora.Gallery.ViewModels.GalleryItem item)
        => new(item.Id, item.SourcePath, item.Name, item.MediaType, item.IsFavorite, item.Rating, item.Size, item.SourceModifiedAtUtc);

    private List<(int FileId, int Day, int Opens, int Seconds, int Zooms, int Shares)> Rows()
    {
        using var connection = _db.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FileId, Day, Opens, Seconds, Zooms, Shares FROM FileUsageDays ORDER BY FileId, Day;";
        var list = new List<(int, int, int, int, int, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)));
        }

        return list;
    }

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using (connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string Describe(IEnumerable<(int FileId, int Day, int Opens, int Seconds, int Zooms, int Shares)> rows)
        => string.Join("; ", rows.Select(r => $"Tag {r.Day}: {r.Opens}× offen, {r.Seconds} s, {r.Zooms}× Zoom, {r.Shares}× weitergegeben")) is { Length: > 0 } text ? text : "keine";

    /// <summary>Simulierte Uhr der Messung (Lightbox), von Hand weiter.</summary>
    private sealed class SmokeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
