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
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Tags;
using Lumora.Services.Thumbnails;
using Lumora.Wpf.Tests.ArchiveTarget;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Ordner-Chip und Pfad über der Galerie (ADR-0068, Mockup 18) sowie „Suchen ▸“ als Bedingung: Ordner aus der Leiste setzt Chip und
/// Breadcrumb, Klick auf ein Glied wählt den Elternordner und meldet ihn der Leiste, das Kreuz hebt den Ordner auf (Nachricht mit
/// „kein Ordner“), ein Suchergebnis wirkt mit den übrigen Filtern zusammen.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GalleryFolderChipTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private ArchiveTestDb _db = null!;
    private int _reiseId;
    private int _bergeId;
    private readonly Dictionary<string, int> _ids = new();

    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        var root = await _db.AddFolderAsync(_db.ArchiveRootId, _db.ArchiveDir);
        var reise = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "Reise"), root.Id);
        var berge = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "Reise", "Berge"), reise.Id);
        _reiseId = reise.Id;
        _bergeId = berge.Id;
        foreach (var (folder, name) in new[] { (reise, "a.jpg"), (berge, "b.jpg"), (berge, "c.jpg"), (root, "d.jpg") })
        {
            _ids[name] = (await _db.AddFileAsync(folder, name)).Id;
        }
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _db.Dispose();
        return Task.CompletedTask;
    }

    private static Task OnUi(Func<Task> scenario)
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

    [Fact]
    public Task Chip_Breadcrumb_und_Kreuz_arbeiten_mit_der_Ordnerleiste_zusammen() => OnUi(async () =>
    {
        var gallery = CreateGallery();
        await gallery.EnsureInitializedAsync();
        gallery.TotalCount.Should().Be(4);
        var sent = new List<FolderSelectedMessage>();
        var recipient = new object();
        WeakReferenceMessenger.Default.Register<FolderSelectedMessage>(recipient, (_, m) => sent.Add(m));
        try
        {
            WeakReferenceMessenger.Default.Send(new FolderSelectedMessage(_db.LibraryId, _bergeId, true));
            await WaitAsync(() => !gallery.IsLoading && gallery.FolderId == _bergeId && gallery.TotalCount == 2, "Ordner Berge");
            await WaitAsync(() => gallery.FolderBreadcrumb.Count == 3, "Pfad geladen");
            gallery.HasFolderFilter.Should().BeTrue();
            gallery.FolderChipText.Should().Be("Ordner: Berge (mit Unterordnern)");
            gallery.FolderBreadcrumb.Select(c => c.Name).Should().Equal("archiv", "Reise", "Berge");
            gallery.FolderBreadcrumb.Last().IsLast.Should().BeTrue();

            sent.Clear();
            await gallery.OpenBreadcrumbCommand.ExecuteAsync(gallery.FolderBreadcrumb[1]);
            await WaitAsync(() => !gallery.IsLoading && gallery.FolderId == _reiseId && gallery.TotalCount == 3, "Elternordner Reise mit Unterordnern");
            sent.Should().ContainSingle(m => m.FolderId == _reiseId, "die Ordnerleiste zieht nach");

            // „Suchen ▸“ wirkt zusammen mit dem Ordner
            await gallery.ApplyRelatedSearchAsync("am gleichen Ort", [_ids["a.jpg"], _ids["d.jpg"]]);
            await WaitAsync(() => !gallery.IsLoading && gallery.TotalCount == 1, "Suchergebnis UND Ordner");
            gallery.MotifFilterValueText.Should().Be("am gleichen Ort");

            sent.Clear();
            await gallery.ClearFolderFilterCommand.ExecuteAsync(null);
            await WaitAsync(() => !gallery.IsLoading && gallery.TotalCount == 2, "ohne Ordner nur noch das Suchergebnis");
            gallery.FolderBreadcrumb.Should().BeEmpty();
            sent.Should().ContainSingle(m => m.FolderId == null, "Kreuz am Chip hebt auch die Auswahl der Leiste auf");
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    });

    private GalleryViewModel CreateGallery()
        => new(
            _db.Factory,
            new GalleryPageSource(_db.Factory),
            NullService.Create<IThumbnailService>(),
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
            NullLogger<GalleryViewModel>.Instance);
}
