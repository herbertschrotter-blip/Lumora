using Lumora.Services.HiddenLibraries.Relocation;

namespace Lumora.Tests.HiddenLibraries;

/// <summary>Container verschieben (Mockup 26): Regeln für das Ziel und Kopieren mit Prüfsumme.</summary>
public sealed class ContainerRelocationRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "relocate-" + Guid.NewGuid().ToString("N"));

    public ContainerRelocationRulesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ContainerTargetCheck Check(string target, long free = 3L << 40, Func<string, bool>? exists = null, IReadOnlyList<string>? sync = null)
        => ContainerRelocationRules.Check(@"C:\Daten\Privat.vhdx", 418L << 30, target, 'X', _ => free, exists ?? (_ => false), sync ?? []);

    [Fact]
    public void Gutes_Ziel_auf_anderem_Laufwerk_kopiert_gleiches_Laufwerk_benennt_um()
    {
        Check(@"E:\Daten\Privat.vhdx").Should().Match<ContainerTargetCheck>(c => c.CanStart && !c.IsRename);
        Check(@"C:\Neu\Privat.vhdx").Should().Match<ContainerTargetCheck>(c => c.CanStart && c.IsRename);
        Check(@"C:\Neu\Privat.vhdx", free: 0).CanStart.Should().BeTrue("Umbenennen braucht keinen Platz");
    }

    [Fact]
    public void Sperrt_ungeeignete_Ziele()
    {
        Check(@"C:\Daten\Privat.vhdx").Problems.Should().Contain(p => p.Contains("Datei selbst"));
        Check(@"E:\Privat.vhdx", exists: _ => true).Problems.Should().Contain(p => p.Contains("schon eine Datei"));
        Check(@"X:\Privat.vhdx").Problems.Should().Contain(p => p.Contains("in sich selbst (X:)"));
        Check(@"\\nas\fotos\Privat.vhdx").Problems.Should().Contain(p => p.Contains("Netzwerkpfade"));
        Check(@"E:\Privat.vhd").Problems.Should().Contain(p => p.Contains("Dateiendung"));
        Check(@"E:\Users\me\OneDrive - Firma\Privat.vhdx").Problems.Should().Contain(p => p.Contains("Cloud"));
        Check(@"E:\Sync\Privat.vhdx", sync: [@"E:\Sync"]).Problems.Should().Contain(p => p.Contains("Cloud"));
        Check(@"E:\Privat.vhdx", free: 418L << 30).Problems.Should().ContainSingle(p => p.StartsWith("Zu wenig Platz auf E:"), "1 GB Reserve fehlt");
        ContainerRelocationRules.Check(@"C:\a.vhdx", 1, @"E:\a.vhdx", 'X', _ => null, _ => false, []).Problems.Should().Contain(p => p.Contains("nicht erreichbar"));
    }

    [Fact]
    public void Groessen_lesbar()
    {
        ContainerRelocationRules.FormatBytes(418L << 30).Should().Be("418 GB");
        ContainerRelocationRules.FormatBytes(1288490188800).Should().Be("1,2 TB");
        ContainerRelocationRules.FormatBytes(5L << 20).Should().Be("5 MB");
    }

    [Fact]
    public async Task Kopiert_prueft_und_meldet_Fortschritt_in_beiden_Abschnitten()
    {
        var source = Path.Combine(_root, "a.vhdx");
        var data = new byte[9 * 1024 * 1024 + 3];
        new Random(1).NextBytes(data);
        await File.WriteAllBytesAsync(source, data);
        var target = Path.Combine(_root, "neu", "a.vhdx");
        var phases = new List<ContainerCopyProgress>();

        var hash = await ContainerFileCopier.CopyAndVerifyAsync(source, target, new SyncProgress(phases.Add), CancellationToken.None);

        (await File.ReadAllBytesAsync(target)).Should().Equal(data);
        hash.Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)));
        File.Exists(target + ContainerFileCopier.PartSuffix).Should().BeFalse();
        phases.Select(p => p.Phase).Distinct().Should().Equal(ContainerCopyPhase.Copy, ContainerCopyPhase.Verify);
        phases.Last().Should().Match<ContainerCopyProgress>(p => p.Done == data.Length && p.Fraction == 1);
    }

    [Fact]
    public async Task Abbruch_loescht_die_halbe_Kopie_und_eine_alte_Teilkopie_wird_ueberschrieben()
    {
        var source = Path.Combine(_root, "b.vhdx");
        await File.WriteAllBytesAsync(source, new byte[5 * 1024 * 1024]);
        var target = Path.Combine(_root, "b-neu.vhdx");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => ContainerFileCopier.CopyAndVerifyAsync(source, target, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(target).Should().BeFalse();
        File.Exists(target + ContainerFileCopier.PartSuffix).Should().BeFalse();
        File.Exists(source).Should().BeTrue();

        await File.WriteAllTextAsync(target + ContainerFileCopier.PartSuffix, "Rest eines Absturzes");
        await ContainerFileCopier.CopyAndVerifyAsync(source, target, null, CancellationToken.None);
        new FileInfo(target).Length.Should().Be(5 * 1024 * 1024);
    }

    private sealed class SyncProgress(Action<ContainerCopyProgress> report) : IProgress<ContainerCopyProgress>
    {
        public void Report(ContainerCopyProgress value) => report(value);
    }
}
