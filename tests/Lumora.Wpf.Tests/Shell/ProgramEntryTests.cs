using System.IO;
using System.Xml.Linq;
using Lumora.Services.HiddenLibraries.Containers;

namespace Lumora.Wpf.Tests.Shell;

/// <summary>
/// ADR-0061, Nachtrag 0.4.26: Lumora.exe erkennt den erhöhten Container-Schritt im eigenen Einsprung, bevor irgendetwas von
/// der App startet — keine Oberfläche, kein Katalog, kein Protokoll. Nur mit Fake-Auftrag, nie echt erhöht.
/// </summary>
public sealed class ProgramEntryTests
{
    [Fact]
    public void Elevated_verb_runs_only_the_job_and_returns_its_exit_code()
    {
        string[] args = ["--container-create-vhdx", "--path", @"D:\Daten\sicherung.vhdx", "--size-mb", "1024", "--letter", "X", "--label", "Daten"];
        IReadOnlyList<string>? received = null;

        var handled = Program.TryRunElevatedVerb(args, () => a =>
        {
            received = a;
            return (int)VhdxJobExit.FormatFailed;
        }, out var exitCode);

        handled.Should().BeTrue("die App darf in diesem Fall gar nicht starten");
        exitCode.Should().Be(7116);
        received.Should().Equal(args);
    }

    [Theory]
    [InlineData]
    [InlineData("_hidden")]
    [InlineData("--smoke")]
    [InlineData("--path", @"D:\x.vhdx", "--container-create-vhdx")]
    [InlineData("--CONTAINER-CREATE-VHDX")]
    public void Normal_starts_ignore_the_job(params string[] args)
    {
        var created = false;

        var handled = Program.TryRunElevatedVerb(args, () =>
        {
            created = true;
            return _ => 0;
        }, out _);

        handled.Should().BeFalse();
        created.Should().BeFalse("ohne das Verb wird die Windows-Seite nicht einmal erzeugt");
    }

    [Fact]
    public void Lumora_exe_uses_the_own_entry_point()
    {
        var root = LumoraIconFileTests.RepoRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "Lumora.Wpf", "Lumora.Wpf.csproj"));
        project.Descendants("StartupObject").Select(e => e.Value.Trim()).Should().Equal("Lumora.Program");
        typeof(Program).GetMethod(nameof(Program.Main))!.GetCustomAttributes(typeof(STAThreadAttribute), false).Should().ContainSingle();

        // Der Einsprung baut die App erst nach der Prüfung des Verbs
        var source = File.ReadAllText(Path.Combine(root, "src", "Lumora.Wpf", "Program.cs"));
        source.IndexOf("TryRunElevatedVerb(args", StringComparison.Ordinal).Should()
            .BeLessThan(source.IndexOf("new App()", StringComparison.Ordinal));
    }
}
