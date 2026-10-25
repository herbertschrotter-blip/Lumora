using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Lumora.Services.Quality;

namespace Lumora.Services.Covers;

/// <summary>
/// Güte eines Gesichts als Titelbild einer Person (ADR-0068): Gesichts-Güte (<c>Faces.Quality</c> = Erkennungs-Score, Größe
/// und Schärfe des Ausschnitts, ADR-0035) mal Frontalität aus den Landmarken (Nase mittig zwischen den Augen = frontal), dazu
/// die Foto-Güte (ADR-0057: Schärfe, Belichtung, Bildwirkung — nur gültige Werte, fehlende zählen neutral 0,5). Eine Quelle für
/// Personen-Kachel und Menü „Als Titelbild für ▸“: als SQLite-Funktion <see cref="FunctionName"/> registriert, damit die
/// Auswahl in derselben Abfrage wie die Zähler läuft (kein N+1).
/// </summary>
public static class FaceCoverScoring
{
    /// <summary>Name der SQLite-Funktion (Gesichts-Güte, Landmarken, Schärfe, Belichtung, Bildwirkung).</summary>
    public const string FunctionName = "lumora_face_cover_score";

    private const double FaceWeight = 0.75;
    private const double PhotoWeight = 0.25;

    /// <summary>Gesamtwert 0..1 (höher = besseres Titelbild).</summary>
    public static double Score(double faceQuality, byte[]? landmarks, double? sharpness, double? exposure, double? aesthetic)
    {
        var face = Math.Clamp(double.IsNaN(faceQuality) ? 0 : faceQuality, 0, 1) * (0.6 + (0.4 * Frontality(landmarks)));
        var parts = new[] { sharpness, exposure, aesthetic }.Where(v => v is not null && !double.IsNaN(v.Value)).Select(v => Math.Clamp(v!.Value, 0, 1)).ToList();
        var photo = parts.Count == 0 ? 0.5 : parts.Average();
        return (FaceWeight * face) + (PhotoWeight * photo);
    }

    /// <summary>
    /// 1 = frontal, 0 = Profil: Verhältnis der waagrechten Abstände Auge links → Nase und Nase → Auge rechts. Ohne lesbare
    /// Landmarken neutral 0,5.
    /// </summary>
    public static double Frontality(byte[]? landmarks)
    {
        if (landmarks is not { Length: >= 24 })
        {
            return 0.5;
        }

        var leftEyeX = BinaryPrimitives.ReadSingleLittleEndian(landmarks.AsSpan(0, 4));
        var rightEyeX = BinaryPrimitives.ReadSingleLittleEndian(landmarks.AsSpan(8, 4));
        var noseX = BinaryPrimitives.ReadSingleLittleEndian(landmarks.AsSpan(16, 4));
        var left = Math.Abs(noseX - leftEyeX);
        var right = Math.Abs(rightEyeX - noseX);
        var sum = left + right;
        if (!float.IsFinite(sum) || sum <= 1e-6)
        {
            return 0.5;
        }

        var asymmetry = Math.Abs(left - right) / sum;
        return Math.Clamp(1 - asymmetry, 0, 1);
    }

    /// <summary>Registriert <see cref="FunctionName"/> auf der Verbindung (mehrfaches Registrieren ist harmlos).</summary>
    public static void Register(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.CreateFunction<double, byte[]?, double?, double?, double?, double>(
            FunctionName,
            static (quality, landmarks, sharpness, exposure, aesthetic) => Score(quality, landmarks, sharpness, exposure, aesthetic),
            isDeterministic: true);
    }

    /// <summary>
    /// SQL-Ausdruck des Werts für ein Gesicht <paramref name="face"/> mit Datei <paramref name="file"/> und LEFT JOIN auf
    /// FileQuality <paramref name="quality"/> (nur gültige Güte-Werte, ADR-0057).
    /// </summary>
    public static string SqlScore(string face, string file, string quality)
    {
        var valid = FileQualityIndexService.ValidQuality(quality, file) + $" AND {quality}.ErrorCode IS NULL";
        return $"{FunctionName}({face}.Quality, {face}.Landmarks, CASE WHEN {valid} THEN {quality}.Sharpness END, "
               + $"CASE WHEN {valid} THEN {quality}.Exposure END, CASE WHEN {valid} THEN {quality}.Aesthetic END)";
    }
}
