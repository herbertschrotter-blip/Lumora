namespace Lumora.AppConnect;

/// <summary>
/// Verständliche Texte zu den Rechten aus Abschnitt 6 des Vertrags (Mockup 11, Karte je App). Unbekannte Rechte zeigt Lumora
/// nicht (Abschnitt 3).
/// </summary>
public static class AppRightTexts
{
    /// <summary>Text und Erklärung eines Rechts, das Lumora der App <paramref name="peerName"/> erlaubt.</summary>
    public static (string Text, string? Hint) Granted(string right, string peerName) => right switch
    {
        Rights.PhotosSearch => ("Fotos suchen (Zeitraum, Gebiet)", "Datum, Position, Bewertung — keine Personen"),
        Rights.PhotosThumbnail => ("Vorschaubilder abrufen", null),
        Rights.PhotosOriginal => ("Originale öffnen", $"{peerName} bekommt den aktuellen Pfad, kopiert nichts"),
        Rights.PhotosPeople => ("Personen auf den Fotos sehen", "Namen der erkannten Personen"),
        Rights.BackgroundStart => ("Lumora im Hintergrund starten", null),
        _ => (right, null),
    };

    /// <summary>Text und Erklärung eines Rechts, das die App <paramref name="peerName"/> Lumora erlaubt.</summary>
    public static (string Text, string? Hint) Received(string right, string peerName) => right switch
    {
        Rights.ToursRead => ("Touren", "für den Galerie-Filter „Tour“"),
        Rights.TracksRead => ("GPS-Spuren der Touren", "für „Ungenaue Orte“ und Fotos ohne Ort (Vorrang wie Garmin-Spur)"),
        Rights.SummitsRead => ("Gipfel und Tourdaten", "für Storys"),
        Rights.BackgroundStart => ($"{peerName} im Hintergrund starten, wenn Lumora etwas braucht", null),
        _ => (right, null),
    };
}
