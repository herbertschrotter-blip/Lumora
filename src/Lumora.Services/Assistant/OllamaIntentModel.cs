using Microsoft.Extensions.Logging;
using Lumora.Services.Infrastructure;

namespace Lumora.Services.Assistant;

/// <summary>Zustand von Ollama und Modell (Einstellungen › Assistent, Hinweis in der Leiste).</summary>
public enum AssistantRuntimeState
{
    /// <summary>Ollama ist nicht installiert (keine <c>ollama.exe</c>) und antwortet nicht.</summary>
    NotInstalled = 0,

    /// <summary>Installiert, aber der Dienst antwortet nicht.</summary>
    NotRunning = 1,

    /// <summary>Dienst läuft, das gewählte Modell fehlt.</summary>
    ModelMissing = 2,

    Ready = 3,
}

/// <summary>Momentaufnahme für die Oberfläche.</summary>
public sealed record AssistantRuntimeStatus(AssistantRuntimeState State, string Model, string? Version = null)
{
    public bool IsReady => State == AssistantRuntimeState.Ready;

    public string Text => State switch
    {
        AssistantRuntimeState.NotInstalled => "Ollama ist nicht installiert.",
        AssistantRuntimeState.NotRunning => "Ollama ist installiert, läuft aber nicht.",
        AssistantRuntimeState.ModelMissing => $"Das Sprachmodell „{Model}“ ist noch nicht heruntergeladen.",
        _ => $"Bereit — Ollama {Version} mit „{Model}“.",
    };
}

/// <summary>Ollama-Zustand, Modell-Download und Start (Einstellungen › Assistent).</summary>
public interface IAssistantRuntime
{
    Task<AssistantRuntimeStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Modell herunterladen — NUR nach Bestätigung in der Oberfläche.</summary>
    Task PullModelAsync(string model, IProgress<OllamaPullProgress>? progress, CancellationToken ct);

    /// <summary>Ollama starten (installiert, läuft nicht); false, wenn nicht möglich.</summary>
    bool TryStartOllama();
}

/// <summary>
/// Sprach-KI über Ollama (ADR-0063): Chat mit JSON-Schema, Temperatur 0, Zeitlimit, <c>keep_alive</c> kurz (danach entlädt
/// Ollama das Modell). Ollama ist ein fremder Prozess — solange eine Anfrage läuft, hält der Assistent die gemeinsame
/// Grafikkarten-Sperre (<see cref="IGpuGate"/>), damit Gesichts- und Motiv-Erkennung zwischen zwei Fotos pausieren statt
/// gleichzeitig zu rechnen. Mit „Grafikkarte nutzen“ aus rechnet Ollama nur auf der CPU (ohne Sperre). Seit 0.5.12 meldet
/// er die Anfrage als Vordergrund-Arbeit an (<see cref="Background.IForegroundWork"/>): mit Grafikkarte warten Gesichter und
/// Motive, auf dem Prozessor alle Hintergrund-Aufgaben. Protokolliert nie den Satz, nur Dauer und Erfolg.
/// </summary>
public sealed class OllamaIntentModel : IIntentModel, IAssistantRuntime
{
    /// <summary>Zeitlimit einer Anfrage (inklusive Laden des Modells beim ersten Mal).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>So lange bleibt das Modell nach einer Anfrage geladen (Folgesätze ohne Ladezeit), dann gibt Ollama den Speicher frei.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(60);

    private readonly OllamaClient _client;
    private readonly IAssistantSettingsStore _settings;
    private readonly IGpuGate? _gpu;
    private readonly Background.IForegroundWork? _foreground;
    private readonly IOllamaLocator _locator;
    private readonly ILogger<OllamaIntentModel> _logger;

    public OllamaIntentModel(OllamaClient client, IAssistantSettingsStore settings, ILogger<OllamaIntentModel> logger, IGpuGate? gpu = null,
        IOllamaLocator? locator = null, Background.IForegroundWork? foreground = null)
    {
        _client = client;
        _settings = settings;
        _logger = logger;
        _gpu = gpu;
        _foreground = foreground;
        _locator = locator ?? new OllamaLocator();
    }

    public async Task<IntentModelResponse> InterpretAsync(IntentPrompt prompt, CancellationToken ct)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var started = System.Diagnostics.Stopwatch.StartNew();
        IDisposable? lease = null;
        using var priority = _foreground?.Begin(settings.UseGpu ? Background.ForegroundNeed.Gpu : Background.ForegroundNeed.Cpu, "Assistent");
        try
        {
            if (settings.UseGpu && _gpu is not null)
            {
                lease = await _gpu.AcquireAsync(ct).ConfigureAwait(false);
            }

            var result = await _client.ChatJsonAsync(
                settings.Model, IntentPromptBuilder.System(prompt), IntentPromptBuilder.User(prompt), StoryIntentJson.Schema,
                settings.UseGpu, KeepAlive, RequestTimeout, ct).ConfigureAwait(false);
            _logger.LogInformation("Assistent: Sprach-KI antwortete in {Ms} ms (Grafikkarte: {Gpu})", started.ElapsedMilliseconds, settings.UseGpu);
            return new IntentModelResponse(result.Content, result.CutOff);
        }
        catch (IntentModelException ex)
        {
            _logger.LogWarning("Assistent: Sprach-KI nicht verfügbar ({Failure})", ex.Failure);
            throw;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public async Task<AssistantRuntimeStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var version = await _client.GetVersionAsync(ct).ConfigureAwait(false);
        if (version is null)
        {
            return new AssistantRuntimeStatus(_locator.FindExecutable() is null ? AssistantRuntimeState.NotInstalled : AssistantRuntimeState.NotRunning, settings.Model);
        }

        var models = await _client.ListModelsAsync(ct).ConfigureAwait(false);
        return new AssistantRuntimeStatus(OllamaClient.HasModel(models, settings.Model) ? AssistantRuntimeState.Ready : AssistantRuntimeState.ModelMissing, settings.Model, version);
    }

    public async Task PullModelAsync(string model, IProgress<OllamaPullProgress>? progress, CancellationToken ct)
    {
        _logger.LogInformation("Assistent: Modell-Download über Ollama gestartet ({Model})", model);
        await _client.PullAsync(model, progress, ct).ConfigureAwait(false);
        _logger.LogInformation("Assistent: Modell-Download fertig ({Model})", model);
    }

    public bool TryStartOllama() => _locator.TryStart();
}
