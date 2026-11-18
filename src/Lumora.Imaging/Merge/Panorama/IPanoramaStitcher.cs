using Lumora.Imaging.Protocol;
using Lumora.Imaging.Worker;

namespace Lumora.Imaging.Merge.Panorama;

/// <summary>
/// Panorama-Werkzeug in zwei Phasen (ADR-0078 Punkt 3; Abnahme 2026-11-18 Nr. 2: eigene Kette auf OpenCV-Bausteinen, Hugin
/// folgt in Stufe 3 als zweite Umsetzung). Phase 1 richtet einmal aus (<see cref="IPanoramaStitchSession.EstimateAsync"/>),
/// Phase 2 setzt mit wechselnder Aufnahmeart beliebig oft zusammen (<see cref="IPanoramaStitchSession.ComposeAsync"/>); volle
/// Auflösung rechnet <see cref="RenderAsync"/> aus dem Rezept ohne neues Ausrichten. Gerechnet wird im Hilfsprozess; das
/// Ergebnis ist eine JPEG-Datei im Temp-Ordner des Katalogs (statt eines Pixelpuffers im Lumora-Prozess).
/// </summary>
public interface IPanoramaStitcher
{
    /// <summary>Vorschau-Sitzung: ein Hilfsprozess, der die gelesenen Quellen und die Ausrichtung im Speicher hält.</summary>
    Task<IPanoramaStitchSession> OpenSessionAsync(int threads, CancellationToken ct);

    /// <summary>Volle Auflösung (bzw. größte passende Stufe) in einem frischen Hilfsprozess.</summary>
    Task<ComposeResult> RenderAsync(RenderRequestData request, Action<ProgressMessage>? progress, int threads, Action<ImagingWorkerProcess>? started, CancellationToken ct);
}

/// <summary>Daten für <see cref="IPanoramaStitcher.RenderAsync"/>.</summary>
public sealed record RenderRequestData(
    IReadOnlyList<SourceSpec> Sources,
    AlignmentSpec Alignment,
    ComposeSpec Settings,
    string OutputPath,
    int JpegQuality,
    long MaxBytes);

/// <summary>Eine Vorschau-Sitzung des Panorama-Werkzeugs.</summary>
public interface IPanoramaStitchSession : IAsyncDisposable
{
    /// <summary>false, wenn der Hilfsprozess nicht mehr läuft (Absturz, beendet) — dann neu öffnen.</summary>
    bool IsAlive { get; }

    Task<EstimateResult> EstimateAsync(IReadOnlyList<SourceSpec> sources, int decodeEdge, int seed, Action<ProgressMessage>? progress, CancellationToken ct);

    /// <param name="framePath">Bühne: ungeschnittenes Panorama mit Zuschnitt-Rahmen hierhin (nur „Gerade Kanten“); null = nicht.</param>
    Task<ComposeResult> ComposeAsync(ComposeSpec settings, string outputPath, int jpegQuality, Action<ProgressMessage>? progress, CancellationToken ct, string? framePath = null);

    void SetThreads(int threads);
}

/// <summary>Umsetzung über den Hilfsprozess <c>Lumora.Imaging.Worker</c>.</summary>
public sealed class WorkerPanoramaStitcher(IImagingWorkerHost host) : IPanoramaStitcher
{
    public async Task<IPanoramaStitchSession> OpenSessionAsync(int threads, CancellationToken ct)
        => new Session(await host.StartAsync(threads, ct).ConfigureAwait(false));

    public async Task<ComposeResult> RenderAsync(RenderRequestData request, Action<ProgressMessage>? progress, int threads, Action<ImagingWorkerProcess>? started, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var worker = await host.StartAsync(threads, ct).ConfigureAwait(false);
        started?.Invoke(worker);
        return await worker.RunAsync<ComposeResult>(
            id => new RenderRequest
            {
                Id = id,
                Sources = request.Sources,
                Alignment = request.Alignment,
                Settings = request.Settings,
                OutputPath = request.OutputPath,
                JpegQuality = request.JpegQuality,
                MaxBytes = request.MaxBytes,
            },
            progress,
            ct).ConfigureAwait(false);
    }

    private sealed class Session(ImagingWorkerProcess worker) : IPanoramaStitchSession
    {
        public bool IsAlive => !worker.HasExited;

        public Task<EstimateResult> EstimateAsync(IReadOnlyList<SourceSpec> sources, int decodeEdge, int seed, Action<ProgressMessage>? progress, CancellationToken ct)
            => worker.RunAsync<EstimateResult>(id => new EstimateRequest { Id = id, Sources = sources, DecodeEdge = decodeEdge, Seed = seed }, progress, ct);

        public Task<ComposeResult> ComposeAsync(ComposeSpec settings, string outputPath, int jpegQuality, Action<ProgressMessage>? progress, CancellationToken ct, string? framePath = null)
            => worker.RunAsync<ComposeResult>(id => new ComposeRequest { Id = id, Settings = settings, OutputPath = outputPath, JpegQuality = jpegQuality, FramePath = framePath }, progress, ct);

        public void SetThreads(int threads) => worker.SetThreads(threads);

        public ValueTask DisposeAsync() => worker.DisposeAsync();
    }
}
