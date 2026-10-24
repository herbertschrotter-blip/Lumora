using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.SecurityCheck;
using Xunit.Abstractions;

namespace Lumora.Tests.HiddenLibraries;

/// <summary>Nur mit <c>LUMORA_SECURITYCHECK_REAL=1</c>: echte Windows-Leser (nur lesend), sonst übersprungen.</summary>
public sealed class RealWindowsFactAttribute : FactAttribute
{
    public RealWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("LUMORA_SECURITYCHECK_REAL") != "1")
        {
            Skip = "Nur auf Wunsch (LUMORA_SECURITYCHECK_REAL=1): liest die echte Registry und Shell — nur lesend.";
        }
    }
}

/// <summary>
/// ADR-0064: Messung auf dem echten PC — die Windows-Leser liefern ohne Administratorrechte Werte (keine Ausnahme), die
/// Shell-Eigenschaft BitLocker ist lesbar. Ausgabe nur Punkt + Status (keine Pfade).
/// </summary>
public sealed class SecurityCheckRealWindowsTests(ITestOutputHelper output)
{
    [RealWindowsFact]
    public async Task Real_readers_answer_without_admin_rights()
    {
        var sources = new SecurityCheckSources(new WindowsRegistryReader(), new ShellVolumePropertyReader(), new LocalUserFileReader(), new VeraCryptLocator());
        ISecurityCheck[] checks =
        [
            new BitLockerCheck(sources), new HibernationCheck(sources), new FastStartupCheck(sources), new PageFileCheck(sources), new RecentItemsCheck(sources),
            new ActivityHistoryCheck(sources), new ThumbnailCheck(sources), new SearchIndexCheck(sources), new VeraCryptHistoryCheck(sources), new ExitAfterLockCheck(),
        ];
        var logger = new NullLogger<SecurityCheckService>();
        var service = new SecurityCheckService(checks, new MemoryStore(), () => 'D', logger, new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger());
        var report = await service.RunAsync();
        foreach (var finding in report.Findings)
        {
            output.WriteLine($"{finding.Id}: {finding.Status}");
        }

        output.WriteLine(report.StatusText);
        new ShellVolumePropertyReader().ReadBitLockerProtection('C').Should().NotBeNull("die Shell kennt den BitLocker-Zustand von C:");
        report.Findings.Should().NotContain(f => f.Detail.Contains("nicht geliefert", StringComparison.Ordinal), "kein Leser darf ohne Administratorrechte scheitern");
    }

    private sealed class MemoryStore : ISecurityCheckSettingsStore
    {
        public Task<SecurityCheckSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(SecurityCheckSettings.Default);

        public Task SaveAsync(SecurityCheckSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }
}
