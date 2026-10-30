using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Database.Backup;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.Infrastructure;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Database;

/// <summary>
/// Katalog-Sicherung (0.4.40, ADR-0069): konsistente Kopie per Online-Backup-API auch bei offenem WAL, Sicherung vor
/// Migrationen nur bei bestehendem Katalog, ohne Sicherung keine Migration, Aufbewahrung der letzten N, versteckter
/// Katalog sichert nur im Container, Wiederherstellen beim nächsten Start mit „…-vor-wiederherstellung.db“.
/// Alles in Temp-Ordnern.
/// </summary>
public sealed class CatalogBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Uhr, die bei jedem Abruf eine Sekunde weiterläuft (eindeutige Zeitstempel, feste Reihenfolge).</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow()
        {
            _now = _now.AddSeconds(1);
            return _now;
        }
    }

    private (TestAppDataPaths Paths, LibraryContextFactory Factory) NewCatalog(string name = "appdata")
    {
        var paths = new TestAppDataPaths(Path.Combine(_root, name));
        paths.EnsureDirectoriesExist();
        return (paths, new LibraryContextFactory(paths, new SqlitePragmaInterceptor()));
    }

    private static CatalogBackupService Backup(IAppDataPaths paths, CatalogBackupOptions? options = null)
        => new(paths, NullLogger<CatalogBackupService>.Instance, options ?? new CatalogBackupOptions(AppVersion: "0.4.40"), new SteppingClock());

    private static async Task MigrateToFirstAsync(LibraryContextFactory factory)
    {
        await using var ctx = factory.Create();
        await ctx.GetService<IMigrator>().MigrateAsync(ctx.Database.GetMigrations().First());
    }

    private static long Scalar(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Exec(LibraryContextFactory factory, string sql)
    {
        using var connection = factory.OpenSqliteConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string InsertLibrary(int id, string name)
        => $"INSERT INTO Libraries (Id, Name, RootPath, CreatedAt, LastOpenedAt) VALUES ({id}, '{name}', 'C:\\Test{id}', 1, 1);";

    [Fact]
    public async Task Sicherung_von_Hand_ist_konsistent_auch_mit_offenem_WAL_und_eine_einzelne_Datei()
    {
        var (paths, factory) = NewCatalog();
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        // Verbindung bleibt offen und kein Checkpoint: die Zeile steht nur im WAL
        using var open = factory.OpenSqliteConnection();
        using (var command = open.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_autocheckpoint=0; " + InsertLibrary(1, "Familie");
            command.ExecuteNonQuery();
        }

        File.Exists(paths.LibraryDatabasePath + "-wal").Should().BeTrue("der Katalog läuft im WAL-Modus");

        var info = await Backup(paths).CreateBackupAsync(CatalogBackupKind.Manual);

        info.Kind.Should().Be(CatalogBackupKind.Manual);
        info.Version.Should().Be("0.4.40");
        info.FileName.Should().MatchRegex(@"^library-0\.4\.40-\d{8}-\d{6}-\d{3}-manuell\.db$");
        Path.GetDirectoryName(info.FilePath).Should().Be(Path.Combine(paths.AppDataRoot, "backups"));
        Scalar(info.FilePath, "SELECT COUNT(*) FROM Libraries WHERE Name = 'Familie'").Should().Be(1, "der WAL-Inhalt ist in der Sicherung");
        File.Exists(info.FilePath + "-wal").Should().BeFalse();
        Directory.EnumerateFiles(Path.GetDirectoryName(info.FilePath)!, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Vor_Migrationen_eines_bestehenden_Katalogs_wird_gesichert()
    {
        var (paths, factory) = NewCatalog();
        await MigrateToFirstAsync(factory);
        Exec(factory, InsertLibrary(1, "Bestand"));
        var backup = Backup(paths);

        var steps = new StepLog();
        var result = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, backup).InitializeAsync(progress: steps);

        result.NewlyMigrated.Should().BeGreaterThan(0);
        steps.Kinds.Should().Equal([DatabaseInitStepKind.Backup, DatabaseInitStepKind.Migrate], "das Startfenster zeigt erst Sichern, dann Aktualisieren");
        steps.Items[0].DatabaseBytes.Should().BeGreaterThan(0);
        steps.Items[1].PendingMigrations.Should().Be(result.NewlyMigrated);
        result.BackupBeforeMigration.Should().NotBeNull();
        result.BackupBeforeMigration!.Kind.Should().Be(CatalogBackupKind.BeforeMigration);
        result.BackupBeforeMigration.FileName.Should().MatchRegex(@"^library-0\.4\.40-\d{8}-\d{6}-\d{3}\.db$");
        Scalar(result.BackupBeforeMigration.FilePath, "SELECT COUNT(*) FROM __EFMigrationsHistory").Should().Be(1, "alter Stand vor der Migration");
        Scalar(result.BackupBeforeMigration.FilePath, "SELECT COUNT(*) FROM Libraries").Should().Be(1);
        Scalar(paths.LibraryDatabasePath, "SELECT COUNT(*) FROM __EFMigrationsHistory").Should().Be(result.AppliedMigrations);

        // nächster Start ohne ausstehende Migration: keine weitere Sicherung
        var quiet = new StepLog();
        var again = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, backup).InitializeAsync(progress: quiet);
        again.BackupBeforeMigration.Should().BeNull();
        quiet.Items.Should().BeEmpty("ohne Migration kein Fenster");
        backup.List().Should().HaveCount(1);
    }

    [Fact]
    public async Task Neuer_Katalog_wird_ohne_Sicherung_angelegt()
    {
        var (paths, factory) = NewCatalog();
        var backup = Backup(paths);

        var steps = new StepLog();
        var result = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, backup).InitializeAsync(progress: steps);

        result.NewlyMigrated.Should().BeGreaterThan(0);
        result.BackupBeforeMigration.Should().BeNull("es gab noch keinen Katalog");
        steps.Kinds.Should().Equal([DatabaseInitStepKind.Migrate], "neuer Katalog: nur Anlegen, keine Sicherung");
        Directory.Exists(backup.BackupFolder).Should().BeFalse();
    }

    [Fact]
    public async Task Scheitert_die_Sicherung_wird_nicht_migriert()
    {
        var (paths, factory) = NewCatalog();
        await MigrateToFirstAsync(factory);
        File.WriteAllText(Path.Combine(paths.AppDataRoot, "backups"), "Datei statt Ordner"); // Sicherungsordner unmöglich

        var act = () => new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, Backup(paths)).InitializeAsync();

        (await act.Should().ThrowAsync<CatalogBackupException>()).Which.Message.Should().Contain("nicht verändert");
        Scalar(paths.LibraryDatabasePath, "SELECT COUNT(*) FROM __EFMigrationsHistory").Should().Be(1, "der Katalog blieb auf dem alten Stand");
    }

    [Fact]
    public async Task Nur_die_letzten_N_Sicherungen_je_Anlass_bleiben()
    {
        var (paths, factory) = NewCatalog();
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        var backup = Backup(paths, new CatalogBackupOptions(AppVersion: "0.4.40", KeepBeforeMigration: 2, KeepManual: 1));

        var created = new List<CatalogBackupInfo>();
        for (var i = 0; i < 4; i++)
        {
            created.Add(await backup.CreateBackupAsync(CatalogBackupKind.BeforeMigration));
        }

        var manual1 = await backup.CreateBackupAsync(CatalogBackupKind.Manual);
        var manual2 = await backup.CreateBackupAsync(CatalogBackupKind.Manual);

        var left = backup.List();
        left.Where(b => b.Kind == CatalogBackupKind.BeforeMigration).Select(b => b.FileName)
            .Should().Equal(created[3].FileName, created[2].FileName);
        left.Where(b => b.Kind == CatalogBackupKind.Manual).Select(b => b.FileName).Should().Equal(manual2.FileName);
        File.Exists(manual1.FilePath).Should().BeFalse();
        left[0].FileName.Should().Be(manual2.FileName, "neueste zuerst");
    }

    [Fact]
    public async Task Versteckter_Katalog_sichert_nur_im_Container()
    {
        var library = Path.Combine(_root, "container", "Bibliothek");
        Directory.CreateDirectory(library);
        var hiddenPaths = HiddenLibraryStore.GetCatalogPaths(library);
        hiddenPaths.EnsureDirectoriesExist();
        var factory = new LibraryContextFactory(hiddenPaths, new SqlitePragmaInterceptor());
        await MigrateToFirstAsync(factory);
        var backup = Backup(hiddenPaths);

        var result = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, backup).InitializeAsync();
        var manual = await backup.CreateBackupAsync(CatalogBackupKind.Manual);

        backup.BackupFolder.Should().Be(Path.Combine(library, ".lumora", "backups"));
        AppDataPaths.IsSameOrInside(result.BackupBeforeMigration!.FilePath, Path.Combine(library, ".lumora")).Should().BeTrue();
        AppDataPaths.IsSameOrInside(manual.FilePath, Path.Combine(library, ".lumora")).Should().BeTrue();
        Directory.EnumerateFiles(Path.Combine(_root, "container"), "*.db", SearchOption.AllDirectories)
            .Should().OnlyContain(f => AppDataPaths.IsSameOrInside(f, Path.Combine(library, ".lumora")), "nichts außerhalb von .lumora");
    }

    [Fact]
    public async Task Wiederherstellen_beim_naechsten_Start_ersetzt_den_Katalog_und_behaelt_den_aktuellen()
    {
        var (paths, factory) = NewCatalog();
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        Exec(factory, InsertLibrary(1, "Vorher"));
        var backup = Backup(paths);
        var saved = await backup.CreateBackupAsync(CatalogBackupKind.Manual);
        Exec(factory, InsertLibrary(2, "Danach"));

        backup.ScheduleRestore(saved);
        backup.ScheduledRestore!.FileName.Should().Be(saved.FileName);

        // nächster Start: der Initializer führt die Vormerkung aus, bevor er die DB öffnet
        SqliteConnection.ClearAllPools();
        var result = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, backup).InitializeAsync();

        result.RestoredFrom!.FileName.Should().Be(saved.FileName);
        Scalar(paths.LibraryDatabasePath, "SELECT COUNT(*) FROM Libraries").Should().Be(1, "Stand der Sicherung");
        backup.ScheduledRestore.Should().BeNull();
        var before = backup.List().Single(b => b.Kind == CatalogBackupKind.BeforeRestore);
        before.FileName.Should().EndWith("-vor-wiederherstellung.db");
        Scalar(before.FilePath, "SELECT COUNT(*) FROM Libraries WHERE Name = 'Danach'").Should().Be(1, "der ersetzte Katalog bleibt erhalten");
        File.Exists(saved.FilePath).Should().BeTrue("die Sicherung selbst bleibt liegen");
    }

    [Fact]
    public async Task Fehlende_Sicherung_beim_Wiederherstellen_meldet_und_laesst_den_Katalog_unveraendert()
    {
        var (paths, factory) = NewCatalog();
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        Exec(factory, InsertLibrary(1, "Aktuell"));
        var backup = Backup(paths);
        var saved = await backup.CreateBackupAsync(CatalogBackupKind.Manual);
        backup.ScheduleRestore(saved);
        File.Delete(saved.FilePath);

        // der Start läuft mit dem unveränderten Katalog weiter; der Grund steht für die Einstellungs-Karte bereit
        var result = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, backup).InitializeAsync();

        result.RestoredFrom.Should().BeNull();
        backup.LastRestoreProblem.Should().Contain("nicht gefunden").And.Contain("unverändert");
        backup.ScheduledRestore.Should().BeNull("die Vormerkung ist gelöscht");
        File.Exists(Path.Combine(backup.BackupFolder, CatalogBackupService.RestoreRequestFileName)).Should().BeFalse();
        Scalar(paths.LibraryDatabasePath, "SELECT COUNT(*) FROM Libraries").Should().Be(1);
    }

    [Fact]
    public async Task Sicherung_einer_neueren_Version_laesst_sich_nicht_vormerken()
    {
        var (paths, factory) = NewCatalog();
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        var backup = new CatalogBackupService(paths, options: new CatalogBackupOptions(AppVersion: "0.4.40"), time: new SteppingClock(), factory: factory);
        var saved = await backup.CreateBackupAsync(CatalogBackupKind.Manual);
        using (var connection = new SqliteConnection($"Data Source={saved.FilePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29991231000000_AusDerZukunft', '10.0.0')";
            command.ExecuteNonQuery();
        }

        var act = () => backup.ScheduleRestore(saved);

        act.Should().Throw<CatalogBackupException>().WithMessage("*neueren Lumora-Version*");
        backup.ScheduledRestore.Should().BeNull();
    }

    [Fact]
    public async Task Nach_dem_Wiederherstellen_werden_Vorschaubilder_verworfen()
    {
        var (paths, factory) = NewCatalog();
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        var backup = Backup(paths);
        var saved = await backup.CreateBackupAsync(CatalogBackupKind.Manual);
        var oldThumb = Path.Combine(paths.ThumbnailsRoot, "1", "101_320_still.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(oldThumb)!);
        File.WriteAllText(oldThumb, "Vorschau eines Fotos, das es in der Sicherung nicht gibt");
        var stash = Path.Combine(paths.ThumbnailsRoot, "1", "_undo", "7", "55_320_still.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(stash)!);
        File.WriteAllText(stash, "Rückgängig-Ablage");
        backup.ScheduleRestore(saved);

        backup.ApplyScheduledRestore().Should().NotBeNull();

        File.Exists(oldThumb).Should().BeFalse("FileIds nach der Sicherung werden neu vergeben — alte Vorschaubilder passten nicht mehr");
        File.Exists(stash).Should().BeTrue("die Rückgängig-Ablage (ADR-0065) ist kein Cache und bleibt");
        Directory.Exists(paths.ThumbnailsRoot).Should().BeTrue();
        Scalar(paths.LibraryDatabasePath, "SELECT COUNT(*) FROM Thumbnails").Should().Be(0);
    }

    [Fact]
    public async Task Vorgemerkte_Sicherung_wird_nicht_weggeraeumt()
    {
        var (paths, factory) = NewCatalog();
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        var backup = Backup(paths, new CatalogBackupOptions(AppVersion: "0.4.40", KeepManual: 1));
        var first = await backup.CreateBackupAsync(CatalogBackupKind.Manual);
        backup.ScheduleRestore(first);

        await backup.CreateBackupAsync(CatalogBackupKind.Manual);

        File.Exists(first.FilePath).Should().BeTrue();
    }

    [Fact]
    public void Dateiname_traegt_Anlass_Version_und_Zeitpunkt()
    {
        var at = new DateTime(2026, 10, 25, 14, 30, 5, 123);
        foreach (var kind in Enum.GetValues<CatalogBackupKind>())
        {
            var name = CatalogBackupService.BuildFileName(kind, "0.5.0", at);
            var info = CatalogBackupService.Describe(Path.Combine(_root, name));
            info.Should().NotBeNull(name);
            info!.Kind.Should().Be(kind);
            info.Version.Should().Be("0.5.0");
            info.CreatedLocal.Should().Be(at);
        }

        CatalogBackupService.Describe(Path.Combine(_root, "library.db")).Should().BeNull();
        CatalogBackupService.Describe(Path.Combine(_root, "library-x-20261025.db")).Should().BeNull();
    }

    /// <summary>Schritte synchron mitschreiben (Progress&lt;T&gt; stellte sie erst später zu).</summary>
    private sealed class StepLog : IProgress<DatabaseInitStep>
    {
        public List<DatabaseInitStep> Items { get; } = [];

        public IEnumerable<DatabaseInitStepKind> Kinds => Items.Select(i => i.Kind);

        public void Report(DatabaseInitStep value) => Items.Add(value);
    }
}
