using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Database.Backup;
using Lumora.Services.Infrastructure;
using Lumora.Settings;
using Lumora.Settings.Backup;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Katalog-Sicherung“ (ADR-0069): Einstellungen › Speicher &amp; Wartung zeigt die Karte, „Katalog jetzt
/// sichern“ → Datei im Ordner <c>backups</c> des Rauchtest-AppData, lesbar, gleiche Dateizahl wie der Katalog, in der
/// Liste (hell/dunkel). Test-Hook für die Sicherung vor Migrationen: ein Wegwerf-Katalog nur bis zur ersten Migration →
/// <see cref="DatabaseInitializer"/> mit Sicherung → Sicherung mit dem alten Stand vorhanden, Katalog danach vollständig
/// migriert; aufgeräumt.
/// </summary>
internal sealed partial class SmokeScenario
{
    private CatalogBackupSettingsViewModel? _catalogBackupSettings;
    private ICatalogBackupService? _catalogBackup;

    private string MigrationHookDir => Path.Combine(_paths.AppDataRoot, "smoke-migration");

    private void InitCatalogBackupSmoke(IServiceProvider services)
    {
        _catalogBackupSettings = services.GetService<CatalogBackupSettingsViewModel>();
        _catalogBackup = services.GetService<ICatalogBackupService>();
    }

    private async Task CatalogBackupAsync()
    {
        Check(_catalogBackupSettings is not null && _catalogBackup is not null, "Katalog-Sicherung und Einstellungs-Karte registriert");
        Check(AppDataPaths.IsSameOrInside(_catalogBackup!.BackupFolder, _paths.AppDataRoot), "Sicherungsordner im Rauchtest-AppData");

        // (1) Einstellungen › Speicher & Wartung, Karte sichtbar
        await OpenSettingsSectionAsync(NavigationTags.SettingsStorage, "Speicher & Wartung").ConfigureAwait(true);
        var card = _settingsPage.FindName("StorageView") is Settings.Sections.SettingsStorageSection storage ? storage.CatalogBackupCard : null;
        Check(card is not null && ReferenceEquals(card.DataContext, _catalogBackupSettings), "Karte „Katalog-Sicherung“ mit ihrem ViewModel");
        await WaitUntilAsync(() => card!.IsVisible && card.ActualHeight > 0, ShortWait, "Karte „Katalog-Sicherung“ sichtbar").ConfigureAwait(true);

        // (2) „Katalog jetzt sichern“ → Datei vorhanden, lesbar, gleicher Inhalt wie der Katalog
        var before = _catalogBackup.List().Count;
        var info = await _catalogBackupSettings!.BackupNowAsync().ConfigureAwait(true);
        Check(info is { Kind: CatalogBackupKind.Manual } && File.Exists(info.FilePath) && info.SizeBytes > 0,
            $"Sicherung angelegt ({info?.FileName}, {info?.SizeBytes} Bytes)");
        var liveFiles = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files").ConfigureAwait(true);
        var backupFiles = await Task.Run(() => CountRows(info!.FilePath, "Files")).ConfigureAwait(true);
        Check(backupFiles == liveFiles && liveFiles > 0, $"Sicherung lesbar: {backupFiles} Dateien wie im Katalog ({liveFiles})");
        Check(!File.Exists(info!.FilePath + "-wal"), "Sicherung ist eine einzelne Datei (kein -wal)");
        Check(_catalogBackupSettings.Backups.Count == before + 1 && _catalogBackupSettings.Backups[0].Info.FileName == info.FileName,
            $"Liste zeigt die neue Sicherung oben ({_catalogBackupSettings.Backups.Count} Einträge)");
        Check(_catalogBackupSettings.StatusText?.StartsWith("Gesichert", StringComparison.Ordinal) == true && !_catalogBackupSettings.StatusIsWarning,
            $"Meldung: {_catalogBackupSettings.StatusText}");

        _settings.IsThemeLight = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Light && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await SnapshotAsync("katalog-sicherung-hell").ConfigureAwait(true);
        _settings.IsThemeDark = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("katalog-sicherung-dunkel").ConfigureAwait(true);
        _settings.IsThemeSystem = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);

        // (3) Test-Hook: Sicherung vor Migrationen auf einem Wegwerf-Katalog im Rauchtest-AppData
        try
        {
            await MigrationBackupHookAsync().ConfigureAwait(true);
        }
        finally
        {
            TryDeleteDirectory(MigrationHookDir);
        }
    }

    private async Task MigrationBackupHookAsync()
    {
        TryDeleteDirectory(MigrationHookDir);
        var paths = new AppDataPaths(MigrationHookDir);
        paths.EnsureDirectoriesExist();
        var factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        string first;
        int all;
        await using (var ctx = factory.Create())
        {
            var migrations = ctx.Database.GetMigrations().ToList();
            first = migrations[0];
            all = migrations.Count;
            await ctx.GetService<IMigrator>().MigrateAsync(first).ConfigureAwait(true);
        }

        var backup = new CatalogBackupService(paths, options: new CatalogBackupOptions(AppVersion: Settings.Info.AppInfo.Version));
        var result = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, backup).InitializeAsync().ConfigureAwait(true);
        Check(result.NewlyMigrated == all - 1, $"Wegwerf-Katalog migriert ({result.NewlyMigrated} von {all - 1} ausstehenden)");
        var saved = result.BackupBeforeMigration;
        Check(saved is { Kind: CatalogBackupKind.BeforeMigration } && File.Exists(saved.FilePath)
              && AppDataPaths.IsSameOrInside(saved.FilePath, Path.Combine(MigrationHookDir, CatalogBackupService.FolderName)),
            $"Sicherung vor der Migration angelegt ({saved?.FileName})");
        var savedHistory = await Task.Run(() => CountRows(saved!.FilePath, "__EFMigrationsHistory")).ConfigureAwait(true);
        var liveHistory = await Task.Run(() => CountRows(paths.LibraryDatabasePath, "__EFMigrationsHistory")).ConfigureAwait(true);
        Check(savedHistory == 1 && liveHistory == all, $"Sicherung hat den alten Stand ({savedHistory} Migration), Katalog den neuen ({liveHistory})");

        // Zweiter Start ohne ausstehende Migration → keine weitere Sicherung
        var again = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, backup).InitializeAsync().ConfigureAwait(true);
        Check(again.BackupBeforeMigration is null && backup.List().Count == 1, "Ohne Migration keine Sicherung");
    }

    private static long CountRows(string dbPath, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
