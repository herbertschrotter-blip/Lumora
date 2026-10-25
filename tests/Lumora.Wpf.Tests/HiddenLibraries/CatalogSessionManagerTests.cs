using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.HiddenLibraries;
using Lumora.Shell.HiddenLibraries;

namespace Lumora.Wpf.Tests.HiddenLibraries;

/// <summary>
/// ADR-0049: Sitzungen der versteckten Bibliotheken — Anlegen, Öffnen (falsches Kennwort), Zurücksetzen mit
/// Bestätigungsdatei, Zusperren nach Untätigkeit (Fake-Uhr) mit Aushängen (Fake), Beenden, ohne Startschalter nichts.
/// Ohne Fenster: der Katalog-Wechsler ist ein Fake.
/// </summary>
public sealed class CatalogSessionManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", "sessions-" + Guid.NewGuid().ToString("N"));
    private readonly string _normalRoot;
    private readonly string _normalLibrary;
    private readonly HiddenLibraryStore _store = new(iterations: 1_000);
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeContainers _containers = new();
    private readonly FakeHostSwitcher _switcher;

    public CatalogSessionManagerTests()
    {
        _normalRoot = Directory.CreateDirectory(Path.Combine(_root, "appdata")).FullName;
        _normalLibrary = Directory.CreateDirectory(Path.Combine(_root, "normal-fotos")).FullName;
        _switcher = new FakeHostSwitcher(_normalLibrary);
    }

    public void Dispose()
    {
        HiddenSessionPrivacy.IsHiddenSessionActive = false;
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
        }

        Directory.Delete(_root, recursive: true);
    }

    private CatalogSessionManager CreateManager(bool enabled = true)
    {
        var manager = new CatalogSessionManager(
            new HiddenModeOptions(enabled, _normalRoot, _normalRoot, MaxFolderDepth: int.MaxValue, UseUiHooks: false),
            _store,
            new FixedDriveRoots(_root),
            _containers,
            _time,
            NullLogger.Instance);
        manager.AttachSwitcher(_switcher);
        return manager;
    }

    private string NewContainerFolder() => Path.Combine(_root, "container", "Tresor" + Guid.NewGuid().ToString("N")[..6]);

    [Fact]
    public async Task Create_opens_a_session_in_the_hidden_catalog_creates_the_library_and_scans()
    {
        var manager = CreateManager();
        var folder = NewContainerFolder();

        var result = await manager.CreateAsync(folder, "Tresor", "kennwort-1");

        result.Success.Should().BeTrue(result.Message);
        manager.IsHiddenSessionActive.Should().BeTrue();
        HiddenSessionPrivacy.IsHiddenSessionActive.Should().BeTrue("Explorer-Aufrufe gesperrt");
        manager.ActiveLibrary!.Name.Should().Be("Tresor");
        var database = Path.Combine(folder, ".lumora", "library.db");
        _switcher.Calls.Should().Equal("hidden:" + database);
        var libraries = _switcher.LibrariesOf(database);
        libraries.Items.Should().ContainSingle(l => l.Name == "Tresor" && l.RootPath == folder);
        libraries.ActiveId.Should().Be(1);
        await manager.InitialScan!;
        _switcher.LastScan!.Calls.Should().Equal((1, Lumora.Services.Scanning.ScanMode.Fresh));
        HiddenLibraryStore.VerifyPassword(folder, "kennwort-1").Should().BeTrue();
        manager.Idle.Timeout.Should().Be(TimeSpan.FromMinutes(10));

        (await manager.CreateAsync(NewContainerFolder(), "Zweite", "kennwort-1")).Success.Should().BeFalse("nur aus der normalen Bibliothek");
    }

    [Fact]
    public async Task Create_refuses_bad_input_and_normal_library_folders()
    {
        var manager = CreateManager();
        (await manager.CreateAsync(NewContainerFolder(), " ", "kennwort-1")).Message.Should().Contain("Namen");
        (await manager.CreateAsync(NewContainerFolder(), "Tresor", "kurz")).Message.Should().Contain("mindestens");
        (await manager.CreateAsync(_normalLibrary, "Tresor", "kennwort-1")).Success.Should().BeFalse("Ordner einer normalen Bibliothek");
        (await manager.CreateAsync(Path.Combine(_normalRoot, "x"), "Tresor", "kennwort-1")).Success.Should().BeFalse("Lumora-Datenordner");
        (await manager.ValidateNewFolderAsync(NewContainerFolder())).Should().BeNull();
        _switcher.Calls.Should().BeEmpty();
        manager.IsHiddenSessionActive.Should().BeFalse();
    }

    [Fact]
    public async Task Locking_ends_hidden_mode_until_restart()
    {
        // Herbert (0.4.14): nach dem Zusperren zeigt Lumora nichts mehr von versteckten Bibliotheken, wie ein normaler Start
        var manager = CreateManager();
        var folder = NewContainerFolder();
        await manager.CreateAsync(folder, "Tresor", "kennwort-1");
        manager.IsHiddenModeEnabled.Should().BeTrue();

        (await manager.LockAsync()).Success.Should().BeTrue();

        manager.IsHiddenModeEnabled.Should().BeFalse("Zusperren beendet den versteckten Modus für diese Laufzeit");
        (await manager.FindAsync()).Should().BeEmpty("kein Suchen nach versteckten Bibliotheken mehr");
        _switcher.Calls.Clear();
        var reopen = await manager.OpenAsync(new HiddenLibraryInfo(folder, "Tresor"), "kennwort-1");
        reopen.Success.Should().BeFalse("wieder öffnen nur nach einem Neustart mit _hidden");
        _switcher.Calls.Should().BeEmpty();
        CreateManager().IsHiddenModeEnabled.Should().BeTrue("ein neuer Start mit _hidden zeigt sie wieder");
    }

    [Fact]
    public async Task Wrong_password_does_not_switch_and_correct_password_reuses_the_catalog_without_scan()
    {
        var manager = CreateManager();
        var folder = NewContainerFolder();
        await manager.CreateAsync(folder, "Tresor", "kennwort-1");
        await manager.LockAsync();
        manager = CreateManager(); // Neustart mit „_hidden“ — Zusperren beendet den versteckten Modus (0.4.14)
        _switcher.Calls.Clear();
        var info = new HiddenLibraryInfo(folder, "Tresor");

        (await manager.VerifyPasswordAsync(info, "falsch-123")).Should().BeFalse();
        var wrong = await manager.OpenAsync(info, "falsch-123");
        wrong.Success.Should().BeFalse();
        wrong.Message.Should().Contain("falsch");
        _switcher.Calls.Should().BeEmpty();

        var opened = await manager.OpenAsync(info, "kennwort-1");
        opened.Success.Should().BeTrue();
        manager.InitialScan.Should().BeNull("Bibliothek existiert im Katalog schon");
        _switcher.LastScan!.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Idle_timeout_locks_switches_back_and_dismounts_but_waits_while_busy()
    {
        var manager = CreateManager();
        var folder = NewContainerFolder();
        await manager.CreateAsync(folder, "Tresor", "kennwort-1");
        manager.UpdateSettings(10, dismountOnLock: true); // Standard seit 0.4.38: aus

        _time.Advance(TimeSpan.FromMinutes(9));
        (await manager.CheckIdleAsync()).Should().BeFalse();
        manager.Idle.NotifyActivity(); // Maus/Tastatur
        _time.Advance(TimeSpan.FromMinutes(9.9));
        (await manager.CheckIdleAsync()).Should().BeFalse("Eingabe setzt die Uhr zurück");

        _time.Advance(TimeSpan.FromMinutes(1));
        _switcher.Busy = true;
        (await manager.CheckIdleAsync()).Should().BeFalse("laufender Scan/Datei-Vorgang wird nicht unterbrochen");
        manager.IsHiddenSessionActive.Should().BeTrue();

        _switcher.Busy = false;
        (await manager.CheckIdleAsync()).Should().BeTrue();
        manager.IsHiddenSessionActive.Should().BeFalse();
        HiddenSessionPrivacy.IsHiddenSessionActive.Should().BeFalse();
        _switcher.Calls.Last().Should().Be("normal");
        _containers.Dismounted.Should().Equal(Path.GetPathRoot(folder));
        (await manager.CheckIdleAsync()).Should().BeFalse("keine Sitzung mehr");
    }

    [Fact]
    public async Task Auto_lock_waits_for_a_window_that_refuses_to_close_manual_lock_does_not()
    {
        var manager = CreateManager();
        await manager.CreateAsync(NewContainerFolder(), "Tresor", "kennwort-1");
        _switcher.WindowsClose = false;
        _time.Advance(TimeSpan.FromMinutes(11));

        (await manager.CheckIdleAsync()).Should().BeFalse("z. B. laufendes Entpacken — nächste Prüfung");
        manager.IsHiddenSessionActive.Should().BeTrue();
        _switcher.CloseWindowsCalls.Should().Be(1, "Lightbox und Dialoge werden vor dem Wechsel geschlossen");

        (await manager.LockAsync()).Success.Should().BeTrue();
        manager.IsHiddenSessionActive.Should().BeFalse();
    }

    [Fact]
    public async Task Container_mounted_under_another_drive_letter_is_refused_with_a_hint()
    {
        var manager = CreateManager();
        var folder = NewContainerFolder();
        await manager.CreateAsync(folder, "Tresor", "kennwort-1");
        await manager.LockAsync();
        manager = CreateManager(); // Neustart mit „_hidden“ — Zusperren beendet den versteckten Modus (0.4.14)
        // Katalog kennt die Bibliothek unter X:\Fotos (Container damals als X: eingehängt)
        var libraries = _switcher.LibrariesOf(Path.Combine(folder, ".lumora", "library.db"));
        libraries.Items[0] = FakeHostSwitcher.NewInfo(1, "Tresor", @"X:\Fotos");

        var result = await manager.OpenAsync(new HiddenLibraryInfo(folder, "Tresor"), "kennwort-1");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("X:").And.Contain("einhängen");
        manager.IsHiddenSessionActive.Should().BeFalse();
    }

    [Fact]
    public async Task Settings_change_timeout_and_disable_dismount()
    {
        var manager = CreateManager();
        var folder = NewContainerFolder();
        await manager.CreateAsync(folder, "Tresor", "kennwort-1");

        manager.UpdateSettings(30, dismountOnLock: false)!.AutoLockMinutes.Should().Be(30);
        manager.Idle.Timeout.Should().Be(TimeSpan.FromMinutes(30));
        HiddenLibraryStore.TryLoad(folder)!.DismountOnLock.Should().BeFalse("in der Kenndatei im Container gespeichert");

        await manager.LockAsync();
        _containers.Dismounted.Should().BeEmpty("Aushängen abgeschaltet");
        manager.UpdateSettings(5, true).Should().BeNull("ohne Sitzung");
    }

    [Fact]
    public async Task Failed_dismount_is_reported_and_exit_dismounts_after_closing()
    {
        var manager = CreateManager();
        var folder = NewContainerFolder();
        await manager.CreateAsync(folder, "Tresor", "kennwort-1");
        manager.UpdateSettings(10, dismountOnLock: true); // Standard seit 0.4.38: aus
        _containers.DismountSucceeds = false;
        var locked = await manager.LockAsync();
        manager = CreateManager(); // Neustart mit „_hidden“ — Zusperren beendet den versteckten Modus (0.4.14)
        locked.Success.Should().BeTrue();
        locked.Message.Should().Contain("geöffnet");

        _containers.DismountSucceeds = true;
        await manager.OpenAsync(new HiddenLibraryInfo(folder, "Tresor"), "kennwort-1");
        _containers.Dismounted.Clear();
        manager.PrepareExit();
        manager.IsHiddenSessionActive.Should().BeFalse();
        manager.DismountAfterExit(TimeSpan.FromSeconds(10))!.Success.Should().BeTrue();
        _containers.Dismounted.Should().Equal(Path.GetPathRoot(folder));
        manager.DismountAfterExit(TimeSpan.FromSeconds(10)).Should().BeNull("nur einmal");
    }

    [Fact]
    public async Task Reset_password_needs_confirmation_file_then_opens()
    {
        var manager = CreateManager();
        var folder = NewContainerFolder();
        await manager.CreateAsync(folder, "Tresor", "vergessen-1");
        await manager.LockAsync();
        manager = CreateManager(); // Neustart mit „_hidden“ — Zusperren beendet den versteckten Modus (0.4.14)
        var info = new HiddenLibraryInfo(folder, "Tresor");

        (await manager.ResetPasswordAndOpenAsync(info, "neu-123456")).Success.Should().BeFalse();
        File.WriteAllText(HiddenLibraryStore.GetResetRequestPath(folder), "");
        var reset = await manager.ResetPasswordAndOpenAsync(info, "neu-123456");

        reset.Success.Should().BeTrue();
        manager.IsHiddenSessionActive.Should().BeTrue();
        HiddenLibraryStore.VerifyPassword(folder, "neu-123456").Should().BeTrue();
    }

    [Fact]
    public async Task Failed_start_leaves_the_normal_session_active()
    {
        var manager = CreateManager();
        var folder = NewContainerFolder();
        await manager.CreateAsync(folder, "Tresor", "kennwort-1");
        await manager.LockAsync();
        manager = CreateManager(); // Neustart mit „_hidden“ — Zusperren beendet den versteckten Modus (0.4.14)
        _switcher.FailNextHidden = true;

        var result = await manager.OpenAsync(new HiddenLibraryInfo(folder, "Tresor"), "kennwort-1");

        result.Success.Should().BeFalse();
        result.Message.Should().NotContain(folder, "keine Pfade in Meldungen außerhalb des Containers");
        manager.IsHiddenSessionActive.Should().BeFalse();
        HiddenSessionPrivacy.IsHiddenSessionActive.Should().BeFalse();
    }

    [Fact]
    public async Task Without_start_switch_nothing_is_offered()
    {
        var manager = CreateManager(enabled: false);
        var folder = NewContainerFolder();
        Directory.CreateDirectory(folder);
        _store.Create(folder, "Tresor", "kennwort-1");

        manager.IsHiddenModeEnabled.Should().BeFalse();
        (await manager.FindAsync()).Should().BeEmpty();
        (await manager.CreateAsync(NewContainerFolder(), "X", "kennwort-1")).Success.Should().BeFalse();
        (await manager.OpenAsync(new HiddenLibraryInfo(folder, "Tresor"), "kennwort-1")).Success.Should().BeFalse();
        _switcher.Calls.Should().BeEmpty();
    }
}
