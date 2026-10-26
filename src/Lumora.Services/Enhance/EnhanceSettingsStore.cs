using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Services.Database;

namespace Lumora.Services.Enhance;

/// <summary>Einstellungen › Verbessern wurden gespeichert (Galerie lädt bei „beste Fassung zeigen“ neu).</summary>
public sealed record EnhanceSettingsChangedMessage(EnhanceSettings Settings);

/// <summary>Lese-/Schreibzugriff auf Einstellungen › Verbessern.</summary>
public interface IEnhanceSettingsStore
{
    Task<EnhanceSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(EnhanceSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Settings-Tabelle des Katalogs (je Sitzung, also auch in der versteckten Bibliothek getrennt): <c>enhance.location</c>,
/// <c>enhance.subfolder</c>, <c>enhance.pattern</c>, <c>enhance.copyMetadata</c>, <c>enhance.copyRatingTags</c>,
/// <c>enhance.showBest</c>, <c>enhance.useGpu</c>, <c>enhance.naturalness</c>, <c>enhance.grain</c> (0…1, invariant),
/// <c>enhance.upscaleModel</c> (<c>Sharp</c> | <c>Natural</c>).
/// </summary>
public sealed class EnhanceSettingsStore(ILibraryContextFactory factory, IMessenger? messenger = null) : IEnhanceSettingsStore
{
    public const string LocationKey = "enhance.location";
    public const string SubfolderKey = "enhance.subfolder";
    public const string PatternKey = "enhance.pattern";
    public const string CopyMetadataKey = "enhance.copyMetadata";
    public const string CopyRatingTagsKey = "enhance.copyRatingTags";
    public const string ShowBestKey = "enhance.showBest";
    public const string UseGpuKey = "enhance.useGpu";
    public const string NaturalnessKey = "enhance.naturalness";
    public const string GrainKey = "enhance.grain";
    public const string UpscaleModelKey = "enhance.upscaleModel";

    private static readonly string[] Keys = [LocationKey, SubfolderKey, PatternKey, CopyMetadataKey, CopyRatingTagsKey, ShowBestKey, UseGpuKey, NaturalnessKey, GrainKey, UpscaleModelKey];

    private readonly IMessenger _messenger = messenger ?? WeakReferenceMessenger.Default;

    public async Task<EnhanceSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var rows = await ctx.Settings.AsNoTracking()
            .Where(s => Keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct)
            .ConfigureAwait(false);
        var d = EnhanceSettings.Default;
        return new EnhanceSettings(
            rows.TryGetValue(LocationKey, out var loc) && Enum.TryParse<EnhanceSaveLocation>(loc, out var location) && Enum.IsDefined(location) ? location : d.Location,
            NonEmpty(rows, SubfolderKey) ?? d.SubfolderName,
            NonEmpty(rows, PatternKey) ?? d.FileNamePattern,
            ReadBool(rows, CopyMetadataKey, d.CopyMetadata),
            ReadBool(rows, CopyRatingTagsKey, d.CopyRatingAndTags),
            ReadBool(rows, ShowBestKey, d.ShowBestVariant),
            ReadBool(rows, UseGpuKey, d.UseGpu),
            ReadShare(rows, NaturalnessKey, d.Naturalness),
            ReadShare(rows, GrainKey, d.Grain),
            rows.TryGetValue(UpscaleModelKey, out var model) && Enum.TryParse<EnhanceUpscaleModel>(model, out var upscaleModel) && Enum.IsDefined(upscaleModel) ? upscaleModel : d.UpscaleModel);
    }

    public async Task SaveAsync(EnhanceSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using (var ctx = factory.Create())
        {
            await UpsertAsync(ctx, LocationKey, settings.Location.ToString(), ct).ConfigureAwait(false);
            await UpsertAsync(ctx, SubfolderKey, EnhanceNaming.CleanFolderName(settings.SubfolderName), ct).ConfigureAwait(false);
            await UpsertAsync(ctx, PatternKey, string.IsNullOrWhiteSpace(settings.FileNamePattern) ? EnhanceSettings.DefaultPattern : settings.FileNamePattern.Trim(), ct).ConfigureAwait(false);
            await UpsertAsync(ctx, CopyMetadataKey, settings.CopyMetadata ? "true" : "false", ct).ConfigureAwait(false);
            await UpsertAsync(ctx, CopyRatingTagsKey, settings.CopyRatingAndTags ? "true" : "false", ct).ConfigureAwait(false);
            await UpsertAsync(ctx, ShowBestKey, settings.ShowBestVariant ? "true" : "false", ct).ConfigureAwait(false);
            await UpsertAsync(ctx, UseGpuKey, settings.UseGpu ? "true" : "false", ct).ConfigureAwait(false);
            await UpsertAsync(ctx, NaturalnessKey, Share(settings.Naturalness), ct).ConfigureAwait(false);
            await UpsertAsync(ctx, GrainKey, Share(settings.Grain), ct).ConfigureAwait(false);
            await UpsertAsync(ctx, UpscaleModelKey, settings.UpscaleModel.ToString(), ct).ConfigureAwait(false);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _messenger.Send(new EnhanceSettingsChangedMessage(settings));
    }

    private static string? NonEmpty(Dictionary<string, string?> rows, string key)
        => rows.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static bool ReadBool(Dictionary<string, string?> rows, string key, bool fallback)
        => rows.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    /// <summary>Anteil 0…1 (invariant); ungültig oder fehlend → <paramref name="fallback"/>.</summary>
    private static double ReadShare(Dictionary<string, string?> rows, string key, double fallback)
        => rows.TryGetValue(key, out var text)
           && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
           && double.IsFinite(value)
            ? Math.Clamp(value, 0, 1)
            : fallback;

    private static string Share(double value)
        => Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task UpsertAsync(Data.LibraryContext ctx, string key, string value, CancellationToken ct)
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

/// <summary>Zielname und Zielordner der verbesserten Fassung.</summary>
public static class EnhanceNaming
{
    /// <summary>
    /// Dateiname aus dem Muster (<c>{name}</c> = Name des Originals ohne Endung), immer „.jpg“. Ungültige Zeichen werden zu „_“;
    /// ergäbe das Muster den Namen des Originals selbst (oder nichts), kommt „_verbessert“ dazu — das Original wird nie
    /// überschrieben.
    /// </summary>
    public static string FileName(string originalName, string? pattern)
    {
        var stem = Path.GetFileNameWithoutExtension(originalName);
        var text = string.IsNullOrWhiteSpace(pattern) ? EnhanceSettings.DefaultPattern : pattern.Trim();
        var name = text.Contains("{name}", StringComparison.OrdinalIgnoreCase)
            ? text.Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
            : stem + text;
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0 || string.Equals(name, stem, StringComparison.OrdinalIgnoreCase))
        {
            name = stem + "_verbessert";
        }

        if (name.Length > 180)
        {
            name = name[..180];
        }

        return name + ".jpg";
    }

    /// <summary>Unterordner-Name ohne Pfadteile und ungültige Zeichen (leer → „verbessert“).</summary>
    public static string CleanFolderName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()).Trim().Trim('.');
        return clean.Length == 0 ? EnhanceSettings.DefaultSubfolder : clean;
    }

    /// <summary>Gewünschter Zielpfad nach den Einstellungen (Nummerierung bei belegtem Namen macht die Datei-Operation).</summary>
    public static string TargetPath(string originalFolder, string originalName, EnhanceSettings settings)
    {
        var folder = settings.Location == EnhanceSaveLocation.Subfolder
            ? Path.Combine(originalFolder, CleanFolderName(settings.SubfolderName))
            : originalFolder;
        return Path.Combine(folder, FileName(originalName, settings.FileNamePattern));
    }
}
