using System.Buffers.Binary;
using Lumora.Services.Covers;

namespace Lumora.Tests.Covers;

/// <summary>Güte eines Gesichts als Titelbild (ADR-0068): Frontalität aus den Landmarken, Foto-Güte, neutrale Werte.</summary>
public sealed class FaceCoverScoringTests
{
    /// <summary>10 float32: Auge links, Auge rechts, Nase, Mundwinkel (nur die x-Werte der ersten drei zählen).</summary>
    public static byte[] Landmarks(float leftEyeX, float rightEyeX, float noseX)
    {
        var bytes = new byte[40];
        void Put(int index, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4, 4), value);
        Put(0, leftEyeX);
        Put(1, 0.4f);
        Put(2, rightEyeX);
        Put(3, 0.4f);
        Put(4, noseX);
        Put(5, 0.55f);
        Put(6, 0.4f);
        Put(7, 0.7f);
        Put(8, 0.6f);
        Put(9, 0.7f);
        return bytes;
    }

    [Fact]
    public void Frontal_ist_eins_Profil_ist_klein_ohne_Landmarken_neutral()
    {
        FaceCoverScoring.Frontality(Landmarks(0.35f, 0.65f, 0.50f)).Should().BeApproximately(1, 1e-6);
        FaceCoverScoring.Frontality(Landmarks(0.35f, 0.65f, 0.64f)).Should().BeLessThan(0.2);
        FaceCoverScoring.Frontality(null).Should().Be(0.5);
        FaceCoverScoring.Frontality(new byte[40]).Should().Be(0.5, "alles 0 = nicht lesbar");
    }

    [Fact]
    public void Foto_Guete_zaehlt_mit_und_fehlende_Werte_sind_neutral()
    {
        var frontal = Landmarks(0.35f, 0.65f, 0.50f);
        var sharp = FaceCoverScoring.Score(0.8, frontal, 0.9, 0.9, 0.9);
        var blurry = FaceCoverScoring.Score(0.8, frontal, 0.1, 0.2, 0.1);
        var unknown = FaceCoverScoring.Score(0.8, frontal, null, null, null);

        sharp.Should().BeGreaterThan(unknown).And.BeGreaterThan(blurry);
        unknown.Should().BeGreaterThan(blurry);
        FaceCoverScoring.Score(double.NaN, null, null, null, null).Should().BeInRange(0, 1);
    }
}
