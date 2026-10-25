using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Services.Database;

namespace Lumora.Services.Marks;

/// <summary>
/// Einstellungen › Darstellung › „Symbole für Tags und Kategorien“ (Mockup 16, 0.4.35): je ein Schalter für die Marken auf
/// den Galerie-Kacheln, die Leiste in der Lightbox und die Zahlen im Ordnerbaum. Standard: alles an. Gespeichert in der
/// Settings-Tabelle des aktiven Katalogs — die versteckte Sitzung hat damit ihre eigenen Werte (ADR-0049).
/// </summary>
public sealed record MarkSettings(bool ShowOnTiles, bool ShowInLightbox, bool ShowInFolderTree)
{
    public const string TilesKey = "ui.marks.tiles";
    public const string LightboxKey = "ui.marks.lightbox";
    public const string FolderTreeKey = "ui.marks.folderTree";

    /// <summary>Alles an (Abnahme Mockup 16).</summary>
    public static MarkSettings Default { get; } = new(true, true, true);
}

/// <summary>Liest und speichert <see cref="MarkSettings"/> in der Settings-Tabelle des Katalogs.</summary>
public interface IMarkSettingsStore
{
    Task<MarkSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(MarkSettings settings, CancellationToken ct = default);
}

/// <inheritdoc cref="IMarkSettingsStore"/>
public sealed class MarkSettingsStore(ILibraryContextFactory factory) : IMarkSettingsStore
{
    private static readonly string[] Keys = [MarkSettings.TilesKey, MarkSettings.LightboxKey, MarkSettings.FolderTreeKey];

    public async Task<MarkSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var rows = await ctx.Settings.AsNoTracking()
            .Where(s => Keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct)
            .ConfigureAwait(false);
        var defaults = MarkSettings.Default;
        return new MarkSettings(
            Flag(rows, MarkSettings.TilesKey, defaults.ShowOnTiles),
            Flag(rows, MarkSettings.LightboxKey, defaults.ShowInLightbox),
            Flag(rows, MarkSettings.FolderTreeKey, defaults.ShowInFolderTree));
    }

    public async Task SaveAsync(MarkSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var ctx = factory.Create();
        await UpsertAsync(ctx, MarkSettings.TilesKey, settings.ShowOnTiles, ct).ConfigureAwait(false);
        await UpsertAsync(ctx, MarkSettings.LightboxKey, settings.ShowInLightbox, ct).ConfigureAwait(false);
        await UpsertAsync(ctx, MarkSettings.FolderTreeKey, settings.ShowInFolderTree, ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Unbekannte oder fehlende Werte fallen auf den Standard zurück.</summary>
    private static bool Flag(Dictionary<string, string?> rows, string key, bool fallback)
        => rows.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static async Task UpsertAsync(Data.LibraryContext ctx, string key, bool value, CancellationToken ct)
    {
        var text = value ? "true" : "false";
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = text });
        }
        else
        {
            row.Value = text;
        }
    }
}
