using System.Text.Json;
using System.Text.Json.Serialization;
using Lumora.Services.Infrastructure;

namespace Lumora.Services.HiddenLibraries;

/// <summary>Eine gefundene versteckte Bibliothek (nur im Speicher, nie gemerkt).</summary>
/// <param name="FolderPath">Bibliotheksordner im Container (enthält <c>.lumora</c>).</param>
/// <param name="Name">Anzeigename aus <c>.lumora\hidden-library.json</c>.</param>
public sealed record HiddenLibraryInfo(string FolderPath, string Name)
{
    /// <summary>Laufwerkswurzel des Bibliotheksordners („X:\“) — für Anzeige und Aushängen.</summary>
    public string DriveRoot => Path.GetPathRoot(FolderPath) ?? FolderPath;
}

/// <summary>
/// Inhalt von <c>.lumora\hidden-library.json</c> (ADR-0049): Name, Kennwort-Prüfwert und die Einstellungen der
/// versteckten Bibliothek (Zusperren nach Untätigkeit, Container beim Zusperren aushängen). Liegt im Container.
/// </summary>
public sealed record HiddenLibraryManifest(
    string Name,
    DateTime CreatedUtc,
    PasswordVerifier Password,
    int AutoLockMinutes,
    bool DismountOnLock)
{
    public const int DefaultAutoLockMinutes = 10;
    public const int MinAutoLockMinutes = 1;
    public const int MaxAutoLockMinutes = 240;
}

/// <summary>
/// Liest und schreibt die Kenndatei einer versteckten Bibliothek und prüft das Lumora-Kennwort (ADR-0049). Lumora
/// verschlüsselt selbst nichts — das tut der Container (VeraCrypt/BitLocker); das Kennwort ist eine zusätzliche Sperre
/// der Oberfläche. Zurücksetzen nur mit Bestätigungsdatei <see cref="ResetRequestFileName"/> im Katalogordner: wer sie
/// anlegen kann, hat den Container schon mit dessen Kennwort eingehängt.
/// </summary>
public sealed class HiddenLibraryStore
{
    public const string ManifestFileName = "hidden-library.json";
    public const string ResetRequestFileName = "reset-password.txt";
    private const string Format = "lumora-hidden-library";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly int _iterations;

    public HiddenLibraryStore()
        : this(HiddenLibraryPassword.DefaultIterations)
    {
    }

    /// <param name="iterations">PBKDF2-Iterationen für neue Kennwörter; nur Tests verringern sie.</param>
    public HiddenLibraryStore(int iterations)
    {
        _iterations = iterations;
    }

    /// <summary><c>&lt;Bibliotheksordner&gt;\.lumora</c>.</summary>
    public static string GetCatalogFolder(string libraryFolder)
        => Path.Combine(libraryFolder, CatalogFolders.HiddenCatalogFolderName);

    public static string GetManifestPath(string libraryFolder)
        => Path.Combine(GetCatalogFolder(libraryFolder), ManifestFileName);

    public static string GetResetRequestPath(string libraryFolder)
        => Path.Combine(GetCatalogFolder(libraryFolder), ResetRequestFileName);

    /// <summary>
    /// Pfade des versteckten Katalogs: DB, Vorschaubilder, Gesichtsausschnitte, Protokolle, Cache und Temp liegen alle in
    /// <c>.lumora</c> — App-Wurzel und Datenordner sind derselbe Ordner.
    /// </summary>
    public static AppDataPaths GetCatalogPaths(string libraryFolder)
    {
        var folder = GetCatalogFolder(Path.GetFullPath(libraryFolder));
        return new AppDataPaths(folder, folder);
    }

    /// <summary>true, wenn im Ordner eine versteckte Bibliothek liegt (Kenndatei vorhanden).</summary>
    public static bool Exists(string libraryFolder) => File.Exists(GetManifestPath(libraryFolder));

    /// <summary>
    /// Legt <c>.lumora</c> samt Kenndatei an (Ordner versteckt, Systemordner, nicht indiziert — kein Scan und keine
    /// Windows-Suche sieht hinein). Wirft, wenn dort schon eine versteckte Bibliothek liegt.
    /// </summary>
    public HiddenLibraryManifest Create(string libraryFolder, string name, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Directory.Exists(libraryFolder))
        {
            throw new DirectoryNotFoundException("Der Bibliotheksordner existiert nicht.");
        }

        if (Exists(libraryFolder))
        {
            throw new InvalidOperationException("In diesem Ordner liegt bereits eine versteckte Bibliothek.");
        }

        var catalog = GetCatalogFolder(libraryFolder);
        Directory.CreateDirectory(catalog);
        HideCatalogFolder(catalog);
        var manifest = new HiddenLibraryManifest(
            name.Trim(),
            DateTime.UtcNow,
            HiddenLibraryPassword.Create(password, _iterations),
            HiddenLibraryManifest.DefaultAutoLockMinutes,
            DismountOnLock: false); // Standard aus (0.4.38, Herbert): Lumora hängt den Container nur auf ausdrücklichen Wunsch aus
        Write(libraryFolder, manifest);
        return manifest;
    }

    /// <summary>Liest die Kenndatei; null, wenn sie fehlt oder unlesbar ist (dann ist es keine versteckte Bibliothek).</summary>
    public static HiddenLibraryManifest? TryLoad(string libraryFolder)
    {
        try
        {
            var file = GetManifestPath(libraryFolder);
            if (!File.Exists(file))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(file), JsonOptions);
            if (dto is null || dto.Format != Format || dto.Password is null || string.IsNullOrWhiteSpace(dto.Name))
            {
                return null;
            }

            return new HiddenLibraryManifest(
                dto.Name,
                dto.CreatedUtc,
                dto.Password,
                Math.Clamp(dto.AutoLockMinutes ?? HiddenLibraryManifest.DefaultAutoLockMinutes, HiddenLibraryManifest.MinAutoLockMinutes, HiddenLibraryManifest.MaxAutoLockMinutes),
                // Bis 0.4.37 stand „aushängen“ ungefragt auf an; erst ein Wert, der ab 0.4.38 geschrieben wurde
                // (DismountChosen), zählt als bewusste Wahl — alte Kenndateien gelten als „aus“.
                dto.DismountChosen == true && (dto.DismountOnLock ?? false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Prüft das Lumora-Kennwort der Bibliothek; false auch, wenn die Kenndatei fehlt.</summary>
    public static bool VerifyPassword(string libraryFolder, string? password)
        => TryLoad(libraryFolder) is { } manifest && HiddenLibraryPassword.Verify(password, manifest.Password);

    /// <summary>true, wenn der Benutzer im Katalogordner die Bestätigungsdatei zum Zurücksetzen angelegt hat.</summary>
    public static bool HasResetRequest(string libraryFolder) => File.Exists(GetResetRequestPath(libraryFolder));

    /// <summary>
    /// Setzt ein neues Kennwort — nur mit Bestätigungsdatei <see cref="ResetRequestFileName"/>, die danach gelöscht wird.
    /// </summary>
    public HiddenLibraryManifest ResetPassword(string libraryFolder, string newPassword)
    {
        var manifest = TryLoad(libraryFolder) ?? throw new InvalidOperationException("Hier liegt keine versteckte Bibliothek.");
        if (!HasResetRequest(libraryFolder))
        {
            throw new InvalidOperationException(
                $"Zum Zurücksetzen zuerst im eingehängten Container die Datei „{CatalogFolders.HiddenCatalogFolderName}\\{ResetRequestFileName}“ anlegen.");
        }

        var updated = manifest with { Password = HiddenLibraryPassword.Create(newPassword, _iterations) };
        Write(libraryFolder, updated);
        File.Delete(GetResetRequestPath(libraryFolder));
        return updated;
    }

    /// <summary>Speichert die Einstellungen der versteckten Bibliothek (in ihrer Kenndatei, nie in den normalen Einstellungen).</summary>
    public static HiddenLibraryManifest SaveSettings(string libraryFolder, int autoLockMinutes, bool dismountOnLock)
    {
        var manifest = TryLoad(libraryFolder) ?? throw new InvalidOperationException("Hier liegt keine versteckte Bibliothek.");
        var updated = manifest with
        {
            AutoLockMinutes = Math.Clamp(autoLockMinutes, HiddenLibraryManifest.MinAutoLockMinutes, HiddenLibraryManifest.MaxAutoLockMinutes),
            DismountOnLock = dismountOnLock,
        };
        Write(libraryFolder, updated);
        return updated;
    }

    /// <summary>Versteckt + System + nicht indizieren (best effort).</summary>
    public static void HideCatalogFolder(string catalogFolder)
    {
        try
        {
            var info = new DirectoryInfo(catalogFolder);
            info.Attributes |= FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nur Komfort: Scan und Flatten überspringen .lumora auch am Namen.
        }
    }

    private static void Write(string libraryFolder, HiddenLibraryManifest manifest)
    {
        var file = GetManifestPath(libraryFolder);
        var tmp = file + ".tmp";
        var dto = new Dto
        {
            Format = Format,
            Version = FormatVersion,
            Name = manifest.Name,
            CreatedUtc = manifest.CreatedUtc,
            Password = manifest.Password,
            AutoLockMinutes = manifest.AutoLockMinutes,
            DismountOnLock = manifest.DismountOnLock,
            DismountChosen = true,
        };
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, JsonOptions));
        File.Move(tmp, file, overwrite: true);
    }

    private sealed class Dto
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public string? Name { get; set; }

        public DateTime CreatedUtc { get; set; }

        public PasswordVerifier? Password { get; set; }

        public int? AutoLockMinutes { get; set; }

        public bool? DismountOnLock { get; set; }

        /// <summary>Ab 0.4.38 gesetzt: <see cref="DismountOnLock"/> ist eine Wahl unter dem neuen Standard „aus“.</summary>
        public bool? DismountChosen { get; set; }
    }
}
