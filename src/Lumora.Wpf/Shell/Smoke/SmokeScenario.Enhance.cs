using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using Lumora.Enhance;
using Lumora.Settings;
using Lumora.Lightbox;
using Lumora.Services.Activity;
using Lumora.Services.Enhance;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Foto verbessern“ (ADR-0071, Mockup 13): Foto in eigenem Ordner einlesen → Lightbox → Leiste „Verbessern“
/// (wie „Bearbeiten ▸ Foto verbessern…“) mit Modell-Zustand je Werkzeug → Hochskalieren (Fake-Prozessor) + Licht → Vorschau mit
/// Vorher/Nachher (Screenshot dunkel) → „Als neue Datei speichern“: neue Datei daneben, Original Byte für Byte gleich, im Katalog
/// verknüpft, Marke „2 Fassungen“ in der Galerie, „beste Fassung zeigen“ blendet das Original aus → Rückgängig (Test-Papierkorb,
/// Katalog sauber) → mehrere Fotos über den Dienst mit Aktivität und Rückgängig → Einstellungen › Verbessern (Screenshot hell).
/// Kein echtes Modell, kein Download.
/// </summary>
internal sealed partial class SmokeScenario
{
    private IPhotoEnhanceService? _enhanceService;
    private IPhotoVariantStore? _variantStore;
    private EnhanceSettingsViewModel? _enhanceSettings;
    private EnhanceGalleryActions? _enhanceActions;
    private int _enhanceSmokeCataloged;

    private string EnhanceDir => Path.Combine(_root, "Smoke-Verbessern");

    private void InitEnhanceSmoke(IServiceProvider services)
    {
        _enhanceService = services.GetService<IPhotoEnhanceService>();
        _variantStore = services.GetService<IPhotoVariantStore>();
        _enhanceSettings = services.GetService<EnhanceSettingsViewModel>();
        _enhanceActions = services.GetService<EnhanceGalleryActions>();
    }

    private async Task EnhanceAsync()
    {
        Check(_enhanceService is not null && _variantStore is not null && _enhanceSettings is not null && _enhanceActions is not null && _smokeBin is not null && _activityCenter is not null,
            "Foto verbessern: Dienst, Fassungen, Einstellungen, Test-Papierkorb und Aktivitäten registriert");
        if (Directory.Exists(EnhanceDir))
        {
            Directory.Delete(EnhanceDir, recursive: true); // Rückstand eines abgebrochenen Laufs
        }

        var themeBefore = _theme.Mode;
        _smokeBin!.TestMode = true;
        try
        {
            await EnhanceCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            if (Application.Current.Windows.OfType<LightboxWindow>().FirstOrDefault() is { } open)
            {
                open.ViewModel.CloseCommand.Execute(null);
                await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);
            }

            await _enhanceSettings!.RefreshAsync().ConfigureAwait(true);
            _enhanceSettings.ShowBestVariant = false;
            await _enhanceSettings.SaveNowAsync().ConfigureAwait(true);
            _messenger.Send(new FolderSelectedMessage(_libraryId, null, true));
            var folderId = await DbScalarAsync<long>("SELECT COALESCE((SELECT Id FROM Folders WHERE Path = $p), 0)", ("$p", EnhanceDir)).ConfigureAwait(true);
            if (folderId > 0)
            {
                await _fileOps.RecycleFolderAsync((int)folderId).ConfigureAwait(true);
            }

            _smokeBin.TestMode = false;
            _smokeBin.Clear();
            var left = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Name LIKE 'verbessern-%'").ConfigureAwait(true);
            Check(left == 0 && !Directory.Exists(EnhanceDir), $"Aufgeräumt: Ordner weg, keine Katalogzeile mehr ({left})");
            _mediaCount -= _enhanceSmokeCataloged;
            _enhanceSmokeCataloged = 0;
            await _theme.SetModeAsync(themeBefore).ConfigureAwait(true);
            _navigation.NavigateTo(NavigationTags.Gallery);
        }
    }

    private async Task EnhanceCoreAsync()
    {
        // (1) Material: ein Foto in eigenem Ordner, eingelesen
        var jpg = Directory.EnumerateFiles(Path.Combine(_root, "Unsortiert"), "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First();
        Directory.CreateDirectory(EnhanceDir);
        var original = Path.Combine(EnhanceDir, "verbessern-1.jpg");
        File.Copy(jpg, original);
        var originalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(original).ConfigureAwait(true)));
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Added: 1 }, $"Eingelesen: 1 neue Datei (Added={_lastScan?.Added})");
        _mediaCount += 1;
        _enhanceSmokeCataloged = 1;
        var fileId = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = 'verbessern-1.jpg'", ("$lib", _libraryId)).ConfigureAwait(true);

        // (2) Lightbox → Leiste „Verbessern“ (derselbe Weg wie „Bearbeiten ▸ Foto verbessern…“)
        var entries = await _enhanceActions!.LoadEntriesAsync([fileId]).ConfigureAwait(true);
        _lightbox.Open(entries, 0);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().First();
        var lightbox = window.ViewModel;
        await WaitUntilAsync(() => lightbox.CurrentItem?.FileId == fileId && lightbox.ImageSource is not null, ShortWait, "Foto in der Lightbox").ConfigureAwait(true);
        _lightbox.ShowEnhancePanel();
        var panel = lightbox.Enhance!;
        await WaitUntilAsync(() => panel.IsOpen && panel.IsLoaded && panel.FileId == fileId && panel.TargetName is not null, ShortWait, "Leiste „Verbessern“ offen").ConfigureAwait(true);
        Check(panel.Tool(EnhanceTool.Upscale) is { IsReady: true, StateText: "bereit" }, $"Hochskalieren: {panel.Tool(EnhanceTool.Upscale).StateText} (Fake)");
        Check(panel.Tool(EnhanceTool.Faces) is { IsReady: false, StateText: "Modell fehlt", CanLoad: true }, $"Gesichter: {panel.Tool(EnhanceTool.Faces).StateText}, „Laden…“");
        Check(panel.TargetName == "verbessern-1_verbessert.jpg", $"Dateiname: {panel.TargetName}");

        // (3) Hochskalieren 2× + Licht → Vorschau, Vorher/Nachher
        panel.ToggleToolCommand.Execute(panel.Tool(EnhanceTool.Upscale));
        panel.ToggleToolCommand.Execute(panel.Tool(EnhanceTool.Adjust));
        panel.Brightness = 0.2;
        await panel.CalculateCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(panel.Preview is { } p && p.AppliedTools.Contains(EnhanceTool.Upscale) && p.AppliedTools.Contains(EnhanceTool.Adjust) && panel.AfterImage is not null && panel.ShowsOverlay,
            $"Vorschau: {panel.PreviewInfo} {panel.Message}");
        var preview = panel.Preview!;
        Check(preview.ResultPath.StartsWith(Path.Combine(_paths.AppDataRoot, "temp"), StringComparison.OrdinalIgnoreCase), "Zwischenstand im Temp-Ordner des Katalogs");
        Check(window.FindName("EnhanceStage") is EnhanceStageOverlay { IsVisible: true }, "Bühne zeigt Vorher/Nachher");
        await _theme.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("31-verbessern-dunkel", window).ConfigureAwait(true);

        // (4) Als neue Datei speichern → daneben, Original unverändert, verknüpft
        await panel.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        var variant = Path.Combine(EnhanceDir, "verbessern-1_verbessert.jpg");
        Check(!panel.HasPreview && panel.Message?.Contains("Gespeichert als", StringComparison.Ordinal) == true && File.Exists(variant), $"Gespeichert: {panel.Message}");
        Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(original).ConfigureAwait(true))) == originalHash, "Original Byte für Byte unverändert");
        var variantId = await DbScalarAsync<int>("SELECT COALESCE((SELECT FileId FROM PhotoVariants WHERE OriginalFileId = $o), 0)", ("$o", fileId)).ConfigureAwait(true);
        var batchId = await DbScalarAsync<int>("SELECT COALESCE((SELECT BatchId FROM PhotoVariants WHERE OriginalFileId = $o), 0)", ("$o", fileId)).ConfigureAwait(true);
        var batchType = await DbScalarAsync<long>("SELECT OperationType FROM OperationBatches WHERE Id = $b", ("$b", batchId)).ConfigureAwait(true);
        Check(variantId > 0 && batchType == (long)Domain.Enums.OperationType.CreateVariant, $"Katalog: Fassung {variantId} verknüpft, Batch {batchId} (CreateVariant)");
        var saveActivity = _activityCenter!.GetOverview().Recent.FirstOrDefault(r => r.Title == "Verbesserte Fassung speichern");
        Check(saveActivity is { State: ActivityState.Succeeded } && saveActivity.HasAction(ActivityActionKind.Undo), $"Aktivität: {saveActivity?.Result}");
        lightbox.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        // (5) Galerie: Marke „2 Fassungen“; „beste Fassung zeigen“ blendet das Original aus
        var folderId = await DbScalarAsync<int>("SELECT Id FROM Folders WHERE Path = $p", ("$p", EnhanceDir)).ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Gallery);
        _messenger.Send(new FolderSelectedMessage(_libraryId, folderId, false));
        await WaitUntilAsync(
            () => !_gallery.IsLoading && _gallery.CurrentPage.Any(i => i.Id == fileId && i.HasVariants) && _gallery.CurrentPage.Any(i => i.Id == variantId && i.VariantText == "2 Fassungen"),
            ShortWait, "Marke „2 Fassungen“ an Original und Fassung").ConfigureAwait(true);
        await _enhanceSettings!.RefreshAsync().ConfigureAwait(true);
        _enhanceSettings.ShowBestVariant = true;
        await _enhanceSettings.SaveNowAsync().ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.CurrentPage.All(i => i.Id != fileId) && _gallery.CurrentPage.Any(i => i.Id == variantId),
            ShortWait, "beste Fassung: Original ausgeblendet").ConfigureAwait(true);
        var groups = await _variantStore!.GetGroupsAsync([variantId]).ConfigureAwait(true);
        Check(groups.TryGetValue(variantId, out var group) && group.AllFileIds.SequenceEqual([fileId, variantId]), "Original über die Marke erreichbar (Gruppe)");
        _enhanceSettings.ShowBestVariant = false;
        await _enhanceSettings.SaveNowAsync().ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.CurrentPage.Any(i => i.Id == fileId), ShortWait, "Original wieder sichtbar").ConfigureAwait(true);

        // (6) Rückgängig: neue Datei in den (Test-)Papierkorb, Katalog sauber
        var undo = await _fileOps.UndoBatchAsync(batchId).ConfigureAwait(true);
        var links = await DbScalarAsync<long>("SELECT COUNT(*) FROM PhotoVariants").ConfigureAwait(true);
        var rows = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Id = $v", ("$v", variantId)).ConfigureAwait(true);
        Check(undo is { Succeeded: 1 } && !File.Exists(variant) && File.Exists(original) && links == 0 && rows == 0, $"Rückgängig: Fassung im Papierkorb, Katalog sauber ({links} Verknüpfungen, {rows} Zeilen)");

        // (7) Mehrere Fotos (hier eines) über den Dienst: Vorgang „Foto verbessern“ mit Rückgängig
        var batch = await _enhanceService!.RunBatchAsync([fileId], new EnhanceOptions { Upscale = true, UpscaleFactor = 2 }).ConfigureAwait(true);
        var enhanceActivity = _activityCenter.GetOverview().Recent.FirstOrDefault(r => r.Kind == ActivityKind.Enhance);
        Check(batch.Saved == 1 && File.Exists(variant) && enhanceActivity is { State: ActivityState.Succeeded } && enhanceActivity.HasAction(ActivityActionKind.Undo),
            $"Mehrere Fotos: {batch.Saved} gespeichert, Aktivität „{enhanceActivity?.Result}“");
        var undoText = await _activityCenter.UndoAsync(enhanceActivity!.Id).ConfigureAwait(true);
        Check(!File.Exists(variant) && await DbScalarAsync<long>("SELECT COUNT(*) FROM PhotoVariants").ConfigureAwait(true) == 0, $"Rückgängig über die Aktivität: {undoText}");

        // (8) Einstellungen › Verbessern (hell)
        await OpenSettingsSectionAsync(NavigationTags.SettingsEnhance, "Reiter „Verbessern“").ConfigureAwait(true);
        await WaitUntilAsync(() => _enhanceSettings.IsLoaded && _enhanceSettings.Models.All(m => m.StateText != "…"), ShortWait, "Einstellungen › Verbessern geladen").ConfigureAwait(true);
        Check(_enhanceSettings.Models.Count == 10 && // seit 0.5.11 mit sieben Hochskalier-Modellen
_enhanceSettings.Models.All(m => m.CanLoad || m.IsReady), $"Modelle: {string.Join(", ", _enhanceSettings.Models.Select(m => m.ModelName + " " + m.StateText))}");
        await _theme.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await SnapshotAsync("31-verbessern-hell").ConfigureAwait(true);
        _logger.LogInformation("Rauchtest   Foto verbessern: Fassung {Variant} angelegt, verknüpft, rückgängig; Stapel 1", variantId);
    }
}
