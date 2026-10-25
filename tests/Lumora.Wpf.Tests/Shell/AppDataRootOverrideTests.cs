using System.IO;
using Lumora.Services.Infrastructure;
using Lumora.Shell;

namespace Lumora.Wpf.Tests.Shell;

/// <summary>
/// Start-Auflösung der App-Wurzel (0.4.40, ADR-0069) mit simuliertem EXE-Ordner im Temp: portabel → <c>Daten</c> neben der
/// EXE samt <c>database-location.json</c>-Auflösung; ohne Markierung der übergebene Standardordner. Läuft der Testprozess
/// mit LUMORA_APPDATA, gewinnt die Variable (Vorrang) — das wird dann geprüft.
/// </summary>
public sealed class AppDataRootOverrideTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string? Variable => Environment.GetEnvironmentVariable(AppDataRootOverride.EnvironmentVariable);

    [Fact]
    public void Portable_exe_folder_uses_Daten_next_to_the_exe()
    {
        var exeDir = Path.Combine(_base, "Lumora-0.5.0");
        Directory.CreateDirectory(exeDir);
        File.WriteAllText(Path.Combine(exeDir, AppDataLocationResolver.PortableMarkerFileName), "portabel");
        var standard = Path.Combine(_base, "standard");

        var location = AppDataRootOverride.ResolveLocation(exeDir, standard);

        if (string.IsNullOrWhiteSpace(Variable))
        {
            location.Should().Be(new AppDataLocation(AppDataLocationMode.Portable, Path.Combine(exeDir, "Daten")));
            AppDataPaths.Resolve(location.Root).LibraryDatabasePath.Should().Be(Path.Combine(exeDir, "Daten", "library.db"));
        }
        else
        {
            location.Mode.Should().Be(AppDataLocationMode.EnvironmentVariable, "LUMORA_APPDATA hat Vorrang");
        }
    }

    [Fact]
    public void Without_marker_the_standard_root_applies()
    {
        var exeDir = Path.Combine(_base, "bin");
        Directory.CreateDirectory(exeDir);
        var standard = Path.Combine(_base, "standard");

        var location = AppDataRootOverride.ResolveLocation(exeDir, standard);

        location.Mode.Should().Be(string.IsNullOrWhiteSpace(Variable) ? AppDataLocationMode.Standard : AppDataLocationMode.EnvironmentVariable);
        if (location.Mode == AppDataLocationMode.Standard)
        {
            location.Root.Should().Be(standard);
        }
    }
}
