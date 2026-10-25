using System.Drawing;
using Lumora.Domain.Enums;
using Lumora.Persons;
using Lumora.Persons.Dialogs;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Recognition;

namespace Lumora.Wpf.Tests.Persons;

/// <summary>Zuordnungsdienst-Attrappe für die ViewModel-Tests der Personen-Seite: feste Daten, merkt sich Aufrufe.</summary>
internal sealed class FakeRecognitionService : IFaceRecognitionService
{
    public List<PersonOverview> Persons { get; } = [];

    public FaceGroupsResult Groups { get; set; } = FaceGroupsResult.Empty;

    public Dictionary<int, PersonFaces> PersonFaces { get; } = [];

    /// <summary>Gesichter, die die Ausreißer-Prüfung meldet.</summary>
    public HashSet<long> Outliers { get; } = [];

    public List<(IReadOnlyCollection<long> Faces, int PersonId)> Confirmed { get; } = [];

    public List<IReadOnlyCollection<long>> Rejected { get; } = [];

    public List<IReadOnlyCollection<long>> Ignored { get; } = [];

    public List<IReadOnlyCollection<long>> Removed { get; } = [];

    public List<string> Created { get; } = [];

    public List<(int Source, int Target)> Merged { get; } = [];

    public List<(IReadOnlyCollection<long> Faces, int? PersonId)> Checks { get; } = [];

    public int NextPersonId { get; set; } = 100;

    public Task<IReadOnlyDictionary<int, IReadOnlyList<FacePersonInfo>>> GetFacesForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<FacePersonInfo>>>(new Dictionary<int, IReadOnlyList<FacePersonInfo>>());

    public Task<IReadOnlyList<PersonSummary>> GetPersonsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PersonSummary>>(Persons.Select(p => p.Person).ToList());

    public Task ConfirmAsync(long faceId, int personId) => ConfirmManyAsync([faceId], personId);

    public Task ConfirmNewPersonAsync(long faceId, string name) => Task.CompletedTask;

    public Task RejectSuggestionAsync(long faceId) => RejectSuggestionsAsync([faceId]);

    public Task IgnoreFaceAsync(long faceId) => IgnoreFacesAsync([faceId]);

    public Task<IReadOnlyList<PersonOverview>> GetPersonOverviewsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PersonOverview>>(Persons.ToList());

    public Task<PersonFaces?> GetPersonFacesAsync(int personId, CancellationToken ct = default)
        => Task.FromResult(PersonFaces.TryGetValue(personId, out var faces) ? faces : null);

    public Task<FaceGroupsResult> GetGroupsAsync(bool refresh = false, CancellationToken ct = default) => Task.FromResult(Groups);

    public Task<IReadOnlyList<FaceOutlierResult>> CheckOutliersAsync(IReadOnlyCollection<long> faceIds, int? personId, CancellationToken ct = default)
    {
        Checks.Add((faceIds.ToList(), personId));
        return Task.FromResult<IReadOnlyList<FaceOutlierResult>>(faceIds.Select(id => new FaceOutlierResult(id, Outliers.Contains(id)
            ? new OutlierCheck(true, 0.2f, 0.6f, OutlierReason.FarFromExamples, "Dieses Gesicht ähnelt den bisherigen Beispielen der Person kaum (0.20). Wirklich zuordnen?")
            : new OutlierCheck(false, 0.7f, 0.6f, OutlierReason.Fits, "passt")))
            .ToList());
    }

    public Task ConfirmManyAsync(IReadOnlyCollection<long> faceIds, int personId, CancellationToken ct = default)
    {
        Confirmed.Add((faceIds.ToList(), personId));
        return Task.CompletedTask;
    }

    public Task<int> CreatePersonAsync(string name, CancellationToken ct = default)
    {
        Created.Add(name);
        var id = NextPersonId++;
        Persons.Add(new PersonOverview(new PersonSummary(id, name, 0, 0, null, false), 0));
        return Task.FromResult(id);
    }

    public Task<int?> FindPersonByNameAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Persons.FirstOrDefault(p => string.Equals(p.Person.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Person.Id);

    public Task RenamePersonAsync(int personId, string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task MergePersonsAsync(int sourceId, int targetId, CancellationToken ct = default)
    {
        Merged.Add((sourceId, targetId));
        return Task.CompletedTask;
    }

    public Task SetPersonHiddenAsync(int personId, bool hidden, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeletePersonAsync(int personId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RejectSuggestionsAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default)
    {
        Rejected.Add(faceIds.ToList());
        return Task.CompletedTask;
    }

    public Task RemoveFromPersonAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default)
    {
        Removed.Add(faceIds.ToList());
        return Task.CompletedTask;
    }

    public Task IgnoreFacesAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default)
    {
        Ignored.Add(faceIds.ToList());
        return Task.CompletedTask;
    }

    public Task SetCoverAsync(int personId, long faceId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Titelbild wieder automatisch (ADR-0068) — merkt sich die Person.</summary>
    public List<int> ClearedCovers { get; } = [];

    public Task ClearCoverAsync(int personId, CancellationToken ct = default)
    {
        ClearedCovers.Add(personId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FaceFileInfo>> GetFilesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FaceFileInfo>>(fileIds.Select(id => new FaceFileInfo(id, $"C:\\x\\{id}.jpg", $"{id}.jpg", MediaType.Image, false, null, 1, DateTime.UtcNow)).ToList());

    public Task<int?> GetActiveLibraryIdAsync(CancellationToken ct = default) => Task.FromResult<int?>(1);

    public FaceRecognitionState State => new(1, 0, 0, 0, false);

    public void Start()
    {
    }

    public void RequestRetrain()
    {
    }

    public void InvalidateAll()
    {
    }

    public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Gruppe mit <paramref name="regular"/> unauffälligen Gesichtern (Id 1…) und <paramref name="outliers"/> Ausreißern am Ende.</summary>
    public static FaceGroupInfo Group(long groupId, int regular, int outliers, int? suggestedPersonId = null, string? suggestedName = null)
    {
        var members = Enumerable.Range(1, regular)
            .Select(i => new FaceGroupMemberInfo(groupId * 1000 + i, i, 0.9f - i * 0.001f, false))
            .Concat(Enumerable.Range(regular + 1, outliers).Select(i => new FaceGroupMemberInfo(groupId * 1000 + i, i, 0.55f, true)))
            .ToList();
        return new FaceGroupInfo(groupId, members, 0.7f, suggestedPersonId, suggestedName, suggestedPersonId is null ? null : 0.6f);
    }

    public static PersonFaceInfo Face(long id, FaceState state, float? score = null)
        => new(id, (int)id, new RectangleF(0.3f, 0.2f, 0.3f, 0.4f), state, 0.9f, score);
}

/// <summary>Dialog-Attrappe: vorgegebene Antworten, merkt sich die Fragen.</summary>
internal sealed class FakePersonsDialogs : IPersonsDialogService
{
    public string? NextName { get; set; }

    public bool ConfirmResult { get; set; } = true;

    public OutlierDecision OutlierDecision { get; set; } = OutlierDecision.SkipOutliers;

    public PersonSummary? NextPick { get; set; }

    public List<(string PersonName, IReadOnlyList<FaceOutlierResult> Outliers, int Total)> OutlierQuestions { get; } = [];

    public List<string> Confirmations { get; } = [];

    public List<(IReadOnlyList<PersonSummary> Persons, int? Suggested, int? Excluded)> Picks { get; } = [];

    public Task<string?> PromptNameAsync(string title, string message, string initialValue) => Task.FromResult(NextName);

    /// <summary>Nächste Antwort des Personen-Dialogs (0.2.47); ohne sie gilt <see cref="NextName"/>.</summary>
    public PersonDetailsInput? NextInput { get; set; }

    public List<PersonEditRequest> EditRequests { get; } = [];

    public Task<PersonDetailsInput?> EditPersonAsync(PersonEditRequest request)
    {
        EditRequests.Add(request);
        return Task.FromResult(NextInput ?? (NextName is { } name ? PersonDetailsInput.FromName(name) : null));
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
    {
        Confirmations.Add(message);
        return Task.FromResult(ConfirmResult);
    }

    public Task<OutlierDecision> AskOutliersAsync(string personName, IReadOnlyList<FaceOutlierResult> outliers, int total)
    {
        OutlierQuestions.Add((personName, outliers, total));
        return Task.FromResult(OutlierDecision);
    }

    public Task<PersonSummary?> PickPersonAsync(string title, IReadOnlyList<PersonSummary> persons, int? suggestedPersonId, int? excludedPersonId)
    {
        Picks.Add((persons, suggestedPersonId, excludedPersonId));
        return Task.FromResult(NextPick);
    }

    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
}
