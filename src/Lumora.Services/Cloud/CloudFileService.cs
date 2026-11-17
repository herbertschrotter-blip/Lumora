using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Messaging;

namespace Lumora.Services.Cloud;

/// <inheritdoc cref="ICloudFileService"/>
public sealed class CloudFileService : ICloudFileService, IDisposable
{
    private readonly ICloudFileSystem _fileSystem;
    private readonly ICloudSyncRoots _roots;
    private readonly IMessenger _messenger;
    private readonly ILogger<CloudFileService> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _watchLock = new();
    private readonly Dictionary<int, Watched> _watched = new();
    private Task? _watchLoop;

    private sealed record Watched(CloudFileRef File, CloudPinAction Action, DateTime DeadlineUtc);

    public CloudFileService(
        ILogger<CloudFileService> logger,
        ICloudFileSystem? fileSystem = null,
        ICloudSyncRoots? roots = null,
        IMessenger? messenger = null)
    {
        _logger = logger;
        _fileSystem = fileSystem ?? new WindowsCloudFileSystem();
        _roots = roots ?? CloudSyncRoots.FromSystem();
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    /// <summary>Wie oft beobachtete Dateien geprüft werden.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Wie lange nach „Immer behalten“ auf den Download gewartet wird (große Videos, langsame Leitung).</summary>
    public TimeSpan KeepTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Wie lange nach „Speicherplatz freigeben“ auf das Freigeben gewartet wird.</summary>
    public TimeSpan FreeTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public CloudFileState GetState(string path)
        => _fileSystem.TryGetAttributes(path) is { } attributes
            ? CloudFiles.Classify(attributes, _roots.Contains(path))
            : CloudFileState.NotCloud;

    public bool MayBeCloud(string path) => _roots.Contains(path);

    public CloudPinResult SetPinState(IReadOnlyList<CloudFileRef> files, CloudPinAction action)
    {
        ArgumentNullException.ThrowIfNull(files);
        var keep = action == CloudPinAction.KeepOnDevice;
        int changed = 0, notCloud = 0, failed = 0;
        var watch = new List<CloudFileRef>();
        foreach (var file in files)
        {
            if (_fileSystem.TryGetAttributes(file.Path) is not { } attributes
                || (attributes & FileAttributes.Directory) != 0
                || !CloudFiles.Classify(attributes, _roots.Contains(file.Path)).IsCloud)
            {
                notCloud++;
                continue;
            }

            try
            {
                _fileSystem.SetAttributes(file.Path, CloudFiles.WithPin(attributes, keep));
                changed++;
                watch.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failed++;
                _logger.LogDebug(ex, "Cloud: Attribut für Datei {FileId} abgelehnt", file.FileId);
            }
        }

        _logger.LogInformation(
            "Cloud: {Action} für {Changed} Dateien gesetzt ({NotCloud} nicht in einem Cloud-Ordner, {Failed} abgelehnt)",
            keep ? "Immer behalten" : "Speicherplatz freigeben", changed, notCloud, failed);
        if (watch.Count > 0)
        {
            Watch(watch, action);
        }

        return new CloudPinResult(action, changed, notCloud, failed);
    }

    public void NotifyBecameLocal(CloudFileRef file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Send(file.LibraryId, [file.FileId], becameLocal: true);
    }

    /// <summary>
    /// Beobachtet die Dateien, bis Windows sie geladen (behalten) bzw. freigegeben hat, und meldet sie dann. Eine erneute
    /// Aktion für dieselbe Datei ersetzt die alte Beobachtung.
    /// </summary>
    private void Watch(IReadOnlyList<CloudFileRef> files, CloudPinAction action)
    {
        var deadline = DateTime.UtcNow + (action == CloudPinAction.KeepOnDevice ? KeepTimeout : FreeTimeout);
        lock (_watchLock)
        {
            foreach (var file in files)
            {
                _watched[file.FileId] = new Watched(file, action, deadline);
            }

            if (_watchLoop is null || _watchLoop.IsCompleted)
            {
                _watchLoop = Task.Run(WatchLoopAsync);
            }
        }
    }

    private async Task WatchLoopAsync()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                List<Watched> pending;
                lock (_watchLock)
                {
                    if (_watched.Count == 0)
                    {
                        _watchLoop = null;
                        return;
                    }

                    pending = [.. _watched.Values];
                }

                var reached = new List<Watched>();
                var expired = new List<Watched>();
                var now = DateTime.UtcNow;
                foreach (var item in pending)
                {
                    var attributes = _fileSystem.TryGetAttributes(item.File.Path);
                    var done = attributes is { } a && (item.Action == CloudPinAction.KeepOnDevice ? !CloudFiles.IsOnlineOnly(a) : CloudFiles.IsOnlineOnly(a));
                    if (done)
                    {
                        reached.Add(item);
                    }
                    else if (attributes is null || now >= item.DeadlineUtc)
                    {
                        expired.Add(item);
                    }
                }

                lock (_watchLock)
                {
                    foreach (var item in reached.Concat(expired))
                    {
                        if (_watched.TryGetValue(item.File.FileId, out var current) && ReferenceEquals(current, item))
                        {
                            _watched.Remove(item.File.FileId);
                        }
                    }
                }

                foreach (var group in reached.GroupBy(r => (r.File.LibraryId, r.Action)))
                {
                    Send(group.Key.LibraryId, group.Select(r => r.File.FileId).ToList(), group.Key.Action == CloudPinAction.KeepOnDevice);
                }

                if (expired.Count > 0)
                {
                    _logger.LogInformation("Cloud: {Count} Dateien haben den Zielzustand nicht rechtzeitig erreicht (Beobachtung beendet)", expired.Count);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Beenden
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud: Beobachtung abgebrochen");
            lock (_watchLock)
            {
                _watchLoop = null;
            }
        }
    }

    private void Send(int libraryId, IReadOnlyList<int> fileIds, bool becameLocal)
    {
        try
        {
            _messenger.Send(new CloudFilesChangedMessage(libraryId, fileIds, becameLocal));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der CloudFilesChangedMessage warf eine Ausnahme");
        }
    }

    /// <summary>Beendet die Beobachtung. Die Quelle wird nicht entsorgt: eine gerade startende Schleife liest noch ihr Token.</summary>
    public void Dispose() => _stop.Cancel();
}
