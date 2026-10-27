using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Faces.Recognition;
using Lumora.Services.Library;
using Lumora.Services.Messaging;

namespace Lumora.Services.Faces.Assignment;

/// <inheritdoc cref="IFaceRecognitionService" />
/// <remarks>
/// Lebenszyklus (ADR-0037): ein Hintergrund-Worker arbeitet Anforderungen entprellt ab — „neu lernen“ (nach
/// Bestätigung/Korrektur, Bibliothekswechsel, Start) trainiert aus allen bestätigten Gesichtern der aktiven Bibliothek
/// und schreibt danach Vorschläge für ALLE offenen Gesichter (stapelweise, abbrechbar, wenn erneut gelernt werden soll);
/// Index-Stapel (<see cref="FacesChangedMessage"/>) bekommen Vorschläge nur für ihre Dateien. Benutzeraktionen schreiben
/// sofort (eigene Transaktion, Warte-Logik ADR-0030) und stoßen danach das Neu-Lernen an. Ein Schreiber zur Zeit
/// (<see cref="_writeLock"/>); Hintergrund-Vorschläge überschreiben nie eine inzwischen getroffene Entscheidung
/// (bedingtes UPDATE in <see cref="FaceAssignmentStore.WriteSuggestions"/>).
/// </remarks>
public sealed partial class FaceRecognitionService : IFaceRecognitionService, IDisposable
{
    private readonly ILibraryContextFactory _factory;
    private readonly ILibraryService _libraries;
    private readonly IFaceRecognizer _recognizer;
    private readonly IFaceClusterer _clusterer;
    private readonly ILogger<FaceRecognitionService> _logger;
    private readonly IMessenger _messenger;
    private readonly FaceRecognitionOptions _options;
    private readonly ILibraryScope _scope;

    /// <summary>Stille Warte-Logik für Hintergrund-Schreibvorgänge (keine „Änderung wartet“-Meldung, wie der Index).</summary>
    private readonly SqliteBusyRetry _silentRetry;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _trainLock = new(1, 1);
    private readonly SemaphoreSlim _groupsLock = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly HashSet<int> _pendingFiles = [];

    private ModelState? _model;
    private GroupsCache? _groups;
    private readonly object _groupsSync = new();
    private HashSet<long>? _removedDuringCompute;
    private bool _invalidatedDuringCompute;
    private bool _retrainRequested;
    private bool _delayStart;
    private volatile bool _busy;
    private int _generation;
    private int _started;
    private int _disposed;
    private long _nextGroupId;
    private Task? _worker;

    public FaceRecognitionService(
        ILibraryContextFactory factory,
        ILibraryService libraries,
        IFaceRecognizer recognizer,
        IFaceClusterer clusterer,
        ILogger<FaceRecognitionService> logger,
        IMessenger? messenger = null,
        FaceRecognitionOptions? options = null,
        ILibraryScope? scope = null)
    {
        _factory = factory;
        _libraries = libraries;
        _scope = scope ?? new LibraryScope(factory);
        _recognizer = recognizer;
        _clusterer = clusterer;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _options = options ?? FaceRecognitionOptions.Default;
        _silentRetry = new SqliteBusyRetry(_options.BusyRetry, new WeakReferenceMessenger(), logger);
    }

    public FaceRecognitionState State
    {
        get
        {
            var model = Volatile.Read(ref _model);
            return new FaceRecognitionState(
                model?.Scope.LibraryId,
                model?.Model.PersonIds.Count ?? 0,
                model?.Model.ExampleCount ?? 0,
                Volatile.Read(ref _generation),
                !IsIdle());
        }
    }

    // ==== Lebenszyklus ================================================================================================

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _messenger.Register<FaceRecognitionService, FacesChangedMessage>(this, static (r, m) => r.OnFacesChanged(m));
        _messenger.Register<FaceRecognitionService, ActiveLibraryChangedMessage>(this, static (r, m) =>
        {
            // Kommt auch nach jedem Scan derselben Bibliothek — dann bleibt das Modell. Wechsel zur Ansicht „Alle verknüpften“
            // (gleiche aktive Bibliothek, anderer Bereich, ADR-0048) erkennt der nächste Durchgang am Bereichs-Schlüssel.
            if (Volatile.Read(ref r._model)?.Scope.LibraryId != m.LibraryId)
            {
                r.InvalidateGroups();
                r.RequestRetrain();
            }
            else
            {
                r._wake.Writer.TryWrite(true);
            }
        });
        // Verknüpfen/Lösen (ADR-0048): Personen-Ids und Gruppen haben sich geändert — alles neu.
        _messenger.Register<FaceRecognitionService, LibraryGroupsChangedMessage>(this, static (r, m) =>
        {
            if (m.TermsChanged)
            {
                r.InvalidateAll(); // Umbenennen der Gruppe ändert keine Ids — dann bleibt das Modell
            }
        });
        _messenger.Register<FaceRecognitionService, FilesChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind == FilesChangeKind.Removed)
            {
                r.InvalidateGroups(); // Gesichter der Dateien sind per Kaskade weg
            }
        });

        lock (_sync)
        {
            _delayStart = _options.StartDelay > TimeSpan.Zero;
            _retrainRequested = true;
            _autoIgnorePending = true; // Mindestgröße prüfen (ADR-0040): einmalig nach dem Update bzw. nach einer Änderung
        }

        _worker = Task.Run(WorkerLoopAsync);
        _wake.Writer.TryWrite(true);
        _logger.LogInformation("Gesichter-Zuordnung bereit (erstes Training in {Delay:N0} s)", _options.StartDelay.TotalSeconds);
    }

    public void RequestRetrain()
    {
        lock (_sync)
        {
            _retrainRequested = true;
        }

        _wake.Writer.TryWrite(true);
    }

    public void InvalidateAll()
    {
        Volatile.Write(ref _model, null);
        Volatile.Write(ref _groups, null);
        RequestRetrain();
    }

    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        // Zweimal hintereinander ruhig: zwischen „Anforderung gelesen“ und „busy“ liegt keine Lücke (siehe Worker),
        // aber eine Nachricht kann gerade erst eintreffen.
        var quiet = 0;
        while (quiet < 2)
        {
            ct.ThrowIfCancellationRequested();
            quiet = IsIdle() ? quiet + 1 : 0;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    private bool IsIdle()
    {
        lock (_sync)
        {
            return !_busy && !_retrainRequested && !_autoIgnorePending && _pendingFiles.Count == 0 && _wake.Reader.Count == 0;
        }
    }

    private void OnFacesChanged(FacesChangedMessage message)
    {
        lock (_sync)
        {
            foreach (var id in message.FileIds)
            {
                _pendingFiles.Add(id);
            }
        }

        if (Volatile.Read(ref _groups)?.Scope.Contains(message.LibraryId) == true)
        {
            InvalidateGroups(); // neue Gesichter → Gruppen beim nächsten Abruf neu berechnen
        }

        _wake.Writer.TryWrite(true);
    }

    private async Task WorkerLoopAsync()
    {
        var stop = _stop.Token;
        try
        {
            while (await _wake.Reader.WaitToReadAsync(stop).ConfigureAwait(false))
            {
                lock (_sync)
                {
                    _wake.Reader.TryRead(out _);
                    _busy = true;
                }

                try
                {
                    bool delayStart;
                    lock (_sync)
                    {
                        delayStart = _delayStart;
                        _delayStart = false;
                    }

                    await Task.Delay(delayStart ? _options.StartDelay : _options.Debounce, stop).ConfigureAwait(false);
                    await ProcessAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gesichter-Zuordnung: Durchgang fehlgeschlagen");
                }
                finally
                {
                    lock (_sync)
                    {
                        _busy = false;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Beenden
        }
    }

    private async Task ProcessAsync(CancellationToken stop)
    {
        // Mindestgröße zuerst (global, ADR-0040) — zurückgeholte Gesichter brauchen danach Vorschläge.
        var restoredBySize = await ApplyAutoIgnoreIfPendingAsync(stop).ConfigureAwait(false);
        bool retrain;
        int[] files;
        lock (_sync)
        {
            retrain = _retrainRequested || restoredBySize;
            _retrainRequested = false;
            files = [.. _pendingFiles];
            _pendingFiles.Clear();
        }

        var scope = await CurrentScopeAsync(stop).ConfigureAwait(false);
        if (scope is null)
        {
            Volatile.Write(ref _model, null);
            return;
        }

        var model = Volatile.Read(ref _model);
        var changed = 0;
        if (retrain || model is null || model.Scope.Key != scope.Key)
        {
            model = await TrainAsync(scope, stop).ConfigureAwait(false);
            changed += await SuggestAllAsync(model, stop).ConfigureAwait(false);
            MessageAfterTraining(model, changed);
        }
        else if (files.Length > 0)
        {
            changed += await SuggestFilesAsync(model, files, stop).ConfigureAwait(false);
            if (changed > 0)
            {
                _messenger.Send(new PersonsChangedMessage(model.Scope.LibraryId));
            }
        }
    }

    /// <summary>Aktueller Bibliotheks-Bereich (ADR-0048) oder null ohne Bibliothek.</summary>
    private async Task<LibraryScopeInfo?> CurrentScopeAsync(CancellationToken ct)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return scope.HasLibrary ? scope : null;
    }

    /// <summary>Gruppe des aktuellen Bereichs (0 ohne Bibliothek) — zum Lesen.</summary>
    private async Task<int> CurrentGroupAsync(CancellationToken ct)
        => (await _scope.GetCurrentAsync(ct).ConfigureAwait(false)).GroupId;

    /// <summary>Aktive Bibliothek (0 ohne) — Schreibwege lösen ihre Gruppe erst IN der Transaktion auf (<see cref="GroupInTransaction"/>).</summary>
    private async Task<int> CurrentLibraryIdAsync(CancellationToken ct)
        => (await _scope.GetCurrentAsync(ct).ConfigureAwait(false)).LibraryId;

    /// <summary>
    /// Gruppe der Bibliothek, gelesen in der Schreibtransaktion: ein gleichzeitiges Verknüpfen (eigene Transaktion) kann die
    /// zuvor gelesene Gruppe aufgelöst haben — dann landete die neue Person in einer Gruppe ohne Bibliothek (ADR-0048).
    /// </summary>
    private static int GroupInTransaction(SqliteConnection connection, SqliteTransaction tx, int libraryId)
        => libraryId == 0 ? 0 : LibraryGroupStore.ReadGroupOf(connection, tx, libraryId) ?? 0;

    private void MessageAfterTraining(ModelState model, int changed)
    {
        _logger.LogInformation(
            "Gesichter-Zuordnung: gelernt aus {Examples} bestätigten Gesichtern ({Persons} Personen), {Changed} Vorschläge geändert",
            model.Model.ExampleCount, model.Model.PersonIds.Count, changed);
        _messenger.Send(new PersonsChangedMessage(model.Scope.LibraryId));
    }

    // ==== Training und Vorschläge =====================================================================================

    /// <summary>Gelerntes Modell eines Bereichs (ADR-0048): Trainingsdaten und Kandidaten nur aus dessen Bibliotheken.</summary>
    private sealed record ModelState(LibraryScopeInfo Scope, string? ModelVersion, IFaceModel Model, int Generation);

    private async Task<ModelState> TrainAsync(LibraryScopeInfo scope, CancellationToken ct)
    {
        await _trainLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    var version = FaceAssignmentStore.ResolveModelVersion(connection, scope);
                    var confirmed = version is null ? [] : FaceAssignmentStore.ReadConfirmedSamples(connection, scope, version);
                    var rejections = FaceAssignmentStore.ReadRejections(connection, scope);
                    var model = _recognizer.Train(confirmed, rejections, _options.Recognizer, ct);
                    var state = new ModelState(scope, version, model, Interlocked.Increment(ref _generation));
                    Volatile.Write(ref _model, state);
                    return state;
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            _trainLock.Release();
        }
    }

    /// <summary>Modell der aktiven Bibliothek; trainiert sofort, wenn noch keins da ist (Benutzeraktion wartet darauf).</summary>
    private async Task<ModelState?> EnsureModelAsync(CancellationToken ct)
    {
        var scope = await CurrentScopeAsync(ct).ConfigureAwait(false);
        if (scope is null)
        {
            return null;
        }

        var model = Volatile.Read(ref _model);
        return model is not null && model.Scope.Key == scope.Key ? model : await TrainAsync(scope, ct).ConfigureAwait(false);
    }

    /// <summary>Vorschläge für alle offenen Gesichter, stapelweise; bricht ab, sobald erneut gelernt werden soll.</summary>
    private async Task<int> SuggestAllAsync(ModelState model, CancellationToken ct)
    {
        if (model.ModelVersion is null)
        {
            return 0;
        }

        var changed = 0;
        long after = 0;
        while (true)
        {
            lock (_sync)
            {
                if (_retrainRequested)
                {
                    return changed; // der nächste Durchgang lernt neu und fängt von vorn an
                }
            }

            var batch = await Task.Run(
                () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    return FaceAssignmentStore.ReadOpenBatch(connection, model.Scope, model.ModelVersion, after, _options.SuggestBatchSize);
                },
                ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                return changed;
            }

            after = batch[^1].FaceId;
            changed += await SuggestAndWriteAsync(model, batch, ct).ConfigureAwait(false);
        }
    }

    private async Task<int> SuggestFilesAsync(ModelState model, IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        if (model.ModelVersion is null)
        {
            return 0;
        }

        var faces = await Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                // nur Dateien des Bereichs des Modells (eine verspätete Meldung einer anderen Bibliothek fällt weg)
                return FaceAssignmentStore.ReadOpenForFiles(connection, model.Scope, fileIds, model.ModelVersion);
            },
            ct).ConfigureAwait(false);
        return faces.Count == 0 ? 0 : await SuggestAndWriteAsync(model, faces, ct).ConfigureAwait(false);
    }

    private async Task<int> SuggestAndWriteAsync(ModelState model, IReadOnlyList<StoredFace> faces, CancellationToken ct)
    {
        var writes = await Task.Run(
            () =>
            {
                Dictionary<long, List<int>> rejected;
                using (var connection = _factory.OpenSqliteConnection())
                {
                    rejected = FaceAssignmentStore.ReadRejectionsFor(connection, faces.Select(f => f.FaceId));
                }

                var result = new SuggestionWrite[faces.Count];
                Parallel.For(0, faces.Count, Background.BackgroundCpu.Options(ct), i => // Vorschläge im Hintergrund: halbe Kerne
                {
                    var face = faces[i];
                    var suggestion = model.Model.Suggest(face.Sample, rejected.TryGetValue(face.FaceId, out var r) ? r : null);
                    result[i] = ToWrite(face.FaceId, suggestion);
                });
                return result;
            },
            ct).ConfigureAwait(false);

        var pending = writes.Where((w, i) => w.PersonId != faces[i].SuggestedPersonId
                                            || w.State != faces[i].State
                                            || w.Score != faces[i].SuggestionScore).ToList();
        if (pending.Count == 0)
        {
            return 0;
        }

        var changed = await WriteAsync(silent: true, (connection, tx) => FaceAssignmentStore.WriteSuggestions(connection, tx, pending), ct)
            .ConfigureAwait(false);
        var nowSuggested = pending.Where(w => w.PersonId is not null).Select(w => w.FaceId).ToList();
        if (nowSuggested.Count > 0)
        {
            RemoveFromGroups(nowSuggested); // Gesichter mit Vorschlag stehen bei der Person, nicht mehr in Gruppen
        }

        var wasSuggested = faces.Where(f => f.State == FaceState.Suggested).Select(f => f.FaceId).ToHashSet();
        if (pending.Any(w => w.PersonId is null && wasSuggested.Contains(w.FaceId)))
        {
            InvalidateGroups(); // Vorschlag weggefallen → wieder unbekannt → gehört in die Gruppierung
        }

        return changed;
    }

    private static SuggestionWrite ToWrite(long faceId, FaceSuggestion suggestion)
        => suggestion is { Unknown: false, PersonId: { } personId }
            ? new SuggestionWrite(faceId, personId, MathF.Round(suggestion.Score, 4))
            : new SuggestionWrite(faceId, null, null);

    // ==== Lesen =======================================================================================================

    public async Task<int?> GetActiveLibraryIdAsync(CancellationToken ct = default)
        => (await CurrentScopeAsync(ct).ConfigureAwait(false))?.LibraryId;

    public Task<IReadOnlyDictionary<int, IReadOnlyList<FacePersonInfo>>> GetFacesForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<FacePersonInfo>>>(new Dictionary<int, IReadOnlyList<FacePersonInfo>>());
        }

        return Task.Run<IReadOnlyDictionary<int, IReadOnlyList<FacePersonInfo>>>(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return FaceAssignmentStore.ReadFacesForFiles(connection, fileIds);
            },
            ct);
    }

    public async Task<IReadOnlyList<PersonSummary>> GetPersonsAsync(CancellationToken ct)
        => (await GetPersonOverviewsAsync(ct).ConfigureAwait(false)).Select(p => p.Person).ToList();

    public async Task<IReadOnlyList<PersonOverview>> GetPersonOverviewsAsync(CancellationToken ct = default)
    {
        // Personen der Gruppe, Zähler über den Bereich (ADR-0048) — nie Personen einer anderen Bibliothek.
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<PersonOverview>>(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return FaceAssignmentStore.ReadPersons(connection, scope);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<PersonFaces?> GetPersonFacesAsync(int personId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                var person = FaceAssignmentStore.ReadPersons(connection, scope, personId).FirstOrDefault();
                if (person is null)
                {
                    return null;
                }

                if (!scope.HasLibrary)
                {
                    return new PersonFaces(person.Person, [], []);
                }

                return new PersonFaces(
                    person.Person,
                    FaceAssignmentStore.ReadPersonFaces(connection, scope, personId, suggested: false),
                    FaceAssignmentStore.ReadPersonFaces(connection, scope, personId, suggested: true));
            },
            ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<FaceFileInfo>> GetFilesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        return Task.Run<IReadOnlyList<FaceFileInfo>>(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return FaceAssignmentStore.ReadFiles(connection, fileIds);
            },
            ct);
    }

    public async Task<int?> FindPersonByNameAsync(string name, CancellationToken ct = default)
    {
        var trimmed = NormalizeName(name);
        var groupId = await CurrentGroupAsync(ct).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return FaceAssignmentStore.FindPersonByName(connection, null, groupId, trimmed);
            },
            ct).ConfigureAwait(false);
    }

    // ==== Prüfen ======================================================================================================

    public async Task<IReadOnlyList<FaceOutlierResult>> CheckOutliersAsync(IReadOnlyCollection<long> faceIds, int? personId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(faceIds);
        var model = await EnsureModelAsync(ct).ConfigureAwait(false);
        if (model is null || faceIds.Count == 0)
        {
            return [];
        }

        return await Task.Run<IReadOnlyList<FaceOutlierResult>>(
            () =>
            {
                List<StoredFace> faces;
                Dictionary<int, string> names;
                using (var connection = _factory.OpenSqliteConnection())
                {
                    faces = FaceAssignmentStore.ReadFaces(connection, null, faceIds);
                    names = FaceAssignmentStore.ReadPersonNames(connection, model.Scope.GroupId);
                }

                var results = new List<FaceOutlierResult>(faces.Count);
                foreach (var face in faces)
                {
                    var sample = face.Sample with { ConfirmedPersonId = null };
                    if (face.ModelVersion != model.ModelVersion)
                    {
                        // Fingerabdrücke anderer Modell-Kennung sind nicht vergleichbar (ADR-0035) — keine Rückfrage erfinden
                        results.Add(new FaceOutlierResult(face.FaceId, new OutlierCheck(false, 0f, 0f, OutlierReason.Fits,
                            "Gesicht aus einer älteren Analyse — nicht vergleichbar.")));
                        continue;
                    }

                    var check = personId is { } id
                        ? _recognizer.CheckOutlier(sample, id, model.Model)
                        : CheckForNewPerson(sample, model.Model, names);
                    results.Add(new FaceOutlierResult(face.FaceId, check));
                }

                return results;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>Neue Person: Rückfrage, wenn das Gesicht sicher einer BEKANNTEN Person ähnelt.</summary>
    private static OutlierCheck CheckForNewPerson(FaceSample sample, IFaceModel model, IReadOnlyDictionary<int, string> names)
    {
        var suggestion = model.Suggest(sample);
        if (suggestion is { Unknown: false, PersonId: { } other })
        {
            var name = names.TryGetValue(other, out var n) ? n : $"#{other}";
            return new OutlierCheck(true, 0f, suggestion.Similarity, OutlierReason.CloserToOtherPerson,
                $"Dieses Gesicht ähnelt der bekannten Person „{name}“ ({suggestion.Similarity:0.00}). Wirklich als neue Person benennen?",
                other);
        }

        return new OutlierCheck(false, 0f, 0f, OutlierReason.FirstExample, "Neue Person — nichts zu vergleichen.");
    }

    // ==== Personen ====================================================================================================

    public async Task<int> CreatePersonAsync(string name, CancellationToken ct = default)
    {
        var trimmed = NormalizeName(name);
        var libraryId = await CurrentLibraryIdAsync(ct).ConfigureAwait(false);
        var id = await WriteAsync(silent: false, (connection, tx) =>
        {
            var groupId = GroupInTransaction(connection, tx, libraryId);
            if (FaceAssignmentStore.FindPersonByName(connection, tx, groupId, trimmed) is { } existing)
            {
                throw new PersonNameTakenException(trimmed, existing);
            }

            return FaceAssignmentStore.InsertPerson(connection, tx, groupId, trimmed);
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Person angelegt (Id {PersonId})", id);
        await NotifyAsync(retrain: false, ct).ConfigureAwait(false);
        return id;
    }

    public async Task RenamePersonAsync(int personId, string name, CancellationToken ct = default)
    {
        var trimmed = NormalizeName(name);
        await WriteAsync(silent: false, (connection, tx) =>
        {
            var groupId = RequirePersonGroup(connection, tx, personId);
            if (FaceAssignmentStore.FindPersonByName(connection, tx, groupId, trimmed, personId) is { } existing)
            {
                throw new PersonNameTakenException(trimmed, existing);
            }

            // Einzelname (alte Schnittstelle): Vor-/Nachname am ersten Leerzeichen, Rufname entfällt (ADR-0045).
            var (first, last) = Domain.Entities.PersonNames.Split(trimmed);
            return FaceAssignmentStore.Execute(connection, tx,
                "UPDATE Persons SET Name = $n, FirstName = $f, LastName = $l, Nickname = NULL WHERE Id = $p;",
                ("$n", trimmed), ("$f", first), ("$l", last), ("$p", personId));
        }, ct).ConfigureAwait(false);
        await NotifyAsync(retrain: false, ct).ConfigureAwait(false);
    }

    public async Task MergePersonsAsync(int sourceId, int targetId, CancellationToken ct = default)
    {
        if (sourceId == targetId)
        {
            throw new ArgumentException("Eine Person kann nicht mit sich selbst zusammengeführt werden.", nameof(targetId));
        }

        await WriteAsync(silent: false, (connection, tx) =>
        {
            if (RequirePersonGroup(connection, tx, sourceId) != RequirePersonGroup(connection, tx, targetId))
            {
                throw new InvalidOperationException("Personen verschiedener (nicht verknüpfter) Bibliotheken lassen sich nicht zusammenführen.");
            }

            var p = new (string, object)[] { ("$s", sourceId), ("$t", targetId) };
            FaceAssignmentStore.Execute(connection, tx, "UPDATE Faces SET PersonId = $t WHERE PersonId = $s;", p);
            FaceAssignmentStore.Execute(connection, tx, "UPDATE Faces SET SuggestedPersonId = $t WHERE SuggestedPersonId = $s;", p);
            // „Ist nicht Quelle“ heißt jetzt „ist nicht Ziel“ — außer das Gesicht IST als Ziel bestätigt.
            FaceAssignmentStore.Execute(connection, tx,
                "INSERT OR IGNORE INTO FaceRejections (FaceId, PersonId) SELECT FaceId, $t FROM FaceRejections WHERE PersonId = $s;", p);
            FaceAssignmentStore.Execute(connection, tx,
                "DELETE FROM FaceRejections WHERE PersonId = $t AND FaceId IN (SELECT Id FROM Faces WHERE PersonId = $t AND State = 2);", p);
            // Vorschlag = abgelehnte Person? (Quelle war vorgeschlagen, Ziel abgelehnt) → zurück auf unbestätigt
            FaceAssignmentStore.Execute(connection, tx, """
                UPDATE Faces SET State = 0, SuggestedPersonId = NULL, SuggestionScore = NULL
                WHERE State = 1 AND SuggestedPersonId = $t
                  AND EXISTS (SELECT 1 FROM FaceRejections r WHERE r.FaceId = Faces.Id AND r.PersonId = $t);
                """, p);
            FaceAssignmentStore.Execute(connection, tx,
                "UPDATE Persons SET CoverFaceId = COALESCE(CoverFaceId, (SELECT CoverFaceId FROM Persons WHERE Id = $s)) WHERE Id = $t;", p);
            MergeDetails(connection, tx, sourceId, targetId); // Beziehung, Geburtsdatum, „Das bin ich“ (ADR-0045)
            return FaceAssignmentStore.Execute(connection, tx, "DELETE FROM Persons WHERE Id = $s;", p);
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Person {Source} in {Target} zusammengeführt", sourceId, targetId);
        InvalidateGroups(); // zurückgesetzte Vorschläge (abgelehnte Zielperson) sind wieder unbekannt
        await NotifyAsync(retrain: true, ct).ConfigureAwait(false);
    }

    public async Task SetPersonHiddenAsync(int personId, bool hidden, CancellationToken ct = default)
    {
        await WriteAsync(silent: false, (connection, tx) =>
        {
            RequirePerson(connection, tx, personId);
            return FaceAssignmentStore.Execute(connection, tx, "UPDATE Persons SET IsHidden = $h WHERE Id = $p;", ("$h", hidden), ("$p", personId));
        }, ct).ConfigureAwait(false);
        await NotifyAsync(retrain: false, ct).ConfigureAwait(false);
    }

    public async Task DeletePersonAsync(int personId, CancellationToken ct = default)
    {
        var released = await WriteAsync(silent: false, (connection, tx) =>
        {
            RequirePerson(connection, tx, personId);
            var count = FaceAssignmentStore.Execute(connection, tx, """
                UPDATE Faces SET State = 0, PersonId = NULL, SuggestedPersonId = NULL, SuggestionScore = NULL
                WHERE (PersonId = $p AND State = 2) OR (SuggestedPersonId = $p AND State = 1);
                """, ("$p", personId));
            FaceAssignmentStore.Execute(connection, tx, "DELETE FROM Persons WHERE Id = $p;", ("$p", personId)); // Ablehnungen per Kaskade
            return count;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Person {PersonId} gelöscht, {Count} Gesichter wieder unbestätigt", personId, released);
        InvalidateGroups(); // freigegebene Gesichter gehören wieder in die Gruppierung
        await NotifyAsync(retrain: true, ct).ConfigureAwait(false);
    }

    public async Task SetCoverAsync(int personId, long faceId, CancellationToken ct = default)
    {
        var updated = await WriteAsync(silent: false, (connection, tx) => FaceAssignmentStore.Execute(connection, tx, """
            UPDATE Persons SET CoverFaceId = $f
            WHERE Id = $p AND EXISTS (SELECT 1 FROM Faces WHERE Id = $f AND PersonId = $p AND State = 2);
            """, ("$f", faceId), ("$p", personId)), ct).ConfigureAwait(false);
        if (updated == 0)
        {
            throw new InvalidOperationException("Titelbild muss ein bestätigtes Gesicht der Person sein.");
        }

        await NotifyAsync(retrain: false, ct).ConfigureAwait(false);
    }

    /// <summary>ADR-0068: selbst gewähltes Titelbild lösen — die Personen-Seite wählt dann automatisch.</summary>
    public async Task ClearCoverAsync(int personId, CancellationToken ct = default)
    {
        await WriteAsync(silent: false, (connection, tx) =>
        {
            RequirePerson(connection, tx, personId);
            return FaceAssignmentStore.Execute(connection, tx, "UPDATE Persons SET CoverFaceId = NULL WHERE Id = $p;", ("$p", personId));
        }, ct).ConfigureAwait(false);
        await NotifyAsync(retrain: false, ct).ConfigureAwait(false);
    }

    // ==== Gesichter zuordnen ==========================================================================================

    public Task ConfirmAsync(long faceId, int personId) => ConfirmManyAsync([faceId], personId);

    public async Task ConfirmNewPersonAsync(long faceId, string name)
    {
        var trimmed = NormalizeName(name);
        var personId = await FindPersonByNameAsync(trimmed).ConfigureAwait(false) ?? await CreatePersonAsync(trimmed).ConfigureAwait(false);
        await ConfirmManyAsync([faceId], personId).ConfigureAwait(false);
    }

    public async Task ConfirmManyAsync(IReadOnlyCollection<long> faceIds, int personId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(faceIds);
        if (faceIds.Count == 0)
        {
            return;
        }

        var count = await WriteAsync(silent: false, (connection, tx) =>
        {
            RequirePerson(connection, tx, personId);
            var n = 0;
            foreach (var id in faceIds.Distinct())
            {
                // Nur Gesichter aus Bibliotheken der Gruppe der Person (ADR-0048) — nie eine Person aus A an ein Gesicht in B.
                var updated = FaceAssignmentStore.Execute(connection, tx, """
                    UPDATE Faces SET State = 2, PersonId = $p, SuggestedPersonId = NULL, SuggestionScore = NULL
                    WHERE Id = $id
                      AND EXISTS (SELECT 1 FROM Files f JOIN Libraries l ON l.Id = f.LibraryId JOIN Persons pp ON pp.GroupId = l.GroupId
                                  WHERE f.Id = Faces.FileId AND pp.Id = $p);
                    """, ("$p", personId), ("$id", id));
                n += updated;
                if (updated > 0)
                {
                    FaceAssignmentStore.Execute(connection, tx,
                        "DELETE FROM FaceRejections WHERE FaceId = $id AND PersonId = $p;", ("$id", id), ("$p", personId));
                }
                else
                {
                    _logger.LogWarning("Gesicht {FaceId} nicht als Person {PersonId} bestätigt: nicht in der Bibliotheks-Gruppe der Person oder nicht mehr vorhanden", id, personId);
                }
            }

            // Ein Titelbild, das gerade einer ANDEREN Person zugeordnet wurde, ist dort keins mehr.
            FaceAssignmentStore.Execute(connection, tx, $"""
                UPDATE Persons SET CoverFaceId = NULL
                WHERE Id <> $p AND CoverFaceId IN ({FaceAssignmentStore.IdList(faceIds)});
                """, ("$p", personId));
            return n;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("{Count} Gesicht(er) als Person {PersonId} bestätigt", count, personId);
        RemoveFromGroups(faceIds);
        await NotifyAsync(retrain: true, ct).ConfigureAwait(false);
    }

    public Task RejectSuggestionAsync(long faceId) => RejectSuggestionsAsync([faceId]);

    public async Task RejectSuggestionsAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(faceIds);
        var model = await EnsureModelAsync(ct).ConfigureAwait(false);
        if (faceIds.Count == 0)
        {
            return;
        }

        // Nächstbester Kandidat sofort (ohne Neu-Training): alle Ablehnungen des Gesichts inklusive der neuen ausschließen.
        var plan = await Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                var faces = FaceAssignmentStore.ReadFaces(connection, null, faceIds)
                    .Where(f => f.State == FaceState.Suggested && f.SuggestedPersonId is not null)
                    .ToList();
                var rejected = FaceAssignmentStore.ReadRejectionsFor(connection, faces.Select(f => f.FaceId));
                return faces.Select(f =>
                {
                    var excluded = rejected.TryGetValue(f.FaceId, out var r) ? new List<int>(r) : [];
                    excluded.Add(f.SuggestedPersonId!.Value);
                    var next = model is null || model.ModelVersion is null || f.ModelVersion != model.ModelVersion
                        ? FaceSuggestion.None
                        : model.Model.Suggest(f.Sample, excluded);
                    return (Face: f, Next: ToWrite(f.FaceId, next));
                }).ToList();
            },
            ct).ConfigureAwait(false);
        if (plan.Count == 0)
        {
            return;
        }

        await WriteAsync(silent: false, (connection, tx) =>
        {
            foreach (var (face, next) in plan)
            {
                FaceAssignmentStore.Execute(connection, tx,
                    "INSERT OR IGNORE INTO FaceRejections (FaceId, PersonId) VALUES ($id, $p);",
                    ("$id", face.FaceId), ("$p", face.SuggestedPersonId!.Value));
                // nur, wenn der Vorschlag noch derselbe ist (sonst hat inzwischen jemand anderes entschieden)
                FaceAssignmentStore.Execute(connection, tx, """
                    UPDATE Faces SET State = $state, SuggestedPersonId = $sp, SuggestionScore = $score
                    WHERE Id = $id AND State = 1 AND SuggestedPersonId = $old;
                    """,
                    ("$state", (int)next.State), ("$sp", (object?)next.PersonId ?? DBNull.Value),
                    ("$score", next.Score is { } s ? (double)s : DBNull.Value), ("$id", face.FaceId), ("$old", face.SuggestedPersonId!.Value));
            }

            return plan.Count;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("{Count} Vorschlag/Vorschläge abgelehnt", plan.Count);
        if (plan.Any(p => p.Next.PersonId is null))
        {
            InvalidateGroups(); // wieder unbekannte Gesichter gehören in die Gruppierung
        }

        await NotifyAsync(retrain: true, ct).ConfigureAwait(false);
    }

    public async Task RemoveFromPersonAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(faceIds);
        if (faceIds.Count == 0)
        {
            return;
        }

        var count = await WriteAsync(silent: false, (connection, tx) =>
        {
            var n = 0;
            foreach (var face in FaceAssignmentStore.ReadFaces(connection, tx, faceIds).Where(f => f.State == FaceState.Confirmed && f.PersonId is not null))
            {
                // Ablehnung merken: dieselbe Person wird für dieses Gesicht nicht wieder vorgeschlagen.
                FaceAssignmentStore.Execute(connection, tx,
                    "INSERT OR IGNORE INTO FaceRejections (FaceId, PersonId) VALUES ($id, $p);", ("$id", face.FaceId), ("$p", face.PersonId!.Value));
                n += FaceAssignmentStore.Execute(connection, tx,
                    "UPDATE Faces SET State = 0, PersonId = NULL, SuggestedPersonId = NULL, SuggestionScore = NULL WHERE Id = $id;",
                    ("$id", face.FaceId));
            }

            ClearCovers(connection, tx, faceIds);
            return n;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("{Count} Gesicht(er) aus ihrer Person entfernt", count);
        InvalidateGroups();
        await NotifyAsync(retrain: true, ct).ConfigureAwait(false);
    }

    public Task IgnoreFaceAsync(long faceId) => IgnoreFacesAsync([faceId]);

    public async Task IgnoreFacesAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(faceIds);
        if (faceIds.Count == 0)
        {
            return;
        }

        var count = await WriteAsync(silent: false, (connection, tx) =>
        {
            var n = 0;
            foreach (var id in faceIds.Distinct())
            {
                n += FaceAssignmentStore.Execute(connection, tx,
                    "UPDATE Faces SET State = 3, PersonId = NULL, SuggestedPersonId = NULL, SuggestionScore = NULL WHERE Id = $id;",
                    ("$id", id));
            }

            ClearCovers(connection, tx, faceIds);
            return n;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("{Count} Gesicht(er) ignoriert", count);
        RemoveFromGroups(faceIds);
        await NotifyAsync(retrain: true, ct).ConfigureAwait(false);
    }

    // ==== Gruppen =====================================================================================================

    private sealed record CachedGroup(long Id, IReadOnlyList<FaceGroupMemberInfo> Members, float Cohesion, float[] Centroid);

    private sealed record GroupsCache(LibraryScopeInfo Scope, IReadOnlyList<CachedGroup> Groups, int UnknownFaces, int Sections, bool Stale);

    public async Task<FaceGroupsResult> GetGroupsAsync(bool refresh = false, CancellationToken ct = default)
    {
        var scope = await CurrentScopeAsync(ct).ConfigureAwait(false);
        if (scope is null)
        {
            return FaceGroupsResult.Empty;
        }

        var model = await EnsureModelAsync(ct).ConfigureAwait(false);
        var cache = Volatile.Read(ref _groups);
        if (refresh || cache is null || cache.Scope.Key != scope.Key || cache.Stale)
        {
            await _groupsLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                cache = Volatile.Read(ref _groups);
                if (refresh || cache is null || cache.Scope.Key != scope.Key || cache.Stale)
                {
                    // Während des Rechnens (Sekunden bei vielen Gesichtern) können Benutzeraktionen Gesichter herausnehmen
                    // oder den Stand ungültig machen — beides merken und auf das neue Ergebnis anwenden.
                    HashSet<long> removedMeanwhile;
                    bool invalidatedMeanwhile;
                    lock (_groupsSync)
                    {
                        _removedDuringCompute = [];
                        _invalidatedDuringCompute = false;
                    }

                    try
                    {
                        cache = await Task.Run(() => ComputeGroups(scope, model?.ModelVersion, ct), ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_groupsSync)
                        {
                            removedMeanwhile = _removedDuringCompute ?? [];
                            invalidatedMeanwhile = _invalidatedDuringCompute;
                            _removedDuringCompute = null;
                        }
                    }

                    Volatile.Write(ref _groups, cache with { Stale = invalidatedMeanwhile });
                    if (removedMeanwhile.Count > 0)
                    {
                        RemoveFromGroups(removedMeanwhile);
                    }

                    cache = Volatile.Read(ref _groups) ?? cache;
                }
            }
            finally
            {
                _groupsLock.Release();
            }
        }

        return await Task.Run(() => ToResult(cache, model), ct).ConfigureAwait(false);
    }

    private GroupsCache ComputeGroups(LibraryScopeInfo scope, string? modelVersion, CancellationToken ct)
    {
        using var connection = _factory.OpenSqliteConnection();
        modelVersion ??= FaceAssignmentStore.ResolveModelVersion(connection, scope);
        var unknownCount = FaceAssignmentStore.CountUnknown(connection, scope);
        if (modelVersion is null)
        {
            return new GroupsCache(scope, [], unknownCount, 0, false);
        }

        var faces = FaceAssignmentStore.ReadUnknownForClustering(connection, scope, modelVersion, _options.Cluster.MinQuality);
        var fileOf = faces.ToDictionary(f => f.FaceId, f => f.FileId);
        var groups = new List<CachedGroup>();
        var section = Math.Max(_options.Cluster.MinGroupSize, _options.MaxClusterSection);
        var sections = 0;
        // Abschnittsweise (ADR-0036: O(n²)) — neueste Gesichter zuerst, je Abschnitt höchstens MaxClusterSection.
        for (var start = 0; start < faces.Count; start += section)
        {
            ct.ThrowIfCancellationRequested();
            sections++;
            var samples = faces.Skip(start).Take(section).Select(f => f.Sample).ToList();
            var result = _clusterer.Cluster(samples, _options.Cluster, ct);
            foreach (var group in result.Groups)
            {
                var members = group.Members
                    .Select(m => new FaceGroupMemberInfo(m.FaceId, fileOf[m.FaceId], 1f - m.Distance, m.IsOutlier))
                    .ToList();
                groups.Add(new CachedGroup(Interlocked.Increment(ref _nextGroupId), members, group.Cohesion, group.Centroid.ToArray()));
            }
        }

        groups.Sort((a, b) => b.Members.Count != a.Members.Count ? b.Members.Count.CompareTo(a.Members.Count) : a.Id.CompareTo(b.Id));
        _logger.LogInformation("Gesichter gruppiert: {Faces} unbekannte Gesichter → {Groups} Gruppen ({Sections} Abschnitt(e))", faces.Count, groups.Count, sections);
        return new GroupsCache(scope, groups, unknownCount, sections, false);
    }

    private FaceGroupsResult ToResult(GroupsCache cache, ModelState? model)
    {
        Dictionary<int, string>? names = null;
        var hasPersons = model is not null && model.Model.PersonIds.Count > 0;
        if (hasPersons)
        {
            using var connection = _factory.OpenSqliteConnection();
            names = FaceAssignmentStore.ReadPersonNames(connection, model!.Scope.GroupId);
        }

        var groups = cache.Groups.Select(g =>
        {
            if (!hasPersons)
            {
                return new FaceGroupInfo(g.Id, g.Members, g.Cohesion, null, null, null);
            }

            var suggestion = model!.Model.Suggest(new FaceSample(-g.Id, g.Centroid, 1f));
            return suggestion is { Unknown: false, PersonId: { } pid } && names!.TryGetValue(pid, out var name)
                ? new FaceGroupInfo(g.Id, g.Members, g.Cohesion, pid, name, suggestion.Score)
                : new FaceGroupInfo(g.Id, g.Members, g.Cohesion, null, null, null);
        }).ToList();
        return new FaceGroupsResult(groups, cache.UnknownFaces, cache.Sections);
    }

    /// <summary>Gesichter aus den zwischengespeicherten Gruppen nehmen; zu kleine Gruppen fallen weg.</summary>
    private void RemoveFromGroups(IEnumerable<long> faceIds)
    {
        var remove = faceIds.ToHashSet();
        lock (_groupsSync)
        {
            _removedDuringCompute?.UnionWith(remove);
        }

        while (true)
        {
            var cache = Volatile.Read(ref _groups);
            if (cache is null || remove.Count == 0)
            {
                return;
            }

            var groups = new List<CachedGroup>(cache.Groups.Count);
            var removed = 0;
            foreach (var group in cache.Groups)
            {
                var members = group.Members.Where(m => !remove.Contains(m.FaceId)).ToList();
                removed += group.Members.Count - members.Count;
                if (members.Count == group.Members.Count)
                {
                    groups.Add(group);
                }
                else if (members.Count >= _options.Cluster.MinGroupSize)
                {
                    groups.Add(group with { Members = members });
                }
            }

            groups.Sort((a, b) => b.Members.Count != a.Members.Count ? b.Members.Count.CompareTo(a.Members.Count) : a.Id.CompareTo(b.Id));
            var next = cache with { Groups = groups, UnknownFaces = Math.Max(0, cache.UnknownFaces - removed) };
            if (Interlocked.CompareExchange(ref _groups, next, cache) == cache)
            {
                return;
            }
        }
    }

    private void InvalidateGroups()
    {
        lock (_groupsSync)
        {
            if (_removedDuringCompute is not null)
            {
                _invalidatedDuringCompute = true; // laufende Berechnung liest evtl. den alten Stand
            }
        }

        while (true)
        {
            var cache = Volatile.Read(ref _groups);
            if (cache is null || cache.Stale || Interlocked.CompareExchange(ref _groups, cache with { Stale = true }, cache) == cache)
            {
                return;
            }
        }
    }

    // ==== Hilfen ======================================================================================================

    private async Task NotifyAsync(bool retrain, CancellationToken ct)
    {
        var libraryId = (await _scope.GetCurrentAsync(ct).ConfigureAwait(false)) is { HasLibrary: true } s ? s.LibraryId : (int?)null;
        if (retrain)
        {
            RequestRetrain();
        }

        if (libraryId is { } id)
        {
            _messenger.Send(new PersonsChangedMessage(id));
        }
    }

    /// <summary>
    /// Schreibt in EINER Transaktion auf dem Thread-Pool — ein Schreiber dieses Dienstes zur Zeit, Warte-Logik bei
    /// gesperrtem Katalog (ADR-0030; Benutzeraktionen melden das Warten in der Statusleiste, Hintergrund-Vorschläge nicht).
    /// Face-Ids bleiben über „Neu aufbauen“ stabil (ADR-0035) — keine Umrechnung wie bei FileIds nötig.
    /// </summary>
    private async Task<T> WriteAsync<T>(bool silent, Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var retry = silent ? _silentRetry : _factory.BusyRetry;
            return await Task.Run(
                async () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await retry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                    var result = write(connection, tx);
                    tx.Commit();
                    return result;
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void RequirePerson(SqliteConnection connection, SqliteTransaction tx, int personId)
    {
        if (!FaceAssignmentStore.PersonExists(connection, tx, personId))
        {
            throw new InvalidOperationException("Die Person gibt es nicht mehr.");
        }
    }

    /// <summary>Gruppe der Person (ADR-0048); wirft, wenn es sie nicht mehr gibt.</summary>
    private static int RequirePersonGroup(SqliteConnection connection, SqliteTransaction tx, int personId)
        => FaceAssignmentStore.ReadPersonGroup(connection, tx, personId)
           ?? throw new InvalidOperationException("Die Person gibt es nicht mehr.");

    private static void ClearCovers(SqliteConnection connection, SqliteTransaction tx, IReadOnlyCollection<long> faceIds)
        => FaceAssignmentStore.Execute(connection, tx,
            $"UPDATE Persons SET CoverFaceId = NULL WHERE CoverFaceId IN ({FaceAssignmentStore.IdList(faceIds)});");

    private static string NormalizeName(string name)
    {
        // Mehrfache Leerzeichen zu einem: Name = Anzeigename aus Vor-/Nachname (ADR-0045) und die Prüfung auf vergebene
        // Namen vergleicht dieselbe Schreibweise, die gespeichert wird.
        var trimmed = System.Text.RegularExpressions.Regex.Replace((name ?? string.Empty).Trim(), @"\s+", " ");
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Der Name darf nicht leer sein.", nameof(name));
        }

        return trimmed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _messenger.UnregisterAll(this);
        _stop.Cancel();
        _wake.Writer.TryComplete();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Beenden
        }

        _stop.Dispose();
    }
}
