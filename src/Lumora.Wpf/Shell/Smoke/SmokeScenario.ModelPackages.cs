using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using Lumora.Services.Activity;
using Lumora.Services.ModelPackages;
using Lumora.Settings;
using Lumora.Settings.ModelPackages;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Modelle &amp; Daten“ (ADR-0070, Mockup 19): Reiter öffnen → alle Einträge (Gesichter, Motive, Testmodell,
/// Ortsnamen, Kartenkacheln, Ollama) mit Zustand; Laden ohne Bestätigung abgelehnt (kein Abruf); Testmodell nach Bestätigung
/// über den Fake-Downloader geladen und per SHA-256 geprüft, Aktivität „Modell laden“ unter „Zuletzt fertig“ (Screenshot hell);
/// Datei verfälscht → „Prüfen“ meldet defekt (Screenshot dunkel); „Update suchen“ ohne Quelle; Entfernen → nicht geladen.
/// Kein echter Download (fremde Adressen lehnt der Fake ab), keine Netzabfrage.
/// </summary>
internal sealed partial class SmokeScenario
{
    private ModelPackagesViewModel? _modelPackages;
    private IModelPackageManager? _modelManager;
    private SmokeModelDownloader? _modelDownloader;

    private void InitModelPackagesSmoke(IServiceProvider services)
    {
        _modelPackages = services.GetService<ModelPackagesViewModel>();
        _modelManager = services.GetService<IModelPackageManager>();
        _modelDownloader = services.GetService<IModelDownloader>() as SmokeModelDownloader;
    }

    private async Task ModelPackagesAsync()
    {
        Check(_modelPackages is not null && _modelManager is not null && _modelDownloader is not null,
            "Modelle & Daten: Ansicht, Manager und Fake-Downloader registriert");
        var themeBefore = _theme.Mode;
        try
        {
            await ModelPackagesCoreAsync(_modelPackages!, _modelManager!, _modelDownloader!).ConfigureAwait(true);
        }
        finally
        {
            if (_modelManager!.Find(SmokeModelPackages.Id) is FileModelPackageSource source && Directory.Exists(source.Folder))
            {
                Directory.Delete(source.Folder, recursive: true);
            }

            await _theme.SetModeAsync(themeBefore).ConfigureAwait(true);
            _navigation.NavigateTo(NavigationTags.Gallery);
        }
    }

    /// <summary>Zeile des Testmodells in den sichtbaren Bereich scrollen (für die Screenshots).</summary>
    private async Task ShowModelRowAsync(ModelPackageItemViewModel item)
    {
        if (_settingsPage.FindName("ModelsView") is SettingsModelPackagesSection section
            && section.ModelList.ItemContainerGenerator.ContainerFromItem(item) is System.Windows.FrameworkElement row)
        {
            row.BringIntoView();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private async Task ModelPackagesCoreAsync(ModelPackagesViewModel vm, IModelPackageManager manager, SmokeModelDownloader downloader)
    {
        // (1) Reiter öffnen, Zustände gelesen
        await OpenSettingsSectionAsync(NavigationTags.SettingsModels, "Reiter „Modelle & Daten“").ConfigureAwait(true);
        await WaitUntilAsync(() => vm.IsLoaded, LongWait, "Zustände gelesen").ConfigureAwait(true);
        var models = vm.ModelItems.Select(i => i.Id).ToList();
        Check(models.SequenceEqual([
                ModelPackageCatalog.FacesId, ModelPackageCatalog.MotifsId,
                Services.Enhance.EnhanceModelCatalog.UpscaleId, Services.Enhance.EnhanceModelCatalog.FacesId, // „Foto verbessern“ (ADR-0071)
                Services.Enhance.EnhanceModelCatalog.ColorizeId, Services.Enhance.EnhanceModelCatalog.InpaintId,
                SmokeModelPackages.Id]),
            $"Modelle: {string.Join(", ", models)}");
        Check(vm.DataItems.Select(i => i.Id).SequenceEqual([ModelPackageCatalog.GeoNamesId, ModelPackageCatalog.MapTilesId])
              && vm.ExternalItems.Single().Id == ModelPackageCatalog.AssistantId, "Daten: Ortsnamen, Kartenkacheln; Extern: Ollama");
        var faces = vm.Find(ModelPackageCatalog.FacesId)!;
        Check(faces.LicenseRestricted && faces.LicenseText.Contains("nicht-kommerziell", StringComparison.Ordinal) && faces.UpdateHint == "Version 0.7, keine automatische Prüfung.",
            $"Gesichter: Lizenz-Hinweis „{faces.LicenseText}“, „{faces.UpdateHint}“, Zustand „{faces.StateText}“");
        var geo = vm.Find(ModelPackageCatalog.GeoNamesId)!;
        Check(geo.State == ModelPackageState.BuiltIn && !geo.CanLoad, $"Ortsnamen: {geo.StateText}");
        Check(vm.Find(ModelPackageCatalog.MapTilesId)!.State == ModelPackageState.OnDemand, "Kartenkacheln: bei Bedarf");
        var ollama = vm.Find(ModelPackageCatalog.AssistantId)!;
        Check(ollama.State is ModelPackageState.ExternalReady or ModelPackageState.ExternalMissing && !ollama.CanLoad && !ollama.CanRemove,
            $"Ollama nur Status: {ollama.StateText}");
        Check(downloader.Requests == 0, "Beim Öffnen nichts geladen");

        // (2) Testmodell: nicht geladen; ohne Bestätigung kein Abruf
        var item = vm.Find(SmokeModelPackages.Id)!;
        Check(item.State == ModelPackageState.NotInstalled && item.CanLoad && item.LoadCommand.CanExecute(null), $"Testmodell: {item.StateText}");
        var request = manager.DescribeDownload(SmokeModelPackages.Id);
        var text = ModelDownloadDialog.MessageOf(request);
        Check(text.Contains("Lumora-Rauchtest-Modell", StringComparison.Ordinal) && text.Contains("rauchtest.lumora.invalid", StringComparison.Ordinal)
              && text.Contains("Größe:", StringComparison.Ordinal) && text.Contains("Lizenz:", StringComparison.Ordinal),
            "Bestätigung nennt Name, Quelle, Größe, Lizenz");
        var refused = false;
        try
        {
            await manager.InstallAsync(request).ConfigureAwait(true);
        }
        catch (ModelDownloadNotConfirmedException)
        {
            refused = true;
        }

        Check(refused && downloader.Requests == 0, "Ohne Bestätigung abgelehnt, kein Abruf");

        // (3) Nach Bestätigung laden → geprüft, Vermerk, Aktivität
        await vm.InstallConfirmedAsync(item, request.Confirm()).ConfigureAwait(true);
        var source = (FileModelPackageSource)manager.Find(SmokeModelPackages.Id)!;
        Check(item.State == ModelPackageState.Installed && item.CheckedText is not null && downloader.Requests == 2
              && File.Exists(source.ManifestPath) && !Directory.EnumerateFiles(source.Folder, "*.download", SearchOption.AllDirectories).Any(),
            $"Geladen und geprüft: {item.StateText}, {item.CheckedText}, {downloader.Requests} Abrufe");
        var activity = _activityCenter?.GetOverview().Recent.FirstOrDefault(r => r.Kind == ActivityKind.ModelDownload);
        Check(activity is { State: ActivityState.Succeeded }, $"Aktivität „{activity?.Title}“: {activity?.Result}");
        await _theme.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await ShowModelRowAsync(item).ConfigureAwait(true);
        await SnapshotAsync("30-modelle-hell").ConfigureAwait(true);

        // (4) Prüfen: in Ordnung; verfälscht → defekt
        await item.VerifyCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(!item.MessageIsError && item.Message?.Contains("stimmen", StringComparison.Ordinal) == true, $"Prüfen: {item.Message}");
        var first = source.PathOf(source.Definition.FileList[0]);
        var bytes = await File.ReadAllBytesAsync(first).ConfigureAwait(true);
        bytes[0] ^= 0xFF;
        await File.WriteAllBytesAsync(first, bytes).ConfigureAwait(true);
        await item.VerifyCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(item.MessageIsError && item.State == ModelPackageState.Broken && item.LoadLabel == "Neu laden…", $"Verfälscht: {item.StateText} — {item.Message}");
        await _theme.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await ShowModelRowAsync(item).ConfigureAwait(true);
        await SnapshotAsync("30-modelle-dunkel").ConfigureAwait(true);

        // (5) Update suchen ohne Quelle; Entfernen
        await item.CheckUpdateCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(item.Message == "Version 1, keine automatische Prüfung.", $"Update suchen: {item.Message}");
        await vm.RemoveConfirmedAsync(item).ConfigureAwait(true);
        Check(item.State == ModelPackageState.NotInstalled && !Directory.Exists(source.Folder), $"Entfernt: {item.StateText}");
        _logger.LogInformation("Rauchtest   Modelle & Daten: {Count} Einträge, Testmodell geladen/geprüft/entfernt", vm.ModelItems.Count + vm.DataItems.Count + vm.ExternalItems.Count);
    }
}
