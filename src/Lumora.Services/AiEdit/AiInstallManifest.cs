using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumora.Services.AiEdit;

/// <summary>
/// Inhalt von <c>.lumora-ki\ki-install.json</c> (ADR-0076 Punkt 2 und 7): was nach Herberts Freigabe im Container liegt —
/// ComfyUI-Paket, Erweiterungen, Python-Pakete und Modelle, je mit Quelle, Lizenz, Prüfsumme und Freigabedatum. Lumora
/// lädt nur Erweiterungen aus dieser Liste (<c>--whitelist-custom-nodes</c>); Vorlagen dürfen später nur Modelle daraus
/// verwenden.
/// </summary>
public sealed record AiInstallManifest
{
    public const string FormatName = "lumora-ki-install";
    public const int FormatVersion = 1;

    public string Format { get; init; } = FormatName;

    public int Version { get; init; } = FormatVersion;

    public AiInstallPackage? ComfyUi { get; init; }

    public IReadOnlyList<AiInstallExtension> Extensions { get; init; } = [];

    public IReadOnlyList<AiInstallPythonPackage> PythonPackages { get; init; } = [];

    public IReadOnlyList<AiInstallModel> Models { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Liest die Datei; null, wenn sie fehlt. Wirft <see cref="InvalidDataException"/> bei falschem Format.</summary>
    public static AiInstallManifest? TryLoad(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        AiInstallManifest? manifest;
        try
        {
            using var stream = File.OpenRead(file);
            manifest = JsonSerializer.Deserialize<AiInstallManifest>(stream, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("ki-install.json ist nicht lesbar.", ex);
        }

        if (manifest is null || !string.Equals(manifest.Format, FormatName, StringComparison.Ordinal) || manifest.Version != FormatVersion)
        {
            throw new InvalidDataException("ki-install.json hat ein unbekanntes Format.");
        }

        return manifest;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>Das ComfyUI-Paket (Archivname, Version, Prüfsumme des Archivs).</summary>
public sealed record AiInstallPackage(string Version, string? Archive = null, string? Source = null, string? Sha256 = null, string? Approved = null);

/// <summary>Eine freigegebene ComfyUI-Erweiterung in <c>custom_nodes\&lt;Name&gt;</c>, fester Stand.</summary>
public sealed record AiInstallExtension(string Name, string? Source = null, string? Commit = null, string? License = null, string? Approved = null);

/// <summary>Ein zusätzlich ins eingebettete Python installiertes Paket.</summary>
public sealed record AiInstallPythonPackage(string Name, string Version, string? Sha256 = null, string? License = null, string? Approved = null);

/// <summary>Ein Modell; <see cref="File"/> relativ zu <c>.lumora-ki\models</c> (mit „/“ oder „\“).</summary>
public sealed record AiInstallModel(
    string File,
    string? Purpose = null,
    string? Source = null,
    string? Revision = null,
    long Bytes = 0,
    string? License = null,
    string? Sha256 = null,
    string? Approved = null);
