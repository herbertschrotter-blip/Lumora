using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Services.Database;

namespace Lumora.Services.Thumbnails;

/// <summary>
/// Schalter „Vorschaubilder auf Vorrat erstellen“ (0.5.40) in der Settings-Tabelle des Katalogs — je Katalog, die versteckte
/// Bibliothek hat also ihren eigenen. Standard an.
/// </summary>
public interface IThumbnailStockSettingsStore
{
    Task<bool> LoadEnabledAsync(CancellationToken ct = default);

    Task SaveEnabledAsync(bool enabled, CancellationToken ct = default);
}

/// <inheritdoc cref="IThumbnailStockSettingsStore"/>
public sealed class ThumbnailStockSettingsStore(ILibraryContextFactory factory) : IThumbnailStockSettingsStore
{
    public const string EnabledKey = "thumbnails.stock.enabled";

    public const bool DefaultEnabled = true;

    public async Task<bool> LoadEnabledAsync(CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var value = await ctx.Settings.AsNoTracking()
            .Where(s => s.Key == EnabledKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return bool.TryParse(value, out var enabled) ? enabled : DefaultEnabled;
    }

    public async Task SaveEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var text = enabled ? "true" : "false";
        var row = await ctx.Settings.FindAsync([EnabledKey], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = EnabledKey, Value = text });
        }
        else
        {
            row.Value = text;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
