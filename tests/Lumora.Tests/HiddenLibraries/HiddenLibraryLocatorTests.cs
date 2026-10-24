using Lumora.Services.HiddenLibraries;

namespace Lumora.Tests.HiddenLibraries;

/// <summary>
/// ADR-0049: Suche nach versteckten Bibliotheken (Wurzel + eine Ebene, nichts tiefer), Regeln für den Ordner einer neuen
/// versteckten Bibliothek und der Startschalter <c>_hidden</c>.
/// </summary>
public sealed class HiddenLibraryLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "locator-" + Guid.NewGuid().ToString("N"));
    private readonly HiddenLibraryStore _store = new(iterations: 1_000);

    public HiddenLibraryLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Finds_libraries_in_drive_roots_and_one_level_below_but_not_deeper()
    {
        // zwei „Laufwerke“ (Ordner als Wurzeln)
        var driveA = Directory.CreateDirectory(Path.Combine(_root, "A")).FullName;
        var driveB = Directory.CreateDirectory(Path.Combine(_root, "B")).FullName;
        _store.Create(driveA, "Ganzes Laufwerk", "kennwort-1");
        var oneLevel = Directory.CreateDirectory(Path.Combine(driveB, "Fotos")).FullName;
        _store.Create(oneLevel, "Bernd", "kennwort-1");
        var twoLevels = Directory.CreateDirectory(Path.Combine(driveB, "Daten", "Tief")).FullName;
        _store.Create(twoLevels, "Zu tief", "kennwort-1");
        var hiddenFolder = Directory.CreateDirectory(Path.Combine(driveB, "Versteckt")).FullName;
        _store.Create(hiddenFolder, "anna", "kennwort-1");
        new DirectoryInfo(hiddenFolder).Attributes |= FileAttributes.Hidden; // versteckte Ordner werden mitgesucht
        var corrupt = Directory.CreateDirectory(Path.Combine(driveB, "Kaputt", ".lumora")).FullName;
        File.WriteAllText(Path.Combine(corrupt, HiddenLibraryStore.ManifestFileName), "kein json");

        var found = new HiddenLibraryLocator(new FixedRoots(driveA, driveB, Path.Combine(_root, "fehlt"))).Find();

        found.Select(l => l.Name).Should().Equal("anna", "Bernd", "Ganzes Laufwerk");
        found.Single(l => l.Name == "Bernd").FolderPath.Should().Be(oneLevel);
        found.Should().NotContain(l => l.Name == "Zu tief");
    }

    [Fact]
    public async Task Nothing_found_without_drives()
        => (await new HiddenLibraryLocator(new FixedRoots()).FindAsync()).Should().BeEmpty();

    [Fact]
    public void Folder_rules_allow_drive_root_or_one_level_and_refuse_everything_else()
    {
        var none = Array.Empty<string>();
        // freier Buchstabe: auf X: kann auf dem Entwickler-PC eine echte versteckte Bibliothek eingehängt sein
        var d = FreeDriveLetter();
        HiddenLibraryFolderRules.Validate($@"{d}:\", none).Should().BeNull();
        HiddenLibraryFolderRules.Validate($@"{d}:\Fotos", none).Should().BeNull();
        HiddenLibraryFolderRules.Validate($@"{d}:\Fotos\2024", none).Should().Contain("direkt auf dem Laufwerk");
        HiddenLibraryFolderRules.Validate($@"{d}:\Fotos\2024", none, maxDepth: int.MaxValue).Should().BeNull("nur der Rauchtest erlaubt mehr");
        HiddenLibraryFolderRules.Validate(@"\\server\freigabe\Fotos", none).Should().Contain("Netzwerkpfad");
        HiddenLibraryFolderRules.Validate("relativ", none).Should().NotBeNull();
        HiddenLibraryFolderRules.Validate(null, none).Should().NotBeNull();
        HiddenLibraryFolderRules.Validate($@"{d}:\.lumora", none).Should().Contain("Katalogordner");

        // nicht in OneDrive/Lumora-Daten und nicht oberhalb davon
        HiddenLibraryFolderRules.Validate(@"D:\OneDrive", [@"D:\OneDrive"]).Should().Contain("OneDrive");
        HiddenLibraryFolderRules.Validate(@"D:\", [@"D:\OneDrive"]).Should().NotBeNull("enthielte OneDrive");
        HiddenLibraryFolderRules.Validate(@"D:\Tresor", [@"D:\OneDrive"]).Should().BeNull();

        var existing = Directory.CreateDirectory(Path.Combine(_root, "schon")).FullName;
        _store.Create(existing, "Schon da", "kennwort-1");
        HiddenLibraryFolderRules.Validate(existing, none, int.MaxValue).Should().Contain("bereits");
    }

    /// <summary>Ein Laufwerksbuchstabe, der auf diesem PC nicht belegt ist (von Z: abwärts).</summary>
    private static char FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(x => char.ToUpperInvariant(x.Name[0])).ToHashSet();
        for (var c = 'Z'; c >= 'D'; c--)
        {
            if (!used.Contains(c))
            {
                return c;
            }
        }

        throw new InvalidOperationException("Kein freier Laufwerksbuchstabe für den Test.");
    }

    [Theory]
    [InlineData(new[] { "_hidden" }, true)]
    [InlineData(new[] { "--HIDDEN" }, true)]
    [InlineData(new[] { "/hidden" }, true)]
    [InlineData(new[] { "datei.jpg", " _hidden " }, true)]
    [InlineData(new[] { "-hidden" }, false)]
    [InlineData(new[] { "hidden" }, false)]
    [InlineData(new string[0], false)]
    public void Start_switch(string[] args, bool expected) => HiddenModeSwitch.IsRequested(args).Should().Be(expected);

    [Fact]
    public void Smoke_simulates_the_switch_only_during_a_smoke_run()
    {
        string? Env(string value) => value == HiddenModeSwitch.SmokeVariable ? "1" : null;
        string? Off(string value) => value == HiddenModeSwitch.SmokeVariable ? "0" : null;
        HiddenModeSwitch.IsRequested([], smokeRequested: false, Env).Should().BeFalse("ohne Rauchtest zählt die Variable nicht");
        HiddenModeSwitch.IsRequested([], smokeRequested: true, Env).Should().BeTrue();
        HiddenModeSwitch.IsRequested([], smokeRequested: true, _ => null).Should().BeTrue("Rauchtest: Standard an");
        HiddenModeSwitch.IsRequested([], smokeRequested: true, Off).Should().BeFalse();
    }

    [Fact]
    public void Idle_monitor_expires_after_timeout_without_activity()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var idle = new IdleLockMonitor(time, TimeSpan.FromMinutes(10));
        idle.IsExpired.Should().BeFalse();

        time.Advance(TimeSpan.FromMinutes(9));
        idle.IsExpired.Should().BeFalse();
        idle.Remaining.Should().Be(TimeSpan.FromMinutes(1));

        idle.NotifyActivity();
        time.Advance(TimeSpan.FromMinutes(9.5));
        idle.IsExpired.Should().BeFalse("Eingabe setzt die Uhr zurück");

        time.Advance(TimeSpan.FromMinutes(1));
        idle.IsExpired.Should().BeTrue();

        idle.Timeout = TimeSpan.FromMinutes(30);
        idle.IsExpired.Should().BeFalse("längere Zeit eingestellt");
    }

    private sealed class FixedRoots(params string[] roots) : IDriveRootSource
    {
        public IReadOnlyList<string> GetSearchRoots() => roots;
    }
}

/// <summary>Uhr für Tests (TimeProvider ohne Zusatzpaket).</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
