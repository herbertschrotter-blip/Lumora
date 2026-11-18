using Lumora.Domain.Enums;
using Lumora.Imaging.Merge;
using Lumora.Imaging.Protocol;

namespace Lumora.Imaging.Tests.Merge;

/// <summary>Kleine Bausteine (ADR-0078 Punkt 5/10/17): Zielname, Rezept, Speicherbudget, Hinweistexte, Protokoll.</summary>
public sealed class MergeBuildingBlocksTests
{
    [Fact]
    public void Zielname_liegt_neben_dem_ersten_Einzelbild_oder_im_Unterordner_bzw_eine_Ebene_hoeher()
    {
        var folder = Path.Combine("D:", "Fotos", "100_0822");
        MergeNaming.TargetPath(folder, "DJI_0001.JPG", MergeSettings.Default).Should().Be(Path.Combine(folder, "DJI_0001_panorama.jpg"));
        MergeNaming.TargetPath(folder, "DJI_0001.JPG", MergeSettings.Default with { Location = MergeSaveLocation.Subfolder })
            .Should().Be(Path.Combine(folder, "zusammengefügt", "DJI_0001_panorama.jpg"));
        MergeNaming.TargetPath(folder, "DJI_0001.JPG", MergeSettings.Default with { DroneFolderOneUp = true })
            .Should().Be(Path.Combine("D:", "Fotos", "DJI_0001_panorama.jpg"));
        MergeNaming.TargetPath(Path.Combine("D:", "Urlaub"), "a.jpg", MergeSettings.Default with { DroneFolderOneUp = true, PanoramaSuffix = "" })
            .Should().Be(Path.Combine("D:", "Urlaub", "a_panorama.jpg"), "nur DJI-Ordner wandern eine Ebene höher; leerer Zusatz träfe das Einzelbild");
    }

    [Fact]
    public void Rezept_ueberlebt_JSON_ohne_Pfade()
    {
        var recipe = new MergeRecipe(
            MergeRecipe.CurrentVersion,
            MergeMethod.Panorama,
            new MergeOptions(PanoramaProjection.Spherical360, MergeSphereForm.Full),
            PanoramaProjection.Spherical360,
            MergeSphereForm.Full,
            [new MergeRecipeSource(11, 0), new MergeRecipeSource(12, 1)],
            new AlignmentSpec { Cameras = [new CameraSpec { Source = 0, Width = 10, Height = 8, F = 9, R = [1, 0, 0, 0, 1, 0, 0, 0, 1] }], Seed = 5 },
            0.7,
            "Lumora 0.5.46");

        var json = recipe.ToJson();
        var back = MergeRecipe.FromJson(json)!;

        back.Options.SphereForm.Should().Be(MergeSphereForm.Full);
        back.Sources.Select(s => s.FileId).Should().Equal(11, 12);
        back.Alignment.Cameras.Single().R.Should().Equal(1, 0, 0, 0, 1, 0, 0, 0, 1);
        json.Should().NotContain(":\\\\", "keine Pfade im Rezept");
        MergeRecipe.FromJson("{kaputt").Should().BeNull();
    }

    [Fact]
    public void Speicherbudget_liegt_unter_dem_freien_Arbeitsspeicher()
    {
        var (total, available) = MergeMemoryBudget.Query();
        total.Should().BeGreaterThan(0);
        MergeMemoryBudget.Budget().Should().BeGreaterThanOrEqualTo(MergeMemoryBudget.Minimum).And.BeLessThanOrEqualTo(Math.Max(MergeMemoryBudget.Minimum, available));
    }

    [Fact]
    public void Hinweis_groesste_passende_Groesse_nennt_Stufe_und_Grund()
    {
        var memory = MergeTexts.Note("size-reduced:memory", 30000, 15000, 0.6, 9_000_000_000)!;
        memory.Text.Should().Contain("60 %").And.Contain("Arbeitsspeicher").And.Contain("30.000 × 15.000");
        MergeTexts.Note("size-reduced:jpeg-edge", 80000, 9000, 0.8)!.Text.Should().Contain("JPEG-Grenze");
        MergeTexts.Note("gain:0.8-1.2").Should().BeNull("technische Codes ohne Text");
    }

    [Fact]
    public void Protokoll_ueberlebt_eine_Zeile_und_ueberliest_fremde_Ausgabe()
    {
        var msg = new ComposeResult { Id = 7, OutputPath = "x.jpg", Check = new QualityCheck { Passed = true, SeamNcc = double.NaN, Problems = ["seams"] }, Projection = WireProjection.Vertical };
        var line = WorkerProtocol.Encode(msg);
        var back = WorkerProtocol.Decode(line).Should().BeOfType<ComposeResult>().Subject;
        back.Id.Should().Be(7);
        back.Projection.Should().Be(WireProjection.Vertical);
        double.IsNaN(back.Check.SeamNcc).Should().BeTrue();
        WorkerProtocol.Decode("OpenCV: [ WARN ] irgendwas").Should().BeNull();
        WorkerProtocol.Decode("@lumora {kaputt").Should().BeNull();
    }
}
