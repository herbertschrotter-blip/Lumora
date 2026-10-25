using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Gallery.Places;
using Lumora.Places;
using Lumora.Services.Library;
using Lumora.Services.Places;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Bibliotheks-Trennung“ (0.2.51, ADR-0048): zweite Bibliothek anlegen (Geschwisterordner mit zwei
/// kopierten Fotos), Tag und Person in A → in B unsichtbar (Tag-Liste, Galerie-Suche, Personen-Flyout, Personenliste);
/// verknüpfen → in B sichtbar, Umschalter bietet „Alle verknüpften“, Galerie zeigt die Summe, Ordnerbaum je Bibliothek
/// ein Kopfknoten; lösen → wieder getrennt, Begriffe in beiden vorhanden. Am Ende ist A wieder aktiv und B entfernt.
/// Seit dem Zusammenbau 0.2.56 auch Motive und Orte: Motiv-Begriff in A (in B weder in den Einstellungen noch im
/// Motiv-Flyout), Ort an einem Foto aus B (in A weder auf der Karte noch im Ort-Flyout); verknüpft bzw. „Alle verknüpften“
/// sichtbar; nach dem Lösen der Begriff in beiden. Der Ort wird vor dem Entfernen von B zurückgenommen.
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string ScopeTagName = "Smoke-NurA";
    private const string ScopePersonName = "Smoke Trennung";
    private const string ScopeMotifName = "Smoke-Motiv-NurA";

    /// <summary>Ort für das Foto aus B: weit weg von allen Testfotos (Reykjavík).</summary>
    private const double ScopePlaceLat = 64.1466;
    private const double ScopePlaceLon = -21.9426;

    private ILibraryLinkService? _linking;
    private ILibraryScope? _libraryScope;
    private IGeoTagService? _scopeGeoTags;
    private IGeoTagUndoService? _scopeGeoUndo;
    private IGeoQueryService? _scopeGeo;
    private IPlacesScope? _scopePlaces;
    private IReverseGeocoder? _scopeGeocoder;

    private void InitLibraryScoping(IServiceProvider services)
    {
        _linking = services.GetService<ILibraryLinkService>();
        _libraryScope = services.GetService<ILibraryScope>();
        _scopeGeoTags = services.GetService<IGeoTagService>();
        _scopeGeoUndo = services.GetService<IGeoTagUndoService>();
        _scopeGeo = services.GetService<IGeoQueryService>();
        _scopePlaces = services.GetService<IPlacesScope>();
        _scopeGeocoder = services.GetService<IReverseGeocoder>();
    }

    /// <summary>Verortete Dateien im Bereich der Karte (genau der Lesepfad der Seite „Orte“).</summary>
    private async Task<IReadOnlyList<int>> MapFileIdsAsync()
    {
        var libraries = await _scopePlaces!.GetLibraryIdsAsync(default).ConfigureAwait(true);
        return (await _scopeGeo!.GetPointsAsync(libraries, null, default).ConfigureAwait(true)).Select(p => p.FileId).ToList();
    }

    /// <summary>Ort-Flyout der Galerie frisch laden und nach dem Ort suchen.</summary>
    private async Task<bool> PlaceFlyoutHasAsync(int placeId)
    {
        await _gallery.LoadPlaceFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        return _gallery.PlaceFilterEntries.Any(e => e.Kind == PlaceFilterEntryKind.Place && e.PlaceIds.Contains(placeId));
    }

    /// <summary>Zweite Bibliothek: Geschwisterordner der Testbibliothek (nie darunter).</summary>
    private string SecondLibraryDir => Path.Combine(Path.GetDirectoryName(_root) ?? _root, Path.GetFileName(_root) + "-Zweite");

    private async Task LibraryScopingAsync()
    {
        Check(_linking is not null && _libraryScope is not null && _recognition is not null, "Bereichsdienst, Verknüpfen und Personen angebunden");
        Check(_motifTerms is not null && _scopeGeoTags is not null && _scopeGeoUndo is not null && _scopeGeo is not null && _scopePlaces is not null && _scopeGeocoder is not null,
            "Motive und Orte angebunden");
        var placeId = _scopeGeocoder!.Nearest(ScopePlaceLat, ScopePlaceLon)?.PlaceId ?? 0;
        Check(placeId != 0, "Ort für B gefunden (GeoNames)");
        await _libraries.SetActiveAsync(_libraryId).ConfigureAwait(true);
        await WaitForGalleryAsync("A aktiv").ConfigureAwait(true);
        var countA = _gallery.TotalCount;

        // (1) Zweite Bibliothek: zwei Fotos aus A kopieren, anlegen + scannen (wird dabei aktiv)
        Directory.CreateDirectory(SecondLibraryDir);
        foreach (var source in MediaFiles().Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).Take(2))
        {
            File.Copy(source, Path.Combine(SecondLibraryDir, "zweite-" + Path.GetFileName(source)), overwrite: true);
        }

        await _library.RunSmokeScanAsync(SecondLibraryDir).ConfigureAwait(true);
        var second = (await _libraries.GetAllAsync().ConfigureAwait(true))
            .Single(l => string.Equals(LibraryService.NormalizeRootPath(l.RootPath), LibraryService.NormalizeRootPath(SecondLibraryDir), StringComparison.OrdinalIgnoreCase));
        var countB = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib", ("$lib", second.Id)).ConfigureAwait(true);
        Check(countB == 2, $"zweite Bibliothek mit {countB} Dateien");
        Check((await _libraryScope!.GetForLibraryAsync(second.Id).ConfigureAwait(true)).GroupId
              != (await _libraryScope.GetForLibraryAsync(_libraryId).ConfigureAwait(true)).GroupId, "eigene Gruppe je Bibliothek");

        // (2) In A: Tag an einer Datei, Person
        await _libraries.SetActiveAsync(_libraryId).ConfigureAwait(true);
        await WaitForGalleryAsync("zurück zu A").ConfigureAwait(true);
        var tag = (await _tags.GetAllAsync().ConfigureAwait(true)).FirstOrDefault(t => t.Name == ScopeTagName)
                  ?? await _tags.CreateAsync(ScopeTagName).ConfigureAwait(true);
        var fileA = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib ORDER BY Id LIMIT 1", ("$lib", _libraryId)).ConfigureAwait(true);
        await _tags.AssignAsync([fileA], [tag.Id]).ConfigureAwait(true);
        var personId = await _recognition!.FindPersonByNameAsync(ScopePersonName).ConfigureAwait(true)
                       ?? await _recognition.CreatePersonAsync(ScopePersonName).ConfigureAwait(true);

        // (2b) Motiv-Begriff in A, Ort an einem Foto aus B (0.2.56)
        if (!(await _motifTerms!.GetTermsAsync(_libraryId).ConfigureAwait(true)).Any(t => t.Name == ScopeMotifName))
        {
            await _motifTerms.AddTermAsync(_libraryId, ScopeMotifName).ConfigureAwait(true);
        }

        await WaitUntilAsync(() => _gallery.MotifFilterEntries.Any(e => e.Name == ScopeMotifName), ShortWait, "Motiv-Flyout in A mit dem neuen Begriff").ConfigureAwait(true);
        var fileB = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib ORDER BY Id LIMIT 1", ("$lib", second.Id)).ConfigureAwait(true);
        var located = await _scopeGeoTags!.SetLocationAsync([fileB], ScopePlaceLat, ScopePlaceLon, keepModifiedTime: true, default).ConfigureAwait(true);
        Check(located.Written + located.CatalogOnly == 1, $"Foto aus B verortet ({located.Written} geschrieben, {located.CatalogOnly} nur Katalog, {located.Failed} Fehler)");
        var geoBatch = await _scopeGeoUndo!.GetUndoableBatchIdAsync().ConfigureAwait(true);
        Check(!(await MapFileIdsAsync().ConfigureAwait(true)).Contains(fileB), "Karte in A ohne den Ort aus B");
        Check(!await PlaceFlyoutHasAsync(placeId).ConfigureAwait(true), "Ort-Flyout in A ohne den Ort aus B");

        // (3) In B: nichts davon sichtbar
        await _libraries.SetActiveAsync(second.Id).ConfigureAwait(true);
        await WaitForGalleryAsync("B aktiv").ConfigureAwait(true);
        Check(_gallery.TotalCount == countB, $"Galerie zeigt nur B ({_gallery.TotalCount})");
        Check(!(await _tags.GetAllAsync().ConfigureAwait(true)).Any(t => t.Name == ScopeTagName), "Tag aus A in B unsichtbar (Tag-Liste)");
        await WaitUntilAsync(() => !_gallery.TagFilterEntries.Any(e => e.Tag.Name == ScopeTagName), ShortWait, "Tag-Flyout ohne Tag aus A").ConfigureAwait(true);
        Check(!_gallery.PersonFilterEntries.Any(e => e.Name == ScopePersonName), "Personen-Flyout ohne Person aus A");
        Check(!(await _recognition.GetPersonsAsync(default).ConfigureAwait(true)).Any(p => p.Name == ScopePersonName), "Personenliste ohne Person aus A");
        Check(await _recognition.FindPersonByNameAsync(ScopePersonName).ConfigureAwait(true) is null, "Person aus A in B nicht auffindbar");
        Check(!(await _motifTerms.GetTermsAsync(second.Id).ConfigureAwait(true)).Any(t => t.Name == ScopeMotifName), "Motiv-Begriff aus A in B unsichtbar (Einstellungen › Motive)");
        await WaitUntilAsync(() => _gallery.MotifFilterEntries.Count > 0, ShortWait, "Motiv-Flyout in B mit der eigenen Start-Liste").ConfigureAwait(true);
        Check(!_gallery.MotifFilterEntries.Any(e => e.Name == ScopeMotifName), $"Motiv-Flyout in B ohne Begriff aus A ({_gallery.MotifFilterEntries.Count} Begriffe)");
        Check((await MapFileIdsAsync().ConfigureAwait(true)).Contains(fileB) && await PlaceFlyoutHasAsync(placeId).ConfigureAwait(true), "B zeigt seinen Ort (Karte, Ort-Flyout)");
        _gallery.SearchTerm = ScopeTagName;
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche nach dem Tag aus A in B").ConfigureAwait(true);
        Check(_gallery.TotalCount == 0, $"Suche nach „{ScopeTagName}“ in B: {_gallery.TotalCount} Treffer (erwartet 0)");
        await _gallery.ClearSearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche geleert").ConfigureAwait(true);

        // (4) Verknüpfen → in B sichtbar; „Alle verknüpften“ zeigt die Summe, Ordnerbaum je Bibliothek ein Kopfknoten
        await _linking!.LinkAsync(_libraryId, second.Id).ConfigureAwait(true);
        await WaitForGalleryAsync("nach dem Verknüpfen").ConfigureAwait(true);
        Check((await _tags.GetAllAsync().ConfigureAwait(true)).Any(t => t.Name == ScopeTagName), "verknüpft: Tag aus A in B sichtbar");
        Check(await _recognition.FindPersonByNameAsync(ScopePersonName).ConfigureAwait(true) == personId, "verknüpft: Person aus A in B sichtbar");
        Check((await _motifTerms.GetTermsAsync(second.Id).ConfigureAwait(true)).Any(t => t.Name == ScopeMotifName), "verknüpft: Motiv-Begriff aus A in B sichtbar");
        await WaitUntilAsync(() => _gallery.MotifFilterEntries.Any(e => e.Name == ScopeMotifName), ShortWait, "verknüpft: Motiv-Flyout in B mit Begriff aus A").ConfigureAwait(true);
        await _switcher.RefreshAsync().ConfigureAwait(true);
        var entry = _switcher.LinkedGroups.SingleOrDefault();
        Check(entry is not null && entry.FileCount == countA + countB, $"Umschalter: „{entry?.Header}“ ({entry?.DetailText})");
        await _switcher.ShowAllLinkedCommand.ExecuteAsync(entry).ConfigureAwait(true);
        await WaitForGalleryAsync("Alle verknüpften").ConfigureAwait(true);
        Check(_gallery.IsAllLinkedView && _gallery.TotalCount == countA + countB,
            $"Alle verknüpften: {_gallery.TotalCount} Dateien = {countA} + {countB}");
        Check(!_gallery.MoveSelectionCommand.CanExecute(null) && !_gallery.ArchiveSelectionCommand.CanExecute(null),
            "Verschieben/„In die Bibliothek“ in „Alle verknüpften“ gesperrt");
        Check(_switcher.ActiveName.StartsWith("Alle verknüpften", StringComparison.Ordinal), $"Umschalter zeigt „{_switcher.ActiveName}“");
        Check((await _scopePlaces!.GetLibraryIdsAsync(default).ConfigureAwait(true)).Count == 2 && (await MapFileIdsAsync().ConfigureAwait(true)).Contains(fileB),
            "Alle verknüpften: Karte mit beiden Bibliotheken und dem Ort aus B");
        Check(await PlaceFlyoutHasAsync(placeId).ConfigureAwait(true) && _gallery.MotifFilterEntries.Any(e => e.Name == ScopeMotifName),
            "Alle verknüpften: Ort-Flyout mit dem Ort aus B, Motiv-Flyout mit dem Begriff");
        await SnapshotAsync("39-alle-verknuepften-galerie").ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && !_folders.IsLoading && _folders.Roots.Count == 2,
            ShortWait, "Ordnerbaum mit zwei Bibliotheken").ConfigureAwait(true);
        Check(_folders.Roots.All(r => r.IsLibraryHeader) && !_folders.AddInboxCommand.CanExecute(null),
            $"Ordnerbaum: {string.Join(", ", _folders.Roots.Select(r => r.Name))} (Eingang hinzufügen gesperrt)");
        await SnapshotAsync("40-alle-verknuepften-ordner").ConfigureAwait(true);
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheken & Eingänge").ConfigureAwait(true);
        await _library.LoadCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(_library.Libraries.Where(l => l.Id == _libraryId || l.Id == second.Id).All(l => l.IsLinked), "Einstellungen zeigen die Verknüpfung");
        await Task.Delay(300).ConfigureAwait(true);
        await SnapshotAsync("41-einstellungen-verknuepft").ConfigureAwait(true);

        // (5) Lösen → wieder getrennt, Begriffe in beiden vorhanden
        await _linking.UnlinkAsync(second.Id).ConfigureAwait(true);
        var tagCopies = await DbScalarAsync<int>("SELECT COUNT(*) FROM Tags WHERE Name = $n", ("$n", ScopeTagName)).ConfigureAwait(true);
        var personCopies = await DbScalarAsync<int>("SELECT COUNT(*) FROM Persons WHERE Name = $n", ("$n", ScopePersonName)).ConfigureAwait(true);
        var motifCopies = await DbScalarAsync<int>("SELECT COUNT(*) FROM MotifTerms WHERE Name = $n", ("$n", ScopeMotifName)).ConfigureAwait(true);
        Check(tagCopies == 2 && personCopies == 2 && motifCopies == 2, $"gelöst: Tag {tagCopies}×, Person {personCopies}×, Motiv-Begriff {motifCopies}× (je Bibliothek)");
        await _libraries.SetActiveAsync(_libraryId).ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitForGalleryAsync("A nach dem Lösen").ConfigureAwait(true);
        // Das Lösen meldet B (die aktive Bibliothek der gemeinsamen Ansicht) evtl. erst nach dem Wechsel auf A: auf den Bereich A warten
        await WaitUntilAsync(() => !_gallery.HasPendingWork && _gallery.ScopeLibraryIdsForSmoke.SequenceEqual([_libraryId]), ShortWait,
            "Galerie-Bereich wieder A").ConfigureAwait(true);
        Check(!_gallery.IsAllLinkedView && _gallery.TotalCount == countA, $"A wieder allein: {_gallery.TotalCount} Dateien");
        await _switcher.RefreshAsync().ConfigureAwait(true);
        Check(_switcher.LinkedGroups.Count == 0, "Umschalter ohne „Alle verknüpften“");
        Check(!(await MapFileIdsAsync().ConfigureAwait(true)).Contains(fileB), "gelöst: Karte in A wieder ohne den Ort aus B");
        Check(!await PlaceFlyoutHasAsync(placeId).ConfigureAwait(true),
            $"gelöst: Ort-Flyout in A wieder ohne den Ort aus B (Bereich der Galerie: {string.Join(",", _gallery.ScopeLibraryIdsForSmoke)})");

        // (6) Aufräumen: Ort in B zurücknehmen, B entfernen (Gruppe samt Kopien weg), Tag/Person/Motiv-Begriff in A löschen
        if (geoBatch is { } batch && await _scopeGeoUndo.GetUndoableBatchIdAsync().ConfigureAwait(true) == batch)
        {
            await _scopeGeoUndo.UndoAsync(batch).ConfigureAwait(true);
        }

        foreach (var term in (await _motifTerms.GetTermsAsync(_libraryId).ConfigureAwait(true)).Where(t => t.Name == ScopeMotifName))
        {
            await _motifTerms.DeleteTermAsync(term.Id).ConfigureAwait(true);
        }

        await _libraries.DeleteAsync(second.Id).ConfigureAwait(true);
        await _tags.DeleteAsync(tag.Id).ConfigureAwait(true);
        await _recognition.DeletePersonAsync(personId).ConfigureAwait(true);
        TryDeleteDirectory(SecondLibraryDir);
        Check(await DbScalarAsync<int>("SELECT COUNT(*) FROM Tags WHERE Name = $n", ("$n", ScopeTagName)).ConfigureAwait(true) == 0
              && await DbScalarAsync<int>("SELECT COUNT(*) FROM MotifTerms WHERE Name = $n", ("$n", ScopeMotifName)).ConfigureAwait(true) == 0, "aufgeräumt");
        Check((await _libraries.GetActiveAsync().ConfigureAwait(true))?.Id == _libraryId, "A ist aktiv");
        await WaitForGalleryAsync("Galerie A").ConfigureAwait(true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rest bleibt liegen (Vorschau-Dekodierung hält eine Datei) — stört den nächsten Lauf nicht: Fotos werden überschrieben
        }
    }
}
