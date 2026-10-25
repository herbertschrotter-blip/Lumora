using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Library;

namespace Lumora.Services.Motifs;

/// <summary>
/// Motiv-Begriffe eines Bibliotheks-Bereichs (ADR-0052): lesen (mit Anzahl Treffer), anlegen, umbenennen, Beschreibung
/// und Schwelle ändern, löschen; Treffer je Datei; Freitext-Bildsuche. Nach jeder Änderung rechnet der Dienst — sofern
/// das Modell vorhanden ist — den Text-Vektor neu (Sprachmodell auf dem Prozessor) und danach nur die Treffer aus den
/// gespeicherten Bild-Vektoren; kein Foto wird dafür neu analysiert. Meldet <see cref="MotifTermsChangedMessage"/> und
/// <see cref="MotifsChangedMessage"/>. Alle Methoden laufen asynchron außerhalb des UI-Threads.
/// Bibliotheks-Trennung (ADR-0048, seit 0.2.56): Begriffe gehören der Gruppe der übergebenen Bibliothek; Zähler und
/// Bildinhalt-Suche gelten für den aktuellen Bereich, wenn die Bibliothek dessen aktive ist („Alle verknüpften“: alle
/// Bibliotheken der Gruppe), sonst nur für die Bibliothek selbst. Nachrichten nennen eine Bibliothek der Gruppe.
/// </summary>
public interface IMotifTermService
{
    /// <summary>Begriffe der Bibliothek; legt beim ersten Aufruf je Bibliothek die Standard-Liste an.</summary>
    Task<IReadOnlyList<MotifTermInfo>> GetTermsAsync(int libraryId, CancellationToken ct = default);

    Task<int> AddTermAsync(int libraryId, string name, string? prompt = null, CancellationToken ct = default);

    Task UpdateTermAsync(int termId, string name, string? prompt, CancellationToken ct = default);

    /// <summary>Eigene Schwelle (0..1) bzw. null = globale.</summary>
    Task SetThresholdAsync(int termId, double? threshold, CancellationToken ct = default);

    Task DeleteTermAsync(int termId, CancellationToken ct = default);

    /// <summary>Fehlende Text-Vektoren rechnen und deren Treffer neu berechnen (nach dem Modell-Download, beim Index-Start).</summary>
    Task<int> EnsureTextVectorsAsync(int libraryId, CancellationToken ct = default);

    /// <summary>Alle Treffer der Bibliothek neu (globale Schwelle geändert).</summary>
    Task RecomputeAllAsync(int libraryId, CancellationToken ct = default);

    Task<IReadOnlyList<FileMotifInfo>> GetMotifsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default);

    /// <summary>Freitext-Bildsuche („Hund am Strand“): Text-Vektor gegen alle Bild-Vektoren der Bibliothek, beste zuerst.</summary>
    Task<IReadOnlyList<MotifSearchHit>> SearchAsync(int libraryId, string text, int limit, CancellationToken ct = default);

    /// <summary>true, wenn das Modell vorhanden ist (sonst bleiben neue Begriffe ohne Vektor und ohne Treffer).</summary>
    bool IsModelAvailable { get; }

    /// <summary>
    /// ADR-0068 „Suchen ▸ Ähnliche Fotos“: Bild-Vektor der Datei gegen alle Bild-Vektoren des Bereichs (gleiche Modell-Version),
    /// ähnlichste zuerst (die Datei selbst eingeschlossen); null, wenn die Datei noch keinen Bild-Vektor hat. Braucht das Modell
    /// nicht. Standard: null.
    /// </summary>
    Task<IReadOnlyList<MotifSearchHit>?> SearchSimilarAsync(int libraryId, int fileId, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MotifSearchHit>?>(null);

    /// <summary>ADR-0068: hat die Datei einen Bild-Vektor (sonst wird „Ähnliche Fotos“ ausgeblendet)? Standard: false.</summary>
    Task<bool> HasImageVectorAsync(int fileId, CancellationToken ct = default) => Task.FromResult(false);
}

/// <inheritdoc cref="IMotifTermService" />
public sealed class MotifTermService : IMotifTermService, IDisposable
{
    public const int MaxNameLength = 60;
    public const int MaxPromptLength = 200;

    /// <summary>Das Sprachmodell (≈ 300 MB im Speicher) wird nach so langer Ruhe wieder freigegeben.</summary>
    private static readonly TimeSpan TextEncoderIdle = TimeSpan.FromMinutes(2);

    private readonly MotifStore _store;
    private readonly IMotifEncoderFactory _encoders;
    private readonly IMotifSettingsStore _settings;
    private readonly ILogger _logger;
    private readonly IMessenger _messenger;
    private readonly ILibraryScope? _scope;
    private readonly SemaphoreSlim _encoderLock = new(1, 1);
    private IMotifTextEncoder? _textEncoder;
    private string? _textEncoderVersion;
    private CancellationTokenSource? _releaseCts;
    private int _disposed;

    public MotifTermService(
        MotifStore store,
        IMotifEncoderFactory encoders,
        IMotifSettingsStore settings,
        ILogger<MotifTermService>? logger = null,
        IMessenger? messenger = null,
        ILibraryScope? scope = null)
    {
        _scope = scope;
        _store = store;
        _encoders = encoders;
        _settings = settings;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public bool IsModelAvailable => _encoders.IsAvailable;

    public async Task<IReadOnlyList<MotifTermInfo>> GetTermsAsync(int libraryId, CancellationToken ct = default)
    {
        await _store.EnsureDefaultsAsync(libraryId, ct).ConfigureAwait(false);
        var scope = await ScopeLibrariesAsync(libraryId, ct).ConfigureAwait(false);
        return await _store.GetTermsAsync(libraryId, scope, _encoders.ModelVersion, ct).ConfigureAwait(false);
    }

    public async Task<int> AddTermAsync(int libraryId, string name, string? prompt = null, CancellationToken ct = default)
    {
        await _store.EnsureDefaultsAsync(libraryId, ct).ConfigureAwait(false);
        var id = await _store.AddTermAsync(libraryId, ValidateName(name), ValidatePrompt(prompt), ct).ConfigureAwait(false);
        _logger.LogInformation("Motive: Begriff angelegt (Bibliothek {LibraryId}, Id {TermId})", libraryId, id);
        await RefreshTermsAsync(libraryId, ct).ConfigureAwait(false);
        return id;
    }

    public async Task UpdateTermAsync(int termId, string name, string? prompt, CancellationToken ct = default)
    {
        var libraryId = await _store.UpdateTermTextAsync(termId, ValidateName(name), ValidatePrompt(prompt), ct).ConfigureAwait(false);
        _logger.LogInformation("Motive: Begriff {TermId} geändert", termId);
        await RefreshTermsAsync(libraryId, ct).ConfigureAwait(false);
    }

    public async Task SetThresholdAsync(int termId, double? threshold, CancellationToken ct = default)
    {
        if (threshold is { } t && (t <= 0 || t >= 1 || double.IsNaN(t)))
        {
            throw new MotifTermException("Die Schwelle muss zwischen 0 und 1 liegen.");
        }

        var libraryId = await _store.SetThresholdAsync(termId, threshold, ct).ConfigureAwait(false);
        if (libraryId < 0)
        {
            return;
        }

        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        await _store.RecomputeAsync(libraryId, [termId], _encoders.Scoring, settings.DefaultThreshold, _encoders.ModelVersion, ct).ConfigureAwait(false);
        Notify(libraryId);
    }

    public async Task DeleteTermAsync(int termId, CancellationToken ct = default)
    {
        var libraryId = await _store.DeleteTermAsync(termId, ct).ConfigureAwait(false);
        if (libraryId >= 0)
        {
            _logger.LogInformation("Motive: Begriff {TermId} gelöscht", termId);
            Notify(libraryId);
        }
    }

    public async Task<int> EnsureTextVectorsAsync(int libraryId, CancellationToken ct = default)
    {
        await _store.EnsureDefaultsAsync(libraryId, ct).ConfigureAwait(false);
        if (!_encoders.IsAvailable)
        {
            return 0;
        }

        var missing = await _store.GetTermsWithoutVectorAsync(libraryId, _encoders.ModelVersion, ct).ConfigureAwait(false);
        if (missing.Count == 0)
        {
            return 0;
        }

        var vectors = await EncodeTermsAsync(missing, ct).ConfigureAwait(false);
        await _store.SetTextVectorsAsync(vectors, _encoders.ModelVersion, ct).ConfigureAwait(false);
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        await _store.RecomputeAsync(libraryId, missing.Select(m => m.Id).ToList(), _encoders.Scoring, settings.DefaultThreshold, _encoders.ModelVersion, ct).ConfigureAwait(false);
        _logger.LogInformation("Motive: {Count} Text-Vektoren gerechnet (Bibliothek {LibraryId})", missing.Count, libraryId);
        Notify(libraryId);
        return missing.Count;
    }

    public async Task RecomputeAllAsync(int libraryId, CancellationToken ct = default)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var files = await _store.RecomputeAsync(libraryId, null, _encoders.Scoring, settings.DefaultThreshold, _encoders.ModelVersion, ct).ConfigureAwait(false);
        _logger.LogInformation("Motive: Treffer neu berechnet ({Files} Dateien, Bibliothek {LibraryId})", files, libraryId);
        Notify(libraryId);
    }

    public Task<IReadOnlyList<FileMotifInfo>> GetMotifsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
        => _store.GetMotifsForFilesAsync(fileIds, ct);

    public async Task<IReadOnlyList<MotifSearchHit>> SearchAsync(int libraryId, string text, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !_encoders.IsAvailable)
        {
            return [];
        }

        var vector = (await EncodeTextsAsync([text.Trim()], ct).ConfigureAwait(false))[0];
        var scope = await ScopeLibrariesAsync(libraryId, ct).ConfigureAwait(false);
        return await _store.SearchAsync(scope, vector, _encoders.ModelVersion, limit, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MotifSearchHit>?> SearchSimilarAsync(int libraryId, int fileId, int limit, CancellationToken ct = default)
    {
        if (await _store.GetImageVectorAsync(fileId, ct).ConfigureAwait(false) is not { } vector)
        {
            return null;
        }

        var scope = await ScopeLibrariesAsync(libraryId, ct).ConfigureAwait(false);
        return await _store.SearchAsync(scope, vector.Vector, vector.ModelVersion, limit, ct).ConfigureAwait(false);
    }

    public async Task<bool> HasImageVectorAsync(int fileId, CancellationToken ct = default)
        => await _store.GetImageVectorAsync(fileId, ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// Bibliotheken für Zähler und Suche: der aktuelle Bereich, wenn <paramref name="libraryId"/> dessen aktive Bibliothek
    /// ist (Ansicht „Alle verknüpften“ → alle der Gruppe), sonst nur die Bibliothek selbst — nie mehr als der Bereich.
    /// </summary>
    private async Task<IReadOnlyList<int>> ScopeLibrariesAsync(int libraryId, CancellationToken ct)
    {
        if (_scope is null)
        {
            return [libraryId];
        }

        var current = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return current.LibraryId == libraryId && current.LibraryIds.Count > 0 ? current.LibraryIds : [libraryId];
    }

    /// <summary>Nach Anlegen/Ändern: fehlende Vektoren rechnen (inkl. Treffer) und melden; ohne Modell nur melden.</summary>
    private async Task RefreshTermsAsync(int libraryId, CancellationToken ct)
    {
        if (libraryId < 0)
        {
            return;
        }

        if (await EnsureTextVectorsAsync(libraryId, ct).ConfigureAwait(false) == 0)
        {
            Notify(libraryId);
        }
    }

    /// <summary>Vektor eines Begriffs = Mittel aus Wort und Beschreibung (MotifDefaults); ohne Beschreibung nur das Wort.</summary>
    private async Task<IReadOnlyList<(int, string, string?, float[])>> EncodeTermsAsync(IReadOnlyList<(int Id, string Name, string? Prompt)> terms, CancellationToken ct)
    {
        var texts = new List<string>();
        foreach (var term in terms)
        {
            texts.Add(term.Name);
            if (!string.IsNullOrWhiteSpace(term.Prompt))
            {
                texts.Add(term.Prompt);
            }
        }

        var encoded = await EncodeTextsAsync(texts, ct).ConfigureAwait(false);
        var result = new List<(int, string, string?, float[])>(terms.Count);
        var index = 0;
        foreach (var term in terms)
        {
            var parts = new List<float[]> { encoded[index++] };
            if (!string.IsNullOrWhiteSpace(term.Prompt))
            {
                parts.Add(encoded[index++]);
            }

            result.Add((term.Id, term.Name, term.Prompt, MotifVectors.MeanNormalized(parts)));
        }

        return result;
    }

    private async Task<IReadOnlyList<float[]>> EncodeTextsAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        await _encoderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _releaseCts?.Cancel();
            if (_textEncoder is null || _textEncoderVersion != _encoders.ModelVersion)
            {
                _textEncoder?.Dispose();
                _textEncoder = await Task.Run(_encoders.CreateTextEncoder, ct).ConfigureAwait(false);
                _textEncoderVersion = _encoders.ModelVersion;
            }

            var encoder = _textEncoder;
            var result = await Task.Run(() => encoder.EncodeTexts(texts), ct).ConfigureAwait(false);
            ScheduleRelease();
            return result;
        }
        finally
        {
            _encoderLock.Release();
        }
    }

    private void ScheduleRelease()
    {
        var cts = new CancellationTokenSource();
        _releaseCts = cts;
        _ = Task.Delay(TextEncoderIdle, cts.Token).ContinueWith(
            async _ =>
            {
                await _encoderLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!cts.IsCancellationRequested && ReferenceEquals(_releaseCts, cts))
                    {
                        _textEncoder?.Dispose();
                        _textEncoder = null;
                        _logger.LogDebug("Motive: Sprachmodell nach Ruhe freigegeben");
                    }
                }
                finally
                {
                    _encoderLock.Release();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void Notify(int libraryId)
    {
        _messenger.Send(new MotifTermsChangedMessage(libraryId));
        _messenger.Send(new MotifsChangedMessage(libraryId, []));
    }

    internal static string ValidateName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new MotifTermException("Der Begriff darf nicht leer sein.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new MotifTermException($"Der Begriff ist zu lang (höchstens {MaxNameLength} Zeichen).");
        }

        return trimmed;
    }

    internal static string? ValidatePrompt(string? prompt)
    {
        var trimmed = prompt?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > MaxPromptLength
            ? throw new MotifTermException($"Die Beschreibung ist zu lang (höchstens {MaxPromptLength} Zeichen).")
            : trimmed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _releaseCts?.Cancel();
        _textEncoder?.Dispose();
        _textEncoder = null;
    }
}
