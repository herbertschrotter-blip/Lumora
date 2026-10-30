using System.Text;
using Lumora.Services.Edit;
using Lumora.Services.Edit.Onnx;
using Lumora.Services.Faces;

namespace Lumora.Tests.Edit;

/// <summary>
/// Grenzen der KI-Bearbeitung (ADR-0076 Punkt 8, Stufe 2): Begriffsliste, Kleidungsliste, Vorprüfung, Ergebnisprüfung mit
/// Schwellen, Kennzeichnung. Ohne echte Personen und ohne echte Fotos — nur erzeugte Werte und Bilder.
/// </summary>
public sealed class EditPolicyTests
{
    private readonly EditPolicy _policy = new();
    private static readonly EditSubject Adult = new(1, 35);
    private static readonly EditSubject Young = new(1, 17);
    private static readonly EditSubject AgeUnknown = new(1, null);

    [Theory]
    [InlineData("nackt am Strand")]
    [InlineData("in Unterwäsche")]
    [InlineData("im Höschen")]
    [InlineData("roter BH")]
    [InlineData("Bikinimode")]
    [InlineData("topless on the beach")]
    [InlineData("see-through dress")]
    [InlineData("sexy pose")]
    [InlineData("LINGERIE")]
    [InlineData("oben ohne")]
    public void Begriffsliste_haelt_freizuegige_Texte_auf(string text)
        => EditTerms.FindBlocked(text).Should().NotBeEmpty();

    [Theory]
    [InlineData("Sonnenuntergang am Meer")]
    [InlineData("Berghütte im Winter mit Schnee")]
    [InlineData("Slipper und Streifenhemd")]
    [InlineData("Butterbrot im Park")]
    [InlineData("Ausflug nach Essex")]
    [InlineData("stripes on the wall")]
    [InlineData("")]
    public void Begriffsliste_laesst_Alltagstexte_durch(string text)
        => EditTerms.FindBlocked(text).Should().BeEmpty();

    [Fact]
    public void Vorpruefung_Vorlagen_Kleidung_Text_und_Alter()
    {
        _policy.CheckRequest(new EditRequest(EditTemplate.Remove), Adult).Allowed.Should().BeTrue();
        _policy.CheckRequest(new EditRequest(EditTemplate.Background, "Bergsee bei Sonnenaufgang"), Adult).Allowed.Should().BeTrue();
        _policy.CheckRequest(new EditRequest(EditTemplate.Background, "Strand, Person im Bikini"), Adult).Reason.Should().Contain("nicht gesendet");
        _policy.CheckRequest(new EditRequest(EditTemplate.Background, new string('a', 201)), Adult).Reason.Should().Contain("zu lang");
        _policy.CheckRequest(new EditRequest(EditTemplate.Remove, "Strand"), Adult).Reason.Should().Contain("nur bei „Hintergrund“");

        _policy.CheckRequest(new EditRequest(EditTemplate.Outfit, OutfitId: "tracht"), Adult).Allowed.Should().BeTrue();
        _policy.CheckRequest(new EditRequest(EditTemplate.Outfit, OutfitId: "bademode"), Adult).Reason.Should().Contain("aus der Liste");
        _policy.CheckRequest(new EditRequest(EditTemplate.Outfit, "rotes Kleid", "sommerkleid"), Adult).Reason.Should().Contain("keinen freien Text");
        _policy.CheckRequest(new EditRequest(EditTemplate.Outfit, OutfitId: "tracht"), Young).Reason.Should().Contain("jünger als 21");
        _policy.CheckRequest(new EditRequest(EditTemplate.Outfit, OutfitId: "tracht"), AgeUnknown).Reason.Should().Contain("nicht schätzen");
        _policy.CheckRequest(new EditRequest(EditTemplate.Outfit, OutfitId: "tracht"), EditSubject.Nobody).Allowed.Should().BeTrue("ohne Person keine Altersgrenze");
        _policy.CheckRequest(new EditRequest(EditTemplate.Remove, OutfitId: "tracht"), Adult).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Kleidungsliste_ist_alltagstauglich_und_besteht_die_Begriffsliste()
    {
        OutfitCatalog.All.Should().HaveCountGreaterThan(5);
        OutfitCatalog.All.Should().OnlyContain(o => o.Prompt.Contains("fully clothed"));
        OutfitCatalog.All.SelectMany(o => EditTerms.FindBlocked(o.Label + " " + o.Prompt)).Should().BeEmpty();
        EditTerms.FindBlocked(new EditPolicy().NegativePrompt).Should().NotBeEmpty("die Negativ-Beschreibung nennt genau diese Dinge");
    }

    private static EditContentScan Scan(float nsfw = 0, params (string Part, float Score)[] parts)
        => new(parts.ToDictionary(p => p.Part, p => p.Score), nsfw);

    [Fact]
    public void Ergebnis_mit_Person_wird_ab_den_Schwellen_verworfen()
    {
        _policy.CheckResult(Scan(), Adult).Keep.Should().BeTrue();
        _policy.CheckResult(Scan(0.79f), Adult).Keep.Should().BeTrue();
        _policy.CheckResult(Scan(0.8f), Adult).Keep.Should().BeFalse();
        _policy.CheckResult(Scan(0, ("FEMALE_BREAST_EXPOSED", 0.36f)), Adult).Keep.Should().BeFalse();
        _policy.CheckResult(Scan(0, ("FEMALE_BREAST_COVERED", 0.9f)), Adult).Keep.Should().BeTrue("bedeckt zählt nur bei jungen Personen");
        _policy.CheckResult(Scan(0, ("FEET_EXPOSED", 0.99f), ("BELLY_EXPOSED", 0.9f)), Adult).Keep.Should().BeTrue();
    }

    [Fact]
    public void Junge_oder_unbekannte_Personen_strenger_ohne_Gesicht_nur_deutliche_Nacktheit()
    {
        foreach (var subject in new[] { Young, AgeUnknown })
        {
            _policy.CheckResult(Scan(0.5f), subject).Keep.Should().BeFalse();
            _policy.CheckResult(Scan(0, ("BUTTOCKS_EXPOSED", 0.2f)), subject).Keep.Should().BeFalse();
            _policy.CheckResult(Scan(0, ("FEMALE_BREAST_COVERED", 0.5f)), subject).Keep.Should().BeFalse("Bademode/Unterwäsche");
            _policy.CheckResult(Scan(0.3f, ("FEMALE_BREAST_COVERED", 0.4f)), subject).Keep.Should().BeTrue();
        }

        _policy.CheckResult(Scan(0.95f), EditSubject.Nobody).Keep.Should().BeTrue("ohne Gesicht zählt der ViT allein nicht");
        _policy.CheckResult(Scan(0, ("FEMALE_GENITALIA_EXPOSED", 0.61f)), EditSubject.Nobody).Keep.Should().BeFalse();
    }

    [Fact]
    public void Personen_aus_Original_und_Ergebnis_zaehlen_zusammen()
    {
        EditSubject.Combine(Adult, EditSubject.Nobody).Should().Be(Adult, "Gesicht im Original zählt auch für das Ergebnis");
        EditSubject.Combine(new EditSubject(2, 40), new EditSubject(1, 19)).Should().Be(new EditSubject(2, 19));
        EditSubject.Combine(Adult, AgeUnknown).YoungestAge.Should().BeNull();
        EditSubject.Combine(EditSubject.Nobody, EditSubject.Nobody).Should().Be(EditSubject.Nobody);
        AgeUnknown.MayBeYoung.Should().BeTrue();
        new EditSubject(1, 21).MayBeYoung.Should().BeFalse();
    }

    [Fact]
    public void Kennzeichnung_und_XMP_im_JPEG()
    {
        var provenance = new EditProvenance(EditTemplate.Remove, @"diffusion_models/flux1-fill-dev-Q4_K_S.gguf", new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        provenance.Tools.Should().Be("ki:entfernen:flux1-fill-dev-Q4_K_S");
        provenance.Software.Should().Be("Lumora KI-Bearbeitung (Entfernen, flux1-fill-dev-Q4_K_S, 2026-10-30)");
        new EditProvenance(EditTemplate.Enhance, "", DateTimeOffset.UnixEpoch).Tools.Should().Be("ki:verbessern:unbekannt");

        // SOI, APP0 (JFIF), altes XMP, DQT, EOI
        var oldXmp = Segment(0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<alt/>"));
        byte[] jpeg = [0xFF, 0xD8, .. Segment(0xE0, Encoding.ASCII.GetBytes("JFIF\0")), .. oldXmp, .. Segment(0xDB, new byte[4]), 0xFF, 0xD9];

        var written = JpegXmp.Insert(jpeg, provenance.ToXmp());

        var xmp = JpegXmp.Read(written)!;
        xmp.Should().Contain(EditProvenance.DigitalSourceType).And.Contain("xmp:CreatorTool=\"Lumora KI-Bearbeitung (Entfernen");
        Encoding.ASCII.GetString(written).Should().NotContain("<alt/>", "altes XMP ersetzt");
        written[..2].Should().Equal(0xFF, 0xD8);
        written[^2..].Should().Equal(0xFF, 0xD9);
        Encoding.ASCII.GetString(written).IndexOf("JFIF", StringComparison.Ordinal).Should().BeLessThan(Encoding.ASCII.GetString(written).IndexOf("xap", StringComparison.Ordinal));
        var act = () => JpegXmp.Insert([1, 2, 3, 4], "x");
        act.Should().Throw<InvalidDataException>();
    }

    private static byte[] Segment(byte marker, byte[] data)
        => [0xFF, marker, (byte)((data.Length + 2) >> 8), (byte)((data.Length + 2) & 0xFF), .. data];

    /// <summary>
    /// Echte Prüfmodelle (nur mit LUMORA_KI_MODELS = Ordner mit nudenet-640m.onnx und vit-nsfw-fp16.onnx): laufen auf erzeugten
    /// Bildern ohne Personen und melden dort nichts. Schwellen selbst sind oben mit festen Werten geprüft.
    /// </summary>
    [Fact]
    public void Echte_Pruefmodelle_laufen_und_melden_auf_neutralen_Bildern_nichts()
    {
        var folder = Environment.GetEnvironmentVariable("LUMORA_KI_MODELS");
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, EditModelFiles.NudeNet)))
        {
            return; // ohne Container übersprungen
        }

        using var scanner = new OnnxEditContentScanner(Path.Combine(folder, EditModelFiles.NudeNet), Path.Combine(folder, EditModelFiles.Vit));
        foreach (var image in new[] { Gradient(800, 600), Gradient(300, 900) })
        {
            var scan = scanner.Scan(image);
            scan.Parts.Should().HaveCount(18);
            EditPolicy.ExposedParts.Max(scan.Part).Should().BeLessThan(EditPolicy.YoungExposedThreshold);
            scan.NsfwProbability.Should().BeInRange(0, 1);
            new EditPolicy().CheckResult(scan, AgeUnknown).Keep.Should().BeTrue();
        }
    }

    /// <summary>genderage (nur mit LUMORA_GENDERAGE_MODEL): Ein- und Ausgabe passen, die Schätzung ist eine Zahl oder null.</summary>
    [Fact]
    public void Altersmodell_laeuft_auf_einem_erzeugten_Ausschnitt()
    {
        var model = Environment.GetEnvironmentVariable("LUMORA_GENDERAGE_MODEL");
        if (string.IsNullOrEmpty(model) || !File.Exists(model))
        {
            return;
        }

        using var estimator = new OnnxAgeEstimator(model);
        var age = estimator.EstimateAge(Gradient(400, 400), new FaceDetection(150, 120, 250, 260, 0.9f, []));
        if (age is { } value)
        {
            value.Should().BeInRange(0, 120);
        }

        estimator.EstimateAge(Gradient(10, 10), new FaceDetection(5, 5, 5, 5, 0.9f, [])).Should().BeNull("leere Box");
    }

    private static FaceImage Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                pixels[i] = (byte)(x * 255 / width);
                pixels[i + 1] = (byte)(y * 255 / height);
                pixels[i + 2] = 128;
            }
        }

        return new FaceImage(pixels, width, height);
    }
}
