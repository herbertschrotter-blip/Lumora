using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Folders;
using Lumora.Folders.Adoption;
using Lumora.Gallery.Tags;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Ordner übernehmen“ (0.2.32, ADR-0034): eigener Eingang als Geschwisterordner der Testbibliothek mit
/// Kette <c>leer\leer2\inhalt</c> und Verzweigung <c>Reise\Tag1|Tag2</c> (+ notiz.txt), Aktualisieren, „Bewertung für alle
/// Dateien“ (Reise → 5 Sterne) und „Tags für alle…“ (inhalt) im Ordnerbaum, dann „In die Bibliothek übernehmen…“ für den
/// ganzen Eingang (Fake-Dialog): Struktur im Bibliotheksordner geprüft (Kette zusammengezogen, Verzweigung erhalten,
/// Nicht-Mediendatei mit), Quelle im Papierkorb, Eingang ausgetragen, FileIds/Bewertung/Tag unverändert, Folders.RootId =
/// Bibliotheksordner; danach Rückgängig über die Galerie (Dateien zurück, Eingang wieder eingetragen, Zielordner weg) und
/// Aufräumen.
/// </summary>
internal sealed partial class SmokeScenario
{
    /// <summary>Eingangs-Ordner des Übernahme-Schritts — Geschwisterordner der Testbibliothek (nie darunter).</summary>
    private string AdoptionInboxDir => Path.Combine(Path.GetDirectoryName(_root) ?? _root, Path.GetFileName(_root) + "-Uebernahme-Eingang");

    private readonly SmokeFolderAdoptionPicker? _adoptionPicker;

    private string AdoptionTargetDir => Path.Combine(_root, "2026", "Smoke-Uebernahme");

    /// <summary>Rückstände eines abgebrochenen Laufs (Eingang und Zielordner) entfernen.</summary>
    private void RemoveAdoptionLeftovers()
    {
        foreach (var dir in new[] { AdoptionInboxDir, AdoptionTargetDir })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                _logger.LogInformation("Rauchtest   Rückstand des Übernahme-Schritts entfernt: {Path}", dir);
            }
        }
    }

    private async Task FolderAdoptionAsync()
    {
        RemoveAdoptionLeftovers();
        var inbox = AdoptionInboxDir;
        var inboxName = Path.GetFileName(inbox);
        var jpgs = Directory.EnumerateFiles(Path.Combine(_root, "Unsortiert"), "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        Check(jpgs.Count == 3, "3 JPGs aus „Unsortiert“ als Material");
        var a = Path.Combine(inbox, "leer", "leer2", "inhalt", "ueb-a.jpg");
        var b = Path.Combine(inbox, "Reise", "Tag1", "ueb-b.jpg");
        var c = Path.Combine(inbox, "Reise", "Tag2", "ueb-c.jpg");
        var note = Path.Combine(inbox, "Reise", "notiz.txt");
        foreach (var (source, dest) in jpgs.Zip(new[] { a, b, c }))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, overwrite: true);
        }

        await File.WriteAllTextAsync(note, "Notiz zur Reise").ConfigureAwait(true);

        // (1) Eingang hinzufügen + Aktualisieren
        await _libraries.AddRootAsync(_libraryId, inbox, LibraryRootRole.Inbox).ConfigureAwait(true);
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheksseite").ConfigureAwait(true);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Outcome: ScanOutcome.Completed, Added: 3 }, $"Aktualisieren mit Übernahme-Eingang: Added={_lastScan?.Added}");
        _mediaCount += 3;
        var ids = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var name = Path.GetFileName(new[] { a, b, c }[i]);
            ids[i] = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = $n", ("$lib", _libraryId), ("$n", name)).ConfigureAwait(true);
        }

        var idList = string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        var archiveRootId = await DbScalarAsync<int>("SELECT Id FROM LibraryRoots WHERE LibraryId = $lib AND Role = 1", ("$lib", _libraryId)).ConfigureAwait(true);

        // (2) Ordnerbaum: „Bewertung für alle Dateien“ (Reise → 5) und „Tags für alle…“ (inhalt)
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && !_folders.IsLoading, ShortWait, "Ordnerbaum").ConfigureAwait(true);
        await WaitUntilAsync(() => FindFolderNode(inbox) is not null, ShortWait, "Eingang im Ordnerbaum").ConfigureAwait(true);
        var inboxNode = FindFolderNode(inbox)!;
        Check(inboxNode.IsInInbox && inboxNode.RoleLabel == "Eingang", $"Eingangs-Wurzel im Baum: Rolle „{inboxNode.RoleLabel}“, übernehmbar");
        var reise = FindFolderNode(Path.Combine(inbox, "Reise"));
        Check(reise is { IsInInbox: true, TotalFileCount: 2 }, $"Ordner „Reise“ mit {reise?.TotalFileCount} Dateien");
        await reise!.RateAllCommand.ExecuteAsync("5").ConfigureAwait(true);
        var rated = await DbScalarAsync<long>($"SELECT COUNT(*) FROM Files WHERE Id IN ({ids[1]},{ids[2]}) AND Rating = 5").ConfigureAwait(true);
        Check(rated == 2 && _folders.ResultMessage is not null && _folders.ResultMessage.Contains("5 Sterne", StringComparison.Ordinal),
            $"Bewertung für alle Dateien: {rated}/2 mit 5 Sternen — „{_folders.ResultMessage}“");
        var tag = await _tags.CreateAsync("Smoke-Uebernahme").ConfigureAwait(true);
        var inhalt = FindFolderNode(Path.Combine(inbox, "leer", "leer2", "inhalt"));
        Check(inhalt is not null, "Ordner „inhalt“ im Baum");
        if (_tagPicker is not null)
        {
            _tagPicker.NextResult = new TagPickResult(new[] { tag.Id }, Array.Empty<int>());
            await _folders.EditFolderTagsCommand.ExecuteAsync(inhalt).ConfigureAwait(true);
        }
        else
        {
            await _tags.AssignAsync(new[] { ids[0] }, new[] { tag.Id }).ConfigureAwait(true);
        }

        var tagged = await DbScalarAsync<long>("SELECT COUNT(*) FROM FileTags WHERE TagId = $t AND FileId = $f", ("$t", tag.Id), ("$f", ids[0])).ConfigureAwait(true);
        Check(tagged == 1, "Tags für alle: ueb-a.jpg trägt das Tag");

        // (3) „In die Bibliothek übernehmen…“ für den ganzen Eingang (Fake-Dialog mit Ziel 2026\Smoke-Uebernahme)
        var picker = _adoptionPicker!;
        picker.NextTarget = AdoptionTargetDir;
        await _folders.AdoptFolderCommand.ExecuteAsync(inboxNode).ConfigureAwait(true);
        Check(_folders.ErrorMessage is null && _folders.ResultWarning is null && _folders.ResultMessage is not null,
            $"Übernahme ohne Fehler: „{_folders.ResultMessage ?? _folders.ResultWarning ?? _folders.ErrorMessage}“");
        var plan = picker.LastPlan;
        Check(picker.LastContext is { IsWholeInbox: true } && plan is { FileCount: 4 }, $"Dialog bekam den ganzen Eingang, Plan mit {plan?.FileCount} Dateien (3 Bilder + notiz.txt)");
        var expectedFolders = new[] { inboxName, $@"{inboxName}\inhalt", $@"{inboxName}\Reise", $@"{inboxName}\Reise\Tag1", $@"{inboxName}\Reise\Tag2" };
        Check(plan!.Folders.Select(f => f.RelativeTargetPath).SequenceEqual(expectedFolders, StringComparer.OrdinalIgnoreCase),
            $"Vorschau: {string.Join(" | ", plan.Folders.Select(f => f.RelativeTargetPath))}");
        Check(plan.CollapsedFolders.Count == 2, $"Kette leer\\leer2 zusammengezogen ({plan.CollapsedFolders.Count} Durchgangsordner)");

        var newA = Path.Combine(AdoptionTargetDir, inboxName, "inhalt", "ueb-a.jpg");
        var newB = Path.Combine(AdoptionTargetDir, inboxName, "Reise", "Tag1", "ueb-b.jpg");
        var newC = Path.Combine(AdoptionTargetDir, inboxName, "Reise", "Tag2", "ueb-c.jpg");
        var newNote = Path.Combine(AdoptionTargetDir, inboxName, "Reise", "notiz.txt");
        Check(new[] { newA, newB, newC, newNote }.All(File.Exists), "Struktur im Bibliotheksordner: inhalt (Kette zusammengezogen), Reise\\Tag1|Tag2, notiz.txt");
        Check(!Directory.Exists(inbox), "Quellordner im Papierkorb");
        var rootsAfter = await DbScalarAsync<long>("SELECT COUNT(*) FROM LibraryRoots WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(rootsAfter == 1, "Eingang ausgetragen (nur noch der Bibliotheksordner)");
        var underArchive = await DbScalarAsync<long>(
            $"SELECT COUNT(*) FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({idList}) AND d.RootId = $r", ("$r", archiveRootId)).ConfigureAwait(true);
        var ratedAfter = await DbScalarAsync<long>($"SELECT COUNT(*) FROM Files WHERE Id IN ({ids[1]},{ids[2]}) AND Rating = 5").ConfigureAwait(true);
        var taggedAfter = await DbScalarAsync<long>("SELECT COUNT(*) FROM FileTags WHERE TagId = $t AND FileId = $f", ("$t", tag.Id), ("$f", ids[0])).ConfigureAwait(true);
        Check(underArchive == 3 && ratedAfter == 2 && taggedAfter == 1, $"DB: dieselben FileIds unter dem Bibliotheksordner ({underArchive}/3), Bewertung ({ratedAfter}/2) und Tag ({taggedAfter}/1) wandern mit");
        var batchType = await DbScalarAsync<long>("SELECT OperationType FROM OperationBatches ORDER BY Id DESC LIMIT 1").ConfigureAwait(true);
        Check(batchType == (long)OperationType.AdoptFolder, $"Ein protokollierter Batch vom Typ AdoptFolder ({batchType})");
        await SnapshotAsync("20-ordnerbaum-uebernommen").ConfigureAwait(true);

        // (4) Rückgängig über die Galerie
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && _gallery.CanUndo, ShortWait, "Rückgängig verfügbar").ConfigureAwait(true);
        await _gallery.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Rückgängig beendet").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null, $"Rückgängig ohne Fehler ({_gallery.StatusText})");
        Check(new[] { a, b, c, note }.All(File.Exists), "Dateien wieder an den alten Pfaden (Ordner neu angelegt)");
        Check(!Directory.Exists(AdoptionTargetDir), "leere Zielordner entfernt");
        var reInboxId = await DbScalarAsync<long>("SELECT COALESCE(MAX(Id), 0) FROM LibraryRoots WHERE LibraryId = $lib AND Role = 0", ("$lib", _libraryId)).ConfigureAwait(true);
        var underInbox = await DbScalarAsync<long>(
            $"SELECT COUNT(*) FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({idList}) AND d.RootId = $r", ("$r", reInboxId)).ConfigureAwait(true);
        Check(reInboxId > 0 && underInbox == 3, $"Eingang wieder eingetragen, FileIds darunter ({underInbox}/3)");

        // (5) Aufräumen: Eingang austragen (nur Katalog), Ordner löschen
        await _libraries.RemoveRootAsync((int)reInboxId).ConfigureAwait(true);
        _mediaCount -= 3;
        Directory.Delete(inbox, recursive: true);
        _folders.DismissResultCommand.Execute(null);
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == _mediaCount, ShortWait, $"Galerie nach dem Aufräumen: {_mediaCount}").ConfigureAwait(true);
    }

    /// <summary>Knoten im Ordnerbaum über den Pfad (klappt die Vorfahren dabei auf).</summary>
    private FolderNode? FindFolderNode(string path)
    {
        FolderNode? Visit(System.Collections.Generic.IEnumerable<FolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsPlaceholder)
                {
                    continue;
                }

                if (PathEquals(node.Path, path))
                {
                    return node;
                }

                if (path.StartsWith(node.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    node.IsExpanded = true;
                    return Visit(node.Children);
                }
            }

            return null;
        }

        return Visit(_folders.Roots);
    }
}

/// <summary>
/// Rauchtest-Ersatz für den Dialog „Ordner in die Bibliothek übernehmen…“: berechnet den Plan wie der Dialog (über
/// <see cref="IFolderAdoptionService.BuildPlan"/>) für das vom Szenario gesetzte Ziel mit Standard-Optionen.
/// </summary>
internal sealed class SmokeFolderAdoptionPicker : IFolderAdoptionPicker
{
    private readonly IFolderAdoptionService _adoption;
    private readonly ILogger<SmokeFolderAdoptionPicker> _logger;

    public SmokeFolderAdoptionPicker(IFolderAdoptionService adoption, ILogger<SmokeFolderAdoptionPicker> logger)
    {
        _adoption = adoption;
        _logger = logger;
    }

    /// <summary>Nächstes Ziel; null = Abbruch.</summary>
    public string? NextTarget { get; set; }

    public FolderAdoptionOptions Options { get; set; } = FolderAdoptionOptions.Default;

    public FolderAdoptionContext? LastContext { get; private set; }

    public FolderAdoptionPlan? LastPlan { get; private set; }

    public Task<FolderAdoptionChoice?> PickAsync(FolderAdoptionContext context, AdoptionSourceSnapshot snapshot)
    {
        LastContext = context;
        var target = NextTarget;
        NextTarget = null;
        if (target is null)
        {
            return Task.FromResult<FolderAdoptionChoice?>(null);
        }

        LastPlan = _adoption.BuildPlan(snapshot, target, Options.CollapseEmptyChains);
        _logger.LogInformation("Rauchtest-Übernahmedialog: {Source} → {Target}, {Files} Dateien, {Folders} Ordner", context.SourceFolder, target, LastPlan.FileCount, LastPlan.Folders.Count);
        return Task.FromResult<FolderAdoptionChoice?>(new FolderAdoptionChoice(LastPlan, Options));
    }
}
