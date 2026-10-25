using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Enhance;
using Lumora.Services.ModelPackages;
using Lumora.Settings.ModelPackages;
using Lumora.Shell;

namespace Lumora.Enhance;

/// <summary>Zeile „Modelle für Foto verbessern“: Werkzeug, Modell, Quelle, Größe, Lizenz, Zustand, „Laden…“.</summary>
public sealed partial class EnhanceModelRow : ObservableObject
{
    public EnhanceModelRow(EnhanceSettingsViewModel owner, string toolName, ModelPackageDefinition definition)
    {
        Owner = owner;
        ToolName = toolName;
        Definition = definition;
    }

    public EnhanceSettingsViewModel Owner { get; }

    public ModelPackageDefinition Definition { get; }

    public string Id => Definition.Id;

    public string ToolName { get; }

    public string ModelName => Definition.Name;

    public string Origin => Definition.Origin.Name;

    public string SizeText => "≈ " + ModelDownloadDialog.SizeText(Definition.DownloadSize);

    public string License => Definition.License;

    [ObservableProperty] private string _stateText = "…";

    [ObservableProperty] private bool _isReady;

    [ObservableProperty] private bool _canLoad;

    [ObservableProperty] private bool _isLoading;
}

/// <summary>
/// Einstellungen › Verbessern (ADR-0071, Mockup 13): Modelle mit Quelle, Größe, Lizenz und Zustand (Laden nur nach Bestätigung
/// über den Update-Manager; alles Weitere in Modelle &amp; Daten), Speicherort, Dateiname, Metadaten übernehmen, beste Fassung
/// zeigen, Grafikkarte. Änderungen werden sofort gespeichert (Settings-Tabelle des Katalogs).
/// </summary>
public sealed partial class EnhanceSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IEnhanceSettingsStore _store;
    private readonly IModelPackageManager? _models;
    private readonly IDialogService? _dialogs;
    private readonly SettingsSectionState? _sections;
    private readonly ILogger _logger;
    private bool _loading = true;
    private CancellationTokenSource? _saveCts;

    public EnhanceSettingsViewModel(
        IEnhanceSettingsStore store,
        IModelPackageManager? models = null,
        IDialogService? dialogs = null,
        SettingsSectionState? sections = null,
        ILogger<EnhanceSettingsViewModel>? logger = null)
    {
        _store = store;
        _models = models;
        _dialogs = dialogs;
        _sections = sections;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        Models =
        [
            new(this, "Hochskalieren / schärfen", EnhanceModelCatalog.Upscale),
            new(this, "Gesichter restaurieren", EnhanceModelCatalog.Faces),
            new(this, "Einfärben", EnhanceModelCatalog.Colorize),
            new(this, "Entfernen / Kratzer", EnhanceModelCatalog.Inpaint),
        ];
        if (_models is not null)
        {
            _models.PackageChanged += OnPackageChanged;
        }
    }

    public ObservableCollection<EnhanceModelRow> Models { get; }

    [ObservableProperty] private bool _isLoaded;

    [ObservableProperty] private bool _useSubfolder;

    [ObservableProperty] private string _subfolderName = EnhanceSettings.DefaultSubfolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileNameExample))]
    private string _fileNamePattern = EnhanceSettings.DefaultPattern;

    [ObservableProperty] private bool _copyMetadata = true;

    [ObservableProperty] private bool _copyRatingAndTags = true;

    [ObservableProperty] private bool _showBestVariant;

    [ObservableProperty] private bool _useGpu = true;

    [ObservableProperty] private string? _message;

    /// <summary>Beispiel: „1987-07 Garten_verbessert.jpg“ für „1987-07 Garten.jpg“.</summary>
    public string FileNameExample => EnhanceNaming.FileName("Foto.jpg", FileNamePattern);

    public async Task RefreshAsync()
    {
        try
        {
            var settings = await _store.LoadAsync().ConfigureAwait(true);
            _loading = true;
            UseSubfolder = settings.Location == EnhanceSaveLocation.Subfolder;
            SubfolderName = settings.SubfolderName;
            FileNamePattern = settings.FileNamePattern;
            CopyMetadata = settings.CopyMetadata;
            CopyRatingAndTags = settings.CopyRatingAndTags;
            ShowBestVariant = settings.ShowBestVariant;
            UseGpu = settings.UseGpu;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einstellungen › Verbessern nicht lesbar");
        }
        finally
        {
            _loading = false;
        }

        await RefreshModelsAsync().ConfigureAwait(true);
        IsLoaded = true;
    }

    public async Task RefreshModelsAsync()
    {
        foreach (var row in Models)
        {
            if (_models?.Find(row.Id) is null)
            {
                row.StateText = "Quelle folgt";
                row.IsReady = false;
                row.CanLoad = false;
                continue;
            }

            try
            {
                var status = await _models.GetStatusAsync(row.Id).ConfigureAwait(true);
                row.StateText = status.Text;
                row.IsReady = status.IsInstalled;
                row.CanLoad = !status.IsInstalled && _dialogs is not null && !_models.IsBusy(row.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Zustand von {Id} nicht lesbar", row.Id);
            }
        }
    }

    private void OnPackageChanged(string id)
    {
        if (id.StartsWith("enhance.", StringComparison.Ordinal))
        {
            UiThread.Post(() => _ = RefreshModelsAsync());
        }
    }

    [RelayCommand]
    private async Task LoadModelAsync(EnhanceModelRow? row)
    {
        if (row is null || _models is null || _dialogs is null || row.IsLoading)
        {
            return;
        }

        var request = await ModelDownloadDialog.AskAsync(_dialogs, _models, row.Id).ConfigureAwait(true);
        if (request is null)
        {
            return;
        }

        row.IsLoading = true;
        row.CanLoad = false;
        try
        {
            await _models.InstallAsync(request).ConfigureAwait(true);
            Message = $"{row.ModelName} geladen und geprüft.";
        }
        catch (OperationCanceledException)
        {
            Message = "Laden abgebrochen.";
        }
        catch (Exception ex) when (ex is ModelPackageException or IOException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            Message = ex.Message;
        }
        finally
        {
            row.IsLoading = false;
            await RefreshModelsAsync().ConfigureAwait(true);
        }
    }

    /// <summary>„Alle Modelle in Modelle &amp; Daten“: Reiter wechseln.</summary>
    [RelayCommand]
    private void OpenModelPackages()
    {
        if (_sections is not null)
        {
            _sections.SelectedSection = NavigationTags.SettingsModels;
        }
    }

    partial void OnUseSubfolderChanged(bool value) => ScheduleSave(0);

    partial void OnSubfolderNameChanged(string value) => ScheduleSave(400);

    partial void OnFileNamePatternChanged(string value) => ScheduleSave(400);

    partial void OnCopyMetadataChanged(bool value) => ScheduleSave(0);

    partial void OnCopyRatingAndTagsChanged(bool value) => ScheduleSave(0);

    partial void OnShowBestVariantChanged(bool value) => ScheduleSave(0);

    partial void OnUseGpuChanged(bool value) => ScheduleSave(0);

    /// <summary>Aktuelle Werte als Einstellungen.</summary>
    public EnhanceSettings Current => new(
        UseSubfolder ? EnhanceSaveLocation.Subfolder : EnhanceSaveLocation.NextToOriginal,
        EnhanceNaming.CleanFolderName(SubfolderName),
        string.IsNullOrWhiteSpace(FileNamePattern) ? EnhanceSettings.DefaultPattern : FileNamePattern.Trim(),
        CopyMetadata,
        CopyRatingAndTags,
        ShowBestVariant,
        UseGpu);

    /// <summary>Speichert sofort (Tests, Rauchtest) — sonst gedrosselt nach jeder Änderung.</summary>
    public Task SaveNowAsync()
    {
        _saveCts?.Cancel();
        return _store.SaveAsync(Current);
    }

    private void ScheduleSave(int delayMs)
    {
        if (_loading)
        {
            return;
        }

        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        _ = SaveLaterAsync(delayMs, cts.Token);
    }

    private async Task SaveLaterAsync(int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(true);
            }

            if (!ct.IsCancellationRequested)
            {
                await _store.SaveAsync(Current, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einstellungen › Verbessern nicht gespeichert");
            Message = $"Nicht gespeichert: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_models is not null)
        {
            _models.PackageChanged -= OnPackageChanged;
        }

        _saveCts?.Cancel();
    }
}
