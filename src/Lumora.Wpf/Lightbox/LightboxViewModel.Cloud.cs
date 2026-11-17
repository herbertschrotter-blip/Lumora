using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Services.Cloud;
using Lumora.Thumbnails.Cloud;

namespace Lumora.Lightbox;

/// <summary>
/// OneDrive-Platzhalter in der Lightbox (ADR-0077, Mockup 27): Liegt das Foto nur online, zeigt die Lightbox zuerst die
/// 1024-px-Vorschau der Shell (ohne Download) mit dem Hinweis „Wird von OneDrive geladen …“ und Fortschritt, dann lädt
/// Windows das Original (<see cref="ICloudFileHydrator"/>) und es wird wie jedes andere angezeigt. „Abbrechen“ schließt die
/// Datei sofort, die Vorschau bleibt; „Erneut laden“ versucht es noch einmal. Ohne Internet erscheint eine Meldung — ein
/// Platzhalter heißt nie „fehlt“. Nachbarn, die nur online liegen, werden nie vorab geladen. Danach melden sich die
/// Hintergrund-Analysen über <see cref="ICloudFileService.NotifyBecameLocal"/>.
/// </summary>
public sealed partial class LightboxViewModel
{
    /// <summary>Kantenlänge der Shell-Vorschau (wie das Vorschaubild der Galerie — der zweite Abruf kommt aus dem Cache).</summary>
    public const int CloudPreviewBox = ShellCloudThumbnailRenderer.RequestBox;

    public const string CloudLoadingText = "Wird von OneDrive geladen …";
    public const string CloudCancelledText = "Laden abgebrochen — angezeigt wird die Vorschau von OneDrive.";
    public const string CloudCancelledNoPreviewText = "Laden abgebrochen — das Foto liegt nur in OneDrive.";
    public const string CloudOfflineText = "OneDrive ist gerade nicht erreichbar — angezeigt wird die Vorschau, das Original liegt nur online.";
    public const string CloudOfflineNoPreviewText = "OneDrive ist gerade nicht erreichbar — das Foto liegt nur online und konnte nicht geladen werden. Internetverbindung prüfen und erneut laden.";

    private ICloudFileService? _cloudFiles;
    private ICloudFileHydrator? _cloudHydrator;
    private ICloudPreviewSource? _cloudPreview;
    private CancellationTokenSource? _cloudCts;

    /// <summary>Hinweis unten in der Lightbox (null = keiner).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloudNotice))]
    private string? _cloudNotice;

    /// <summary>Das Original wird gerade von OneDrive geladen (Fortschritt, „Abbrechen“).</summary>
    [ObservableProperty] private bool _isCloudLoading;

    /// <summary>0…1.</summary>
    [ObservableProperty] private double _cloudProgress;

    /// <summary>Abgebrochen oder ohne Verbindung: „Erneut laden“.</summary>
    [ObservableProperty] private bool _canRetryCloudLoad;

    /// <summary>Das angezeigte Foto liegt in OneDrive (Rechtsklick „Immer behalten“ / „Speicherplatz freigeben“).</summary>
    [ObservableProperty] private bool _isCurrentCloudFile;

    public bool HasCloudNotice => CloudNotice is not null;

    private void InitCloud(ICloudFileService? cloudFiles, ICloudFileHydrator? hydrator, ICloudPreviewSource? preview)
    {
        _cloudFiles = cloudFiles;
        _cloudHydrator = hydrator;
        _cloudPreview = preview;
    }

    /// <summary>Nur online und Laden möglich? Nur Attribute (kein Download).</summary>
    private bool IsCloudPlaceholder(LightboxItem item)
        => _cloudHydrator is not null && item.IsImage && CloudStateOf(item).IsOnlineOnly;

    /// <summary>
    /// Zustand nur aus den Attributen; Netzwerkpfade nie (dort gibt es keine Platzhalter, und ein getrenntes Netzlaufwerk
    /// soll den UI-Thread nicht aufhalten).
    /// </summary>
    private CloudFileState CloudStateOf(LightboxItem item)
        => _cloudFiles is { } cloud && !item.Path.StartsWith(@"\\", StringComparison.Ordinal) ? cloud.GetState(item.Path) : CloudFileState.NotCloud;

    /// <summary>Beim Wechsel des Fotos: laufendes Laden abbrechen, Hinweis weg, Cloud-Zustand für das Menü.</summary>
    private void ResetCloud(LightboxItem item)
    {
        _cloudCts?.Cancel();
        _cloudCts = null;
        CloudNotice = null;
        IsCloudLoading = false;
        CloudProgress = 0;
        CanRetryCloudLoad = false;
        IsCurrentCloudFile = CloudStateOf(item).IsCloud;
    }

    private async Task ShowCloudImageAsync(LightboxItem item, int version)
    {
        var cts = new CancellationTokenSource();
        _cloudCts = cts;
        IsLoading = false;
        BuildFileInfo(item);
        MediaInfoHeader = "Aufnahme";
        MediaInfoRows.Clear();
        ImageSource = null;
        CloudNotice = CloudLoadingText;
        IsCloudLoading = true;
        CanRetryCloudLoad = false;
        CloudProgress = 0;

        if (_cloudPreview is { } preview)
        {
            try
            {
                var bitmap = await preview.GetPreviewAsync(item.Path, CloudPreviewBox, cts.Token).ConfigureAwait(true);
                if (version != _showVersion)
                {
                    return;
                }

                if (bitmap is not null)
                {
                    ImageSource = bitmap;
                }
            }
            catch (OperationCanceledException)
            {
                ShowCloudCancelled(version, cts);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Lightbox: keine Vorschau von OneDrive");
            }
        }

        await LoadCloudOriginalAsync(item, version, cts).ConfigureAwait(true);
    }

    private async Task LoadCloudOriginalAsync(LightboxItem item, int version, CancellationTokenSource cts)
    {
        var progress = new Progress<double>(p =>
        {
            if (version == _showVersion && IsCloudLoading && ReferenceEquals(_cloudCts, cts))
            {
                CloudProgress = p;
                CloudNotice = $"{CloudLoadingText} {p:P0}";
            }
        });
        try
        {
            await _cloudHydrator!.HydrateAsync(item.Path, progress, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ShowCloudCancelled(version, cts);
            return;
        }
        catch (Exception ex)
        {
            if (version == _showVersion && ReferenceEquals(_cloudCts, cts))
            {
                IsCloudLoading = false;
                CanRetryCloudLoad = true;
                CloudNotice = ImageSource is null ? CloudOfflineNoPreviewText : CloudOfflineText;
                _logger.LogInformation("Lightbox: Original von OneDrive nicht geladen ({Error})", ex.GetType().Name);
            }

            return;
        }

        if (version != _showVersion || !ReferenceEquals(_cloudCts, cts))
        {
            return;
        }

        CloudNotice = null;
        IsCloudLoading = false;
        CloudProgress = 1;
        _cloudCts = null;
        await ShowImageAsync(item, version, afterCloud: true).ConfigureAwait(true);
        _cloudFiles?.NotifyBecameLocal(new CloudFileRef(0, item.FileId, item.Path)); // Analysen holen sie nach
    }

    private void ShowCloudCancelled(int version, CancellationTokenSource cts)
    {
        if (version != _showVersion || !ReferenceEquals(_cloudCts, cts))
        {
            return; // weiter navigiert oder Fenster zu
        }

        IsCloudLoading = false;
        CanRetryCloudLoad = true;
        CloudNotice = ImageSource is null ? CloudCancelledNoPreviewText : CloudCancelledText;
    }

    /// <summary>„Abbrechen“: das Laden von OneDrive beenden, die Vorschau bleibt.</summary>
    [RelayCommand]
    private void CancelCloudLoad() => _cloudCts?.Cancel();

    /// <summary>„Erneut laden“ nach Abbruch oder ohne Verbindung.</summary>
    [RelayCommand]
    private async Task RetryCloudLoadAsync()
    {
        if (CurrentItem is not { } item || _cloudHydrator is null)
        {
            return;
        }

        var version = _showVersion;
        if (!IsCloudPlaceholder(item))
        {
            CloudNotice = null;
            CanRetryCloudLoad = false;
            await ShowImageAsync(item, version, afterCloud: true).ConfigureAwait(true); // inzwischen lokal
            return;
        }

        var cts = new CancellationTokenSource();
        _cloudCts = cts;
        CloudNotice = CloudLoadingText;
        IsCloudLoading = true;
        CanRetryCloudLoad = false;
        CloudProgress = 0;
        await LoadCloudOriginalAsync(item, version, cts).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task KeepCurrentOnDeviceAsync() => SetCurrentPinAsync(CloudPinAction.KeepOnDevice);

    [RelayCommand]
    private Task FreeCurrentSpaceAsync() => SetCurrentPinAsync(CloudPinAction.FreeUpSpace);

    private async Task SetCurrentPinAsync(CloudPinAction action)
    {
        if (_cloudFiles is not { } cloud || CurrentItem is not { } item)
        {
            return;
        }

        var result = await Task.Run(() => cloud.SetPinState([new CloudFileRef(0, item.FileId, item.Path)], action)).ConfigureAwait(true);
        ShowStatus(result.StatusText);
    }
}
