using Lumora.Services.Infrastructure;

namespace Lumora.Tests.Database;

/// <summary>
/// Portabler Modus (0.4.40, ADR-0069): Vorrang LUMORA_APPDATA &gt; portabel (lumora.portable oder Ordner „Daten“ neben der
/// EXE) &gt; Standard. Der EXE-Ordner ist ein Temp-Ordner; %LocalAppData% wird nie angefasst.
/// </summary>
public sealed class AppDataLocationResolverTests : IDisposable
{
    private readonly string _exeDir = Path.Combine(Path.GetTempPath(), "Lumora.Tests", Guid.NewGuid().ToString("N"), "Lumora-0.5.0");
    private readonly string _defaultRoot = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "standard-wurzel");

    public AppDataLocationResolverTests()
    {
        Directory.CreateDirectory(_exeDir);
    }

    public void Dispose()
    {
        Directory.Delete(Path.GetDirectoryName(_exeDir)!, recursive: true);
    }

    [Fact]
    public void Ohne_Markierung_gilt_der_Standardordner()
    {
        var location = AppDataLocationResolver.Resolve(null, _exeDir, _defaultRoot);

        location.Should().Be(new AppDataLocation(AppDataLocationMode.Standard, _defaultRoot));
        location.DisplayText.Should().Contain("Standard").And.Contain(@"%LocalAppData%\Lumora");
    }

    [Fact]
    public void Markierungsdatei_neben_der_EXE_schaltet_den_portablen_Modus_ein()
    {
        File.WriteAllText(Path.Combine(_exeDir, AppDataLocationResolver.PortableMarkerFileName), "x");

        var location = AppDataLocationResolver.Resolve("  ", _exeDir, _defaultRoot);

        location.Mode.Should().Be(AppDataLocationMode.Portable);
        location.Root.Should().Be(Path.Combine(_exeDir, "Daten"));
        location.DisplayText.Should().Contain("Portabel").And.NotContain(_exeDir, "die Anzeige nennt keinen Pfad");
    }

    [Fact]
    public void Ordner_Daten_neben_der_EXE_schaltet_den_portablen_Modus_ebenfalls_ein()
    {
        Directory.CreateDirectory(Path.Combine(_exeDir, AppDataLocationResolver.PortableDataFolderName));

        AppDataLocationResolver.Resolve(null, _exeDir, _defaultRoot).Mode.Should().Be(AppDataLocationMode.Portable);
    }

    [Fact]
    public void Umgebungsvariable_hat_Vorrang_vor_dem_portablen_Modus()
    {
        File.WriteAllText(Path.Combine(_exeDir, AppDataLocationResolver.PortableMarkerFileName), "x");
        var variable = Path.Combine(Path.GetDirectoryName(_exeDir)!, "rauchtest");

        var location = AppDataLocationResolver.Resolve(variable, _exeDir, _defaultRoot);

        location.Should().Be(new AppDataLocation(AppDataLocationMode.EnvironmentVariable, Path.GetFullPath(variable)));
        location.DisplayText.Should().Contain("LUMORA_APPDATA");
    }

    [Fact]
    public void Portable_Pfade_landen_alle_unter_Daten()
    {
        File.WriteAllText(Path.Combine(_exeDir, AppDataLocationResolver.PortableMarkerFileName), "x");
        var location = AppDataLocationResolver.Resolve(null, _exeDir, _defaultRoot);

        var paths = AppDataPaths.Resolve(location.Root);

        var daten = Path.Combine(_exeDir, "Daten");
        foreach (var path in new[] { paths.LibraryDatabasePath, paths.ThumbnailsRoot, paths.LogsRoot, new CatalogContext(paths, isHidden: false).CacheRoot })
        {
            AppDataPaths.IsSameOrInside(path, daten).Should().BeTrue(path);
        }
    }
}
