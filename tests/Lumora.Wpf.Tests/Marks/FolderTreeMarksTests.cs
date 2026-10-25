using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Folders;
using Lumora.Gallery.Services;
using Lumora.Marks;
using Lumora.Services.Categories;
using Lumora.Services.Library;
using Lumora.Services.Marks;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Tags;
using Lumora.Wpf.Tests.ArchiveTarget;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.Marks;

/// <summary>
/// Ordnerbaum mit Tag-/Kategorie-Zahlen (Mockup 16, 0.4.35) gegen eine echte, migrierte DB: Zahlen nur für Dateien direkt im
/// Ordner, Tooltip „n von m Fotos“ mit Namen, Neuzählung nach einer Zuweisung (FilesChangedMessage Metadata), Schalter aus →
/// keine Zahlen; die Auswahl-Optik (0.4.30) und das Zählen der Dateien bleiben unberührt.
/// </summary>
public sealed class FolderTreeMarksTests : IAsyncLifetime
{
    private ArchiveTestDb _db = null!;
    private TagService _tags = null!;
    private CategoryService _categories = null!;
    private string _y2020 = null!;
    private string _feier = null!;
    private int _a;
    private int _b;
    private int _c;

    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        var root = await _db.AddFolderAsync(_db.ArchiveRootId, _db.ArchiveDir);
        _y2020 = Path.Combine(_db.ArchiveDir, "2020");
        _feier = Path.Combine(_y2020, "Feier");
        var y2020 = await _db.AddFolderAsync(_db.ArchiveRootId, _y2020, root.Id);
        var feier = await _db.AddFolderAsync(_db.ArchiveRootId, _feier, y2020.Id);
        _a = (await _db.AddFileAsync(y2020, "a.jpg")).Id;
        _b = (await _db.AddFileAsync(y2020, "b.jpg")).Id;
        _c = (await _db.AddFileAsync(feier, "c.jpg")).Id;
        await _db.AddFileAsync(y2020, "ohne.jpg");

        var messenger = new WeakReferenceMessenger(); // eigene Nachrichten: der Test steuert die Neuzählung selbst
        _tags = new TagService(_db.Factory, NullLogger<TagService>.Instance, messenger);
        _categories = new CategoryService(_db.Factory, NullLogger<CategoryService>.Instance, messenger);
    }

    public Task DisposeAsync()
    {
        _categories.Dispose();
        _db.Dispose();
        return Task.CompletedTask;
    }

    private FolderTreeViewModel Create(MarkSettingsState state)
    {
        var libraries = new LibraryService(_db.Factory, _db.Paths, NullLogger<LibraryService>.Instance, new WeakReferenceMessenger());
        var folderActions = new FolderActions(null!, null!, null!, null!, null!, null!, null!, NullService.Create<IGalleryDialogService>(), NullLogger<FolderActions>.Instance);
        return new FolderTreeViewModel(
            _db.Factory,
            libraries,
            NullService.Create<IFileOperationService>(),
            new ScriptedDialogs(),
            folderActions,
            NullLogger<FolderTreeViewModel>.Instance,
            markCounter: new FolderMarkCounter(_db.Factory),
            markSettings: state,
            markTags: _tags,
            markCategories: _categories);
    }

    /// <summary>Zuordnungen direkt in die Tabellen — unabhängig von der Gruppen-Prüfung der Dienste.</summary>
    private async Task LinkAsync(int fileId, int? tagId = null, (int CategoryId, int ValueId)? value = null)
    {
        await using var ctx = _db.Factory.Create();
        if (tagId is { } t)
        {
            ctx.FileTags.Add(new FileTag { FileId = fileId, TagId = t });
        }

        if (value is { } v)
        {
            ctx.FileCategoryValues.Add(new FileCategoryValue { FileId = fileId, CategoryId = v.CategoryId, ValueId = v.ValueId });
        }

        await ctx.SaveChangesAsync();
    }

    private static FolderNode Find(IEnumerable<FolderNode> nodes, string path)
    {
        foreach (var node in nodes.Where(n => !n.IsPlaceholder))
        {
            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (path.StartsWith(node.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                node.IsExpanded = true;
                return Find(node.Children, path);
            }
        }

        throw new InvalidOperationException("Knoten nicht gefunden: " + path);
    }

    /// <summary>Nachrichten und Schalter laufen über UiThread.Post (ggf. auf dem Test-UI-Thread) — deshalb warten statt sofort prüfen.</summary>
    private static async Task WaitForAsync(Func<bool> condition, string what)
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
    public async Task Zahlen_direkt_im_Ordner_mit_Tooltip_und_Neuzaehlung_nach_Zuweisung()
    {
        var urlaub = await _tags.CreateAsync("Urlaub");
        var anlass = await _categories.CreateAsync("Anlass", ["Feier"]);
        await LinkAsync(_a, urlaub.Id);
        await LinkAsync(_c, urlaub.Id);
        await LinkAsync(_b, value: (anlass.Id, anlass.Values[0].Id));
        var vm = Create(new MarkSettingsState());
        await vm.EnsureLoadedAsync();
        await vm.MarksRefresh;

        var y2020 = Find(vm.Roots, _y2020);
        y2020.FileCount.Should().Be(3);
        y2020.Marks.Should().NotBeNull();
        y2020.Marks!.TaggedFiles.Should().Be(1, "c liegt im Unterordner „Feier“ und zählt dort, nicht hier");
        y2020.Marks.CategorizedFiles.Should().Be(1);
        var content = y2020.Marks.BuildToolTipContent();
        content.TagsHeader.Should().Be("Tags · 1 von 3 Fotos");
        content.Tags.Select(c => c.Name).Should().Equal("Urlaub");
        content.CategoriesHeader.Should().Be("Kategorien · 1 von 3 Fotos");
        content.Categories.Select(c => c.Name).Should().Equal("Anlass: Feier");
        Find(vm.Roots, _feier).Marks!.TaggedFiles.Should().Be(1);
        Find(vm.Roots, _db.ArchiveDir).Marks.Should().BeNull("in der Wurzel selbst liegen keine Dateien");

        // Zuweisung → FilesChangedMessage (Metadata) → gebündelt neu gezählt, ohne den Baum neu aufzubauen
        var before = Find(vm.Roots, _y2020);
        await LinkAsync(_b, urlaub.Id);
        WeakReferenceMessenger.Default.Send(new FilesChangedMessage(_db.LibraryId, FilesChangeKind.Metadata, [_b]));
        await WaitForAsync(() => before.Marks?.TaggedFiles == 2, "Neuzählung nach der Zuweisung");
        var after = Find(vm.Roots, _y2020);
        after.Should().BeSameAs(before, "nur die Zahlen ändern sich, der Knoten bleibt");
        after.Marks!.TaggedFiles.Should().Be(2);
    }

    [Fact]
    public async Task Schalter_aus_zeigt_keine_Zahlen_und_an_wieder()
    {
        var urlaub = await _tags.CreateAsync("Urlaub");
        await LinkAsync(_a, urlaub.Id);
        var state = new MarkSettingsState();
        var vm = Create(state);
        await vm.EnsureLoadedAsync();
        await vm.MarksRefresh;
        Find(vm.Roots, _y2020).Marks.Should().NotBeNull();

        var node = Find(vm.Roots, _y2020);
        state.ShowInFolderTree = false;
        await WaitForAsync(() => node.Marks is null, "Zahlen weg");
        Find(vm.Roots, _y2020).FileCount.Should().Be(3, "die Dateizahl bleibt");

        state.ShowInFolderTree = true;
        await WaitForAsync(() => node.Marks?.TaggedFiles == 1, "Zahlen wieder da");
    }

    [Fact]
    public async Task Ohne_Zaehldienst_bleibt_der_Baum_wie_bisher()
    {
        var libraries = new LibraryService(_db.Factory, _db.Paths, NullLogger<LibraryService>.Instance, new WeakReferenceMessenger());
        var folderActions = new FolderActions(null!, null!, null!, null!, null!, null!, null!, NullService.Create<IGalleryDialogService>(), NullLogger<FolderActions>.Instance);
        var vm = new FolderTreeViewModel(_db.Factory, libraries, NullService.Create<IFileOperationService>(), new ScriptedDialogs(), folderActions, NullLogger<FolderTreeViewModel>.Instance);
        await vm.EnsureLoadedAsync();
        await vm.MarksRefresh;

        Find(vm.Roots, _y2020).Marks.Should().BeNull();
        Find(vm.Roots, _y2020).TotalFileCount.Should().Be(4);
    }
}
