namespace Lumora.Services.Operations;

/// <summary>
/// Abstraktion über den Windows-Papierkorb, damit <see cref="FileOperationService"/> ohne echte
/// Shell-Aufrufe testbar bleibt (Fake im Testprojekt). Produktiv: <see cref="WindowsRecycleBin"/>.
/// </summary>
public interface IRecycleBin
{
    /// <summary>
    /// Verschiebt eine Datei in den Papierkorb. Wirft bei Fehlern (Datei gesperrt, nicht vorhanden,
    /// Papierkorb nicht verfügbar) — der Aufrufer protokolliert das Item dann als Failed.
    /// </summary>
    void MoveToRecycleBin(string filePath);

    /// <summary>
    /// Verschiebt einen ganzen Ordner (samt Inhalt) in den Papierkorb — für „Ordner in die Bibliothek übernehmen“
    /// (ADR-0034), nachdem alle Dateien daraus verschoben wurden. Standard: nicht unterstützt (Fakes ohne Ordner).
    /// </summary>
    void MoveDirectoryToRecycleBin(string directoryPath)
        => throw new NotSupportedException("Dieser Papierkorb unterstützt keine Ordner.");

    /// <summary>
    /// Holt die zuletzt (nach <paramref name="deletedAfterUtc"/>) gelöschte Datei mit diesem Ursprungspfad aus dem Papierkorb
    /// an ihren Platz zurück (Rückgängig von „Übrige in den Papierkorb“, ADR-0053). Wirft, wenn sie nicht (mehr) im Papierkorb
    /// liegt oder der Platz belegt ist. Standard: nicht unterstützt.
    /// </summary>
    void RestoreFromRecycleBin(string originalPath, DateTime deletedAfterUtc)
        => throw new NotSupportedException("Dieser Papierkorb kann nichts zurückholen.");

    // ---- ADR-0065: Ordner in den Papierkorb ----

    /// <summary>
    /// Grund, warum für <paramref name="path"/> kein Papierkorb verfügbar ist (Netz, Wechsel-/VeraCrypt-Laufwerk …), oder
    /// null, wenn es einen gibt. Ohne Dateizugriff. Standard: verfügbar (Fakes).
    /// </summary>
    string? GetUnavailableReasonFor(string path) => null;

    /// <summary>
    /// Holt den zuletzt (nach <paramref name="deletedAfterUtc"/>) gelöschten Ordner mit diesem Ursprungspfad samt Inhalt aus
    /// dem Papierkorb an seinen Platz zurück (Rückgängig von „Ordner in den Papierkorb“). Wirft, wenn er nicht (mehr) im
    /// Papierkorb liegt oder der Platz belegt ist. Standard: nicht unterstützt.
    /// </summary>
    void RestoreDirectoryFromRecycleBin(string originalPath, DateTime deletedAfterUtc)
        => throw new NotSupportedException("Dieser Papierkorb kann keine Ordner zurückholen.");

    /// <summary>
    /// Liegt der Ordner (gelöscht nach <paramref name="deletedAfterUtc"/>) wirklich im Papierkorb? false, wenn die Shell ihn
    /// endgültig gelöscht hat (z. B. zu groß für den Papierkorb und im Shell-Dialog bestätigt). Standard: ja.
    /// </summary>
    bool ContainsDirectory(string originalPath, DateTime deletedAfterUtc) => true;
}
