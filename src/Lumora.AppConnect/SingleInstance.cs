using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Lumora.AppConnect;

/// <summary>
/// Eine Instanz je App und Benutzer (Abschnitt 9): benannter Mutex <c>Local\AppVerbindung.lumora</c>. Ein zweiter Start
/// schickt über die Pipe <c>AppVerbindung.lumora</c> die Nachricht <c>{"action":"showWindow"}</c> und beendet sich; die
/// laufende Instanz zeigt ihr Fenster im selben Prozess. Pipe nur für den eigenen Benutzer.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Aktion für „Fenster zeigen“ (Abschnitt 9).</summary>
    public const string ShowWindowAction = "showWindow";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    public static string MutexName(string appId) => @"Local\AppVerbindung." + appId;

    public static string PipeName(string appId) => "AppVerbindung." + appId;

    /// <summary>Die eigene Instanz, oder null, wenn schon eine läuft.</summary>
    public static SingleInstance? TryAcquire(string appId = AppConnectContract.AppId)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName(appId), out var created);
        if (created)
        {
            return new SingleInstance(mutex, PipeName(appId));
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Bittet die laufende Instanz, ihr Fenster zu zeigen; false, wenn sie nicht erreichbar ist.</summary>
    public static bool SendShowWindow(string appId = AppConnectContract.AppId, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(appId), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            client.Write(Encoding.UTF8.GetBytes("{\"action\":\"" + ShowWindowAction + "\"}\n"));
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Nimmt Nachrichten weiterer Starts entgegen (eigener Thread) und meldet die Aktion.</summary>
    public void Listen(Action<string> onAction) => _ = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                if (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line && ParseAction(line) is { } action)
                {
                    onAction(action);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gegenseite weg oder Pipe belegt — kurz warten, damit ein dauerhafter Fehler keine Schleife unter Volllast wird
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    });

    /// <summary>Feld <c>action</c> einer Nachricht; null bei ungültigem Inhalt.</summary>
    internal static string? ParseAction(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("action", out var action)
                   && action.ValueKind == JsonValueKind.String
                ? action.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // nicht vom eigenen Thread gehalten (der Mutex verfällt mit dem Prozess)
        }

        _mutex.Dispose();
        _cts.Dispose();
    }
}
