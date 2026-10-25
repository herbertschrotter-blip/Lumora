using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Domain.Enums;
using Lumora.Persons.Controls;
using Lumora.Services.Faces.Assignment;

namespace Lumora.Persons.ViewModels;

/// <summary>Kachel mit einem Gesichts-Ausschnitt als Bild (Person: Titelbild, Gruppe: erstes Mitglied, Gesicht: es selbst).</summary>
public abstract partial class FaceImageTile : ObservableObject, IAutomationNamed
{
    /// <summary>Gesicht, dessen Ausschnitt die Kachel zeigt; null = Platzhalter.</summary>
    public abstract long? ImageFaceId { get; }

    public abstract string AutomationName { get; }

    [ObservableProperty] private ImageSource? _image;
}

/// <summary>Kachel einer benannten Person.</summary>
public sealed class PersonTileViewModel(PersonOverview overview) : FaceImageTile
{
    public PersonOverview Overview { get; } = overview;

    public int PersonId => Overview.Person.Id;

    public string Name => Overview.Person.Name;

    public bool IsHidden => Overview.Person.IsHidden;

    /// <summary>Beziehung (0.2.47, ADR-0045); null = keine.</summary>
    public string? RelationName => Overview.Person.Details?.RelationName;

    public override long? ImageFaceId => Overview.Person.CoverFaceId;

    // ---- Mockup 17 (ADR-0068): Kachel ohne abgeschnittenen Text, Chips, „automatisch gewählt“ ----

    /// <summary>Beziehung als eigene Zeile (leer = keine).</summary>
    public bool HasRelation => !string.IsNullOrEmpty(RelationName);

    /// <summary>Fotos mit bestätigtem Gesicht.</summary>
    public int ConfirmedCount => Overview.Person.FileCount;

    /// <summary>Chip „✓ n“ (grün).</summary>
    public string ConfirmedText => ConfirmedCount == 0 ? "0 Fotos" : ConfirmedCount.ToString("N0", CultureInfo.CurrentCulture);

    public bool HasConfirmed => ConfirmedCount > 0;

    public int SuggestionCount => Overview.SuggestionCount;

    /// <summary>Chip „n“ Vorschläge (gelb, Klick öffnet „Vorschläge“).</summary>
    public string SuggestionText => SuggestionCount.ToString("N0", CultureInfo.CurrentCulture);

    public bool HasSuggestions => SuggestionCount > 0;

    /// <summary>Titelbild automatisch gewählt (kleines Symbol am Bild).</summary>
    public bool IsCoverAutomatic => !Overview.Person.CoverIsManual && Overview.Person.CoverFaceId is not null;

    /// <summary>Tooltip der Kachel wie im Mockup.</summary>
    public string TileToolTip
    {
        get
        {
            var lines = new List<string> { RelationName is { } relation ? $"{Name} · {relation}" : Name };
            lines.Add($"✓ {ConfirmedCount.ToString("N0", CultureInfo.CurrentCulture)} bestätigt zugeordnet");
            if (HasSuggestions)
            {
                lines.Add($"{SuggestionText} Vorschläge – Klick auf den Chip öffnet „Vorschläge“");
            }

            lines.Add(Overview.Person.CoverFaceId is null
                ? "Titelbild: noch keins"
                : Overview.Person.CoverIsManual ? "Titelbild: von dir gewählt" : "Titelbild: automatisch (beste Aufnahme)");
            if (IsHidden)
            {
                lines.Add("ausgeblendet");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>„Schwester · 12 Fotos · 3 Vorschläge“.</summary>
    public string Details
    {
        get
        {
            var text = Overview.Person.FileCount == 1 ? "1 Foto" : $"{Overview.Person.FileCount.ToString("N0", CultureInfo.CurrentCulture)} Fotos";
            if (Overview.SuggestionCount > 0)
            {
                text += $" · {Overview.SuggestionCount.ToString("N0", CultureInfo.CurrentCulture)} Vorschl{(Overview.SuggestionCount == 1 ? "ag" : "äge")}";
            }

            if (RelationName is { } relation)
            {
                text = relation + " · " + text;
            }

            return IsHidden ? text + " · ausgeblendet" : text;
        }
    }

    public override string AutomationName => $"{Name}, {Details}";
}

/// <summary>Kachel einer Gruppe unbenannter Gesichter.</summary>
public sealed class GroupTileViewModel(FaceGroupInfo group) : FaceImageTile
{
    public FaceGroupInfo Group { get; } = group;

    public override long? ImageFaceId => Group.Members.Count > 0 ? Group.Members[0].FaceId : null;

    public string Title => $"{Group.Count.ToString("N0", CultureInfo.CurrentCulture)} Gesichter";

    /// <summary>„ähnelt Anna?“ bzw. leer.</summary>
    public string? Hint => Group.SuggestedPersonName is { } name ? $"ähnelt {name}?" : null;

    public override string AutomationName => Hint is null ? $"Gruppe, {Title}" : $"Gruppe, {Title}, {Hint}";
}

/// <summary>Ein Gesicht in der Detail- oder Gruppenansicht; die Auswahl hält die Kachel selbst.</summary>
public sealed partial class FaceTileViewModel : FaceImageTile
{
    public FaceTileViewModel(long faceId, int fileId, FaceState state, float? score, bool isOutlier = false, float? similarity = null)
    {
        FaceId = faceId;
        FileId = fileId;
        State = state;
        Score = score;
        IsOutlier = isOutlier;
        Similarity = similarity;
    }

    public long FaceId { get; }

    public int FileId { get; }

    public FaceState State { get; }

    public float? Score { get; }

    /// <summary>Gruppen-Ausreißer (deutlich markiert, standardmäßig abgewählt).</summary>
    public bool IsOutlier { get; }

    public float? Similarity { get; }

    /// <summary>Automatisch ignoriert, weil kleiner als die Mindestgröße (ADR-0040) — Kachel zeigt „zu klein“.</summary>
    public bool IsAutoIgnored { get; init; }

    /// <summary>Seite der Box in Pixeln (nur in der Ansicht „Ignorierte Gesichter“ gesetzt).</summary>
    public float? FaceSize { get; init; }

    public bool IsSuggestion => State == FaceState.Suggested;

    public override long? ImageFaceId => FaceId;

    /// <summary>Score bzw. Ähnlichkeit als Prozent („82 %“).</summary>
    public string? ScoreText => FaceSize is { } px
        ? $"{Math.Round(px):0} px"
        : (Score ?? Similarity) is { } v ? $"{Math.Round(v * 100):0} %" : null;

    [ObservableProperty] private bool _isSelected;

    public override string AutomationName
    {
        get
        {
            var kind = IsOutlier ? "Gesicht (möglicher Ausreißer)"
                : IsSuggestion ? "Vorschlag"
                : IsAutoIgnored ? "Ignoriertes Gesicht (zu klein)"
                : State == FaceState.Ignored ? "Ignoriertes Gesicht"
                : "Gesicht";
            var selected = IsSelected ? ", ausgewählt" : string.Empty;
            return ScoreText is { } s ? $"{kind} {FaceId}, {s}{selected}" : $"{kind} {FaceId}{selected}";
        }
    }
}

/// <summary>
/// Kachel-Liste mit nachgeladenen Ausschnitten: die Ansicht meldet den sichtbaren Bereich (virtualisiert), nur dafür
/// werden Ausschnitte angefordert; fertige Bilder werden über <see cref="Apply"/> zugeordnet.
/// </summary>
public sealed class TileCollection<T> : ObservableCollection<T>
    where T : FaceImageTile
{
    /// <summary>Zusätzlich angeforderte Kacheln vor/nach dem sichtbaren Bereich (flüssiges Scrollen).</summary>
    public const int Buffer = 24;

    private Dictionary<long, List<T>> _byFace = [];

    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        _byFace = Items.Where(t => t.ImageFaceId is not null)
            .GroupBy(t => t.ImageFaceId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>Gesichter ohne Bild im Bereich [first − Buffer, last + Buffer].</summary>
    public IReadOnlyList<long> MissingImages(int first, int last)
    {
        if (Count == 0 || first < 0 || last < first)
        {
            return [];
        }

        var from = Math.Max(0, first - Buffer);
        var to = Math.Min(Count - 1, last + Buffer);
        var list = new List<long>();
        for (var i = from; i <= to; i++)
        {
            if (this[i] is { Image: null, ImageFaceId: { } id })
            {
                list.Add(id);
            }
        }

        return list;
    }

    public void Apply(IReadOnlyList<(long FaceId, ImageSource Image)> images)
    {
        foreach (var (faceId, image) in images)
        {
            if (_byFace.TryGetValue(faceId, out var tiles))
            {
                foreach (var tile in tiles)
                {
                    tile.Image = image;
                }
            }
        }
    }
}
