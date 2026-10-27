using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Duplicates;
using Lumora.Services.Duplicates;
using Lumora.Services.Scanning;
using Lumora.Settings;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Doppelte“ (ADR-0053, vor „Versteckte Bibliothek“): zu einem im Schritt erzeugten „Foto“ eine
/// byte-gleiche Kopie und eine verkleinerte, neu gespeicherte Kopie anlegen, Incremental-Scan, Hintergrund-Index →
/// gleiche Prüfsumme bzw. pHash-Abstand ≤ Schwelle; Galerie-Filter „Doppelte“; Seite „Doppelte“ exakt (Gruppe, Vorschlag)
/// und ähnlich (verkleinerte Kopie, Vorschlag höhere Auflösung), Screenshots hell/dunkel; „Übrige in den Papierkorb“ mit
/// Übertragung der Bewertung, Datei im Papierkorb, Katalogzeile weg; „Rückgängig“ → Datei mit derselben FileId und
/// Bewertung zurück, Übertragung zurückgenommen. Zum Schluss alle drei über die Datei-Operationen in den Papierkorb (auch nach einem Fehlschlag).
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string DuplicateOriginalName = "smoke-original.jpg";
    private const string DuplicateCopyName = "smoke-kopie.jpg";
    private const string DuplicateSmallName = "smoke-verkleinert.jpg";

    private DuplicatesViewModel? _duplicatesPage;
    private IDuplicateIndexService? _duplicateIndex;
    private IChecksumBackfillService? _checksumBackfill;

    private void InitDuplicates(IServiceProvider services)
    {
        _duplicatesPage = services.GetService<DuplicatesViewModel>();
        _duplicateIndex = services.GetService<IDuplicateIndexService>();
        _checksumBackfill = services.GetService<IChecksumBackfillService>(); // Schritt „App-Verbindung“: Prüfsummen im Leerlauf
    }

    private async Task DuplicatesAsync()
    {
        Check(_duplicatesPage is not null && _duplicateIndex is not null, "Seite und Index der Doppelten registriert");
        var vm = _duplicatesPage!;

        // (1) Vorlage: ein im Schritt erzeugtes „Foto“ mit Formen (die Bilder der Testbibliothek sind teils einfarbig),
        //     dazu eine byte-gleiche und eine verkleinerte, neu gespeicherte Kopie
        var dir = Path.Combine(_root, "Doppelte");
        Directory.CreateDirectory(dir);
        var originalPath = Path.Combine(dir, DuplicateOriginalName);
        var copyPath = Path.Combine(dir, DuplicateCopyName);
        var smallPath = Path.Combine(dir, DuplicateSmallName);
        WriteSceneJpeg(originalPath);
        File.Copy(originalPath, copyPath, overwrite: true);
        WriteDownscaledJpeg(originalPath, smallPath);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        try
        {
            await DuplicatesCoreAsync(vm, originalPath, copyPath).ConfigureAwait(true);
        }
        finally
        {
            // aufräumen: die drei Dateien über die Datei-Operationen in den Papierkorb (auch nach einem Fehlschlag)
            var ids = (await DbRowsAsync(
                "SELECT Id FROM Files WHERE LibraryId = $lib AND Name IN ($a, $b, $c)",
                ("$lib", _libraryId), ("$a", DuplicateOriginalName), ("$b", DuplicateCopyName), ("$c", DuplicateSmallName)).ConfigureAwait(true))
                .Select(r => int.Parse(r, System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
            if (ids.Count > 0)
            {
                await _fileOps.RecycleAsync(ids).ConfigureAwait(true);
            }

            foreach (var group in vm.Groups)
            {
                group.IsIncluded = true;
            }

            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
    }

    private async Task DuplicatesCoreAsync(DuplicatesViewModel vm, string originalPath, string copyPath)
    {
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 3 }, $"Original und Kopien aufgenommen (Added={_lastScan?.Added})");
        var originalId = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = $n", ("$lib", _libraryId), ("$n", DuplicateOriginalName)).ConfigureAwait(true);
        var copyId = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = $n", ("$lib", _libraryId), ("$n", DuplicateCopyName)).ConfigureAwait(true);
        var smallId = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = $n", ("$lib", _libraryId), ("$n", DuplicateSmallName)).ConfigureAwait(true);

        // (2) Hintergrund-Index: Prüfsummen gleich, Bild-Fingerabdruck nah
        await _duplicateIndex!.RunPassAsync().ConfigureAwait(true);
        var hashes = await DbRowsAsync("SELECT hex(ContentHash) FROM FileFingerprints WHERE FileId IN ($a, $b)", ("$a", originalId), ("$b", copyId)).ConfigureAwait(true);
        Check(hashes.Count == 2 && hashes[0] == hashes[1] && hashes[0].Length == 64, $"gleiche SHA-256 für Original und Kopie ({hashes.Count} Zeilen)");
        var images = await DbRowsAsync("SELECT ImageHash, ImageLowDetail FROM FileFingerprints WHERE FileId IN ($a, $b) ORDER BY FileId", ("$a", originalId), ("$b", smallId)).ConfigureAwait(true);
        Check(images.Count == 2 && images.All(r => r.EndsWith("| 0", StringComparison.Ordinal)), $"Bild-Fingerabdrücke: {string.Join(" / ", images)}");
        var distance = PerceptualHash.Distance(
            PerceptualHash.FromStored(long.Parse(images[0].Split(" | ")[0], System.Globalization.CultureInfo.InvariantCulture)),
            PerceptualHash.FromStored(long.Parse(images[1].Split(" | ")[0], System.Globalization.CultureInfo.InvariantCulture)));
        Check(distance <= DuplicateSettings.DefaultSimilarThreshold, $"verkleinerte Kopie nah am Original (Abstand {distance})");

        // (3) Galerie-Filter „Doppelte“
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie aktiv").ConfigureAwait(true);
        _gallery.DuplicatesOnly = true;
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.ActiveConditions.Any(c => c.Label == "Doppelte (exakt gleich)"), ShortWait, "Filter „Doppelte“ aktiv").ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount >= 2, ShortWait, "Galerie zeigt die Doppelten").ConfigureAwait(true);
        var expected = await DbScalarAsync<int>(
            "SELECT COUNT(*) FROM Files f WHERE f.LibraryId = $lib AND f.Missing = 0 AND " + DuplicateSql.HasExactDuplicate("f", [_libraryId]),
            ("$lib", _libraryId)).ConfigureAwait(true);
        Check(_gallery.TotalCount == expected && _gallery.CurrentPage.Any(i => i.Id == copyId) && _gallery.CurrentPage.Any(i => i.Id == originalId),
            $"Galerie „nur Doppelte“: {_gallery.TotalCount} Dateien (DB {expected}), Original und Kopie dabei");
        _gallery.DuplicatesOnly = false;
        await WaitUntilAsync(() => !_gallery.IsLoading && !_gallery.HasActiveFilters, ShortWait, "Filter aufgehoben").ConfigureAwait(true);

        // (4) Seite „Doppelte“: exakt gleich
        _navigation.NavigateTo(NavigationTags.Duplicates);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Duplicates, ShortWait, "Seite „Doppelte“ aktiv").ConfigureAwait(true);
        vm.IsExactMode = true;
        vm.InboxOnly = false;
        await vm.LoadAsync().ConfigureAwait(true);
        var exact = vm.Groups.FirstOrDefault(g => g.Members.Any(m => m.FileId == copyId));
        Check(exact is not null && exact.Members.Any(m => m.FileId == originalId) && exact.Members.Count == 2, $"Gruppe exakt gleich: {exact?.HeaderText}");
        Check(exact!.Keeper!.FileId == Math.Min(originalId, copyId) && exact.ReasonText.EndsWith("gleichwertig — die länger im Katalog", StringComparison.Ordinal),
            $"Vorschlag behalten bei gleichwertigen Dateien: die ältere FileId ({exact.ReasonText})");
        await SnapshotAsync("48-doppelte-exakt-hell").ConfigureAwait(true);
        _settings.IsThemeDark = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);

        // (5) ähnlich: verkleinerte Kopie in derselben Gruppe, Vorschlag höhere Auflösung (ohne Bildmaße: größere Datei)
        vm.IsSimilarMode = true;
        await vm.LoadAsync().ConfigureAwait(true);
        var similar = vm.Groups.FirstOrDefault(g => g.Members.Any(m => m.FileId == smallId));
        Check(similar is not null && similar.Members.Any(m => m.FileId == originalId), $"Gruppe ähnlich: {similar?.HeaderText}");
        Check(similar!.Keeper!.FileId != smallId && (similar.ReasonText.Contains("höhere Auflösung", StringComparison.Ordinal) || similar.ReasonText.Contains("größere Datei", StringComparison.Ordinal)), $"verkleinerte Kopie wird nicht behalten ({similar.ReasonText})");
        await SnapshotAsync("49-doppelte-aehnlich-dunkel").ConfigureAwait(true);
        _settings.IsThemeSystem = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);

        // (6) Übrige in den Papierkorb (nur diese Gruppe), Bewertung der Kopie wird übertragen
        var ratingBefore = await DbScalarAsync<int>("SELECT IFNULL(Rating, 0) FROM Files WHERE Id = $id", ("$id", originalId)).ConfigureAwait(true);
        await DbScalarAsync<int>("UPDATE Files SET Rating = 5 WHERE Id = $id; SELECT changes();", ("$id", copyId)).ConfigureAwait(true);
        vm.IsExactMode = true;
        vm.TransferMetadata = true;
        await vm.LoadAsync().ConfigureAwait(true);
        foreach (var group in vm.Groups)
        {
            group.IsIncluded = group.Members.Any(m => m.FileId == copyId);
        }

        var target = vm.Groups.Single(g => g.IsIncluded);
        target.Members.Single(m => m.FileId == originalId).IsKeep = true;
        Check(vm.DeleteCount == 1, $"genau die Kopie zum Löschen gewählt ({vm.CleanUpText})");
        await vm.CleanUpCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(!File.Exists(copyPath), "Kopie im Papierkorb");
        Check(await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE Id = $id", ("$id", copyId)).ConfigureAwait(true) == 0, "Katalogzeile der Kopie entfernt");
        Check(await DbScalarAsync<int>("SELECT Rating FROM Files WHERE Id = $id", ("$id", originalId)).ConfigureAwait(true) == 5, "Bewertung auf das Original übertragen");
        var batch = await DbRowsAsync("SELECT OperationType, Status, Description FROM OperationBatches ORDER BY Id DESC LIMIT 1").ConfigureAwait(true);
        Check(batch.Single().StartsWith("0 | 2 | Doppelte aufgeräumt: 1 Datei", StringComparison.Ordinal), $"Protokoll: {batch.Single()}");
        Check(vm.UndoInfo is not null && vm.StatusText?.StartsWith("1 Datei im Papierkorb", StringComparison.Ordinal) == true, $"Ergebniszeile: {vm.StatusText}");

        // (7) Rückgängig: Kopie mit derselben FileId und Bewertung zurück, Übertragung zurückgenommen
        await vm.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(File.Exists(copyPath), "Kopie aus dem Papierkorb zurück");
        Check(await DbScalarAsync<int>("SELECT Rating FROM Files WHERE Id = $id", ("$id", copyId)).ConfigureAwait(true) == 5, "Kopie mit derselben FileId und ihrer Bewertung im Katalog");
        Check(await DbScalarAsync<int>("SELECT IFNULL(Rating, 0) FROM Files WHERE Id = $id", ("$id", originalId)).ConfigureAwait(true) == ratingBefore, "Bewertung des Originals zurückgenommen");
        await WaitUntilAsync(() => !vm.IsLoading && vm.Groups.Any(g => g.Members.Any(m => m.FileId == copyId)), ShortWait, "Gruppe wieder da").ConfigureAwait(true);
    }

    /// <summary>„Foto“ 1200 × 900 mit Farbverlauf und Formen (aussagekräftiger Bild-Fingerabdruck), Qualität 92.</summary>
    private static void WriteSceneJpeg(string path)
    {
        const int width = 1200;
        const int height = 900;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                byte r = (byte)(40 + (x * 150 / width)), g = (byte)(60 + (y * 120 / height)), b = 170;
                if (Math.Pow((x - 380) / 220.0, 2) + Math.Pow((y - 330) / 160.0, 2) <= 1)
                {
                    (r, g, b) = (230, 200, 40); // Sonne
                }
                else if (x > 650 && x < 1050 && y > 420 && y < 780)
                {
                    (r, g, b) = (150, 40, 30); // Haus
                }
                else if (y > 720)
                {
                    (r, g, b) = (40, 130, 50); // Wiese
                }

                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Halbe Größe, Qualität 70 (wie eine verschickte Kopie); ohne Metadaten.</summary>
    private static void WriteDownscaledJpeg(string sourcePath, string targetPath)
    {
        var decoder = BitmapDecoder.Create(new Uri(sourcePath), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(sourcePath);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Max(64, frame.PixelWidth / 2);
        bitmap.EndInit();
        bitmap.Freeze();
        // EXIF-Drehung anwenden (die Kopie hat keine Metadaten mehr, wie eine verschickte Kopie)
        var oriented = Lumora.Thumbnails.WicImageThumbnailRenderer.ApplyOrientation(
            bitmap, Lumora.Thumbnails.WicImageThumbnailRenderer.ReadOrientation(frame.Metadata as BitmapMetadata));
        var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
        encoder.Frames.Add(BitmapFrame.Create(oriented));
        using var stream = File.Create(targetPath);
        encoder.Save(stream);
    }
}
