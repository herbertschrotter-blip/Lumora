using Lumora.Services.Metadata;

namespace Lumora.Tests.Metadata;

/// <summary>Dateikopf einmal lesen für Datum, Orte und Genauigkeit (ADR-0074).</summary>
public sealed class FileMetadataProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumora-probe-" + Guid.NewGuid().ToString("N"));

    public FileMetadataProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Dieselbe_Datei_wird_nur_einmal_gelesen_bis_sie_sich_aendert()
    {
        var path = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(path, new byte[64]);
        var probe = new FileMetadataProbe();

        probe.Read(path).Should().Be(FileMetadata.Empty, "kein bekanntes Format");
        probe.Read(path);
        probe.Reads.Should().Be(1);

        File.AppendAllText(path, "x");
        probe.Read(path);

        probe.Reads.Should().Be(2, "Größe geändert");
    }

    [Fact]
    public void Fehlende_Datei_ergibt_leer_ohne_Lesen()
    {
        var probe = new FileMetadataProbe();

        probe.Read(Path.Combine(_dir, "fehlt.jpg")).Should().Be(FileMetadata.Empty);
        probe.Reads.Should().Be(0);
    }

    [Fact]
    public void Leser_fuer_Datum_und_Orte_teilen_sich_den_Lesedurchgang()
    {
        var path = Path.Combine(_dir, "IMG_20240712_153012.jpg");
        File.WriteAllBytes(path, new byte[64]);
        var probe = new FileMetadataProbe();

        new CaptureDateReader(probe).Read(path).Should().NotBeNull("Datum aus dem Dateinamen");
        new GeoLocationReader(probe).Read(path).Should().BeNull();

        probe.Reads.Should().Be(1);
    }
}
