using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Gallery.Archives;
using Lumora.Gallery.Categories;
using Lumora.Gallery.Data;
using Lumora.Gallery.ListView;
using Lumora.Gallery.Services;
using Lumora.Gallery.Tags;
using Lumora.Gallery.ViewModels;
using Lumora.Lightbox;
using Lumora.Removal;
using Lumora.Services.Catalog;
using Lumora.Services.Categories;
using Lumora.Services.Operations;
using Lumora.Services.Removal;
using Lumora.Services.Tags;
using Lumora.Services.Thumbnails;
using Lumora.Wpf.Tests.ArchiveTarget;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Removal;

/// <summary>
/// Galerie (ADR-0065): Filter „Ablage: Nur fehlende“ (SQL, Chip, Bedingung, leere Ansicht) und „Aus der Bibliothek
/// entfernen“ für die Auswahl — sichtbar bei Filter oder fehlenden in der Auswahl, gemischte Auswahl nur mit Hinweis.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GalleryMissingFilterTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private ArchiveTestDb _db = null!;
    private readonly FakeRemovalService _service = new();
    private readonly FakeRemovalDialogs _dialogs = new();
    private int _folderA;
    private int _missing1;
    private int _missing2;
    private int _present;

    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        var a = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "A"));
        var b = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "B"));
        _folderA = a.Id;
        _missing1 = (await _db.AddFileAsync(a, "weg-a.jpg")).Id;
        _missing2 = (await _db.AddFileAsync(b, "weg-b.jpg")).Id;
        _present = (await _db.AddFileAsync(a, "da.jpg")).Id;
        await using var ctx = _db.Factory.Create();
        await ctx.Files.Where(f => f.Id == _missing1 || f.Id == _missing2).ExecuteUpdateAsync(s => s.SetProperty(f => f.Missing, true));
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Filter_nur_fehlende_zaehlt_nur_fehlende_auch_mit_Ordnerfilter()
    {
        using var source = new GalleryPageSource(_db.Factory);
        source.Open();
        var missing = new GalleryFilterState(Location: GalleryLocation.Missing);

        (await source.GetTotalCountAsync(new GalleryQuery(_db.LibraryId, missing, null), CancellationToken.None)).Should().Be(2);
        (await source.GetTotalCountAsync(new GalleryQuery(_db.LibraryId, missing, null, FolderId: _folderA), CancellationToken.None)).Should().Be(1);
        var page = await source.LoadPageAsync(new GalleryQuery(_db.LibraryId, missing, null), 0, 10, 2, CancellationToken.None);
        page.Should().OnlyContain(f => f.Missing);
        missing.ActiveCount.Should().Be(1);
        GalleryPageSource.BuildFilterClause(missing).Should().Contain("f.Missing = 1");
        GalleryViewModel.EmptyMessageForFileFilter(missing).Should().Contain("Keine fehlenden Dateien");
    }

    [Fact]
    public Task Auswahl_entfernen_nur_fehlende_mit_Knopf_und_Hinweis_bei_gemischter_Auswahl() => OnUiAsync(async () =>
    {
        var gallery = CreateGallery();
        await gallery.EnsureInitializedAsync();
        await WaitAsync(() => !gallery.IsLoading && gallery.TotalCount == 3, "erste Seite");
        gallery.ShowRemoveMissingAction.Should().BeFalse("weder Filter noch fehlende gewählt");
        gallery.LocationOptions.Should().Contain(o => o.Location == GalleryLocation.Missing && o.Label == "Nur fehlende");

        gallery.FilterState = new GalleryFilterState(Location: GalleryLocation.Missing);
        await WaitAsync(() => !gallery.IsLoading && gallery.TotalCount == 2, "nur fehlende");
        gallery.ShowRemoveMissingAction.Should().BeTrue();
        gallery.LocationChipText.Should().Be("Fehlt");

        // alle gewählten fehlen → Bestätigung, Entfernen mit genau diesen Ids
        foreach (var item in gallery.CurrentPage)
        {
            item.IsSelected = true;
        }

        _service.Summary = new MissingSummary([_missing1, _missing2], 0, 0, [], UserDataCounts.None);
        _service.RemoveResult = new MissingRemovalBatchResult(4, 2, 0, 0, 0, 0, 0, []);
        await gallery.RemoveMissingSelectionCommand.ExecuteAsync(null);
        var removed = _service.RemoveCalls.Should().ContainSingle().Subject.Should().BeOfType<MissingTarget.Selection>().Subject;
        removed.FileIds.Should().BeEquivalentTo([_missing1, _missing2]);
        _dialogs.Requests.Should().ContainSingle();

        // gemischte Auswahl → Hinweis, nichts entfernt
        gallery.FilterState = GalleryFilterState.None;
        await WaitAsync(() => !gallery.IsLoading && gallery.TotalCount == 3, "alle");
        gallery.ClearSelectionCommand.Execute(null);
        foreach (var item in gallery.CurrentPage.Where(i => i.Id == _missing1 || i.Id == _present))
        {
            item.IsSelected = true;
        }

        gallery.SelectedMissingCount.Should().Be(1);
        gallery.ShowRemoveMissingAction.Should().BeTrue("fehlende in der Auswahl");
        _service.Summary = new MissingSummary([_missing1], 1, 0, [], UserDataCounts.None);
        await gallery.RemoveMissingSelectionCommand.ExecuteAsync(null);
        _service.RemoveCalls.Should().HaveCount(1, "gemischte Auswahl entfernt nichts");
        _dialogs.Requests.Should().HaveCount(1);
        gallery.StatusText.Should().Contain("Nur fehlende Dateien");
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
            NullLogger<GalleryViewModel>.Instance,
            removalActions: new RemovalActions(_service, _dialogs, NullLogger<RemovalActions>.Instance));

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
