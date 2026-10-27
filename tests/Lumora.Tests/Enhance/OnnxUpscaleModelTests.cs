using System.Security.Cryptography;
using Lumora.Services.Enhance;
using Lumora.Services.Enhance.Onnx;

namespace Lumora.Tests.Enhance;

/// <summary>Nur mit echten Modellen (<c>LUMORA_ENHANCE_MODELS</c> zeigt auf einen Ordner mit den ONNX-Dateien), sonst übersprungen.</summary>
public sealed class EnhanceModelTheoryAttribute : TheoryAttribute
{
    public EnhanceModelTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUMORA_ENHANCE_MODELS")))
        {
            Skip = "Modelle nicht vorhanden (LUMORA_ENHANCE_MODELS auf den Ordner mit den Hochskalier-Modellen setzen).";
        }
    }
}

/// <summary>
/// Die Hochskalier-Modelle von 0.5.11 mit den echten Dateien (ohne Fotos): Prüfsumme wie im Katalog, Laden auf der CPU,
/// ein kleines Testbild wird genau 4× größer, Werte im erlaubten Bereich und nicht einfarbig.
/// </summary>
public sealed class OnnxUpscaleModelTests
{
    public static TheoryData<EnhanceTool> NewEngines =>
        new(EnhanceTool.UpscaleClear, EnhanceTool.UpscaleClearSoft, EnhanceTool.UpscalePhoto, EnhanceTool.UpscaleSiax, EnhanceTool.UpscaleSkin);

    [EnhanceModelTheory]
    [MemberData(nameof(NewEngines))]
    public void Modell_laedt_und_vergroessert_ein_Testbild_vierfach(EnhanceTool engine)
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("LUMORA_ENHANCE_MODELS")!, EnhanceModelCatalog.FileOf(engine)!);
        if (!File.Exists(path))
        {
            return; // nur die vorhandenen Dateien prüfen
        }

        var expected = EnhanceModelCatalog.DefinitionOf(engine)!.FileList[0];
        new FileInfo(path).Length.Should().Be(expected.Size);
        using (var stream = File.OpenRead(path))
        {
            Convert.ToHexStringLower(SHA256.HashData(stream)).Should().Be(expected.Sha256);
        }

        // Verlauf mit Kante, 40 × 30
        var source = EnhanceImage.Blank(40, 30);
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                var i = (y * 40 + x) * 3;
                source.Pixels[i] = (byte)(x * 6);
                source.Pixels[i + 1] = (byte)(y * 8);
                source.Pixels[i + 2] = (byte)(x < 20 ? 40 : 220);
            }
        }

        using var model = new OnnxEnhanceEngineFactory().Create(engine, path, useGpu: false);
        var result = model.Process(new EnhanceStepInput(source, Factor: 4), null, CancellationToken.None);

        (result.Width, result.Height).Should().Be((160, 120));
        result.Pixels.Distinct().Count().Should().BeGreaterThan(50, "kein einfarbiges oder leeres Ergebnis");
        var leftBlue = result.Pixels[(60 * 160 + 20) * 3 + 2];
        var rightBlue = result.Pixels[(60 * 160 + 140) * 3 + 2];
        rightBlue.Should().BeGreaterThan(leftBlue, "die Kante bleibt an ihrem Platz");
    }
}
