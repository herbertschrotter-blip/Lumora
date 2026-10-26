using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services;
using Lumora.Services.Database;
using Lumora.Services.Database.Relocation;
using Lumora.Services.Faces.Models;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Media;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.HiddenLibraries;

/// <summary>
/// ADR-0049: Sitzung einer versteckten Bibliothek in der Services-Schicht — eigener Katalog in <c>.lumora</c>, Kontext
/// „versteckt“, Katalog-Umzug gesperrt, Gesichtsmodelle aus dem normalen Ordner, <c>.lumora</c> nie mitgescannt, nichts
/// im normalen Datenordner.
/// </summary>
public sealed class HiddenCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "hidden-catalog-" + Guid.NewGuid().ToString("N"));

    public HiddenCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Normal_session_registers_a_normal_catalog_context_with_cache_and_temp_under_app_data()
    {
        var paths = new TestAppDataPaths(Path.Combine(_root, "appdata"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppDataPaths>(paths);
        services.AddLumoraServices();
        using var provider = services.BuildServiceProvider();

        var context = provider.GetRequiredService<ICatalogContext>();
        context.IsHidden.Should().BeFalse();
        context.DatabasePath.Should().Be(paths.LibraryDatabasePath);
        context.CacheRoot.Should().Be(Path.Combine(paths.AppDataRoot, "cache"));
        context.TempRoot.Should().Be(Path.Combine(paths.AppDataRoot, "temp"));
        provider.GetRequiredService<ICatalogStoragePaths>().Should().BeSameAs(context);
        services.Single(d => d.ServiceType == typeof(IDatabaseRelocationService)).ImplementationType.Should().Be(typeof(DatabaseRelocationService));
    }

    [Fact]
    public async Task Hidden_session_keeps_catalog_scan_and_settings_inside_the_container()
    {
        var normalRoot = Directory.CreateDirectory(Path.Combine(_root, "appdata")).FullName;
        var library = Directory.CreateDirectory(Path.Combine(_root, "container", "Tresor")).FullName;
        new HiddenLibraryStore(iterations: 1_000).Create(library, "Tresor", "kennwort-1");
        foreach (var name in new[] { "geheim-1.jpg", "geheim-2.png", Path.Combine("Urlaub", "geheim-3.jpg") })
        {
            var file = Path.Combine(library, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[512]);
        }

        var hiddenPaths = HiddenLibraryStore.GetCatalogPaths(library);
        hiddenPaths.EnsureDirectoriesExist();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppDataPaths>(hiddenPaths);
        services.AddHiddenCatalog(hiddenPaths, normalRoot);
        services.AddLumoraServices();
        services.AddSingleton<Lumora.Services.Enhance.IEnhanceImageCodec>(new Lumora.Tests.Enhance.FakeEnhanceCodec()); // WIC lebt im Wpf-Projekt (ADR-0071)

        await using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<ICatalogContext>().IsHidden.Should().BeTrue();
            provider.GetRequiredService<ICatalogStoragePaths>().CacheRoot.Should().StartWith(HiddenLibraryStore.GetCatalogFolder(library));
            var relocation = provider.GetRequiredService<IDatabaseRelocationService>();
            relocation.Should().BeOfType<HiddenCatalogRelocationBlocker>();
            (await relocation.CheckTargetAsync(normalRoot)).State.Should().Be(RelocationTargetState.Invalid);
            (await relocation.RelocateAsync(normalRoot, RelocationMode.Move)).Success.Should().BeFalse();
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(IFaceModelProvider.OverrideVariable)))
            {
                provider.GetRequiredService<IFaceModelProvider>().ModelFolder.Should().Be(Path.Combine(normalRoot, "models", "buffalo_l"));
            }

            // ADR-0070: alle Modelle aus dem normalen Modellordner (keine Benutzerdaten, nicht ein zweites Mal in den Container)
            provider.GetRequiredService<Lumora.Services.ModelPackages.IModelStorage>().ModelsRoot.Should().Be(Path.Combine(normalRoot, "models"));
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Lumora.Services.Motifs.Models.IMotifModelProvider.OverrideVariable)))
            {
                provider.GetRequiredService<Lumora.Services.Motifs.Models.IMotifModelProvider>().ModelFolder.Should().Be(Path.Combine(normalRoot, "models", "siglip2-base-patch16-224"));
            }

            var packages = provider.GetRequiredService<Lumora.Services.ModelPackages.IModelPackageManager>().Packages;
            packages.Select(p => p.Definition.Id).Should().Equal(
                Lumora.Services.ModelPackages.ModelPackageCatalog.FacesId, Lumora.Services.ModelPackages.ModelPackageCatalog.MotifsId,
                Lumora.Services.Enhance.EnhanceModelCatalog.UpscaleId, Lumora.Services.Enhance.EnhanceModelCatalog.UpscaleNaturalId, // ADR-0071, 0.5.7
                Lumora.Services.Enhance.EnhanceModelCatalog.FacesId,
                Lumora.Services.Enhance.EnhanceModelCatalog.ColorizeId, Lumora.Services.Enhance.EnhanceModelCatalog.InpaintId,
                Lumora.Services.ModelPackages.ModelPackageCatalog.GeoNamesId, Lumora.Services.ModelPackages.ModelPackageCatalog.MapTilesId,
                Lumora.Services.ModelPackages.ModelPackageCatalog.AssistantId);
            packages.Single(p => p.Definition.Id == Lumora.Services.ModelPackages.ModelPackageCatalog.MapTilesId).Folder
                .Should().StartWith(HiddenLibraryStore.GetCatalogFolder(library), "Kartenkacheln bleiben im Container");
            // ADR-0071: Modelle von „Foto verbessern“ aus dem normalen Modellordner, Zwischenstände im Container
            packages.Where(p => p.Definition.Id.StartsWith("enhance.", StringComparison.Ordinal)).Should()
                .OnlyContain(p => p.Folder!.StartsWith(Path.Combine(normalRoot, "models"), StringComparison.OrdinalIgnoreCase));
            ((Lumora.Services.Enhance.PhotoEnhanceService)provider.GetRequiredService<Lumora.Services.Enhance.IPhotoEnhanceService>()).TempFolder
                .Should().StartWith(HiddenLibraryStore.GetCatalogFolder(library), "Zwischenstände von „Foto verbessern“ bleiben im Container");

            await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            var libraries = provider.GetRequiredService<ILibraryService>();
            var created = await libraries.CreateAsync("Tresor", library);
            await libraries.SetActiveAsync(created.Id);
            var scan = await provider.GetRequiredService<IScanService>().ScanAsync(created.Id, ScanMode.Fresh, null, CancellationToken.None);
            scan.Outcome.Should().Be(ScanOutcome.Completed);
            scan.Added.Should().Be(3, ".lumora (Katalog, Protokolle) wird nicht mitgescannt");
        }

        File.Exists(Path.Combine(library, ".lumora", "library.db")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(normalRoot, "*", SearchOption.AllDirectories).Should().BeEmpty("nichts im normalen Datenordner");
    }

    /// <summary>
    /// Zusammenbau 0.2.56 (ADR-0048/0049): die versteckte Bibliothek bekommt beim Anlegen ihre eigene Gruppe (nicht erst
    /// beim ersten Bereichs-Zugriff), Verknüpfen und „Alle verknüpften“ sind gesperrt, Motiv-Begriffe und Orte landen nur
    /// im Katalog des Containers.
    /// </summary>
    [Fact]
    public async Task Hidden_library_gets_its_group_cannot_be_linked_and_keeps_motifs_and_places_in_its_catalog()
    {
        var normalRoot = Directory.CreateDirectory(Path.Combine(_root, "appdata")).FullName;
        var library = Directory.CreateDirectory(Path.Combine(_root, "container", "Tresor")).FullName;
        new HiddenLibraryStore(iterations: 1_000).Create(library, "Tresor", "kennwort-1");
        File.WriteAllBytes(Path.Combine(library, "geheim-1.jpg"), new byte[512]);
        var hiddenPaths = HiddenLibraryStore.GetCatalogPaths(library);
        hiddenPaths.EnsureDirectoriesExist();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppDataPaths>(hiddenPaths);
        services.AddHiddenCatalog(hiddenPaths, normalRoot);
        services.AddLumoraServices();

        await using (var provider = services.BuildServiceProvider())
        {
            await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            var created = await provider.GetRequiredService<ILibraryService>().CreateAsync("Tresor", library);
            var factory = provider.GetRequiredService<ILibraryContextFactory>();
            using (var connection = factory.OpenSqliteConnection())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM Libraries l JOIN LibraryGroups g ON g.Id = l.GroupId WHERE l.Id = $id;";
                cmd.Parameters.AddWithValue("$id", created.Id);
                Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(1, "Gruppe schon beim Anlegen, ohne Umweg über den Bereichsdienst");
            }

            var scope = provider.GetRequiredService<ILibraryScope>();
            var current = await scope.GetCurrentAsync();
            current.Should().Match<LibraryScopeInfo>(s => s.LibraryId == created.Id && s.GroupId != 0 && !s.IsAllLinked && !s.IsLinked);
            var linking = provider.GetRequiredService<ILibraryLinkService>();
            await FluentActions.Awaiting(() => linking.LinkAsync(created.Id, created.Id)).Should().ThrowAsync<LibraryLinkNotAllowedException>();
            await FluentActions.Awaiting(() => linking.PreviewUnlinkAsync(created.Id)).Should().ThrowAsync<LibraryLinkNotAllowedException>();
            await FluentActions.Awaiting(() => scope.ShowAllLinkedAsync(current.GroupId)).Should().ThrowAsync<LibraryLinkNotAllowedException>();

            // Motiv-Begriffe der versteckten Sitzung: Start-Liste + eigener Begriff in der Gruppe der versteckten Bibliothek
            var motifs = provider.GetRequiredService<Lumora.Services.Motifs.IMotifTermService>();
            await motifs.AddTermAsync(created.Id, "Geheimnis");
            (await motifs.GetTermsAsync(created.Id)).Should().Contain(t => t.Name == "Geheimnis");
            using (var connection = factory.OpenSqliteConnection())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM MotifTerms WHERE GroupId <> $g;";
                cmd.Parameters.AddWithValue("$g", current.GroupId);
                Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(0);
            }

            var geo = provider.GetRequiredService<Lumora.Services.Places.IGeoCatalogReader>();
            (await geo.GetPlaceCountsAsync(current.LibraryIds)).Places.Should().BeEmpty();
        }

        File.Exists(Path.Combine(library, ".lumora", "library.db")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(normalRoot, "*", SearchOption.AllDirectories).Should().BeEmpty("Begriffe und Orte nur im Katalog des Containers");
    }

    [Fact]
    public void Enumerator_skips_lumora_even_without_hidden_attribute()
    {
        var library = Directory.CreateDirectory(Path.Combine(_root, "lib")).FullName;
        File.WriteAllBytes(Path.Combine(library, "foto.jpg"), new byte[10]);
        var catalog = Directory.CreateDirectory(Path.Combine(library, ".lumora", "thumbs", "1")).FullName;
        File.WriteAllBytes(Path.Combine(catalog, "1_400_still.jpg"), new byte[10]);

        var files = new FolderEnumerator(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance).Enumerate(library).ToList();

        files.Select(f => f.Name).Should().Equal("foto.jpg");
        CatalogFolders.IsCatalogFolder(@"X:\Tresor\.LUMORA\").Should().BeTrue();
        CatalogFolders.IsCatalogFolder(@"X:\Tresor\lumora").Should().BeFalse();
    }
}
