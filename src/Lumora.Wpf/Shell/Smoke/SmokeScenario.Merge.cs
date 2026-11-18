using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using Lumora.Domain.Enums;
using Lumora.Gallery.Controls;
using Lumora.Lightbox;
using Lumora.Merge;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Settings;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Zusammenfügen“ (ADR-0078 Stufe 1 Teil B, Punkt 18): drei überlappende Ausschnitte einer zur Laufzeit
/// gezeichneten Szene (keine externen Bilder) einlesen → Auswahl → Rechtsklick „Bearbeiten ▸ 3 Fotos zusammenfügen…“ (Screenshot)
/// → Fenster mit Vorschau vom echten Rechen-Prozess (Screenshot hell) → „Als neue Datei speichern“ → Fertig-Ansicht (Screenshot
/// dunkel) → Datei neben dem ersten Einzelbild, Quellen Byte für Byte gleich, Katalog-Verknüpfung, Ergebnis-Marke in der Galerie
/// (Screenshot), Lightbox-Zeile „Zusammengefügt aus 3 Fotos“ samt Info-Abschnitt (Screenshot) → Einstellungen › Zusammenfügen
/// (Screenshot) → Rückgängig räumt Datei und Verknüpfungen weg.
/// </summary>
internal sealed partial class SmokeScenario
{
    private MergeGalleryActions? _mergeActions;
    private IMergeWindowService? _mergeWindows;
    private MergeSettingsViewModel? _mergeSettings;
    private int _mergeSmokeCataloged;

    private string MergeDir => Path.Combine(_root, "Smoke-Zusammenfuegen");

    private void InitMergeSmoke(IServiceProvider services)
    {
        _mergeActions = services.GetService<MergeGalleryActions>();
        _mergeWindows = services.GetService<IMergeWindowService>();
        _mergeSettings = services.GetService<MergeSettingsViewModel>();
    }

    private async Task MergeAsync()
    {
        Check(_mergeActions is not null && _mergeWindows is not null && _mergeSettings is not null && _smokeBin is not null, "Zusammenfügen: Galerie-Anbindung, Fenster, Einstellungen und Test-Papierkorb registriert");
        if (Directory.Exists(MergeDir))
        {
            Directory.Delete(MergeDir, recursive: true); // Rückstand eines abgebrochenen Laufs
        }

        var themeBefore = _theme.Mode;
        _smokeBin!.TestMode = true;
        try
        {
            await MergeCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _mergeWindows!.Current?.Close();
            if (Application.Current.Windows.OfType<LightboxWindow>().FirstOrDefault() is { } open)
            {
                open.ViewModel.CloseCommand.Execute(null);
                await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);
            }

            _messenger.Send(new FolderSelectedMessage(_libraryId, null, true));
            var folderId = await DbScalarAsync<long>("SELECT COALESCE((SELECT Id FROM Folders WHERE Path = $p), 0)", ("$p", MergeDir)).ConfigureAwait(true);
            if (folderId > 0)
            {
                await _fileOps.RecycleFolderAsync((int)folderId).ConfigureAwait(true);
            }

            _smokeBin.TestMode = false;
            _smokeBin.Clear();
            var left = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Name LIKE 'zusammen-%'").ConfigureAwait(true);
            Check(left == 0 && !Directory.Exists(MergeDir), $"Aufgeräumt: Ordner weg, keine Katalogzeile mehr ({left})");
            _mediaCount -= _mergeSmokeCataloged;
            _mergeSmokeCataloged = 0;
            await _theme.SetModeAsync(themeBefore).ConfigureAwait(true);
            _navigation.NavigateTo(NavigationTags.Gallery);
        }
    }

    private async Task MergeCoreAsync()
    {
        // (1) Material: drei überlappende Ausschnitte (≈ 42 %) einer gezeichneten Szene, ohne EXIF, eingelesen
        Directory.CreateDirectory(MergeDir);
        var paths = WriteOverlappingViews(MergeDir, 3);
        var hashes = paths.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToList();
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Added: 3 }, $"Eingelesen: 3 neue Dateien (Added={_lastScan?.Added})");
        _mediaCount += 3;
        _mergeSmokeCataloged = 3;
        var ids = new int[3];
        for (var i = 0; i < 3; i++)
        {
            ids[i] = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = $n", ("$lib", _libraryId), ("$n", Path.GetFileName(paths[i]))).ConfigureAwait(true);
        }

        // (2) Galerie: Ordner, Auswahl, Rechtsklick „Bearbeiten ▸ 3 Fotos zusammenfügen…“ und Aktionsleiste
        var folderId = await FolderIdAsync(MergeDir).ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Gallery);
        _messenger.Send(new FolderSelectedMessage(_libraryId, folderId, false));
        await WaitUntilAsync(() => !_gallery.IsLoading && ids.All(id => PageItem(id) is not null), ShortWait, "drei Fotos in der Galerie").ConfigureAwait(true);
        _gallery.ClearSelectionCommand.Execute(null);
        foreach (var id in ids)
        {
            PageItem(id)!.IsSelected = true;
        }

        await WaitUntilAsync(() => _gallery.SelectedCount == 3, ShortWait, "drei gewählt").ConfigureAwait(true);
        Check(_gallery.ShowMergeAction && _gallery.MergeSelectionCommand.CanExecute(null), $"Aktionsleiste: „Zusammenfügen…“ frei ({_gallery.MergeActionToolTip})");
        await _theme.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        // Menü an der linken der drei Kacheln (das Untermenü „Bearbeiten ▸“ passt so rechts daneben ins Fenster)
        await ShowMergeMenuAsync(_gallery.CurrentPage.First(i => ids.Contains(i.Id)).Id).ConfigureAwait(true);

        // (3) Fenster mit Vorschau (echter Rechen-Prozess)
        await _gallery.MergeFromMenuAsync(PageItem(ids[0])!, ids).ConfigureAwait(true);
        await WaitUntilAsync(() => _mergeWindows!.Current is { IsLoaded: true }, ShortWait, "Fenster „Zusammenfügen“ offen").ConfigureAwait(true);
        var window = _mergeWindows!.Current!;
        var vm = window.ViewModel;
        await WaitUntilAsync(() => !vm.IsComputing && (vm.Preview is not null || vm.IsLocked || vm.Notes.Any(n => n.Kind == MergeNoteKind.Error)),
            TimeSpan.FromSeconds(90), "Vorschau gerechnet").ConfigureAwait(true);
        Check(vm.Preview is { CanSave: true } && vm.StageImage is not null,
            $"Vorschau: {vm.StageLabel} · {vm.StageMessage} · {string.Join(" | ", vm.Notes.Select(n => n.Text))}");
        Check(vm.Methods.Single(x => x.Method == MergeMethod.Panorama).Suits && vm.Methods.Count(x => x.FitText == "kommt später") == 8, "Verfahren: Panorama passt, übrige „kommt später“");
        Check(vm.SourceSummary == "3 von 3 angehakt" && vm.TargetName == "zusammen-1_panorama.jpg", $"Filmstreifen „{vm.SourceSummary}“, Ziel „{vm.TargetName}“");
        Check(vm.Sizes.Any(s => s.IsSelected) && vm.EstimateText.Length > 0, $"Schätzung: {vm.EstimateText} · {vm.EstimateDetail}");
        await WaitUntilAsync(() => vm.Sources.All(s => s.Thumbnail is not null), ShortWait, "Vorschaubilder im Filmstreifen").ConfigureAwait(true);
        // Regler „Farbe kräftiger“: nur neu tonen
        var started = vm.Preview!.Elapsed;
        vm.ColorBoost = 130;
        await WaitUntilAsync(() => vm.Preview is not null && vm.ColorBoostText == "130 %" && !vm.IsComputing, ShortWait, "Farbe kräftiger 130 %").ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);
        vm.ColorBoost = 100;
        await Task.Delay(400).ConfigureAwait(true);
        await WaitUntilAsync(() => !vm.IsComputing, ShortWait, "neu getont").ConfigureAwait(true);
        await SnapshotAsync("33-zusammenfuegen-vorschau-hell", window).ConfigureAwait(true);
        _logger.LogInformation("Rauchtest   Zusammenfügen: Vorschau {Label} ({Ms:N0} ms erste Vorschau)", vm.StageLabel, started.TotalMilliseconds);

        // (4) Speichern → Fertig-Ansicht
        await vm.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(vm.IsDone && vm.ResultFileId > 0 && vm.MadeFromText == "aus 3 Fotos zusammengefügt", $"Gespeichert: {string.Join(" | ", vm.Notes.Select(n => n.Text))}");
        await _theme.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("34-zusammenfuegen-fertig-dunkel", window).ConfigureAwait(true);
        var resultId = vm.ResultFileId;
        var resultPath = Path.Combine(MergeDir, "zusammen-1_panorama.jpg");
        Check(File.Exists(resultPath), "Ergebnis neben dem ersten Einzelbild");
        Check(paths.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).SequenceEqual(hashes), "Einzelbilder Byte für Byte unverändert");
        var sources = await DbScalarAsync<long>("SELECT COUNT(*) FROM MergedPhotoSources WHERE ResultFileId = $r", ("$r", resultId)).ConfigureAwait(true);
        var count = await DbScalarAsync<long>("SELECT COALESCE((SELECT SourceCount FROM MergedPhotos WHERE FileId = $r), 0)", ("$r", resultId)).ConfigureAwait(true);
        var batchId = await DbScalarAsync<int>("SELECT COALESCE((SELECT BatchId FROM MergedPhotos WHERE FileId = $r), 0)", ("$r", resultId)).ConfigureAwait(true);
        var batchType = await DbScalarAsync<long>("SELECT OperationType FROM OperationBatches WHERE Id = $b", ("$b", batchId)).ConfigureAwait(true);
        Check(sources == 3 && count == 3 && batchType == (long)OperationType.CreateMerged, $"Katalog: Ergebnis {resultId} mit {sources} Quellen verknüpft, Batch {batchId} (CreateMerged)");
        window.Close();
        await WaitUntilAsync(() => _mergeWindows.Current is null, ShortWait, "Fenster geschlossen").ConfigureAwait(true);

        // (5) Galerie: Ergebnis-Marke „Panorama · aus 3 Fotos“ (hell)
        await _theme.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        _messenger.Send(new FolderSelectedMessage(_libraryId, folderId, false));
        await WaitUntilAsync(() => !_gallery.IsLoading && PageItem(resultId) is { IsMergeResult: true }, ShortWait, "Ergebnis-Marke in der Galerie").ConfigureAwait(true);
        Check(PageItem(resultId)!.MergeMarkText == "Panorama · aus 3 Fotos", $"Marke „{PageItem(resultId)!.MergeMarkText}“");
        _gallery.ClearSelectionCommand.Execute(null);
        await SnapshotAsync("35-zusammenfuegen-marke-hell").ConfigureAwait(true);

        // (6) Lightbox über die Marke: Ergebnis zuerst, Zeile und Info-Abschnitt „Zusammengefügt“
        _gallery.OpenMergeResultCommand.Execute(PageItem(resultId));
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox offen").ConfigureAwait(true);
        var lightboxWindow = Application.Current.Windows.OfType<LightboxWindow>().First();
        var lightbox = lightboxWindow.ViewModel;
        await WaitUntilAsync(() => lightbox.CurrentItem?.FileId == resultId && lightbox.HasMergeInfo && lightbox.ImageSource is not null, ShortWait, "Ergebnis zuerst, Zeile „Zusammengefügt“").ConfigureAwait(true);
        Check(lightbox.CounterText == "1 / 4" && lightbox.MergeRowText.StartsWith("Zusammengefügt aus 3 Fotos · Panorama", StringComparison.Ordinal),
            $"Lightbox: {lightbox.CounterText} · „{lightbox.MergeRowText}“");
        Check(lightbox.MergeInfoRows.Any(r => r.Label == "Quellen" && r.Value == "3 · alle vorhanden"), $"Info-Abschnitt: {string.Join(", ", lightbox.MergeInfoRows.Select(r => r.Label + "=" + r.Value))}");
        if (!lightbox.IsInfoVisible)
        {
            lightbox.ToggleInfoCommand.Execute(null);
        }

        await SnapshotAsync("36-zusammenfuegen-lightbox", lightboxWindow).ConfigureAwait(true);
        lightbox.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        // (7) Einstellungen › Zusammenfügen (hell)
        await OpenSettingsSectionAsync(NavigationTags.SettingsMerge, "Reiter „Zusammenfügen“").ConfigureAwait(true);
        await WaitUntilAsync(() => _mergeSettings!.IsLoaded && _mergeSettings.ToolState != "wird geprüft…", ShortWait, "Einstellungen › Zusammenfügen geladen").ConfigureAwait(true);
        Check(_mergeSettings!.ToolReady && _mergeSettings.ColorBoost == 100, $"Rechen-Prozess: {_mergeSettings.ToolState} ({_mergeSettings.ToolDetail}), Farbe {_mergeSettings.ColorBoostText}");
        await SnapshotAsync("37-zusammenfuegen-einstellungen-hell").ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Gallery);

        // (8) Rückgängig: Ergebnis in den (Test-)Papierkorb, Verknüpfungen weg, Einzelbilder bleiben
        var undo = await _fileOps.UndoBatchAsync(batchId).ConfigureAwait(true);
        var merged = await DbScalarAsync<long>("SELECT COUNT(*) FROM MergedPhotos").ConfigureAwait(true);
        var links = await DbScalarAsync<long>("SELECT COUNT(*) FROM MergedPhotoSources").ConfigureAwait(true);
        var rows = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Id = $r", ("$r", resultId)).ConfigureAwait(true);
        Check(undo is { Succeeded: 1 } && !File.Exists(resultPath) && paths.All(File.Exists) && merged == 0 && links == 0 && rows == 0,
            $"Rückgängig: Ergebnis im Papierkorb, Katalog sauber ({merged} Ergebnisse, {links} Verknüpfungen, {rows} Zeilen)");
        _logger.LogInformation("Rauchtest   Zusammenfügen: Ergebnis {Result} angelegt, verknüpft, rückgängig", resultId);
    }

    /// <summary>Rechtsklick auf eine gewählte Kachel, „Bearbeiten ▸“ offen (Screenshot).</summary>
    private async Task ShowMergeMenuAsync(int fileId)
    {
        var view = FindVisibleGalleryView(Application.Current.MainWindow);
        Check(view is not null, "Galerie sichtbar");
        var list = FindVisualChild<GalleryListBox>(view)!;
        list.ScrollIntoView(PageItem(fileId)!);
        list.UpdateLayout();
        var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(PageItem(fileId)!);
        var menu = container.ContextMenu!;
        menu.PlacementTarget = container;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint; // wie ein Rechtsklick in die Kachel, nicht an der Maus
        menu.HorizontalOffset = 40;
        menu.VerticalOffset = -300; // nach oben, damit das Untermenü „Bearbeiten ▸“ ganz im Fenster liegt
        menu.IsOpen = true;
        try
        {
            await WaitUntilAsync(() => menu.Items.Count > 5, ShortWait, "Menü gebaut").ConfigureAwait(true);
            var edit = menu.Items.OfType<MenuItem>().First(i => (string)i.Header == "Bearbeiten");
            var headers = edit.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList();
            Check(headers.IndexOf("3 Fotos zusammenfügen…") == headers.IndexOf("3 Fotos verbessern…") + 1 && edit.Items.OfType<MenuItem>().First(i => (string)i.Header == "3 Fotos zusammenfügen…").IsEnabled,
                $"Bearbeiten ▸ {string.Join(" · ", headers)}");
            edit.IsSubmenuOpen = true;
            await Task.Delay(400).ConfigureAwait(true);
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (_shotsDir is not null && Application.Current.MainWindow is { } main)
            {
                // Popups leben in eigenen Fenstern: Menü und Untermenü „Bearbeiten ▸“ an ihrer Lage dazuzeichnen
                var popup = edit.Template?.FindName("PART_Popup", edit) as System.Windows.Controls.Primitives.Popup ?? FindVisualChild<System.Windows.Controls.Primitives.Popup>(edit);
                if (popup is { IsOpen: false })
                {
                    popup.IsOpen = true; // ohne Maus über dem Eintrag schließt WPF das Untermenü gleich wieder
                    await Task.Delay(300).ConfigureAwait(true);
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                }

                var submenu = popup is { IsOpen: true, Child: FrameworkElement child } ? child : null;
                _logger.LogInformation("Rauchtest   Untermenü „Bearbeiten“: Popup {Found}, offen {Open}", popup is not null, popup?.IsOpen);
                var path = Path.Combine(_shotsDir, "32-zusammenfuegen-rechtsklick-hell.png");
                var method = WindowCapture.TrySaveWithPopups(main, path, submenu is null ? [menu] : [menu, submenu]);
                _logger.LogInformation("Rauchtest   Screenshot {State}: {Path}", method ?? "FEHLGESCHLAGEN", path);
            }

            edit.IsSubmenuOpen = false;
        }
        finally
        {
            menu.IsOpen = false;
            menu.ClearValue(ContextMenu.PlacementProperty);
            menu.ClearValue(ContextMenu.HorizontalOffsetProperty);
            menu.ClearValue(ContextMenu.VerticalOffsetProperty);
        }
    }

    /// <summary>
    /// Szene zur Laufzeit zeichnen (Himmel-Verlauf, Formen, Linien, Buchstaben mit festem Startwert) und überlappende
    /// Ausschnitte als JPEG schreiben — wie ein Schwenk über ein flaches Motiv. Änderungszeiten aufsteigend (Reihenfolge).
    /// </summary>
    private static string[] WriteOverlappingViews(string dir, int count)
    {
        const int w = 1200, h = 860, step = 700;
        var width = w + (count - 1) * step;
        var visual = new DrawingVisual();
        var rnd = new Random(78);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(96, 150, 210), Color.FromRgb(214, 190, 140), 90), null, new Rect(0, 0, width, h));
            var typeface = new Typeface("Segoe UI");
            for (var i = 0; i < 900; i++)
            {
                var color = Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
                var brush = new SolidColorBrush(color);
                double x = rnd.Next(width), y = rnd.Next(h);
                var size = 6 + Math.Pow(rnd.NextDouble(), 2.2) * 90;
                switch (rnd.Next(4))
                {
                    case 0:
                        dc.DrawEllipse(brush, null, new Point(x, y), size / 2, size / 2.5);
                        break;
                    case 1:
                        dc.DrawRectangle(brush, new Pen(Brushes.Black, 1), new Rect(x, y, size, size * (0.3 + rnd.NextDouble())));
                        break;
                    case 2:
                        dc.DrawLine(new Pen(brush, 1 + rnd.Next(4)), new Point(x, y), new Point(x + rnd.Next(-120, 120), y + rnd.Next(-60, 60)));
                        break;
                    default:
                        dc.DrawText(new FormattedText(((char)('A' + rnd.Next(26))).ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10 + rnd.Next(40), brush, 1.0), new Point(x, y));
                        break;
                }
            }
        }

        var scene = new RenderTargetBitmap(width, h, 96, 96, PixelFormats.Pbgra32);
        scene.Render(visual);
        scene.Freeze();
        var paths = new string[count];
        var time = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < count; i++)
        {
            var crop = new CroppedBitmap(scene, new Int32Rect(i * step, 0, w, h));
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(crop));
            paths[i] = Path.Combine(dir, $"zusammen-{i + 1}.jpg");
            using (var stream = File.Create(paths[i]))
            {
                encoder.Save(stream);
            }

            File.SetLastWriteTimeUtc(paths[i], time.AddSeconds(i * 3));
        }

        return paths;
    }
}
