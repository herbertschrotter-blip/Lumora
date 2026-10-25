using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Data;
using Lumora.Gallery.Motifs;
using Lumora.Services.Motifs;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Motive in der Galerie (ADR-0052): Chip „Motive“ in der Zeile „Inhalt“ mit Flyout (Begriffe des Bereichs mit Anzahl,
/// „eines davon“/„alle zusammen“), Bedingung in der „Es gilt“-Zeile, Motive je Datei für Listenspalte und Kachel-Tooltip.
/// Gelesen wird direkt über <see cref="GalleryPageSource"/>; Änderungen kommen über <see cref="MotifsChangedMessage"/>
/// (Index-Stapel, neu berechnete Treffer) und <see cref="MotifTermsChangedMessage"/> (Begriffe geändert).
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>So viele ähnlichste Fotos zeigt die Bildinhalt-Suche.</summary>
    public const int ContentSearchLimit = 200;

    /// <summary>
    /// Untergrenze für die Bildinhalt-Suche (Kosinus): unpassende Bild/Text-Paare liegen bei SigLIP 2 um 0,00–0,05,
    /// passende bei 0,09–0,14 (Spike 14) — darunter erscheint ein Foto auch in einer kleinen Bibliothek nicht.
    /// </summary>
    public const float ContentSearchMinCosine = 0.05f;

    private long _motifsRequestSequence;
    private CancellationTokenSource? _motifCountsDebounce;
    private IMotifTermService? _motifTerms;
    private string? _appliedContentSearch;
    private IReadOnlyList<int>? _contentSearchIds;

    /// <summary>Text der Bildinhalt-Suche im Flyout („Hund am Strand“).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchContentCommand))]
    private string? _contentSearchText;

    /// <summary>Bildinhalt-Suche verfügbar (Modell vorhanden).</summary>
    public bool CanSearchContent => _motifTerms is { IsModelAvailable: true };

    /// <summary>Angewendete Bildinhalt-Suche oder null.</summary>
    public string? AppliedContentSearch => _appliedContentSearch;

    /// <summary>Begriffe im Flyout (Häkchen + Zähler).</summary>
    public ObservableCollection<MotifFilterEntry> MotifFilterEntries { get; } = new();

    /// <summary>Chip „Motive“: sobald es Treffer gibt oder der Filter wirkt.</summary>
    [ObservableProperty] private bool _showMotifFilter;

    /// <summary>Verknüpfung: eines der gewählten Motive oder alle zusammen.</summary>
    [ObservableProperty] private GalleryMotifMode _motifFilterMode = GalleryMotifMode.Any;

    /// <summary>Begriffe gewählt oder Bildinhalt-Suche aktiv.</summary>
    public bool HasMotifFilter => MotifFilterEntries.Any(e => e.IsSelected) || _appliedContentSearch is not null;

    /// <summary>Wert im Chip „Motive: …“ („alle“ ohne Filter).</summary>
    public string MotifFilterValueText
    {
        get
        {
            var names = MotifFilterEntries.Where(e => e.IsSelected).Select(e => e.Name).ToList();
            var terms = names.Count switch
            {
                0 => null,
                1 => names[0],
                _ => string.Join(", ", names) + (MotifFilterMode == GalleryMotifMode.All ? " (alle)" : " (eines)"),
            };
            var content = _appliedContentSearch is { } text ? (_appliedSearchIsRelated ? text : $"„{text}“") : null; // ADR-0068: „Suchen ▸“
            return (terms, content) switch
            {
                (null, null) => "alle",
                ({ } t, null) => t,
                (null, { } c) => c,
                ({ } t, { } c) => $"{t} · {c}",
            };
        }
    }

    /// <summary>Bedingung der „Es gilt“-Zeile; null ohne Motiv-Filter.</summary>
    private string? MotifConditionLabel()
    {
        var names = MotifFilterEntries.Where(e => e.IsSelected).Select(e => $"„{e.Name}“").ToList();
        var terms = names.Count switch
        {
            0 => null,
            1 => $"Motiv {names[0]}",
            _ => MotifFilterMode == GalleryMotifMode.All ? $"Motive {string.Join(" und ", names)}" : $"Motiv {string.Join(" oder ", names)}",
        };
        var content = _appliedContentSearch is { } text ? (_appliedSearchIsRelated ? $"Fotos {text}" : $"Bildinhalt „{text}“") : null; // ADR-0068
        return terms is null ? content : content is null ? terms : $"{terms} · {content}";
    }

    private bool CanRunContentSearch() => CanSearchContent && !string.IsNullOrWhiteSpace(ContentSearchText);

    /// <summary>
    /// Bildinhalt-Suche („Hund am Strand“, ADR-0052): Text-Vektor gegen alle Bild-Vektoren des Bereichs, die
    /// <see cref="ContentSearchLimit"/> ähnlichsten Fotos werden zur Bedingung (mit allen übrigen Filtern UND).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunContentSearch))]
    private async Task SearchContentAsync()
    {
        var text = ContentSearchText?.Trim();
        if (_motifTerms is null || string.IsNullOrEmpty(text) || !HasLibrary)
        {
            return;
        }

        try
        {
            var hits = await _motifTerms.SearchAsync(_libraryId, text, ContentSearchLimit);
            _contentSearchIds = hits.Where(h => h.Score >= ContentSearchMinCosine).Select(h => h.FileId).ToList();
            _appliedContentSearch = text;
            _appliedSearchIsRelated = false;
            _logger.LogInformation("Galerie: Bildinhalt-Suche mit {Count} Treffern", _contentSearchIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bildinhalt-Suche gescheitert");
            ErrorMessage = $"Bildinhalt-Suche gescheitert: {ex.Message}";
            return;
        }

        NotifyMotifFilterChanged();
        await ChangeQueryAsync();
    }

    /// <summary>Leertext bei aktivem Motiv-Filter (null ohne Filter).</summary>
    private string? EmptyMessageMotifs => HasMotifFilter
        ? "Keine Fotos mit diesem Motiv — noch nicht analysiert oder nicht erkannt. Die Schwelle lässt sich unter Einstellungen › Motive anpassen."
        : null;

    partial void OnMotifFilterModeChanged(GalleryMotifMode value)
    {
        NotifyMotifFilterChanged();
        if (!_suppressReload && _isInitialized && MotifFilterEntries.Count(e => e.IsSelected) > 1)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    /// <summary>Häkchen im Flyout geändert → Filter anwenden.</summary>
    [RelayCommand]
    private Task ApplyMotifFilterAsync()
    {
        NotifyMotifFilterChanged();
        return _suppressReload || !_isInitialized ? Task.CompletedTask : ChangeQueryAsync();
    }

    /// <summary>„Leeren“ im Flyout bzw. × an der Bedingung.</summary>
    [RelayCommand]
    private Task ClearMotifFilterAsync()
    {
        if (!HasMotifFilter)
        {
            return Task.CompletedTask;
        }

        ResetMotifFilter();
        return ChangeQueryAsync();
    }

    /// <summary>Häkchen und Verknüpfung zurücksetzen (ohne Neuladen; Bibliothekswechsel, „Filter zurücksetzen“).</summary>
    private void ResetMotifFilter()
    {
        var suppress = _suppressReload;
        _suppressReload = true;
        try
        {
            foreach (var entry in MotifFilterEntries)
            {
                entry.IsSelected = false;
            }

            MotifFilterMode = GalleryMotifMode.Any;
            _appliedContentSearch = null;
            _appliedSearchIsRelated = false;
            _contentSearchIds = null;
            ContentSearchText = null;
        }
        finally
        {
            _suppressReload = suppress;
        }

        NotifyMotifFilterChanged();
    }

    private void NotifyMotifFilterChanged()
    {
        OnPropertyChanged(nameof(HasMotifFilter));
        OnPropertyChanged(nameof(AppliedContentSearch));
        OnPropertyChanged(nameof(CanSearchContent));
        OnPropertyChanged(nameof(MotifFilterValueText));
        OnPropertyChanged(nameof(EmptyMessage));
        UpdateShowMotifFilter();
        RefreshFilterIndicators();
    }

    private void UpdateShowMotifFilter() => ShowMotifFilter = HasMotifFilter || MotifFilterEntries.Any(e => e.FileCount > 0);

    /// <summary>Aus dem Konstruktor: Begriffs-Dienst für die Bildinhalt-Suche (optional, Tests ohne).</summary>
    private void InitMotifs(IMotifTermService? motifTerms) => _motifTerms = motifTerms;

    /// <summary>Motiv-Teil der Abfrage (Begriffe und Bildinhalt-Suche).</summary>
    private GalleryQuery WithMotifs(GalleryQuery query) => query with { MotifFilter = SelectedMotifFilter(), ContentSearchIds = _contentSearchIds };

    /// <summary>Filter für die Abfrage; null ohne Auswahl.</summary>
    private GalleryMotifFilter? SelectedMotifFilter()
    {
        var ids = MotifFilterEntries.Where(e => e.IsSelected).Select(e => e.TermId).ToList();
        return ids.Count == 0 ? null : new GalleryMotifFilter(ids, MotifFilterMode);
    }

    /// <summary>Begriffe und Zähler des Bereichs laden; Häkchen bleiben je Begriff erhalten, gelöschte fallen heraus.</summary>
    private async Task LoadMotifsAsync()
    {
        if (!HasLibrary || !_source.IsOpen)
        {
            return;
        }

        try
        {
            var rows = await _source.GetMotifCountsAsync(ScopeLibraryIds, CancellationToken.None);
            if (rows.Count == MotifFilterEntries.Count && rows.Select(r => (r.Id, r.Name)).SequenceEqual(MotifFilterEntries.Select(e => (e.TermId, e.Name))))
            {
                // Gleiche Begriffe (Index läuft): nur die Zähler nachziehen
                for (var i = 0; i < rows.Count; i++)
                {
                    MotifFilterEntries[i].FileCount = rows[i].FileCount;
                }

                UpdateShowMotifFilter();
                return;
            }

            var selected = MotifFilterEntries.Where(e => e.IsSelected).Select(e => e.TermId).ToHashSet();
            MotifFilterEntries.Clear();
            foreach (var row in rows)
            {
                MotifFilterEntries.Add(new MotifFilterEntry(row.Id, row.Name, row.FileCount) { IsSelected = selected.Contains(row.Id) });
            }

            NotifyMotifFilterChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Motive konnten nicht geladen werden");
        }
    }

    /// <summary>Motive der Dateien (Batch) laden und den Items der Page zuordnen.</summary>
    private async Task ApplyMotifsToItemsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0 || !_source.IsOpen)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _motifsRequestSequence);
        try
        {
            var rows = await _source.GetMotifsForFilesAsync(fileIds, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var byFile = rows.ToLookup(r => r.FileId);
            await DispatchAsync(() =>
            {
                foreach (var id in fileIds)
                {
                    if (_currentItems.TryGetValue(id, out var item) && item.MotifsSequence < sequence)
                    {
                        item.MotifsText = string.Join(", ", byFile[id].Select(r => r.Name));
                        item.MotifsSequence = sequence;
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Motive der Seite konnten nicht geladen werden");
        }
    }

    private void RegisterMotifMessages()
    {
        _messenger.Register<GalleryViewModel, MotifsChangedMessage>(this, static (r, m) => r.RunOnUi(() => r.HandleMotifsChangedAsync(m)));
        _messenger.Register<GalleryViewModel, MotifTermsChangedMessage>(this, static (r, m) => r.RunOnUi(() => r.HandleMotifTermsChangedAsync(m)));
    }

    /// <summary>
    /// Treffer geschrieben: Motive der betroffenen Dateien dieser Page nachziehen und die Zähler (gebündelt) neu lesen.
    /// Während der Index läuft, lädt die Trefferliste nicht neu (sonst spränge die Galerie alle paar Sekunden); nach
    /// einer Neuberechnung aller Treffer (leere Liste) mit aktivem Filter schon.
    /// </summary>
    private Task HandleMotifsChangedAsync(MotifsChangedMessage m)
    {
        if (!HasLibrary || !InMotifGroup(m.LibraryId))
        {
            return Task.CompletedTask;
        }

        ScheduleMotifCountsRefresh();
        if (m.FileIds.Count == 0 && HasMotifFilter)
        {
            ScheduleReload();
        }

        var ids = m.FileIds.Count == 0 ? _currentItems.Keys.ToList() : m.FileIds.Where(_currentItems.ContainsKey).ToList();
        return ApplyMotifsToItemsAsync(ids, CancellationToken.None);
    }

    private async Task HandleMotifTermsChangedAsync(MotifTermsChangedMessage m)
    {
        if (!HasLibrary || !InMotifGroup(m.LibraryId))
        {
            return;
        }

        var had = HasMotifFilter;
        await LoadMotifsAsync();
        if (had && !HasMotifFilter)
        {
            ScheduleReload(); // gewählter Begriff gelöscht
        }
    }

    /// <summary>
    /// Betrifft die Nachricht die Begriffe dieses Bereichs? Motiv-Begriffe gehören der Gruppe (ADR-0048) — Nachrichten
    /// nennen eine Bibliothek der Gruppe (bei Begriffsänderungen den Anker, beim Index die analysierte Bibliothek).
    /// </summary>
    private bool InMotifGroup(int libraryId) => InScope(libraryId) || _scope.GroupLibraryIds.Contains(libraryId);

    private void ScheduleMotifCountsRefresh()
    {
        _motifCountsDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _motifCountsDebounce = cts;
        FireAndForget(async () =>
        {
            try
            {
                await Task.Delay(1500, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await LoadMotifsAsync();
        });
    }
}
