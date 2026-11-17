using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Cloud;
using Lumora.Services.Media;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;
using Lumora.Thumbnails.Cloud;
using Xunit.Abstractions;

namespace Lumora.Wpf.Tests.Cloud;

/// <summary>Nur nativ auf Wunsch (<c>LUMORA_ONEDRIVE_PROBE</c> = OneDrive-Ordner), sonst übersprungen.</summary>
public sealed class OneDriveProbeFactAttribute : FactAttribute
{
    public const string Variable = "LUMORA_ONEDRIVE_PROBE";

    public OneDriveProbeFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = "Nur nativ: LUMORA_ONEDRIVE_PROBE auf einen OneDrive-Ordner setzen (optional LUMORA_ONEDRIVE_PROBE_OUT, _COUNT, _OFFSET).";
        }
    }
}

/// <summary>
/// Native Prüfung (ADR-0077, Kriterium 9): mit Lumoras eigenem Shell-Weg (<see cref="ShellThumbnailExtractor"/> +
/// <see cref="ShellCloudThumbnailRenderer"/>, 8 gleichzeitig) Vorschaubilder für 6–10 Platzhalter holen, das Aufnahmedatum
/// über <see cref="ShellCloudPropertyReader"/> lesen, den Scanner (<see cref="FolderEnumerator"/>) im Ordner des ersten
/// Platzhalters zählen lassen — und danach prüfen, dass jede Datei noch nur online liegt. Wurde doch eine geladen, gibt der
/// Test den Platz wieder frei (+U −P) und meldet die Zahl. Läuft nicht im App-Container (dort liefert die Shell nichts) und
/// gibt ausschließlich Zahlen aus — keine Dateinamen oder Pfade.
/// </summary>
public sealed class OneDriveNativeProbeTests(ITestOutputHelper output)
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".heic", ".png"];

    [OneDriveProbeFact]
    public async Task Shell_Weg_liefert_Vorschaubilder_ohne_Download()
    {
        var root = Environment.GetEnvironmentVariable(OneDriveProbeFactAttribute.Variable)!;
        var count = Math.Clamp(int.TryParse(Environment.GetEnvironmentVariable(OneDriveProbeFactAttribute.Variable + "_COUNT"), out var c) ? c : 8, 6, 10);
        var offset = int.TryParse(Environment.GetEnvironmentVariable(OneDriveProbeFactAttribute.Variable + "_OFFSET"), out var o) ? o : 0;
        var report = new StringBuilder();
        void Line(string text)
        {
            report.AppendLine(text);
            output.WriteLine(text);
        }

        Line($"Prozess: Paket {(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACKAGE_FAMILY_NAME")) ? "keins (nativ)" : "App-Container")}");
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var all = Directory.EnumerateFiles(root, "*", options)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && CloudFiles.IsOnlineOnly(f))
            .Take(20_000)
            .ToList();
        Line($"Platzhalter gefunden (bis 20 000): {all.Count}");
        all.Count.Should().BeGreaterThanOrEqualTo(count, "genug Platzhalter für die Prüfung");
        var step = Math.Max(1, all.Count / count);
        var picked = Enumerable.Range(0, count).Select(i => all[Math.Min(all.Count - 1, (i * step + offset) % all.Count)]).Distinct().ToList();

        // (1) Scanner: der Ordner des ersten Platzhalters — findet Lumoras Scanner die Dateien (bis 0.5.40: 0)?
        var folder = Path.GetDirectoryName(picked[0])!;
        var enumerator = new FolderEnumerator(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance);
        var scanned = enumerator.Enumerate(folder).Count();
        var direct = Directory.EnumerateFiles(folder, "*", options).Count(f => new MediaFormatRegistry().TryClassify(Path.GetExtension(f), out _));
        Line($"Scanner im Ordner des ersten Platzhalters: {scanned} von {direct} Mediendateien");

        // (2) Vorschaubilder über Lumoras Shell-Weg, alle gleichzeitig (höchstens 8 parallel)
        var temp = Directory.CreateTempSubdirectory("lumora-onedrive-probe-");
        using var extractor = new ShellThumbnailExtractor();
        var renderer = new ShellCloudThumbnailRenderer(extractor);
        var clock = Stopwatch.StartNew();
        var results = await Task.WhenAll(picked.Select(async (path, i) =>
        {
            var sw = Stopwatch.StartNew();
            var result = await renderer.RenderAsync(path, Path.Combine(temp.FullName, $"{i}_400_still.jpg"), ThumbnailSizing.ShortEdge, CancellationToken.None);
            return (Result: result, Ms: sw.ElapsedMilliseconds);
        }));
        clock.Stop();
        var ok = results.Where(r => r.Result.Success).ToList();
        var times = ok.Select(r => r.Ms).OrderBy(t => t).ToList();
        Line($"Vorschaubilder: {ok.Count} von {picked.Count} in {clock.Elapsed.TotalSeconds:N1} s"
             + (times.Count > 0 ? $", je Bild Median {times[times.Count / 2]} ms, langsamstes {times[^1]} ms" : string.Empty));
        foreach (var size in ok.Select(r => $"{r.Result.Width}×{r.Result.Height}").GroupBy(s => s))
        {
            Line($"  Größe {size.Key}: {size.Count()}");
        }

        foreach (var failure in results.Where(r => !r.Result.Success).GroupBy(r => r.Result.ErrorCode))
        {
            Line($"  gescheitert ({failure.Key}): {failure.Count()}");
        }

        // (3) Aufnahmedatum über den Eigenschaftsspeicher (ohne Download)
        using var properties = new ShellCloudPropertyReader(NullLogger<ShellCloudPropertyReader>.Instance);
        var dates = picked.Count(p => properties.ReadCaptureDate(p) is not null);
        Line($"Aufnahmedatum aus der Shell: {dates} von {picked.Count}");

        // (4) Danach noch nur online? Sonst Platz wieder freigeben (+U −P) und melden
        var hydrated = picked.Where(p => !CloudFiles.IsOnlineOnly(p)).ToList();
        foreach (var path in hydrated)
        {
            File.SetAttributes(path, CloudFiles.WithPin(File.GetAttributes(path), keepOnDevice: false));
        }

        Line(hydrated.Count == 0
            ? $"Danach: alle {picked.Count} Dateien weiterhin nur online — nichts heruntergeladen."
            : $"Danach: {hydrated.Count} von {picked.Count} heruntergeladen — Platz wieder freigegeben (+U −P).");
        temp.Delete(recursive: true);

        if (Environment.GetEnvironmentVariable(OneDriveProbeFactAttribute.Variable + "_OUT") is { Length: > 0 } outFile)
        {
            await File.WriteAllTextAsync(outFile, report.ToString());
        }

        ok.Count.Should().Be(picked.Count, "Lumoras Shell-Weg liefert für jeden Platzhalter ein Vorschaubild");
        hydrated.Should().BeEmpty("der Shell-Weg lädt nichts herunter");
        scanned.Should().Be(direct, "der Scanner liest Cloud-Dateien ein");
    }

    /// <summary>
    /// Nachbesserung 0.5.41: Entspricht das Aufnahmedatum der Shell (Eigenschaftsspeicher, für Platzhalter vorläufig) dem
    /// EXIF-Datum, das Lumora selbst liest? Verglichen wird an LOKALEN Fotos im Ordner (nichts wird geladen): Wanduhrzeit
    /// und UTC getrennt, dazu wie viele Dateien einen Zeitversatz nennen, der von der Zeitzone dieses PCs abweicht.
    /// Ausgabe nur Zahlen.
    /// </summary>
    [OneDriveProbeFact]
    public void Shell_Datum_im_Vergleich_zum_EXIF_Datum_lokaler_Fotos()
    {
        var root = Environment.GetEnvironmentVariable(OneDriveProbeFactAttribute.Variable)!;
        var count = Math.Clamp(int.TryParse(Environment.GetEnvironmentVariable(OneDriveProbeFactAttribute.Variable + "_DATES"), out var c) ? c : 300, 20, 2000);
        var report = new StringBuilder();
        void Line(string text)
        {
            report.AppendLine(text);
            output.WriteLine(text);
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var local = Directory.EnumerateFiles(root, "*", options)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && CloudFiles.IsLocallyAvailable(f))
            .Take(50_000)
            .ToList();
        var step = Math.Max(1, local.Count / count);
        var picked = local.Where((_, i) => i % step == 0).Take(count).ToList();
        Line($"Lokale Fotos gefunden (bis 50 000): {local.Count}, verglichen: {picked.Count}");

        var exifReader = new Lumora.Services.Metadata.CaptureDateReader();
        using var shellReader = new ShellCloudPropertyReader(NullLogger<ShellCloudPropertyReader>.Instance);
        int both = 0, onlyExif = 0, onlyShell = 0, none = 0, wallEqual = 0, utcEqual = 0, foreignOffset = 0, foreignWallEqual = 0, foreignUtcEqual = 0;
        var wallDiffs = new List<double>();
        foreach (var path in picked)
        {
            var exif = exifReader.Read(path);
            var shell = shellReader.ReadCaptureDate(path);
            if (exif is null || exif.Value.Source != Lumora.Domain.Enums.CaptureDateSource.Exif)
            {
                none += shell is null ? 1 : 0;
                onlyShell += shell is null ? 0 : 1;
                continue;
            }

            if (shell is null)
            {
                onlyExif++;
                continue;
            }

            both++;
            var e = exif.Value;
            var s = shell.Value;
            var sameWall = Math.Abs((e.Local - s.Local).TotalSeconds) < 1;
            var sameUtc = Math.Abs((e.Utc - s.Utc).TotalSeconds) < 1;
            wallEqual += sameWall ? 1 : 0;
            utcEqual += sameUtc ? 1 : 0;
            if (!sameWall)
            {
                wallDiffs.Add((s.Local - e.Local).TotalMinutes);
            }

            var pcOffset = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(e.Local).TotalMinutes);
            if (e.OffsetMinutes != pcOffset)
            {
                foreignOffset++;
                foreignWallEqual += sameWall ? 1 : 0;
                foreignUtcEqual += sameUtc ? 1 : 0;
            }
        }

        Line($"Datum aus EXIF und Shell: {both}; nur EXIF: {onlyExif}; nur Shell: {onlyShell}; keins: {none}");
        Line($"  Wanduhrzeit gleich: {wallEqual} von {both}; UTC gleich: {utcEqual} von {both}");
        Line($"  Zeitversatz der Datei weicht von der Zone dieses PCs ab: {foreignOffset} (davon Wanduhrzeit gleich {foreignWallEqual}, UTC gleich {foreignUtcEqual})");
        foreach (var group in wallDiffs.GroupBy(d => Math.Round(d)).OrderByDescending(g => g.Count()).Take(5))
        {
            Line($"  Abweichung der Wanduhrzeit {group.Key:+0;-0} min: {group.Count()}");
        }

        if (Environment.GetEnvironmentVariable(OneDriveProbeFactAttribute.Variable + "_OUT") is { Length: > 0 } outFile)
        {
            File.WriteAllText(outFile, report.ToString());
        }

        both.Should().BeGreaterThan(0, "lokale Fotos mit Datum zum Vergleich");
    }
}
