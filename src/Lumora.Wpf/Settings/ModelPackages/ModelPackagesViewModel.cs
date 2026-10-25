using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Places.Styles;
using Lumora.Services.ModelPackages;
using Lumora.Shell;

namespace Lumora.Settings.ModelPackages;

/// <summary>
/// Einstellungen › Modelle &amp; Daten (ADR-0070, Mockup 19): alle Modelle und Datenpakete mit Zustand; Laden nur nach
/// Bestätigung (<see cref="ModelDownloadDialog"/>), Fortschritt in der Zeile und in der Aktivitäts-Anzeige, Prüfen,
/// Entfernen, Update suchen (nur auf Klick). Nichts startet beim Öffnen außer dem Lesen der Zustände.
/// </summary>
public sealed partial class ModelPackagesViewModel : ObservableObject, IDisposable
{
    private readonly IModelPackageManager _manager;
    private readonly IDialogService _dialogs;
    private readonly IMapLinkOpener _links;
    private readonly INavigationService? _navigation;
    private readonly ILogger _logger;

    [ObservableProperty] private bool _isLoaded;

    public ModelPackagesViewModel(
        IModelPackageManager manager,
        IDialogService dialogs,
        ILogger<ModelPackagesViewModel>? logger = null,
        INavigationService? navigation = null,
        IMapLinkOpener? links = null)
    {
        _manager = manager;
        _dialogs = dialogs;
        _navigation = navigation;
        _links = links ?? new ShellMapLinkOpener();
        _logger = logger ?? (ILogger)NullLogger.Instance;
        foreach (var source in manager.Packages)
        {
            var item = new ModelPackageItemViewModel(source, this);
            (source.Definition.Group switch
            {
                ModelPackageGroup.Model => ModelItems,
                ModelPackageGroup.Data => DataItems,
                _ => ExternalItems,
            }).Add(item);
        }

        _manager.PackageChanged += OnPackageChanged;
    }

    public ObservableCollection<ModelPackageItemViewModel> ModelItems { get; } = [];

    public ObservableCollection<ModelPackageItemViewModel> DataItems { get; } = [];

    public ObservableCollection<ModelPackageItemViewModel> ExternalItems { get; } = [];

    public ModelPackageItemViewModel? Find(string id) => ModelItems.Concat(DataItems).Concat(ExternalItems).FirstOrDefault(i => i.Id == id);

    /// <summary>Beim Anzeigen des Reiters: Zustände neu lesen (ohne Prüfsummen, ohne Netz außer dem lokalen Ollama).</summary>
    public async Task RefreshAsync()
    {
        await Task.WhenAll(ModelItems.Concat(DataItems).Concat(ExternalItems).Select(RefreshItemAsync)).ConfigureAwait(true);
        IsLoaded = true;
    }

    internal async Task RefreshItemAsync(ModelPackageItemViewModel item)
    {
        try
        {
            // Dateiprüfungen (Laufwerk im Ruhezustand, Virenscanner) und Ollama nicht auf dem UI-Thread
            item.Apply(await Task.Run(() => _manager.GetStatusAsync(item.Id)).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Modelle: Zustand von {Id} nicht ermittelbar ({Error})", item.Id, ex.GetType().Name);
            item.ShowMessage("Zustand nicht ermittelbar.", error: true);
        }
    }

    /// <summary>„Laden…“: Bestätigung, dann laden.</summary>
    internal async Task LoadAsync(ModelPackageItemViewModel item)
    {
        ModelDownloadRequest? request;
        try
        {
            request = await ModelDownloadDialog.AskAsync(_dialogs, _manager, item.Id).ConfigureAwait(true);
        }
        catch (ModelPackageException ex)
        {
            item.ShowMessage(ex.Message, error: true);
            return;
        }

        if (request is not null)
        {
            await InstallConfirmedAsync(item, request).ConfigureAwait(true);
        }
    }

    /// <summary>Laden nach der Bestätigung (Rauchtest, Tests).</summary>
    internal async Task InstallConfirmedAsync(ModelPackageItemViewModel item, ModelDownloadRequest request)
    {
        using var cts = new CancellationTokenSource();
        item.DownloadCts = cts;
        item.IsBusy = true;
        item.IsDownloading = true;
        item.ProgressPercent = 0;
        item.ProgressText = "Verbindung wird aufgebaut…";
        item.ShowMessage(null, error: false);
        try
        {
            var progress = new Progress<ModelPackageProgress>(p =>
            {
                item.ProgressPercent = p.Total > 0 ? Math.Clamp(100.0 * p.Done / p.Total, 0, 100) : item.ProgressPercent;
                item.ProgressText = p.Total > 0 ? $"{p.Stage}: {p.Done / 1_048_576.0:N0} von {p.Total / 1_048_576.0:N0} MB" : $"{p.Stage}…";
            });
            await Task.Run(() => _manager.InstallAsync(request, progress, cts.Token), cts.Token).ConfigureAwait(true);
            item.ShowMessage("Geladen und geprüft (SHA-256).", error: false);
        }
        catch (OperationCanceledException)
        {
            item.ShowMessage("Abgebrochen — angefangene Dateien wurden verworfen.", error: false);
        }
        catch (Exception ex)
        {
            // Der Manager liefert Benutzer-Meldungen ohne Pfade (ModelPackageException bzw. die der Modell-Anbieter)
            item.ShowMessage(ex.Message, error: true);
        }
        finally
        {
            item.DownloadCts = null;
            item.IsDownloading = false;
            item.ProgressText = null;
            item.IsBusy = false;
            await RefreshItemAsync(item).ConfigureAwait(true);
        }
    }

    internal async Task VerifyAsync(ModelPackageItemViewModel item)
    {
        item.IsBusy = true;
        item.ShowMessage("Wird geprüft…", error: false);
        try
        {
            var result = await Task.Run(() => _manager.VerifyAsync(item.Id)).ConfigureAwait(true);
            item.ShowMessage(result.Message, error: !result.Ok);
        }
        catch (Exception ex)
        {
            item.ShowMessage(ex is ModelPackageException ? ex.Message : $"Prüfen nicht möglich ({ex.GetType().Name}).", error: true);
        }
        finally
        {
            item.IsBusy = false;
            await RefreshItemAsync(item).ConfigureAwait(true);
        }
    }

    internal async Task RemoveAsync(ModelPackageItemViewModel item)
    {
        var ok = await _dialogs.ConfirmAsync(
            $"{item.Title} entfernen?",
            $"Die Dateien von „{item.Name}“ ({item.SizeText}) werden gelöscht. Was Lumora damit schon erkannt hat, bleibt im Katalog. " +
            "Mit „Laden…“ lässt sich das Modell später wieder holen (nach Bestätigung).",
            "Entfernen",
            isDestructive: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        await RemoveConfirmedAsync(item).ConfigureAwait(true);
    }

    internal async Task RemoveConfirmedAsync(ModelPackageItemViewModel item)
    {
        item.IsBusy = true;
        try
        {
            await Task.Run(() => _manager.RemoveAsync(item.Id)).ConfigureAwait(true);
            item.ShowMessage("Entfernt.", error: false);
        }
        catch (Exception ex)
        {
            item.ShowMessage(ex is ModelPackageException ? ex.Message : $"Entfernen nicht möglich ({ex.GetType().Name}).", error: true);
        }
        finally
        {
            item.IsBusy = false;
            await RefreshItemAsync(item).ConfigureAwait(true);
        }
    }

    internal async Task CheckUpdateAsync(ModelPackageItemViewModel item)
    {
        item.IsBusy = true;
        item.ShowMessage("Frage beim Hersteller nach…", error: false);
        try
        {
            var result = await Task.Run(() => _manager.CheckForUpdateAsync(item.Id)).ConfigureAwait(true);
            item.ShowMessage(result.Message, error: result.Outcome == ModelUpdateOutcome.Failed);
        }
        catch (Exception ex)
        {
            item.ShowMessage(ex is ModelPackageException ? ex.Message : $"Update-Suche nicht möglich ({ex.GetType().Name}).", error: true);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    internal void OpenWebsite(ModelPackageItemViewModel item)
    {
        if (item.Website is { } uri && !_links.Open(uri))
        {
            item.ShowMessage("Die Website konnte nicht geöffnet werden.", error: true);
        }
    }

    internal void OpenAssistantSettings() => _navigation?.NavigateTo(NavigationTags.SettingsAssistant);

    private void OnPackageChanged(string id)
        => UiThread.Post(() =>
        {
            if (Find(id) is { IsBusy: false } item)
            {
                _ = RefreshItemAsync(item);
            }
        });

    public void Dispose()
    {
        _manager.PackageChanged -= OnPackageChanged;
        foreach (var item in ModelItems)
        {
            item.DownloadCts?.Cancel();
        }
    }
}
