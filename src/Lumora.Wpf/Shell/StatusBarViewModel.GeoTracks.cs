using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Messaging;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Tracks;

namespace Lumora.Shell;

/// <summary>
/// Ungenaue Orte und GPS-Spuren in der Statusleiste (ADR-0062, Mockup 10): „GPS-Spuren: 120 / 1 284 Dateien“ beim
/// Einlesen, „Orte prüfen: 1.234 / 19.556“ während der Genauigkeits-Prüfung, sonst „Orte: 158 geschätzt · 122 Vorschläge ·
/// 1 284 Garmin-Aktivitäten“ (sobald die Seite „Orte“ die Zahlen kennt; ohne geschätzte Orte und Spuren ausgeblendet).
/// </summary>
public sealed partial class StatusBarViewModel
{
    private GpsTrackImportStatus _trackImport = GpsTrackImportStatus.Idle;
    private GeoAccuracyStatus _accuracy = GeoAccuracyStatus.Idle;
    private GeoPlacesSummaryMessage? _placesSummary;

    /// <summary>Text des Hinweises; null = ausblenden.</summary>
    [ObservableProperty] private string? _geoTracksText;

    /// <summary>Im Konstruktor aufgerufen (eine Zeile in <c>StatusBarViewModel.cs</c>).</summary>
    private void InitGeoTracks()
    {
        _messenger.Register<StatusBarViewModel, GpsTrackImportStatusMessage>(this, static (r, m) => UiThread.Post(() => { r._trackImport = m.Status; r.UpdateGeoTracksText(); }));
        _messenger.Register<StatusBarViewModel, GeoAccuracyStatusMessage>(this, static (r, m) => UiThread.Post(() => { r._accuracy = m.Status; r.UpdateGeoTracksText(); }));
        _messenger.Register<StatusBarViewModel, GeoPlacesSummaryMessage>(this, static (r, m) => UiThread.Post(() => { r._placesSummary = m; r.UpdateGeoTracksText(); }));
    }

    /// <summary>
    /// Nur die Zusammenfassung „Orte: n geschätzt · m Vorschläge · k Garmin-Aktivitäten“ — Einlesen und Prüfung zeigt seit
    /// 0.4.36 die Aktivitäts-Anzeige (ADR-0067); null = ausblenden.
    /// </summary>
    [ObservableProperty] private string? _geoPlacesSummaryText;

    private void UpdateGeoTracksText()
    {
        GeoTracksText = DescribeGeoTracks(_trackImport, _accuracy, _placesSummary);
        GeoPlacesSummaryText = DescribeGeoTracks(GpsTrackImportStatus.Idle, GeoAccuracyStatus.Idle, _placesSummary);
    }

    /// <summary>Text je Zustand; null = ausblenden.</summary>
    internal static string? DescribeGeoTracks(GpsTrackImportStatus import, GeoAccuracyStatus accuracy, GeoPlacesSummaryMessage? summary)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        if (import.IsRunning)
        {
            return $"GPS-Spuren: {import.FilesRead.ToString("N0", de)} / {import.FilesFound.ToString("N0", de)} Dateien";
        }

        if (accuracy.IsRunning)
        {
            return $"Orte prüfen: {accuracy.Done.ToString("N0", de)} / {accuracy.Total.ToString("N0", de)}";
        }

        if (summary is null || (summary.Estimated == 0 && summary.Activities == 0))
        {
            return null;
        }

        var parts = new List<string> { $"Orte: {summary.Estimated.ToString("N0", de)} geschätzt" };
        if (summary.Suggestions is { } s)
        {
            parts.Add($"{s.ToString("N0", de)} {(s == 1 ? "Vorschlag" : "Vorschläge")}");
        }

        parts.Add($"{summary.Activities.ToString("N0", de)} Garmin-{(summary.Activities == 1 ? "Aktivität" : "Aktivitäten")}");
        return string.Join(" · ", parts);
    }
}
