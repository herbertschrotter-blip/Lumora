using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Enhance;
using Lumora.Services.ModelPackages;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Ersatz für „Foto verbessern“ (ADR-0071): Hochskalieren gilt als bereit und rechnet als Fake (nächster Nachbar ×
/// Faktor) — kein echtes Modell, kein Download. Die übrigen Werkzeuge folgen dem echten Update-Manager (dort „Modell fehlt“).
/// </summary>
internal static class SmokeEnhance
{
    public static void Register(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IEnhanceEngineFactory, SmokeEnhanceEngineFactory>());
        services.Replace(ServiceDescriptor.Singleton<IEnhanceModelAccess>(sp => new SmokeEnhanceModelAccess(new EnhanceModelAccess(sp.GetRequiredService<IModelPackageManager>()))));
    }
}

/// <summary>Hochskalieren „geladen“, Rest wie der echte Manager.</summary>
internal sealed class SmokeEnhanceModelAccess(IEnhanceModelAccess inner) : IEnhanceModelAccess
{
    public Task<EnhanceModelState> GetStateAsync(EnhanceTool tool, CancellationToken ct)
        => tool == EnhanceTool.Upscale ? Task.FromResult(EnhanceModelState.Ready) : inner.GetStateAsync(tool, ct);

    public string? GetModelPath(EnhanceTool tool) => tool == EnhanceTool.Upscale ? "rauchtest-fake.onnx" : inner.GetModelPath(tool);
}

/// <summary>Fake-Modelle: kleine Bildoperationen statt ONNX.</summary>
internal sealed class SmokeEnhanceEngineFactory : IEnhanceEngineFactory
{
    public int Created { get; private set; }

    public IEnhanceEngine Create(EnhanceTool tool, string modelPath, bool useGpu)
    {
        Created++;
        return new SmokeEnhanceEngine(tool);
    }
}

/// <summary>Hochskalieren = nächster Nachbar; andere Werkzeuge geben das Bild unverändert zurück.</summary>
internal sealed class SmokeEnhanceEngine(EnhanceTool tool) : IEnhanceEngine
{
    public EnhanceTool Tool => tool;

    public string ExecutionProvider => "Fake";

    public EnhanceImage Process(EnhanceStepInput input, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (tool != EnhanceTool.Upscale)
        {
            return input.Image.Clone();
        }

        var src = input.Image;
        var f = Math.Max(1, input.Factor);
        var dst = EnhanceImage.Blank(src.Width * f, src.Height * f);
        for (var y = 0; y < dst.Height; y++)
        {
            for (var x = 0; x < dst.Width; x++)
            {
                Buffer.BlockCopy(src.Pixels, ((y / f) * src.Width + (x / f)) * 3, dst.Pixels, (y * dst.Width + x) * 3, 3);
            }
        }

        progress?.Report(1);
        return dst;
    }

    public void Dispose()
    {
    }
}
