using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumora.Imaging.Protocol;

/// <summary>
/// Zeilen-Protokoll des Hilfsprozesses: jede Nachricht ist eine Zeile „@lumora {json}“. Zeilen ohne Kennung (z. B. Meldungen
/// nativer Bibliotheken auf stdout) werden überlesen — so kann fremde Ausgabe das Protokoll nicht stören.
/// </summary>
public static class WorkerProtocol
{
    /// <summary>Version des Protokolls; der Client verweigert einen Hilfsprozess mit anderer Version.</summary>
    public const int Version = 1;

    /// <summary>Kennung am Zeilenanfang.</summary>
    public const string Prefix = "@lumora ";

    /// <summary>Name der Programmdatei des Hilfsprozesses (neben Lumora.exe).</summary>
    public const string WorkerExeName = "Lumora.Imaging.Worker.exe";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Nachricht als Protokollzeile (ohne Zeilenende).</summary>
    public static string Encode(WorkerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Prefix + JsonSerializer.Serialize(message, Options);
    }

    /// <summary>Protokollzeile lesen; null bei fremder Ausgabe oder unlesbarem JSON.</summary>
    public static WorkerMessage? Decode(string? line)
    {
        if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkerMessage>(line.AsSpan(Prefix.Length), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
