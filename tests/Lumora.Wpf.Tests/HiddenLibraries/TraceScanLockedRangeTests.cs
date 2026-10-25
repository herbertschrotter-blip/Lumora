using System.IO;
using System.Text;
using Lumora.Shell.Smoke;

namespace Lumora.Wpf.Tests.HiddenLibraries;

/// <summary>
/// Spurenprüfung (Rauchtest „Versteckte Bibliothek“, 0.3.0): Dateien mit gesperrten Byte-Bereichen — wie SQLite sie in
/// einer offenen DB samt <c>-wal</c>/<c>-shm</c> hält — gelten nicht mehr pauschal als Spur; der lesbare Rest wird
/// durchsucht. Gesperrt wird mit <see cref="FileStream.Lock"/> (LockFileEx wie SQLite).
/// </summary>
public sealed class TraceScanLockedRangeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", "tracelock-" + Guid.NewGuid().ToString("N"));

    public TraceScanLockedRangeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] FileWith(string? needleAtStart)
    {
        var bytes = new byte[3 * 4096];
        if (needleAtStart is not null)
        {
            Encoding.UTF8.GetBytes(needleAtStart).CopyTo(bytes, 10);
        }

        return bytes;
    }

    [Fact]
    public void Gesperrter_Bereich_ohne_Treffer_ist_keine_Spur()
    {
        var file = Path.Combine(_root, "library.db-shm");
        File.WriteAllBytes(file, FileWith(null));
        using var holder = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        holder.Lock(4096, 4096);

        SmokeScenario.FindTraces(_root, ["Geheim-Bibliothek"]).Should().BeEmpty("nur ein Bereich ist gesperrt, der Rest ist lesbar und leer");
    }

    [Fact]
    public void Treffer_ausserhalb_des_gesperrten_Bereichs_wird_gefunden()
    {
        var file = Path.Combine(_root, "library.db-wal");
        File.WriteAllBytes(file, FileWith("Geheim-Bibliothek"));
        using var holder = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        holder.Lock(8192, 4096);

        SmokeScenario.FindTraces(_root, ["Geheim-Bibliothek"]).Should().ContainSingle().Which.Should().Be("library.db-wal");
    }

    [Fact]
    public void Ganz_gesperrte_Datei_bleibt_eine_moegliche_Spur()
    {
        var file = Path.Combine(_root, "library.db");
        File.WriteAllBytes(file, FileWith(null));
        using var holder = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        holder.Lock(0, 3 * 4096);

        SmokeScenario.FindTraces(_root, ["Geheim-Bibliothek"]).Should().ContainSingle().Which.Should().Be("library.db (nicht lesbar)");
    }

    [Fact]
    public void Kurz_ganz_gesperrte_Datei_wird_nach_erneutem_Lesen_durchsucht()
    {
        // Wie SQLite direkt nach einem Sitzungswechsel: -wal kurz komplett gesperrt, danach frei (0.4.10). Die Sperre löst
        // sich vor der ersten Wiederholung — ohne feste Wartezeit (wackelte unter Last, 0.4.41).
        var file = Path.Combine(_root, "library.db-wal");
        File.WriteAllBytes(file, FileWith(null));
        using var holder = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        holder.Lock(0, 3 * 4096);
        var retries = 0;

        var hits = SmokeScenario.FindTraces(_root, ["Geheim-Bibliothek"], retries: 3, retryDelay: TimeSpan.FromMilliseconds(10), beforeRetry: () =>
        {
            if (retries++ == 0)
            {
                holder.Unlock(0, 3 * 4096);
            }
        });

        retries.Should().Be(1, "der erste Leseversuch scheitert an der Sperre, der zweite gelingt");
        hits.Should().BeEmpty("die Sperre löst sich innerhalb der Wiederholungen");
    }

    [Fact]
    public void Dauerhaft_gesperrte_Datei_bleibt_auch_mit_Wiederholungen_eine_moegliche_Spur()
    {
        var file = Path.Combine(_root, "library.db-wal");
        File.WriteAllBytes(file, FileWith(null));
        using var holder = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        holder.Lock(0, 3 * 4096);

        SmokeScenario.FindTraces(_root, ["Geheim-Bibliothek"], retries: 3, retryDelay: TimeSpan.FromMilliseconds(10))
            .Should().ContainSingle().Which.Should().Be("library.db-wal (nicht lesbar)");
    }
}
