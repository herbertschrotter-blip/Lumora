using Microsoft.Extensions.Logging;
using Lumora.Services.Infrastructure;

namespace Lumora.Imaging.Worker;

/// <summary>
/// Startet Hilfsprozesse für den aktiven Katalog und lässt höchstens EINEN schweren Lauf zur Zeit zu (ADR-0078 Punkt 2/12 —
/// Vorschau und Speichern teilen sich Speicher und Kerne). Temp-Ordner der Prozesse: <c>TempRoot\merge</c> des Katalogs.
/// </summary>
public interface IImagingWorkerHost
{
    /// <summary>Arbeitsordner der Hilfsprozesse (im Temp-Ordner des Katalogs, in der versteckten Sitzung im Container).</summary>
    string WorkRoot { get; }

    /// <summary>true, wenn die Programmdatei des Hilfsprozesses vorhanden ist (Paket vollständig).</summary>
    bool IsInstalled { get; }

    Task<ImagingWorkerProcess> StartAsync(int threads, CancellationToken ct);

    /// <summary>Ein schwerer Lauf; wartet, solange ein anderer läuft. Dispose gibt frei.</summary>
    Task<IDisposable> EnterHeavyAsync(CancellationToken ct);

    /// <summary>true, solange ein schwerer Lauf läuft.</summary>
    bool IsBusy { get; }
}

/// <inheritdoc />
public sealed class ImagingWorkerHost(ICatalogStoragePaths storage, ImagingWorkerOptions options, ILogger<ImagingWorkerHost>? logger = null) : IImagingWorkerHost
{
    private readonly SemaphoreSlim _heavy = new(1, 1);

    public string WorkRoot => Path.Combine(storage.TempRoot, "merge");

    public bool IsInstalled => File.Exists(options.ResolveExePath());

    public bool IsBusy => _heavy.CurrentCount == 0;

    public Task<ImagingWorkerProcess> StartAsync(int threads, CancellationToken ct)
        => ImagingWorkerProcess.StartAsync(options, WorkRoot, threads, logger, ct);

    public async Task<IDisposable> EnterHeavyAsync(CancellationToken ct)
    {
        await _heavy.WaitAsync(ct).ConfigureAwait(false);
        return new Release(_heavy);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
