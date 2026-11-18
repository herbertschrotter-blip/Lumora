using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Services.Database;

namespace Lumora.Imaging.Merge;

/// <summary>Lese-/Schreibzugriff auf Einstellungen › Zusammenfügen (Teil Ergebnis).</summary>
public interface IMergeSettingsStore
{
    Task<MergeSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(MergeSettings settings, CancellationToken ct = default);

    /// <summary>Merkt die zuletzt gewählte Form der Kugel (Vorauswahl beim nächsten Mal, Abnahme Nr. 4).</summary>
    Task RememberSphereFormAsync(MergeSphereForm form, CancellationToken ct = default);

    /// <summary>Merkt die zuletzt gewählte Farbverstärkung in Prozent (0–150; Vorauswahl beim nächsten Mal).</summary>
    Task RememberColorBoostAsync(int percent, CancellationToken ct = default);
}

/// <summary>
/// Settings-Tabelle des Katalogs (je Sitzung, also auch in der versteckten Bibliothek getrennt): <c>merge.panorama.sphereForm</c>
/// (<c>Band</c> | <c>Full</c>, beim ersten Mal Band), <c>merge.location</c>, <c>merge.subfolder</c>, <c>merge.suffix.panorama</c>,
/// <c>merge.jpegQuality</c>, <c>merge.copyTags</c>, <c>merge.copyRating</c>, <c>merge.droneOneUp</c>, <c>merge.colorBoost</c> (Prozent 0–150,
/// beim ersten Mal 100).
/// </summary>
public sealed class MergeSettingsStore(ILibraryContextFactory factory) : IMergeSettingsStore
{
    public const string SphereFormKey = "merge.panorama.sphereForm";
    public const string LocationKey = "merge.location";
    public const string SubfolderKey = "merge.subfolder";
    public const string PanoramaSuffixKey = "merge.suffix.panorama";
    public const string JpegQualityKey = "merge.jpegQuality";
    public const string CopyTagsKey = "merge.copyTags";
    public const string CopyRatingKey = "merge.copyRating";
    public const string DroneOneUpKey = "merge.droneOneUp";
    public const string ColorBoostKey = "merge.colorBoost";

    private static readonly string[] Keys = [SphereFormKey, LocationKey, SubfolderKey, PanoramaSuffixKey, JpegQualityKey, CopyTagsKey, CopyRatingKey, DroneOneUpKey, ColorBoostKey];

    public async Task<MergeSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var rows = await ctx.Settings.AsNoTracking().Where(s => Keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value, ct).ConfigureAwait(false);
        var d = MergeSettings.Default;
        return new MergeSettings(
            ReadEnum(rows, SphereFormKey, d.SphereForm),
            ReadEnum(rows, LocationKey, d.Location),
            NonEmpty(rows, SubfolderKey) ?? d.SubfolderName,
            NonEmpty(rows, PanoramaSuffixKey) ?? d.PanoramaSuffix,
            rows.TryGetValue(JpegQualityKey, out var q) && int.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality) ? Math.Clamp(quality, 50, 100) : d.JpegQuality,
            ReadBool(rows, CopyTagsKey, d.CopyCommonTags),
            ReadBool(rows, CopyRatingKey, d.CopyRating),
            ReadBool(rows, DroneOneUpKey, d.DroneFolderOneUp),
            rows.TryGetValue(ColorBoostKey, out var c) && int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var boost) ? ClampBoost(boost) : d.ColorBoost);
    }

    public async Task SaveAsync(MergeSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var ctx = factory.Create();
        await UpsertAsync(ctx, SphereFormKey, settings.SphereForm.ToString(), ct).ConfigureAwait(false);
        await UpsertAsync(ctx, LocationKey, settings.Location.ToString(), ct).ConfigureAwait(false);
        await UpsertAsync(ctx, SubfolderKey, MergeNaming.CleanFolderName(settings.SubfolderName), ct).ConfigureAwait(false);
        await UpsertAsync(ctx, PanoramaSuffixKey, MergeNaming.CleanSuffix(settings.PanoramaSuffix), ct).ConfigureAwait(false);
        await UpsertAsync(ctx, JpegQualityKey, Math.Clamp(settings.JpegQuality, 50, 100).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await UpsertAsync(ctx, CopyTagsKey, settings.CopyCommonTags ? "true" : "false", ct).ConfigureAwait(false);
        await UpsertAsync(ctx, CopyRatingKey, settings.CopyRating ? "true" : "false", ct).ConfigureAwait(false);
        await UpsertAsync(ctx, DroneOneUpKey, settings.DroneFolderOneUp ? "true" : "false", ct).ConfigureAwait(false);
        await UpsertAsync(ctx, ColorBoostKey, ClampBoost(settings.ColorBoost).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RememberSphereFormAsync(MergeSphereForm form, CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        await UpsertAsync(ctx, SphereFormKey, form.ToString(), ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RememberColorBoostAsync(int percent, CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        await UpsertAsync(ctx, ColorBoostKey, ClampBoost(percent).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static int ClampBoost(int percent) => Math.Clamp(percent, MergeLimits.ColorBoostOff, MergeLimits.ColorBoostMax);

    private static T ReadEnum<T>(Dictionary<string, string?> rows, string key, T fallback)
        where T : struct, Enum
        => rows.TryGetValue(key, out var text) && Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value) ? value : fallback;

    private static string? NonEmpty(Dictionary<string, string?> rows, string key)
        => rows.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static bool ReadBool(Dictionary<string, string?> rows, string key, bool fallback)
        => rows.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static async Task UpsertAsync(Lumora.Data.LibraryContext ctx, string key, string value, CancellationToken ct)
    {
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }
}

/// <summary>Zielname und Zielordner des Ergebnisses (ADR-0078 Punkt 10): <c>&lt;erstes Bild&gt;_panorama.jpg</c> neben dem ersten Einzelbild.</summary>
public static partial class MergeNaming
{
    /// <summary>Gewünschter Zielpfad (Nummerierung bei belegtem Namen macht die Datei-Operation).</summary>
    public static string TargetPath(string firstFolder, string firstName, MergeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var folder = firstFolder;
        if (settings.DroneFolderOneUp && DjiPanoramaFolder().IsMatch(Path.GetFileName(firstFolder.TrimEnd('\\', '/'))) && Path.GetDirectoryName(firstFolder) is { Length: > 0 } parent)
        {
            folder = parent;
        }

        if (settings.Location == MergeSaveLocation.Subfolder)
        {
            folder = Path.Combine(folder, CleanFolderName(settings.SubfolderName));
        }

        return Path.Combine(folder, Path.GetFileNameWithoutExtension(firstName) + CleanSuffix(settings.PanoramaSuffix) + ".jpg");
    }

    /// <summary>Unterordner-Name ohne Pfadteile und ungültige Zeichen (leer → „zusammengefügt“).</summary>
    public static string CleanFolderName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()).Trim().Trim('.');
        return clean.Length == 0 ? MergeSettings.DefaultSubfolder : clean;
    }

    /// <summary>Namenszusatz ohne ungültige Zeichen (leer → „_panorama“, sonst würde das erste Einzelbild „getroffen“).</summary>
    public static string CleanSuffix(string? suffix)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((suffix ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? MergeSettings.DefaultPanoramaSuffix : clean;
    }

    /// <summary>DJI-Panorama-Ordner (z. B. „100_0822“, ADR-0078 Punkt 4).</summary>
    [GeneratedRegex(@"^\d{3}_\d{4}$")]
    private static partial Regex DjiPanoramaFolder();
}
