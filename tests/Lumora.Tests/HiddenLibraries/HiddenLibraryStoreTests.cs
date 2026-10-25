using System.Text;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.Infrastructure;

namespace Lumora.Tests.HiddenLibraries;

/// <summary>
/// ADR-0049: Kenndatei einer versteckten Bibliothek in <c>.lumora</c>, Kennwort nur als PBKDF2-Prüfwert, Zurücksetzen nur
/// mit Bestätigungsdatei im Container, Einstellungen in der Kenndatei.
/// </summary>
public sealed class HiddenLibraryStoreTests : IDisposable
{
    private const int FastIterations = 1_000; // Testlaufzeit; Produktion: HiddenLibraryPassword.DefaultIterations
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "hidden-" + Guid.NewGuid().ToString("N"));
    private readonly HiddenLibraryStore _store = new(FastIterations);

    public HiddenLibraryStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Password_verifier_uses_pbkdf2_sha256_with_at_least_600000_iterations_and_never_the_password()
    {
        HiddenLibraryPassword.DefaultIterations.Should().BeGreaterThanOrEqualTo(600_000);
        var verifier = HiddenLibraryPassword.Create("geheim-Passwort", FastIterations);
        verifier.Algorithm.Should().Be("PBKDF2-SHA256");
        verifier.Iterations.Should().Be(FastIterations);
        verifier.Hash.Should().NotContain("geheim");
        HiddenLibraryPassword.Verify("geheim-Passwort", verifier).Should().BeTrue();
        HiddenLibraryPassword.Verify("geheim-passwort", verifier).Should().BeFalse("Groß/Klein zählt");
        HiddenLibraryPassword.Verify("", verifier).Should().BeFalse();
        HiddenLibraryPassword.Verify("geheim-Passwort", verifier with { Algorithm = "MD5" }).Should().BeFalse();
        HiddenLibraryPassword.Verify("geheim-Passwort", verifier with { Hash = "kaputt" }).Should().BeFalse();

        var other = HiddenLibraryPassword.Create("geheim-Passwort", FastIterations);
        other.Salt.Should().NotBe(verifier.Salt, "jedes Kennwort bekommt ein eigenes Salz");
    }

    [Theory]
    [InlineData("", "", "Bitte ein Kennwort eingeben.")]
    [InlineData("kurz", "kurz", "Das Kennwort braucht mindestens 6 Zeichen.")]
    [InlineData("langgenug", "langgenug2", "Die beiden Kennwörter stimmen nicht überein.")]
    [InlineData("langgenug", "langgenug", null)]
    public void Password_rules(string password, string repeat, string? expected)
        => HiddenLibraryPassword.Validate(password, repeat).Should().Be(expected);

    [Fact]
    public void Create_writes_manifest_into_hidden_system_catalog_folder_without_the_password()
    {
        var folder = NewLibraryFolder();

        var manifest = _store.Create(folder, "  Tresor  ", "kennwort-123");

        manifest.Name.Should().Be("Tresor");
        manifest.AutoLockMinutes.Should().Be(10, "Standard: 10 Minuten");
        manifest.DismountOnLock.Should().BeFalse("Standard seit 0.4.38: nicht aushängen");
        var catalog = HiddenLibraryStore.GetCatalogFolder(folder);
        catalog.Should().Be(Path.Combine(folder, ".lumora"));
        var attributes = new DirectoryInfo(catalog).Attributes;
        attributes.Should().HaveFlag(FileAttributes.Hidden).And.HaveFlag(FileAttributes.System).And.HaveFlag(FileAttributes.NotContentIndexed);
        var json = File.ReadAllText(HiddenLibraryStore.GetManifestPath(folder));
        json.Should().Contain("lumora-hidden-library").And.NotContain("kennwort-123");
        HiddenLibraryStore.TryLoad(folder)!.Name.Should().Be("Tresor");
        HiddenLibraryStore.Exists(folder).Should().BeTrue();

        var again = () => _store.Create(folder, "Zweite", "kennwort-123");
        again.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Verify_password_and_missing_or_corrupt_manifest()
    {
        var folder = NewLibraryFolder();
        HiddenLibraryStore.VerifyPassword(folder, "egal-123").Should().BeFalse("keine Kenndatei");

        _store.Create(folder, "Tresor", "richtig-123");
        HiddenLibraryStore.VerifyPassword(folder, "richtig-123").Should().BeTrue();
        HiddenLibraryStore.VerifyPassword(folder, "falsch-123").Should().BeFalse();

        File.WriteAllText(HiddenLibraryStore.GetManifestPath(folder), "{ kaputt", Encoding.UTF8);
        HiddenLibraryStore.TryLoad(folder).Should().BeNull();
        HiddenLibraryStore.VerifyPassword(folder, "richtig-123").Should().BeFalse();
    }

    [Fact]
    public void Reset_requires_confirmation_file_in_the_container_and_removes_it()
    {
        var folder = NewLibraryFolder();
        _store.Create(folder, "Tresor", "vergessen-1");

        var withoutFile = () => _store.ResetPassword(folder, "neu-123456");
        withoutFile.Should().Throw<InvalidOperationException>().WithMessage("*reset-password.txt*");
        HiddenLibraryStore.VerifyPassword(folder, "vergessen-1").Should().BeTrue("unverändert");

        File.WriteAllText(HiddenLibraryStore.GetResetRequestPath(folder), "ja");
        HiddenLibraryStore.HasResetRequest(folder).Should().BeTrue();
        _store.ResetPassword(folder, "neu-123456");

        HiddenLibraryStore.VerifyPassword(folder, "neu-123456").Should().BeTrue();
        HiddenLibraryStore.VerifyPassword(folder, "vergessen-1").Should().BeFalse();
        HiddenLibraryStore.HasResetRequest(folder).Should().BeFalse("Bestätigungsdatei wird nach dem Zurücksetzen gelöscht");
    }

    [Fact]
    public void Settings_are_stored_in_the_manifest_and_clamped()
    {
        var folder = NewLibraryFolder();
        _store.Create(folder, "Tresor", "kennwort-1");

        HiddenLibraryStore.SaveSettings(folder, 30, dismountOnLock: false);
        var loaded = HiddenLibraryStore.TryLoad(folder)!;
        loaded.AutoLockMinutes.Should().Be(30);
        loaded.DismountOnLock.Should().BeFalse();
        HiddenLibraryStore.VerifyPassword(folder, "kennwort-1").Should().BeTrue("Einstellungen ändern das Kennwort nicht");

        HiddenLibraryStore.SaveSettings(folder, 0, dismountOnLock: true).AutoLockMinutes.Should().Be(HiddenLibraryManifest.MinAutoLockMinutes);
        HiddenLibraryStore.SaveSettings(folder, 100_000, dismountOnLock: true).AutoLockMinutes.Should().Be(HiddenLibraryManifest.MaxAutoLockMinutes);
    }

    [Fact]
    public void Old_manifest_with_unchosen_dismount_counts_as_off_until_chosen_again()
    {
        // Bis 0.4.37 stand „aushängen“ ungefragt auf an (ohne „dismountChosen“) — gilt jetzt als aus
        var folder = NewLibraryFolder();
        _store.Create(folder, "Tresor", "kennwort-1");
        var path = HiddenLibraryStore.GetManifestPath(folder);
        var old = File.ReadAllText(path).Replace("\"dismountOnLock\": false", "\"dismountOnLock\": true").Replace(",\n  \"dismountChosen\": true", string.Empty).Replace(",\r\n  \"dismountChosen\": true", string.Empty);
        old.Should().Contain("\"dismountOnLock\": true").And.NotContain("dismountChosen");
        File.WriteAllText(path, old);

        HiddenLibraryStore.TryLoad(folder)!.DismountOnLock.Should().BeFalse("alte Kenndatei ohne bewusste Wahl");

        HiddenLibraryStore.SaveSettings(folder, 10, dismountOnLock: true);
        HiddenLibraryStore.TryLoad(folder)!.DismountOnLock.Should().BeTrue("bewusst eingeschaltet");
        HiddenLibraryStore.SaveSettings(folder, 10, dismountOnLock: false);
        HiddenLibraryStore.TryLoad(folder)!.DismountOnLock.Should().BeFalse();
    }

    [Fact]
    public void Catalog_paths_keep_database_thumbnails_logs_cache_and_temp_inside_lumora()
    {
        var folder = NewLibraryFolder();
        var paths = HiddenLibraryStore.GetCatalogPaths(folder);
        var catalog = HiddenLibraryStore.GetCatalogFolder(folder);
        var context = new CatalogContext(paths, isHidden: true);

        foreach (var path in new[] { paths.LibraryDatabasePath, paths.ThumbnailsRoot, paths.LogsRoot, paths.AppDataRoot, context.CacheRoot, context.TempRoot, context.CatalogRoot })
        {
            AppDataPaths.IsSameOrInside(path, catalog).Should().BeTrue(path);
        }

        context.IsHidden.Should().BeTrue();
        paths.GetLibraryThumbnailsPath(1).Should().StartWith(catalog);
    }

    private string NewLibraryFolder()
    {
        var folder = Path.Combine(_root, "lib-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
