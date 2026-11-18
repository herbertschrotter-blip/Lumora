using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumora.Domain.Enums;
using Lumora.Imaging.Merge;
using Lumora.Services.Database;
using Lumora.Services.Thumbnails;

namespace Lumora.Merge;

/// <summary>
/// Vorschaubilder des Filmstreifens über den Vorschaubild-Dienst der Galerie (gleicher Cache): lokale Fotos werden bei Bedarf
/// erzeugt, OneDrive-Platzhalter nur aus dem Cache gemeldet — nie heruntergeladen (ADR-0078 Punkt 14).
/// </summary>
public sealed class MergeThumbnailLoader(IThumbnailService thumbnails, ILibraryContextFactory factory) : IMergeThumbnailLoader
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(45);

    public async Task LoadAsync(IReadOnlyList<MergeSourceItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        var byId = items.GroupBy(i => i.FileId).ToDictionary(g => g.Key, g => g.First());
        var rows = await Task.Run(() => Rows(byId.Keys.ToList()), ct).ConfigureAwait(true);
        var pending = new HashSet<int>(rows.Select(r => r.FileId));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(ThumbnailReadyEventArgs e)
        {
            if (!byId.TryGetValue(e.FileId, out var item))
            {
                return;
            }

            var image = Decode(e.ThumbnailPath);
            _ = dispatcher.BeginInvoke(() => item.Thumbnail = image, DispatcherPriority.Background);
            lock (pending)
            {
                if (pending.Remove(e.FileId) && pending.Count == 0)
                {
                    done.TrySetResult();
                }
            }
        }

        thumbnails.ThumbnailReady += OnReady;
        try
        {
            var local = rows.Where(r => byId[r.FileId].State == MergeSourceState.Usable).ToList();
            var cloud = rows.Where(r => byId[r.FileId].State != MergeSourceState.Usable).ToList();
            if (local.Count > 0)
            {
                await thumbnails.RequestThumbnailsAsync(local, ct).ConfigureAwait(true);
            }

            if (cloud.Count > 0)
            {
                await thumbnails.ReportCachedThumbnailsAsync(cloud, ct).ConfigureAwait(true);
            }

            await Task.WhenAny(done.Task, Task.Delay(MaxWait, ct)).ConfigureAwait(true);
        }
        finally
        {
            thumbnails.ThumbnailReady -= OnReady;
        }
    }

    private List<ThumbnailRequest> Rows(IReadOnlyList<int> ids)
    {
        var list = new List<ThumbnailRequest>(ids.Count);
        using var connection = factory.OpenSqliteConnection();
        foreach (var chunk in ids.Chunk(400))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT f.Id, f.LibraryId, d.Path, f.Name, f.MediaType, f.ModifiedAtUtc FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({string.Join(',', chunk)});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ThumbnailRequest(
                    reader.GetInt32(1),
                    reader.GetInt32(0),
                    Path.Combine(reader.GetString(2), reader.GetString(3)),
                    (MediaType)reader.GetInt32(4),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).UtcDateTime));
            }
        }

        return list;
    }

    private static BitmapSource? Decode(string path)
    {
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelHeight = 128;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
