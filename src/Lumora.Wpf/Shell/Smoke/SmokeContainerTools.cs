using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Services.HiddenLibraries;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Container-Werkzeuge im Rauchtest (ADR-0049): hängt nie etwas ein oder aus, merkt sich nur die Aufrufe — der Schritt
/// „Versteckte Bibliothek“ prüft, dass Zusperren das Aushängen anstößt.
/// </summary>
internal sealed class SmokeContainerTools : IContainerTools
{
    private readonly List<string> _dismounted = new();

    public bool IsVeraCryptInstalled => false;

    /// <summary>Laufwerkswurzeln, für die „Aushängen“ angestoßen wurde.</summary>
    public IReadOnlyList<string> Dismounted
    {
        get
        {
            lock (_dismounted)
            {
                return _dismounted.ToArray();
            }
        }
    }

    public ContainerKind Detect(string driveRoot) => ContainerKind.Unknown;

    public Task<ContainerCommandResult> DismountAsync(string driveRoot, CancellationToken ct = default)
    {
        lock (_dismounted)
        {
            _dismounted.Add(driveRoot);
        }

        return Task.FromResult(new ContainerCommandResult(true, "Rauchtest: Aushängen simuliert."));
    }

    public Task<ContainerCommandResult> MountVeraCryptAsync(string containerFile, char driveLetter, CancellationToken ct = default)
        => Task.FromResult(new ContainerCommandResult(false, "Rauchtest: kein VeraCrypt."));

    public Task<ContainerCommandResult> MountVirtualDiskAsync(string imageFile, CancellationToken ct = default)
        => Task.FromResult(new ContainerCommandResult(false, "Rauchtest: nichts einhängen."));
}
