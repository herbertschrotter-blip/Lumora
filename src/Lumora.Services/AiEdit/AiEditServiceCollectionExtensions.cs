using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;

namespace Lumora.Services.AiEdit;

/// <summary>
/// DI der KI-Bearbeitung (ADR-0076 Punkt 1) — nur für den Container einer versteckten Sitzung aufrufen. Die normale Sitzung
/// bekommt keinen Dienst, keine Einstellung und keine Protokollzeile davon.
/// </summary>
public static class AiEditServiceCollectionExtensions
{
    /// <param name="containerRoot">Wurzel des geöffneten Containers („X:\“), null = keiner.</param>
    /// <param name="launcher">Prozess-Starter; null = Windows mit Job-Objekt (Tests/Rauchtest: Attrappe).</param>
    public static IServiceCollection AddAiEdit(this IServiceCollection services, Func<string?> containerRoot, IComfyProcessLauncher? launcher = null)
    {
        services.TryAddSingleton<IAiEditSettingsStore>(sp => new AiEditSettingsStore(sp.GetRequiredService<ILibraryContextFactory>()));
        services.TryAddSingleton<IComfyRuntime>(sp =>
        {
            var store = sp.GetRequiredService<IAiEditSettingsStore>();
            return new ComfyRuntime(
                containerRoot,
                launcher ?? new ComfyProcessLauncher(),
                sp.GetRequiredService<ILogger<ComfyRuntime>>(),
                loadIdleMinutes: async ct => (await store.LoadAsync(ct).ConfigureAwait(false)).IdleMinutes);
        });
        // Grenzen (ADR-0076 Punkt 8, Stufe 2): Prüfmodelle aus dem Container, buffalo_l auf dem Prozessor
        services.TryAddSingleton<Edit.IEditPolicy, Edit.EditPolicy>();
        services.TryAddSingleton<Edit.IEditResultChecker>(sp =>
        {
            string? Model(string file) => containerRoot() is { } root
                && Path.Combine(new AiEditLayout(root).ModelsRoot, Edit.EditModelFiles.Folder, file) is var path
                && File.Exists(path) ? path : null;
            return new Edit.EditResultChecker(
                sp.GetRequiredService<Edit.IEditPolicy>(),
                sp.GetRequiredService<Faces.IFaceImageLoader>(),
                sp.GetRequiredService<Faces.IFaceAnalyzerFactory>(),
                () => Model(Edit.EditModelFiles.NudeNet) is { } nude && Model(Edit.EditModelFiles.Vit) is { } vit
                    ? new Edit.Onnx.OnnxEditContentScanner(nude, vit)
                    : null,
                () => Model(Edit.EditModelFiles.GenderAge) is { } age ? new Edit.Onnx.OnnxAgeEstimator(age) : new Edit.Onnx.UnknownAgeEstimator(),
                sp.GetRequiredService<ILogger<Edit.EditResultChecker>>());
        });
        services.AddHostedService<AiEditSessionHook>();
        return services;
    }
}

/// <summary>
/// Hängt die Laufzeit an die Sitzung: beim Anhalten der Sitzung (Zusperren, Wechsel, Beenden) ComfyUI beenden und die
/// Arbeitsordner leeren — vor dem Aushängen. Beim Start der Sitzung tut er nichts: Hosted Services starten vor der Migration
/// des Katalogs, die Leerlauf-Zeit liest die Laufzeit erst beim Start von ComfyUI.
/// </summary>
internal sealed class AiEditSessionHook(IComfyRuntime runtime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Die Zeitgrenze des Hosts gilt hier nicht: ComfyUI muss vor dem Aushängen zu sein (Dispose der Laufzeit fängt den Rest).
    public Task StopAsync(CancellationToken cancellationToken) => runtime.StopAsync("Sitzung endet");
}
