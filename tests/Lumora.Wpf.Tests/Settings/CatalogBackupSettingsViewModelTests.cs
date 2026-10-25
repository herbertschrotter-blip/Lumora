using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Database.Backup;
using Lumora.Services.Infrastructure;
using Lumora.Settings.Backup;
using Lumora.Settings.Info;
using Lumora.Shell;

namespace Lumora.Wpf.Tests.Settings;

/// <summary>
/// Einstellungen › Speicher &amp; Wartung › Katalog-Sicherung (0.4.40, ADR-0069): „Katalog jetzt sichern“ legt eine lesbare
/// Datei an und zeigt sie in der Liste (Datum, Version, Größe); Wiederherstellen nur nach Rückfrage — dann vorgemerkt und
/// Lumora beendet sich; Abbrechen der Vormerkung; Erklärung nennt die nicht gesicherten Vorschaubilder.
/// </summary>
public sealed class CatalogBackupSettingsViewModelTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private AppDataPaths _paths = null!;
    private LibraryContextFactory _factory = null!;

    private sealed class FakeDialogs(bool answer) : IDialogService
    {
        public int Confirms { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
        {
            Confirms++;
            return Task.FromResult(answer);
        }

        public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
            => Task.FromResult(DialogChoice.None);

        public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK") => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public string? PickFolder(string title) => null;
    }

    private sealed class FakeShutdown : IAppShutdownService
    {
        public int Calls { get; private set; }

        public void Shutdown() => Calls++;
    }

    private sealed class FakeActions : IInfoActions
    {
        public List<string> Folders { get; } = [];

        public void RevealFile(string path)
        {
        }

        public void OpenFolder(string path) => Folders.Add(path);

        public void OpenFile(string path)
        {
        }

        public void CopyText(string text)
        {
        }

        public void ShowText(string title, string message, string details)
        {
        }
    }

    public async Task InitializeAsync()
    {
        _paths = new AppDataPaths(Path.Combine(_root, "appdata"));
        _paths.EnsureDirectoriesExist();
        _factory = new LibraryContextFactory(_paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    private (CatalogBackupSettingsViewModel Vm, CatalogBackupService Service, FakeDialogs Dialogs, FakeShutdown Shutdown, FakeActions Actions) Create(bool confirm = true, bool hidden = false)
    {
        var service = new CatalogBackupService(_paths, options: new CatalogBackupOptions(AppVersion: "0.4.40"));
        var dialogs = new FakeDialogs(confirm);
        var shutdown = new FakeShutdown();
        var actions = new FakeActions();
        var vm = new CatalogBackupSettingsViewModel(
            service, dialogs, actions, shutdown, NullLogger<CatalogBackupSettingsViewModel>.Instance, _factory,
            new CatalogContext(_paths, isHidden: hidden));
        return (vm, service, dialogs, shutdown, actions);
    }

    [Fact]
    public async Task Katalog_jetzt_sichern_legt_eine_lesbare_Datei_an_und_zeigt_sie_in_der_Liste()
    {
        var (vm, service, _, _, _) = Create();
        await vm.RefreshAsync();
        vm.HasBackups.Should().BeFalse();

        var info = await vm.BackupNowAsync();

        info.Should().NotBeNull();
        File.Exists(info!.FilePath).Should().BeTrue();
        Path.GetDirectoryName(info.FilePath).Should().Be(service.BackupFolder);
        vm.HasBackups.Should().BeTrue();
        vm.Backups.Should().ContainSingle();
        var row = vm.Backups[0];
        row.VersionText.Should().Be("Version 0.4.40");
        row.KindText.Should().Be("von Hand");
        row.SizeText.Should().NotBeNullOrWhiteSpace();
        row.DateText.Should().MatchRegex(@"^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}$");
        vm.StatusText.Should().StartWith("Gesichert");
        vm.StatusIsWarning.Should().BeFalse();
        vm.IsBusy.Should().BeFalse();

        using var connection = new SqliteConnection($"Data Source={info.FilePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
        Convert.ToInt64(command.ExecuteScalar()).Should().BeGreaterThan(0, "die Sicherung ist ein lesbarer Katalog");
    }

    [Fact]
    public async Task Wiederherstellen_ohne_Bestaetigung_aendert_nichts()
    {
        var (vm, service, dialogs, shutdown, _) = Create(confirm: false);
        await vm.BackupNowAsync();

        await vm.RestoreCommand.ExecuteAsync(vm.Backups[0]);

        dialogs.Confirms.Should().Be(1);
        service.ScheduledRestore.Should().BeNull();
        shutdown.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Wiederherstellen_nach_Bestaetigung_merkt_vor_und_beendet_Lumora()
    {
        var (vm, service, _, shutdown, _) = Create(confirm: true);
        var info = await vm.BackupNowAsync();

        await vm.RestoreCommand.ExecuteAsync(vm.Backups[0]);

        service.ScheduledRestore!.FileName.Should().Be(info!.FileName);
        shutdown.Calls.Should().Be(1);
        vm.ScheduledRestoreText.Should().Contain("nächsten Start").And.Contain("0.4.40");
        vm.Backups[0].IsScheduled.Should().BeTrue();
        File.Exists(_paths.LibraryDatabasePath).Should().BeTrue("ersetzt wird erst beim nächsten Start");

        await vm.CancelRestoreCommand.ExecuteAsync(null);
        service.ScheduledRestore.Should().BeNull();
        vm.ScheduledRestoreText.Should().BeNull();
    }

    [Fact]
    public async Task Nicht_ausgefuehrte_Wiederherstellung_wird_als_Warnung_gezeigt()
    {
        var (vm, service, _, _, _) = Create();
        var info = await vm.BackupNowAsync();
        service.ScheduleRestore(info!);
        File.Delete(info!.FilePath);
        service.ApplyScheduledRestore().Should().BeNull("die Sicherung fehlt — der Start läuft weiter");

        var fresh = new CatalogBackupSettingsViewModel(
            service, new FakeDialogs(true), new FakeActions(), new FakeShutdown(), NullLogger<CatalogBackupSettingsViewModel>.Instance);
        await fresh.RefreshAsync();

        fresh.StatusIsWarning.Should().BeTrue();
        fresh.StatusText.Should().Contain("nicht ausgeführt").And.Contain("nicht gefunden");
    }

    [Fact]
    public async Task Ordner_oeffnen_zeigt_den_Sicherungsordner()
    {
        var (vm, service, _, _, actions) = Create();
        await vm.RefreshAsync();

        vm.OpenFolderCommand.Execute(null);

        actions.Folders.Should().Equal(service.BackupFolder);
    }

    [Fact]
    public void Erklaerung_nennt_Vorschaubilder_und_im_versteckten_Modus_den_Container()
    {
        Create().Vm.ExplanationText.Should().Contain("Vorschaubilder werden nicht gesichert").And.Contain("die letzten 5");
        var hidden = Create(hidden: true).Vm;
        hidden.IsHidden.Should().BeTrue();
        hidden.ExplanationText.Should().Contain(@".lumora\backups").And.Contain("Vorschaubilder werden nicht gesichert");
    }
}
