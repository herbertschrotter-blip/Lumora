using System.Collections.Concurrent;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Gallery.Archives;
using Lumora.Gallery.Categories;
using Lumora.Gallery.Data;
using Lumora.Gallery.ListView;
using Lumora.Gallery.Services;
using Lumora.Gallery.Tags;
using Lumora.Gallery.ViewModels;
using Lumora.Lightbox;
using Lumora.Services.Catalog;
using Lumora.Services.Categories;
using Lumora.Services.Cloud;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Tags;
using Lumora.Services.Thumbnails;
using Lumora.Wpf.Tests.ArchiveTarget;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Cloud;

/// <summary>
/// Galerie mit OneDrive-Platzhaltern (ADR-0077, Nachbesserung 0.5.41): Vorschaubilder aus dem Cache meldet die Galerie für
/// alle Platzhalter der Seite (nur Cache, nichts eingereiht) — sonst blitzten bei jedem Besuch leere Kacheln auf; fehlende
/// fordert sie nur für den sichtbaren Bereich plus Vorlauf an (Shell-Abruf). Lokale Dateien wie bisher die ganze Seite.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GalleryCloudThumbnailTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int Placeholders = 40;
    private ArchiveTestDb _db = null!;
    private int _folderId;
    private string _folderPath = null!;
    private readonly List<int> _online = [];
    private int _local;

    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        _folderPath = Path.Combine(_db.ArchiveDir, "OneDrive");
        var folder = await _db.AddFolderAsync(_db.ArchiveRootId, _folderPath);
        _folderId = folder.Id;
        for (var i = 0; i < Placeholders; i++)
        {
            _online.Add((await _db.AddFileAsync(folder, $"online{i:D2}.jpg")).Id);
        }

        _local = (await _db.AddFileAsync(folder, "lokal.jpg")).Id;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _db.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public Task Platzhalter_der_ganzen_Seite_nur_aus_dem_Cache_fehlende_nur_sichtbar() => OnUiAsync(async () =>
    {
        var cloud = new CloudUiTests.FakeCloudFiles();
        cloud.Roots.Add(_folderPath);
        for (var i = 0; i < Placeholders; i++)
        {
            cloud.States[Path.Combine(_folderPath, $"online{i:D2}.jpg")] = new CloudFileState(true, true, false, false);
        }

        var thumbnails = new RecordingThumbnails();
        var gallery = CreateGallery(thumbnails, cloud);
        await gallery.EnsureInitializedAsync();
        WeakReferenceMessenger.Default.Send(new FolderSelectedMessage(_db.LibraryId, _folderId, true));
        await WaitAsync(() => thumbnails.Cached.Count > 0 && thumbnails.Requested.Any(r => r.Any(q => _online.Contains(q.FileId))), "Anforderungen der Seite");

        var requested = thumbnails.Requested.SelectMany(r => r).Select(r => r.FileId).ToList();
        requested.Should().Contain(_local, "lokale Dateien wie bisher");
        var visible = thumbnails.Requested.Where(r => r.Any(q => _online.Contains(q.FileId))).ToList();
        visible.Should().OnlyContain(
            r => r.Count(q => _online.Contains(q.FileId)) <= 2 * GalleryViewModel.CloudLookahead && !r.Any(q => q.FileId == _local),
            "fehlende Platzhalter nur im sichtbaren Bereich (hier noch unbekannt: die ersten Kacheln), getrennt von den lokalen");
        var visibleIds = visible.SelectMany(r => r).Select(r => r.FileId).ToHashSet();
        var cachedIds = thumbnails.Cached.SelectMany(r => r).Select(r => r.FileId).ToHashSet();
        cachedIds.Should().NotIntersectWith(visibleIds, "sichtbare nicht doppelt melden");
        cachedIds.Union(visibleIds).Should().BeEquivalentTo(_online,
            "alle übrigen Platzhalter der Seite: Treffer aus dem Cache sofort, ohne Shell-Abruf");
    });

    private GalleryViewModel CreateGallery(IThumbnailService thumbnails, ICloudFileService cloud)
        => new(
            _db.Factory,
            new GalleryPageSource(_db.Factory),
            thumbnails,
            NullService.Create<ICatalogUpdateService>(),
            NullService.Create<IFileOperationService>(),
            NullService.Create<IArchiveMoveService>(),
            NullService.Create<ILightboxService>(),
            NullService.Create<IGalleryDialogService>(),
            NullService.Create<IFolderPickerService>(),
            NullService.Create<IArchiveExtractDialogService>(),
            NullService.Create<ITagService>(),
            NullService.Create<ITagPickerService>(),
            NullService.Create<ICategoryService>(),
            NullService.Create<ICategoryPickerService>(),
            NullService.Create<IGalleryViewSettingsStore>(),
            NullLogger<GalleryViewModel>.Instance,
            cloudFiles: cloud);

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Nicht erreicht: " + what);
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Merkt sich Anforderungen und „nur Cache“-Abfragen; meldet nichts.</summary>
    private sealed class RecordingThumbnails : IThumbnailService
    {
        public ConcurrentQueue<IReadOnlyList<ThumbnailRequest>> Requested { get; } = new();

        public ConcurrentQueue<IReadOnlyList<ThumbnailRequest>> Cached { get; } = new();

        public int ThumbnailSize => ThumbnailService.Size;

        public int PendingCount => 0;

        public event Action<ThumbnailReadyEventArgs>? ThumbnailReady
        {
            add { }
            remove { }
        }

        public Task RequestThumbnailsAsync(IReadOnlyList<ThumbnailRequest> requests, CancellationToken cancellationToken = default)
        {
            Requested.Enqueue([.. requests]);
            return Task.CompletedTask;
        }

        public Task ReportCachedThumbnailsAsync(IReadOnlyList<ThumbnailRequest> requests, CancellationToken cancellationToken = default)
        {
            Cached.Enqueue([.. requests]);
            return Task.CompletedTask;
        }
    }
}
