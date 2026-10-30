using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
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
/// Ansicht „Ordner“: das Nachtragen des Aufnahmedatums meldet alle paar Sekunden einen Stapel — die Zeilen dürfen dabei
/// nicht neu aufgebaut werden (sonst begann der Aufbau großer Ordner immer wieder von vorn, 2026-10-30). In den Kacheln
/// lädt die Galerie solche Meldungen gebündelt nach.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class FolderRowsReloadTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private ArchiveTestDb _db = null!;
    private int _parent;
    private readonly List<int> _files = [];

    /// <summary>Ordner „Set“ mit den Unterordnern „A“ und „B“, je zwei Fotos.</summary>
    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        var parent = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "Set"));
        _parent = parent.Id;
        foreach (var name in new[] { "A", "B" })
        {
            var sub = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "Set", name), parent.Id);
            _files.Add((await _db.AddFileAsync(sub, name + "1.jpg")).Id);
            _files.Add((await _db.AddFileAsync(sub, name + "2.jpg")).Id);
        }
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _db.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public Task Datumsmeldungen_bauen_die_Ordner_Zeilen_nicht_neu_auf() => OnUiAsync(async () =>
    {
        var gallery = CreateGallery();
        await gallery.EnsureInitializedAsync();
        gallery.ViewMode = GalleryViewMode.Folders;
        WeakReferenceMessenger.Default.Send(new FolderSelectedMessage(_db.LibraryId, _parent, true));
        await WaitAsync(() => gallery.ShowFolderRows && gallery.FolderRows.Count(r => !r.IsOpenedFolder) == 2 && !gallery.IsLoading, "zwei Ordner-Zeilen");
        var rows = gallery.FolderRows.ToList();

        for (var i = 0; i < 3; i++)
        {
            WeakReferenceMessenger.Default.Send(new CaptureDatesChangedMessage(_db.LibraryId, [_files[i]]));
            await Task.Delay(120);
        }

        await Task.Delay(500); // länger als die 300 ms Bündelung des Neuladens
        gallery.FolderRows.Should().Equal(rows, "dieselben Zeilen, kein Neuaufbau");
    });

    [Fact]
    public Task Kacheln_laden_Datumsmeldungen_ausserhalb_der_Seite_gebuendelt() => OnUiAsync(async () =>
    {
        var interval = GalleryViewModel.CaptureDateReloadInterval;
        GalleryViewModel.CaptureDateReloadInterval = TimeSpan.FromMilliseconds(600);
        try
        {
            var gallery = CreateGallery();
            await gallery.EnsureInitializedAsync();
            var loads = 0;
            gallery.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GalleryViewModel.IsLoading) && gallery.IsLoading)
                {
                    loads++;
                }
            };

            for (var i = 0; i < 4; i++)
            {
                WeakReferenceMessenger.Default.Send(new CaptureDatesChangedMessage(_db.LibraryId, [999_000 + i])); // nicht auf der Seite
                await Task.Delay(100);
            }

            loads.Should().Be(0, "erst nach der Bündelungszeit");
            await WaitAsync(() => loads == 1 && !gallery.IsLoading, "ein gebündeltes Neuladen");
            await Task.Delay(800);
            loads.Should().Be(1, "vier Meldungen → einmal neu laden");
        }
        finally
        {
            GalleryViewModel.CaptureDateReloadInterval = interval;
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
}
