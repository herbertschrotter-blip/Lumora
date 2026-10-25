using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Domain.Enums;
using Lumora.Lightbox;
using Lumora.Services.Database;
using Lumora.Services.Enhance;

namespace Lumora.Enhance;

/// <summary>
/// Was die Galerie für „Foto verbessern“ braucht (ADR-0071): Fassungs-Marken, „beste Fassung zeigen“, Stapel-Dialog und Lauf,
/// Lightbox mit allen Fassungen einer Gruppe. Eine Instanz je Katalog-Sitzung (Singleton der Sitzung).
/// </summary>
public sealed class EnhanceGalleryActions(
    IPhotoEnhanceService service,
    IPhotoVariantStore variants,
    IEnhanceSettingsStore settings,
    IEnhanceBatchDialogService dialog,
    ILibraryContextFactory factory)
{
    public IPhotoEnhanceService Service => service;

    public IPhotoVariantStore Variants => variants;

    public IEnhanceSettingsStore Settings => settings;

    public IEnhanceBatchDialogService Dialog => dialog;

    /// <summary>Lightbox-Einträge der Dateien in dieser Reihenfolge (auch Dateien außerhalb der Seite, z. B. ausgeblendete Originale).</summary>
    public Task<IReadOnlyList<LightboxEntry>> LoadEntriesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<LightboxEntry>>(() =>
        {
            if (fileIds.Count == 0)
            {
                return [];
            }

            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.Id, d.Path, f.Name, f.MediaType, f.IsFavorite, f.Rating, f.Size, f.ModifiedAtUtc
                FROM Files f JOIN Folders d ON d.Id = f.FolderId
                WHERE f.Id IN ({string.Join(',', fileIds.Distinct())});
                """;
            var found = new Dictionary<int, LightboxEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                found[id] = new LightboxEntry(
                    id,
                    Path.Combine(reader.GetString(1), reader.GetString(2)),
                    reader.GetString(2),
                    (MediaType)reader.GetInt32(3),
                    reader.GetInt64(4) != 0,
                    reader.IsDBNull(5) ? null : (byte)reader.GetInt32(5),
                    reader.GetInt64(6),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)).UtcDateTime);
            }

            return fileIds.Distinct().Where(found.ContainsKey).Select(id => found[id]).ToList();
        }, ct);
}
