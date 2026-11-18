using Lumora.Domain.Enums;

namespace Lumora.Services.Messaging;

/// <summary>
/// Ein zusammengefügtes Ergebnis wurde gespeichert (ADR-0078): Galerie (Ergebnis-Marke), Lightbox und später die Serien
/// (abgeleiteter Zustand „zusammengefügt“) laden nach. Rückgängig meldet sich wie jeder Datei-Vorgang über
/// <see cref="FilesChangedMessage"/>.
/// </summary>
public sealed record MergeSavedMessage(int LibraryId, int ResultFileId, IReadOnlyList<int> SourceFileIds, MergeMethod Method, int BatchId);
