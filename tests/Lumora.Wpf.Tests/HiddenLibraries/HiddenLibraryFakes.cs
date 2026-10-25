using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Domain.Enums;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.SecurityCheck;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Scanning;
using Lumora.Shell.HiddenLibraries;

namespace Lumora.Wpf.Tests.HiddenLibraries;

/// <summary>Katalog-Wechsler ohne echte Fenster: je Katalog (DB-Pfad) ein kleiner Container mit Fake-Bibliotheken und -Scan.</summary>
internal sealed class FakeHostSwitcher : ICatalogHostSwitcher
{
    private readonly Dictionary<string, FakeLibraries> _catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServiceProvider _normal;

    public FakeHostSwitcher(params string[] normalRoots)
    {
        var libraries = new FakeLibraries();
        var id = 1;
        foreach (var root in normalRoots)
        {
            libraries.Items.Add(NewInfo(id++, Path.GetFileName(root), root));
        }

        _normal = Build(libraries, new FakeScan());
        CurrentServices = _normal;
    }

    public IServiceProvider CurrentServices { get; private set; }

    public List<string> Calls { get; } = new();

    public bool Busy { get; set; }

    public bool FailNextHidden { get; set; }

    public FakeScan? LastScan { get; private set; }

    public FakeLibraries LibrariesOf(string databasePath) => _catalogs[databasePath];

    public async Task SwitchToHiddenAsync(AppDataPaths hiddenPaths, Func<IServiceProvider, Task>? prepare)
    {
        Calls.Add("hidden:" + hiddenPaths.LibraryDatabasePath);
        if (FailNextHidden)
        {
            FailNextHidden = false;
            CurrentServices = _normal;
            throw new IOException("Container schreibgeschützt");
        }

        if (!_catalogs.TryGetValue(hiddenPaths.LibraryDatabasePath, out var libraries))
        {
            libraries = new FakeLibraries();
            _catalogs[hiddenPaths.LibraryDatabasePath] = libraries;
        }

        LastScan = new FakeScan();
        var services = Build(libraries, LastScan, HiddenSecuritySettings, HiddenSecurityService);
        if (prepare is not null)
        {
            await prepare(services);
        }

        CurrentServices = services;
    }

    public Task SwitchToNormalAsync()
    {
        Calls.Add("normal");
        CurrentServices = _normal;
        return Task.CompletedTask;
    }

    /// <summary>Gesetzt: die Belegt-Abfrage wartet, bis der Test sie freigibt (zwei Prüfungen gleichzeitig unterwegs).</summary>
    public TaskCompletionSource<bool>? BusyGate { get; set; }

    public Task<bool> IsCatalogBusyAsync() => BusyGate?.Task ?? Task.FromResult(Busy);

    /// <summary>false = ein Fenster lehnt das Schließen ab (z. B. laufendes Entpacken).</summary>
    public bool WindowsClose { get; set; } = true;

    public int CloseWindowsCalls { get; private set; }

    public Task<bool> CloseSecondaryWindowsAsync()
    {
        CloseWindowsCalls++;
        return Task.FromResult(WindowsClose);
    }

    /// <summary>Einstellungen des Sicherheits-Checks im versteckten Katalog (ADR-0064); null = nicht registriert.</summary>
    public ISecurityCheckSettingsStore? HiddenSecuritySettings { get; set; }

    /// <summary>Dienst des Sicherheits-Checks im versteckten Container (Stand im Speicher); null = nicht registriert.</summary>
    public ISecurityCheckService? HiddenSecurityService { get; set; }

    /// <summary>„Lumora nach dem Zusperren ganz beenden“ (ADR-0064) — statt Application.Shutdown nur gezählt.</summary>
    public int ShutdownCalls { get; private set; }

    public void ShutdownAfterLock()
    {
        Calls.Add("shutdown");
        ShutdownCalls++;
    }

    internal static LibraryInfo NewInfo(int id, string name, string root)
        => new(id, name, root, DateTime.UtcNow, DateTime.UtcNow, 0, 0, true,
            [new LibraryRootInfo(id * 10, id, root, LibraryRootRole.Archive, name, 0, true)]);

    private static ServiceProvider Build(FakeLibraries libraries, FakeScan scan, ISecurityCheckSettingsStore? securitySettings = null, ISecurityCheckService? securityService = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (securitySettings is not null)
        {
            services.AddSingleton(securitySettings); // Sicherheits-Check (ADR-0064): nur in versteckten Sitzungen
        }

        if (securityService is not null)
        {
            services.AddSingleton(securityService);
        }

        services.AddSingleton<ILibraryService>(libraries);
        services.AddSingleton<IScanService>(scan);
        return services.BuildServiceProvider();
    }
}

internal sealed class FakeLibraries : ILibraryService
{
    public List<LibraryInfo> Items { get; } = new();

    public int? ActiveId { get; private set; }

    public Task<IReadOnlyList<LibraryInfo>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LibraryInfo>>(Items.ToList());

    public Task<LibraryInfo?> GetAsync(int libraryId, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(l => l.Id == libraryId));

    public Task<LibraryInfo?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(l => l.Id == ActiveId));

    public Task<LibraryInfo> CreateAsync(string name, string rootPath, CancellationToken ct = default)
    {
        var info = FakeHostSwitcher.NewInfo(Items.Count + 1, name, rootPath);
        Items.Add(info);
        return Task.FromResult(info);
    }

    public Task SetActiveAsync(int libraryId, CancellationToken ct = default)
    {
        ActiveId = libraryId;
        return Task.CompletedTask;
    }

    public Task RenameAsync(int libraryId, string newName, CancellationToken ct = default) => throw new NotSupportedException();

    public Task ClearAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task DeleteThumbnailCacheAsync(int libraryId) => throw new NotSupportedException();

    public Task DeleteAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<LibraryRootInfo>> GetRootsAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<LibraryRootInfo> AddRootAsync(int libraryId, string path, LibraryRootRole role, string? name = null, CancellationToken ct = default) => throw new NotSupportedException();

    public Task RenameRootAsync(int rootId, string newName, CancellationToken ct = default) => throw new NotSupportedException();

    public Task SetRootRoleAsync(int rootId, LibraryRootRole role, CancellationToken ct = default) => throw new NotSupportedException();

    public Task RemoveRootAsync(int rootId, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class FakeScan : IScanService
{
    public List<(int LibraryId, ScanMode Mode)> Calls { get; } = new();

    public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        => ScanAsync(libraryId, mode, ScanOptions.Default, progress, cancellationToken);

    public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((libraryId, mode));
        }

        return Task.FromResult<ScanResult>(null!);
    }
}

internal sealed class FakeContainers : IContainerTools
{
    public List<string> Dismounted { get; } = new();

    public List<(string File, char Letter)> Mounted { get; } = new();

    public bool DismountSucceeds { get; set; } = true;

    public bool IsVeraCryptInstalled { get; set; } = true;

    public ContainerKind Detect(string driveRoot) => ContainerKind.VeraCrypt;

    public Task<ContainerCommandResult> DismountAsync(string driveRoot, CancellationToken ct = default)
    {
        Dismounted.Add(driveRoot);
        return Task.FromResult(new ContainerCommandResult(DismountSucceeds, DismountSucceeds ? "ok" : "noch Dateien geöffnet"));
    }

    public Task<ContainerCommandResult> MountVeraCryptAsync(string containerFile, char driveLetter, CancellationToken ct = default)
    {
        Mounted.Add((containerFile, driveLetter));
        return Task.FromResult(new ContainerCommandResult(true, "ok"));
    }

    public List<string> MountedImages { get; } = new();

    public ContainerCommandResult VirtualDiskResult { get; set; } = new(true, "Container ist als X: eingehängt und entsperrt.");

    public Task<ContainerCommandResult> MountVirtualDiskAsync(string imageFile, CancellationToken ct = default)
    {
        MountedImages.Add(imageFile);
        return Task.FromResult(VirtualDiskResult);
    }
}

internal sealed class FixedDriveRoots(params string[] roots) : IDriveRootSource
{
    public IReadOnlyList<string> GetSearchRoots() => roots;
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Dialoge der versteckten Bibliotheken mit vorgegebenen Antworten.</summary>
internal sealed class ScriptedHiddenDialogs : IHiddenLibraryDialogs
{
    public HiddenLibraryCreateAnswer? CreateAnswer { get; set; }

    public HiddenLibraryUnlockAnswer? UnlockAnswer { get; set; }

    public ContainerMountAnswer? MountAnswer { get; set; }

    public List<string> Asked { get; } = new();

    public bool? LastCanReset { get; private set; }

    /// <summary>Laufwerke des letzten Anlegen-Dialogs (das erste ist vorgewählt).</summary>
    public IReadOnlyList<string>? LastDrives { get; private set; }

    public HiddenLibraryCreateAnswer? AskCreate(IReadOnlyList<string> drives, Func<string, Task<string?>> validateFolder)
    {
        Asked.Add("create");
        LastDrives = drives;
        return CreateAnswer;
    }

    public HiddenLibraryUnlockAnswer? AskUnlock(HiddenLibraryInfo library, bool canReset, Func<string, Task<bool>> verify)
    {
        Asked.Add("unlock:" + library.Name);
        LastCanReset = canReset;
        return UnlockAnswer;
    }

    public ContainerMountAnswer? AskMountContainer(IReadOnlyList<char> freeLetters)
    {
        Asked.Add("mount");
        return MountAnswer;
    }
}
