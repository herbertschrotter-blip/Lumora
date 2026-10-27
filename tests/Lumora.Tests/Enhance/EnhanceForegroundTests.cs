using Lumora.Services.Background;
using Lumora.Services.Enhance;

namespace Lumora.Tests.Enhance;

/// <summary>Foto verbessern hat Vorrang vor den Hintergrund-Aufgaben (ADR-0074 Nachtrag 0.5.12).</summary>
public sealed class EnhanceForegroundTests
{
    [Fact]
    public async Task Vorschau_meldet_sich_als_Vordergrund_Arbeit_an_und_danach_wieder_ab()
    {
        await using var w = await EnhanceWorld.CreateAsync(["Garten.jpg"]);
        var id = await w.IdAsync("Garten.jpg");
        var seen = new List<ForegroundNeed>();
        w.Foreground.Changed += () => seen.Add(w.Foreground.Need);

        var preview = await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true, UpscaleFactor = 2 });
        w.Service.Discard(preview);

        seen.Should().Equal(ForegroundNeed.Gpu | ForegroundNeed.Cpu, ForegroundNeed.None);
        w.Foreground.What.Should().BeNull();
    }

    [Fact]
    public async Task Stapel_haelt_den_Vorrang_ueber_alle_Fotos_und_gibt_ihn_am_Ende_frei()
    {
        await using var w = await EnhanceWorld.CreateAsync(["a.jpg", "b.jpg"]);
        var ids = new[] { await w.IdAsync("a.jpg"), await w.IdAsync("b.jpg") };
        using var planner = new BackgroundPlanner(foreground: w.Foreground);
        var seen = new List<ForegroundNeed>();
        Task<IDisposable>? faces = null;
        bool? facesWaitedDuringBatch = null;
        w.Foreground.Changed += () =>
        {
            seen.Add(w.Foreground.Need);
            if (faces is null)
            {
                faces = planner.AcquireAsync(BackgroundJob.Faces, CancellationToken.None);
                facesWaitedDuringBatch = !faces.IsCompleted;
            }
        };

        await w.Service.RunBatchAsync(ids, new EnhanceOptions { Upscale = true, UpscaleFactor = 2 });

        facesWaitedDuringBatch.Should().BeTrue("Gesichter warten, solange verbessert wird");
        seen.Should().NotBeEmpty();
        seen[..^1].Should().NotContain(ForegroundNeed.None, "zwischen zwei Fotos rutscht keine Hintergrund-Aufgabe dazwischen");
        seen[^1].Should().Be(ForegroundNeed.None);
        faces!.IsCompleted.Should().BeTrue("nach dem Stapel laufen Gesichter weiter");
    }
}
