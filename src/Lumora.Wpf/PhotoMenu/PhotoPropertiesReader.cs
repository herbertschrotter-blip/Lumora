using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Lumora.Domain.Enums;
using Lumora.Services.Categories;
using Lumora.Services.Database;
using Lumora.Services.Tags;

namespace Lumora.PhotoMenu;

/// <summary>Eine Zeile im Fenster „Eigenschaften“.</summary>
public sealed record PropertyRow(string Label, string Value);

/// <summary>Ein Abschnitt (Datei, Aufnahme, Bibliothek, EXIF).</summary>
public sealed record PropertySection(string Title, IReadOnlyList<PropertyRow> Rows);

/// <summary>Alles für das Fenster „Eigenschaften“ einer Datei.</summary>
public sealed record PhotoProperties(string Name, string FullPath, IReadOnlyList<PropertySection> Sections);

/// <summary>
/// Liest die Angaben für „Eigenschaften“ (ADR-0068): Katalog (Datei, Aufnahme, Ort, Bewertung, Tags, Kategorien) und die
/// EXIF-Felder der Datei über WIC (nur lesend, Datei bleibt geteilt offen). Unlesbare Felder fehlen einfach.
/// </summary>
public static class PhotoPropertiesReader
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>EXIF-Abfragen (WIC-Pfade) mit Beschriftung und Formatierung.</summary>
    private static readonly (string Label, string Query, Func<object, string?> Format)[] ExifFields =
    [
        ("Kamera-Hersteller", "/app1/ifd/{ushort=271}", v => v as string),
        ("Kamera-Modell", "/app1/ifd/{ushort=272}", v => v as string),
        ("Objektiv", "/app1/ifd/exif/{ushort=42036}", v => v as string),
        ("Belichtungszeit", "/app1/ifd/exif/{ushort=33434}", Exposure),
        ("Blende", "/app1/ifd/exif/{ushort=33437}", v => Rational(v) is { } f ? $"f/{f.ToString("0.#", German)}" : null),
        ("ISO", "/app1/ifd/exif/{ushort=34855}", v => Convert.ToString(v, CultureInfo.InvariantCulture)),
        ("Brennweite", "/app1/ifd/exif/{ushort=37386}", v => Rational(v) is { } f ? $"{f.ToString("0.#", German)} mm" : null),
        ("Blitz", "/app1/ifd/exif/{ushort=37385}", v => Convert.ToInt32(v, CultureInfo.InvariantCulture) % 2 == 1 ? "ausgelöst" : "nicht ausgelöst"),
        ("Aufgenommen (EXIF)", "/app1/ifd/exif/{ushort=36867}", v => v as string),
        ("Ausrichtung", "/app1/ifd/{ushort=274}", v => Convert.ToString(v, CultureInfo.InvariantCulture)),
        ("Software", "/app1/ifd/{ushort=305}", v => v as string),
    ];

    public static async Task<PhotoProperties?> ReadAsync(ILibraryContextFactory factory, ITagService? tags, ICategoryService? categories, int fileId, CancellationToken ct = default)
    {
        var row = await Task.Run(() => ReadRow(factory, fileId), ct).ConfigureAwait(true);
        if (row is null)
        {
            return null;
        }

        var sections = new List<PropertySection>();
        var file = new List<PropertyRow>
        {
            new("Name", row.Name),
            new("Ordner", row.Folder),
            new("Typ", row.MediaType switch { MediaType.Video => $"Video ({row.Extension})", MediaType.Archive => $"Archiv ({row.Extension})", _ => $"Bild ({row.Extension})" }),
            new("Größe", FormatSize(row.Size)),
        };
        if (row.Width is > 0 && row.Height is > 0)
        {
            file.Add(new("Maße", $"{row.Width:N0} × {row.Height:N0} Pixel"));
        }

        if (row.DurationMs is > 0)
        {
            file.Add(new("Dauer", TimeSpan.FromMilliseconds(row.DurationMs.Value).ToString(row.DurationMs >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss", German)));
        }

        file.Add(new("Erstellt", Local(row.Created)));
        file.Add(new("Geändert", Local(row.Modified)));
        if (row.Missing)
        {
            file.Add(new("Zustand", "fehlt auf der Platte"));
        }

        sections.Add(new PropertySection("Datei", file));

        var capture = new List<PropertyRow>();
        if (row.CapturedAt is { } captured)
        {
            var local = captured.AddMinutes(row.CapturedOffset ?? 0);
            capture.Add(new("Aufgenommen", local.ToString("dddd, d. MMMM yyyy, HH:mm", German)));
        }

        if (row.Camera is { Length: > 0 } camera)
        {
            capture.Add(new("Kamera", camera));
        }

        if (row.Latitude is { } lat && row.Longitude is { } lon)
        {
            capture.Add(new("Ort (GPS)", $"{lat.ToString("0.00000", CultureInfo.InvariantCulture)}, {lon.ToString("0.00000", CultureInfo.InvariantCulture)}"));
        }

        if (capture.Count > 0)
        {
            sections.Add(new PropertySection("Aufnahme", capture));
        }

        var library = new List<PropertyRow>
        {
            new("Bewertung", row.Rating is > 0 ? new string('★', row.Rating.Value) : "keine"),
            new("Favorit", row.IsFavorite ? "ja" : "nein"),
        };
        if (tags is not null)
        {
            var all = (await tags.GetAllAsync(ct).ConfigureAwait(true)).ToDictionary(t => t.Id);
            var ids = (await tags.GetTagIdsForFilesAsync([fileId], ct).ConfigureAwait(true)).GetValueOrDefault(fileId) ?? [];
            library.Add(new("Tags", ids.Count == 0 ? "keine" : string.Join(", ", ids.Where(all.ContainsKey).Select(id => all[id].Name))));
        }

        if (categories is not null)
        {
            var all = await categories.GetAllAsync(ct).ConfigureAwait(true);
            var values = (await categories.GetValuesForFilesAsync([fileId], ct).ConfigureAwait(true)).GetValueOrDefault(fileId);
            foreach (var category in all)
            {
                if (values is not null && values.TryGetValue(category.Id, out var valueId) && category.Values.FirstOrDefault(v => v.Id == valueId) is { } value)
                {
                    library.Add(new(category.Name, value.Name));
                }
            }
        }

        sections.Add(new PropertySection("Bibliothek", library));

        if (row.MediaType == MediaType.Image && !row.Missing)
        {
            var exif = await Task.Run(() => ReadExif(row.FullPath), ct).ConfigureAwait(true);
            if (exif.Count > 0)
            {
                sections.Add(new PropertySection("EXIF", exif));
            }
        }

        return new PhotoProperties(row.Name, row.FullPath, sections);
    }

    private sealed record Row(
        string Name, string Folder, string Extension, MediaType MediaType, long Size, int? Width, int? Height, int? DurationMs,
        DateTime Created, DateTime Modified, bool Missing, DateTime? CapturedAt, int? CapturedOffset, string? Camera,
        double? Latitude, double? Longitude, byte? Rating, bool IsFavorite)
    {
        public string FullPath => Path.Combine(Folder, Name);
    }

    private static Row? ReadRow(ILibraryContextFactory factory, int fileId)
    {
        using var connection = factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.Name, d.Path, f.Extension, f.MediaType, f.Size, f.Width, f.Height, f.DurationMs, f.CreatedAtUtc, f.ModifiedAtUtc,
                   f.Missing, f.CapturedAt, f.CapturedAtOffset, f.CameraModel, f.Latitude, f.Longitude, f.Rating, f.IsFavorite
            FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id = $f;
            """;
        cmd.Parameters.AddWithValue("$f", fileId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        static DateTime Unix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;
        return new Row(
            r.GetString(0), r.GetString(1), r.GetString(2), (MediaType)r.GetInt32(3), r.GetInt64(4),
            r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7),
            Unix(r.GetInt64(8)), Unix(r.GetInt64(9)), r.GetBoolean(10),
            r.IsDBNull(11) ? null : Unix(r.GetInt64(11)), r.IsDBNull(12) ? null : r.GetInt32(12), r.IsDBNull(13) ? null : r.GetString(13),
            r.IsDBNull(14) ? null : r.GetDouble(14), r.IsDBNull(15) ? null : r.GetDouble(15),
            r.IsDBNull(16) ? null : (byte)r.GetInt32(16), r.GetBoolean(17));
    }

    /// <summary>EXIF-Felder über WIC (nur Kopf, keine Pixel); leere Liste, wenn die Datei kein lesbares EXIF hat.</summary>
    internal static List<PropertyRow> ReadExif(string path)
    {
        var rows = new List<PropertyRow>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames[0].Metadata is not BitmapMetadata metadata)
            {
                return rows;
            }

            foreach (var (label, query, format) in ExifFields)
            {
                try
                {
                    if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is { } value && format(value) is { Length: > 0 } text)
                    {
                        rows.Add(new PropertyRow(label, text.Trim()));
                    }
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or FormatException or InvalidCastException or OverflowException)
                {
                    // Feld in diesem Format nicht lesbar
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            // kein lesbares Bild/EXIF
        }

        return rows;
    }

    private static double? Rational(object value) => value switch
    {
        ulong u => (u >> 32) == 0 ? null : (u & 0xFFFFFFFF) / (double)(u >> 32),
        long l => ((ulong)l >> 32) == 0 ? null : ((ulong)l & 0xFFFFFFFF) / (double)((ulong)l >> 32),
        _ => null,
    };

    private static string? Exposure(object value)
    {
        if (value is not ulong and not long)
        {
            return null;
        }

        var raw = value is ulong u ? u : (ulong)(long)value;
        var numerator = raw & 0xFFFFFFFF;
        var denominator = raw >> 32;
        if (numerator == 0 || denominator == 0)
        {
            return null;
        }

        var seconds = numerator / (double)denominator;
        return seconds >= 1 ? $"{seconds.ToString("0.#", German)} s" : $"1/{Math.Round(1 / seconds).ToString("0", German)} s";
    }

    private static string FormatSize(long bytes)
        => bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.##} GB ({bytes:N0} Byte)"
            : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB ({bytes:N0} Byte)"
            : bytes >= 1 << 10 ? $"{bytes / 1024.0:0.#} KB ({bytes:N0} Byte)"
            : $"{bytes:N0} Byte";

    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("d. MMMM yyyy, HH:mm", German);
}
