namespace Lumora.Services.Thumbnails;

/// <summary>
/// Stabile ErrorCodes der Renderer (landen in <c>Thumbnails.ErrorCode</c>). Der WIC-Renderer im
/// Wpf-Projekt liefert <see cref="WicDecodeFailed"/>; darauf reagiert der FFmpeg-Fallback.
/// <see cref="SourceMissing"/> und <see cref="FfmpegNotFound"/> sind „äußere“ Ursachen: sie verbrauchen
/// keinen <c>AttemptCount</c> (siehe <see cref="ThumbnailService"/>).
/// </summary>
public static class ThumbnailErrorCodes
{
    public const string SourceMissing = "source_missing";
    public const string WicDecodeFailed = "wic_decode_failed";
    public const string FfmpegNotFound = "ffmpeg_not_found";
    public const string FfmpegStartFailed = "ffmpeg_start_failed";
    public const string FfmpegException = "ffmpeg_exception";

    /// <summary>Lauf vom Aufrufer abgebrochen (Shutdown) — kein Dateiproblem.</summary>
    public const string FfmpegCancelled = "ffmpeg_cancelled";

    /// <summary>FFmpeg hing länger als das Frame-Timeout (typisch kaputte Datei) — zählt als Versuch.</summary>
    public const string FfmpegTimeout = "ffmpeg_timeout";

    public const string FfmpegFailed = "ffmpeg_failed";

    /// <summary>
    /// OneDrive-Platzhalter (ADR-0077): die Shell lieferte kein Vorschaubild (keine Verbindung, Zeitgrenze, kein Anbieter-
    /// Vorschaubild) oder es gibt keinen Shell-Weg (Tests, versteckte Sitzung). Äußere Ursache: verbraucht keinen Versuch —
    /// das Original wird dafür nie dekodiert (das löste einen Download aus).
    /// </summary>
    public const string CloudUnavailable = "cloud_unavailable";
}
