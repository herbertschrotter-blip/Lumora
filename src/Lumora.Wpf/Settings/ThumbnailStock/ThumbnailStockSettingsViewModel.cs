using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Lumora.Services.Thumbnails;

namespace Lumora.Settings.ThumbnailStock;

/// <summary>
/// Schalter „Vorschaubilder auf Vorrat erstellen“ (0.5.40) in Einstellungen › Speicher &amp; Wartung, Karte „Vorschaubilder“ —
/// neben Ordner, Belegung und „Cache leeren“. Gilt je Katalog (die versteckte Bibliothek hat ihren eigenen), Standard an;
/// wirkt sofort (aus beendet einen laufenden Durchgang nach den Dateien, die gerade entstehen).
/// </summary>
public sealed partial class ThumbnailStockSettingsViewModel : ObservableObject
{
    private readonly IThumbnailStockService _stock;
    private readonly ILogger<ThumbnailStockSettingsViewModel> _logger;
    private bool _loading;

    public ThumbnailStockSettingsViewModel(IThumbnailStockService stock, ILogger<ThumbnailStockSettingsViewModel> logger)
    {
        _stock = stock;
        _logger = logger;
        Refresh();
    }

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string? _statusText;

    /// <summary>Stand des Dienstes anzeigen, ohne ihn neu zu speichern (beim Anzeigen der Seite).</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            IsEnabled = _stock.IsEnabled;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading)
        {
            _ = SaveAsync(value);
        }
    }

    /// <summary>Zuletzt begonnenes Speichern des Schalters (Tests und Rauchtest warten darauf).</summary>
    internal Task SavePending { get; private set; } = Task.CompletedTask;

    private Task SaveAsync(bool enabled) => SavePending = SaveCoreAsync(enabled);

    private async Task SaveCoreAsync(bool enabled)
    {
        try
        {
            await _stock.SetEnabledAsync(enabled).ConfigureAwait(true);
            StatusText = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einstellungen › Vorschaubilder auf Vorrat: nicht gespeichert");
            StatusText = "Nicht gespeichert: " + ex.Message;
        }
    }
}
