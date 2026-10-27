using System.Text.Json;

namespace Lumora.AppConnect;

/// <summary>
/// Schalter oben im Reiter „Verbundene Apps“ (Mockup 11, Stufe 6). Lumora-eigen, nicht Teil des Vertrags; liegen neben den
/// Kopplungen in <c>appconnect\settings.json</c>, weil ein Hintergrundstart sie prüft, bevor der Katalog offen ist.
/// </summary>
/// <param name="Enabled">„Verbindungen erlauben“ — aus: Lumora antwortet keiner App und fragt keine (keine Schnittstelle).</param>
/// <param name="AllowBackgroundStart">„Andere Apps dürfen Lumora im Hintergrund starten“ (zusätzlich zum Recht je App).</param>
/// <param name="TrayIcon">„Symbol im Infobereich zeigen, solange Lumora im Hintergrund läuft“ (Standard aus, Abschnitt 9).</param>
public sealed record AppConnectSettings(bool Enabled = true, bool AllowBackgroundStart = true, bool TrayIcon = false);

/// <summary>Liest und schreibt <see cref="AppConnectSettings"/> (atomar); eine fehlende oder kaputte Datei gilt als Standard.</summary>
public sealed class AppConnectSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();
    private AppConnectSettings _current;

    public AppConnectSettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    /// <summary>Ein Schalter hat sich geändert.</summary>
    public event EventHandler? Changed;

    public AppConnectSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public void Update(Func<AppConnectSettings, AppConnectSettings> change)
    {
        lock (_lock)
        {
            var next = change(_current);
            if (next == _current)
            {
                return;
            }

            _current = next;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, _path, overwrite: true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static AppConnectSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppConnectSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }
}
