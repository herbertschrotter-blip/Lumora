using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Imaging.Merge;

namespace Lumora.Merge;

/// <summary>
/// Einstellungen › Zusammenfügen (ADR-0078 Punkt 17, Mockup 28 Ansicht 15) — Stufe 1: „Farbe kräftiger“, Vorauswahl der Kugel
/// (Horizontstreifen | 2 : 1), Ablage (neben dem ersten Einzelbild | Unterordner; Drohnen-Ordner eine Ebene höher), Namenszusatz,
/// JPEG-Qualität, Übernahme von Tags und Sternen/Favorit, Größe/Speichergrenze (Anzeige) und der Rechen-Prozess (OpenCV).
/// Serien erkennen kommt mit Stufe 2. Änderungen werden sofort gespeichert (Settings-Tabelle des Katalogs, <c>merge.*</c>).
/// </summary>
public sealed partial class MergeSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IMergeService _service;
    private readonly ILogger _logger;
    private bool _loading = true;
    private CancellationTokenSource? _saveCts;

    public MergeSettingsViewModel(IMergeService service, ILogger<MergeSettingsViewModel>? logger = null)
    {
        _service = service;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    [ObservableProperty] private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBoostText))]
    private int _colorBoost = MergeLimits.ColorBoostDefault;

    public string ColorBoostText => MergeTexts.ColorBoost(ColorBoost);

    public int ColorBoostMax => MergeLimits.ColorBoostMax;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SphereFull))]
    private bool _sphereBand = true;

    public bool SphereFull
    {
        get => !SphereBand;
        set => SphereBand = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextToFirst), nameof(FileNameExample))]
    private bool _useSubfolder;

    public bool NextToFirst
    {
        get => !UseSubfolder;
        set => UseSubfolder = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileNameExample))]
    private string _subfolderName = MergeSettings.DefaultSubfolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileNameExample))]
    private string _panoramaSuffix = MergeSettings.DefaultPanoramaSuffix;

    [ObservableProperty] private bool _droneFolderOneUp;

    [ObservableProperty] private int _jpegQuality = 92;

    [ObservableProperty] private bool _copyCommonTags = true;

    [ObservableProperty] private bool _copyRating = true;

    /// <summary>Beispiel „IMG_0001_panorama.jpg“ bzw. mit Unterordner.</summary>
    public string FileNameExample => (UseSubfolder ? MergeNaming.CleanFolderName(SubfolderName) + "\\" : string.Empty) + "IMG_0001" + MergeNaming.CleanSuffix(PanoramaSuffix) + ".jpg";

    /// <summary>Freier Arbeitsspeicher und Budget (Anzeige „Größe und Speichergrenze“).</summary>
    [ObservableProperty] private string _memoryText = string.Empty;

    /// <summary>Zustand des Rechen-Prozesses (OpenCV).</summary>
    [ObservableProperty] private string _toolState = "wird geprüft…";

    [ObservableProperty] private string _toolDetail = "OpenCV im eigenen Rechen-Prozess";

    [ObservableProperty] private bool _toolReady;

    public async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            var s = await _service.LoadSettingsAsync().ConfigureAwait(true);
            ColorBoost = s.ColorBoost;
            SphereBand = s.SphereForm == MergeSphereForm.Band;
            UseSubfolder = s.Location == MergeSaveLocation.Subfolder;
            SubfolderName = s.SubfolderName;
            PanoramaSuffix = s.PanoramaSuffix;
            DroneFolderOneUp = s.DroneFolderOneUp;
            JpegQuality = s.JpegQuality;
            CopyCommonTags = s.CopyCommonTags;
            CopyRating = s.CopyRating;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Einstellungen › Zusammenfügen nicht lesbar");
        }
        finally
        {
            _loading = false;
        }

        var (_, available) = MergeMemoryBudget.Query();
        var budget = MergeMemoryBudget.Budget();
        MemoryText = available > 0
            ? $"Frei {MergeTexts.Bytes(available)} · Lumora rechnet bis {MergeTexts.Bytes(Math.Max(0, budget))} (frei − 1,5 GB); darüber die größte passende Stufe. Keine Kante über 65 500 px (JPEG)."
            : "Volle Auflösung; reicht der Arbeitsspeicher nicht, die größte passende Stufe. Keine Kante über 65 500 px (JPEG).";
        IsLoaded = true;
        _ = CheckToolAsync();
    }

    private async Task CheckToolAsync()
    {
        try
        {
            var a = await _service.GetAvailabilityAsync().ConfigureAwait(true);
            ToolReady = a.Available;
            ToolState = a.Available ? "geladen" : "fehlt";
            ToolDetail = a.Available ? $"OpenCV {a.OpenCvVersion ?? "?"} im eigenen Rechen-Prozess (stürzt er ab, läuft Lumora weiter)" : a.Reason ?? "nicht verfügbar";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ToolState = "fehlt";
            ToolDetail = ex.Message;
        }
    }

    partial void OnColorBoostChanged(int value) => ScheduleSave(250);

    partial void OnSphereBandChanged(bool value) => ScheduleSave(0);

    partial void OnUseSubfolderChanged(bool value) => ScheduleSave(0);

    partial void OnSubfolderNameChanged(string value) => ScheduleSave(400);

    partial void OnPanoramaSuffixChanged(string value) => ScheduleSave(400);

    partial void OnDroneFolderOneUpChanged(bool value) => ScheduleSave(0);

    partial void OnJpegQualityChanged(int value) => ScheduleSave(300);

    partial void OnCopyCommonTagsChanged(bool value) => ScheduleSave(0);

    partial void OnCopyRatingChanged(bool value) => ScheduleSave(0);

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

            await SaveNowAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public Task SaveNowAsync()
        => _service.SaveSettingsAsync(new MergeSettings(
            SphereBand ? MergeSphereForm.Band : MergeSphereForm.Full,
            UseSubfolder ? MergeSaveLocation.Subfolder : MergeSaveLocation.NextToFirst,
            SubfolderName,
            PanoramaSuffix,
            Math.Clamp(JpegQuality, 50, 100),
            CopyCommonTags,
            CopyRating,
            DroneFolderOneUp,
            Math.Clamp(ColorBoost, MergeLimits.ColorBoostOff, MergeLimits.ColorBoostMax)));

    public void Dispose()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
    }

    internal static string Percent(int value) => value.ToString(CultureInfo.InvariantCulture) + " %";
}
