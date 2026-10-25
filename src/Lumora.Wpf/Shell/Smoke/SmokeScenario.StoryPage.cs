using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Services.Stories;
using Lumora.Settings;
using Lumora.Stories;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Story-Seite“ (ADR-0060, nach „Diashow“, vor „Versteckte Bibliothek“): Mindestgröße 5 und Erkennung
/// (wie im Schritt „Storys“), Navigation „Storys“ öffnen, die Übersicht zeigt die erkannten Storys (Zähler wie der Dienst),
/// Statusleiste „Storys aktuell“, eine Story öffnen (gesehen, Tagesabschnitte mit Collage-Kacheln), Highlights ↔ Alle Fotos,
/// Anheften (steht danach unter „Angeheftet“), Diashow starten (Highlights) + Esc, zurück zur Übersicht. Screenshots der
/// Übersicht und der Story hell/dunkel. Danach aufgeräumt (Anheften gelöst, Einstellungen zurück, Farbschema System).
/// </summary>
internal sealed partial class SmokeScenario
{
    private IServiceProvider? _storyPageServices;

    /// <summary>Die Seite wird erst im Schritt geholt (wie beim ersten Öffnen in der App), nicht schon beim Start des Rauchtests.</summary>
    private void InitStoryPageSmoke(IServiceProvider services) => _storyPageServices = services;

    private async Task StoryPageAsync()
    {
        var storiesPage = _storyPageServices?.GetService<StoriesViewModel>();
        Check(storiesPage is not null && _stories is not null && _storyIndex is not null && _storySettings is not null && _slideshow is not null,
            "Story-Seite, Story-Dienste und Diashow registriert");
        var vm = storiesPage!;
        var settingsBefore = await _storySettings!.LoadAsync().ConfigureAwait(true);
        int? pinned = null;
        try
        {
            await _storySettings.SaveAsync(settingsBefore with { MinFiles = 5 }).ConfigureAwait(true);
            await _storyIndex!.RunAllAsync().ConfigureAwait(true);
            var overview = await _stories!.GetOverviewAsync().ConfigureAwait(true);
            Check(overview.Counts.All >= 1, $"Dienst: {overview.Counts.All} Storys");

            // (1) Navigation „Storys“ → Übersicht
            _navigation.NavigateTo(NavigationTags.Stories);
            await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Stories, ShortWait, "Seite „Storys“ aktiv").ConfigureAwait(true);
            await vm.LoadAsync().ConfigureAwait(true);
            await WaitUntilAsync(() => !vm.IsLoading && vm.Rows.Count > 0, ShortWait, "Übersicht geladen").ConfigureAwait(true);
            var cards = vm.Rows.OfType<OverviewCardRow>().SelectMany(r => r.Cards).Select(c => c.Id).Distinct().ToList();
            var expected = overview.Pinned.Concat(overview.EventsAndTrips).Concat(overview.Persons).Concat(overview.Places).Concat(overview.Custom).Select(c => c.Id).Distinct().ToList();
            Check(cards.Count >= 1 && cards.All(expected.Contains) && (overview.EventsAndTrips.Count == 0 || cards.Contains(overview.EventsAndTrips[0].Id)),
                $"Übersicht zeigt die erkannten Storys: {cards.Count} Karten, {vm.KindOptions[0].Count} gesamt ({vm.SummaryText})");
            Check(vm.KindOptions.Single(o => o.Filter == StoryKindFilter.All).Count == overview.Counts.All, "Zähler „Alle“ wie der Dienst");
            Check(StoriesNavigationBadge.Shared.NewCount == overview.NewCount, $"Zähler „neu“ an der Navigation: {StoriesNavigationBadge.Shared.Text}");
            await WaitUntilAsync(() => _statusBar.StoryIndexText is not null, ShortWait, "Statusleiste Story-Erkennung").ConfigureAwait(true);
            Check(_statusBar.StoryIndexText == "Storys aktuell", $"Statusleiste: {_statusBar.StoryIndexText}");
            _settings.IsThemeLight = true;
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
            await SnapshotAsync("62-storys-uebersicht-hell").ConfigureAwait(true);
            _settings.IsThemeDark = true;
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
            await SnapshotAsync("63-storys-uebersicht-dunkel").ConfigureAwait(true);

            // (2) Story öffnen: größte automatische (Ereignis/Reise/Person/Ort), sonst die erste
            var first = overview.EventsAndTrips.Concat(overview.Persons).Concat(overview.Places).Concat(overview.Custom)
                .OrderByDescending(c => c.FileCount).First();
            await vm.OpenStoryAsync(first.Id).ConfigureAwait(true);
            var story = vm.CurrentStory;
            await WaitUntilAsync(() => story is { IsLoading: false, Details: not null }, ShortWait, "Story geladen").ConfigureAwait(true);
            Check(vm.IsStoryOpen && story!.Rows.OfType<StoryDayHeaderRow>().Any() && story.Rows.OfType<StoryTilesRow>().Any(),
                $"Story „{story!.Title}“: {story.Days.Count} Abschnitte, {story.HighlightCount} von {story.AllCount} als Highlights");
            Check((await _stories.GetAsync(first.Id).ConfigureAwait(true))?.IsSeen == true, "Öffnen markiert als gesehen");
            await SnapshotAsync("64-story-dunkel").ConfigureAwait(true);
            _settings.IsThemeLight = true;
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
            await SnapshotAsync("65-story-hell").ConfigureAwait(true);

            // (3) Highlights ↔ Alle Fotos
            story.ShowAllPhotos();
            var dense = story.Rows.OfType<StoryTilesRow>().ToList();
            Check(dense.Count > 0 && dense.All(r => r.IsDense) && dense.Sum(r => r.Tiles.Count) == story.AllCount,
                $"Alle Fotos: {dense.Sum(r => r.Tiles.Count)} Kacheln in {dense.Count} Reihen");
            await SnapshotAsync("66-story-alle-fotos-hell").ConfigureAwait(true);
            story.ShowHighlights();
            Check(story.Rows.OfType<StoryTilesRow>().All(r => !r.IsDense), "zurück zu den Highlights");

            // (4) Anheften
            if (!story.IsPinned)
            {
                await story.TogglePinAsync().ConfigureAwait(true);
                pinned = first.Id;
            }

            Check(story.IsPinned && (await _stories.GetAsync(first.Id).ConfigureAwait(true))?.IsPinned == true, "angeheftet");

            // (5) Diashow mit den Highlights, Esc
            story.StartSlideshow();
            await WaitUntilAsync(() => _slideshow!.Window is { IsLoaded: true } w && w.ViewModel.CurrentSlide is not null, ShortWait, "Diashow aus der Story").ConfigureAwait(true);
            var show = _slideshow!.Window!;
            Check(show.ViewModel.Count == story.SlideshowFileIds.Count, $"Diashow: {show.ViewModel.PositionText} (Highlights der Story)");
            Check(show.HandleKey(Key.Escape), "Esc behandelt");
            await WaitUntilAsync(() => !_slideshow.IsOpen, ShortWait, "Diashow geschlossen").ConfigureAwait(true);

            // (6) zurück zur Übersicht: angeheftet steht oben
            story.Back();
            Check(!vm.IsStoryOpen, "zurück zur Übersicht");
            // Auf den echten Zustand warten (0.4.41, Issue #20): Das Anheften meldet StoriesChangedMessage, die Seite lädt
            // gebündelt neu — überholt das unser LoadAsync, kehrt es ohne Übernahme zurück („ein neuerer Durchgang läuft“).
            bool PinnedShown()
            {
                var rows = vm.Rows.ToList();
                var section = rows.FindIndex(r => r is OverviewSectionRow { Title: "Angeheftet" });
                return section >= 0 && rows.Skip(section + 1).OfType<OverviewCardRow>().FirstOrDefault()?.Cards.Any(c => c.Id == first.Id) == true;
            }

            await vm.LoadAsync().ConfigureAwait(true);
            try
            {
                await WaitUntilAsync(() => !vm.IsLoading && PinnedShown(), ShortWait, "Übersicht mit der angehefteten Story").ConfigureAwait(true);
            }
            catch (SmokeCheckException)
            {
                // Meldung mit Befund unten
            }

            var sections = string.Join(", ", vm.Rows.OfType<OverviewSectionRow>().Select(r => r.Title));
            Check(PinnedShown(), $"Übersicht: Story unter „Angeheftet“ (Abschnitte: {sections}; in der DB angeheftet: {(await _stories.GetAsync(first.Id).ConfigureAwait(true))?.IsPinned})");
        }
        finally
        {
            _slideshow?.Dispose();
            vm.CloseStory();
            if (pinned is { } id)
            {
                await _stories!.SetPinnedAsync(id, false).ConfigureAwait(true);
            }

            await _storySettings.SaveAsync(settingsBefore).ConfigureAwait(true);
            _settings.IsThemeSystem = true;
            await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);
        }
    }
}
