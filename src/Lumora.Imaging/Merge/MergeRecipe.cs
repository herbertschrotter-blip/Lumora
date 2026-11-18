using System.Text.Json;
using System.Text.Json.Serialization;
using Lumora.Domain.Enums;
using Lumora.Imaging.Protocol;

namespace Lumora.Imaging.Merge;

/// <summary>
/// Eine Quelle im Rezept (FileId zum Zeitpunkt des Speicherns, Stelle = <c>SourceSpec.Index</c>, auf die sich
/// <c>CameraSpec.Source</c> und <c>Dropped</c> der Ausrichtung beziehen). Das Rezept führt alle gerechneten Quellen, auch
/// ausgelassene; der Katalog (<c>MergedPhotoSources</c>) nur die im Ergebnis verwendeten.
/// </summary>
public sealed record MergeRecipeSource(int FileId, int Position);

/// <summary>
/// Rezept eines Ergebnisses (ADR-0078 Punkt 5, <c>MergedPhotos.RecipeJson</c>): Verfahren, Optionen, gewählte Aufnahmeart,
/// Ausrichtung der Kameras und Maßstab — volle Auflösung, „Neu berechnen…“ und später der Stapel rechnen daraus, ohne neu
/// auszurichten. Enthält keine Pfade. <see cref="ColorBoost"/>: Farbverstärkung in Prozent (0 = aus, 100 = Standard, bis 150;
/// fehlt das Feld in einem älteren Rezept, gilt 100).
/// </summary>
public sealed record MergeRecipe(
    int Version,
    MergeMethod Method,
    MergeOptions Options,
    PanoramaProjection Projection,
    MergeSphereForm SphereForm,
    IReadOnlyList<MergeRecipeSource> Sources,
    AlignmentSpec Alignment,
    double Scale,
    string App,
    int ColorBoost = MergeLimits.ColorBoostDefault)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = CreateOptions();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static MergeRecipe? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MergeRecipe>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
