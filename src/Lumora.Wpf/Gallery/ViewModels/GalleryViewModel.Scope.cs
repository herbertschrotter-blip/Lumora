using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Services.Library;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Bibliotheks-Bereich der Galerie (ADR-0048): eine Bibliothek oder die Ansicht „Alle verknüpften“. Abfragen, Zähler,
/// Personen-/Beziehungsfilter und Nachrichten-Prüfungen nehmen ihre Bibliotheks-Ids von hier. Aktionen, die eine
/// Ziel-Bibliothek brauchen (Verschieben, „In die Bibliothek“, Entpacken), sind in „Alle verknüpften“ gesperrt —
/// die Auswahl kann Dateien mehrerer Bibliotheken enthalten, und ein Ziel wäre nicht eindeutig. Was je Datei wirkt
/// (Favorit, Bewertung, Tags, Kategorien, Personen, Datum, Papierkorb), bleibt erlaubt.
/// </summary>
public sealed partial class GalleryViewModel
{
    private readonly ILibraryScope _libraryScope;
    private LibraryScopeInfo _scope = LibraryScopeInfo.None;

    /// <summary>true in der Ansicht „Alle verknüpften“.</summary>
    public bool IsAllLinkedView => _scope.IsAllLinked;

    /// <summary>Auswahl-Aktionen mit Ziel-Bibliothek: nur in der Ansicht einer einzelnen Bibliothek.</summary>
    public bool CanActOnLibrarySelection => CanActOnSelection && !IsAllLinkedView;

    /// <summary>Hinweis für gesperrte Aktionen (Tooltip, Statuszeile).</summary>
    public string AllLinkedActionHint => "In der Ansicht „Alle verknüpften“ nicht möglich — bitte eine einzelne Bibliothek wählen.";

    /// <summary>Bibliotheken des Bereichs (nie leer, solange eine Bibliothek geladen ist).</summary>
    private IReadOnlyList<int> ScopeLibraryIds => _scope.HasLibrary ? _scope.LibraryIds : [_libraryId];

    /// <summary>Bibliotheken des Galerie-Bereichs — nur für Meldungen des Rauchtests.</summary>
    internal IReadOnlyList<int> ScopeLibraryIdsForSmoke => ScopeLibraryIds;

    /// <summary>Für <c>GalleryQuery.LibraryIds</c>: null außer in „Alle verknüpften“ (dann bleibt die SQL wie bisher).</summary>
    private IReadOnlyList<int>? ScopeLibraryIdsForQuery => _scope.IsAllLinked ? _scope.LibraryIds : null;

    /// <summary>Gehört die Bibliothek zum angezeigten Bereich (Nachrichten, Thumbnails)?</summary>
    private bool InScope(int libraryId) => libraryId == _libraryId || _scope.Contains(libraryId);

    /// <summary>
    /// Liest den Bereich der Bibliothek und übernimmt ihn nur, wenn <paramref name="generation"/> noch der neueste
    /// Bibliothekswechsel ist (sonst false). Rauchtest-Wackler 0.4.41 (Issue #20): Beim Lösen einer Verknüpfung kam der
    /// Wechsel auf B und gleich danach der auf A; las B seinen Bereich langsamer, überschrieb er den von A — Seite aus A,
    /// Flyouts (Orte, Motive, Personen) aus B.
    /// </summary>
    private async Task<bool> LoadScopeAsync(int libraryId, int generation)
    {
        LibraryScopeInfo scope;
        try
        {
            scope = await _libraryScope.GetCurrentAsync();
            if (scope.LibraryId != libraryId)
            {
                scope = await _libraryScope.GetForLibraryAsync(libraryId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Galerie: Bibliotheks-Bereich nicht lesbar — nur die aktive Bibliothek");
            scope = LibraryScopeInfo.ForLibrary(libraryId, 0);
        }

        if (generation != _libraryLoadGeneration)
        {
            return false;
        }

        _scope = scope;
        NotifyScopeChanged();
        return true;
    }

    /// <summary>
    /// true, solange die Galerie noch arbeitet: Seite lädt oder ein Bibliothekswechsel läuft (auch vor der ersten Seite:
    /// Bereich, Personen, Motive, Begriffe). Für Warte-Helfer (Rauchtest), die sonst nur <see cref="IsLoading"/> sahen und
    /// während eines langsamen Wechsels zu früh weitermachten. Gebündelte Neuladungen zählen bewusst nicht: Hintergrund-
    /// Indizes stoßen sie laufend an, ein Warten darauf endete nie.
    /// </summary>
    internal bool HasPendingWork => IsLoading || _libraryReloadsRunning > 0;

    private int _libraryReloadsRunning;

    private void ClearScope()
    {
        _scope = LibraryScopeInfo.None;
        NotifyScopeChanged();
    }

    /// <summary>Anzeigename: in „Alle verknüpften“ der Gruppenname, sonst „Name · Pfad“.</summary>
    private string ScopeDisplayName(string single)
        => _scope.IsAllLinked ? $"Alle verknüpften ({_scope.GroupName}) · {_scope.LibraryIds.Count} Bibliotheken" : single;

    private void NotifyScopeChanged()
    {
        OnPropertyChanged(nameof(IsAllLinkedView));
        OnPropertyChanged(nameof(CanActOnLibrarySelection));
        OnPropertyChanged(nameof(CanExtractSelection));
        MoveSelectionCommand.NotifyCanExecuteChanged();
        ArchiveSelectionCommand.NotifyCanExecuteChanged();
        ExtractSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>true (mit Hinweis), wenn die Aktion in „Alle verknüpften“ gesperrt ist.</summary>
    private async Task<bool> RejectInAllLinkedViewAsync(string action)
    {
        if (!IsAllLinkedView)
        {
            return false;
        }

        SetResultStatus($"{action}: {AllLinkedActionHint}");
        await _dialogs.ShowErrorsAsync(action, AllLinkedActionHint, []);
        return true;
    }
}
