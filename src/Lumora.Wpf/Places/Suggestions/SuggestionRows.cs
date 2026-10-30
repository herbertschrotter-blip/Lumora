using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Domain.Enums;
using Lumora.Services.Places;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Suggestions;

namespace Lumora.Places.Suggestions;

/// <summary>Filter-Chips der Liste „Ungenau“ (Mockup 10).</summary>
public enum SuggestionFilter
{
    All,
    Track,
    Sure,
    Likely,
    None,

    /// <summary>Spur und Zeit liegen weit auseinander (Mockup 25).</summary>
    Disagree,
}

/// <summary>Eine Quelle im Vergleich (Mockup 25).</summary>
public enum SuggestionSourceKind
{
    /// <summary>„laut Foto“: gespeicherte, geschätzte Position.</summary>
    Stored,

    /// <summary>„laut Spur“: GPS-Spur zur Aufnahmezeit.</summary>
    Track,

    /// <summary>„laut Zeit“: zwischen bzw. neben genauen GPS-Fotos.</summary>
    Time,
}

/// <summary>Bewertung des Vergleichs unter den Quellen: passt, Vorsicht, nur eine Quelle.</summary>
public enum SuggestionVerdictKind
{
    Ok,
    Warn,
    Info,
}

/// <summary>Bereich der Seitenleiste.</summary>
public enum PlacesSideMode
{
    /// <summary>„Ohne Ort“: Kacheln zum Ziehen (ADR-0051).</summary>
    Unlocated,

    /// <summary>„Ungenau“: geschätzte Positionen mit Vorschlägen.</summary>
    Inaccurate,

    /// <summary>„Ohne Ort“ mit Vorschlag (Liste wie „Ungenau“).</summary>
    UnlocatedSuggestions,
}

/// <summary>Ein Filter-Chip mit Zähler.</summary>
public sealed partial class SuggestionFilterChip(SuggestionFilter filter, string label) : ObservableObject
{
    public SuggestionFilter Filter { get; } = filter;

    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private int _count;

    [ObservableProperty] private bool _isSelected;

    public string Text => Filter == SuggestionFilter.All ? Label : $"{Label} {PlacesSuggestionText.N(Count)}";
}

/// <summary>Kopfzeile eines Tages bzw. Ausflugs.</summary>
public sealed partial class SuggestionGroupRow(string key, string title) : ObservableObject
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    [ObservableProperty] private string _detail = string.Empty;

    /// <summary>Fotos der Gruppe mit (noch offenem) Vorschlag im aktuellen Filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private int _openSuggestions;

    public bool CanApply => OpenSuggestions > 0;

    public List<SuggestionItemRow> Items { get; } = new();
}

/// <summary>Ein Foto der Liste.</summary>
public sealed partial class SuggestionItemRow : ObservableObject
{
    public SuggestionItemRow(GeoSuggestionItem item, SuggestionGroupRow group, int displayOffsetMinutes)
    {
        Item = item;
        Group = group;
        DisplayOffsetMinutes = displayOffsetMinutes;
        Latitude = item.Suggestion?.Latitude;
        Longitude = item.Suggestion?.Longitude;
        _chosenSource = item.Suggestion?.Kind switch
        {
            GeoSuggestionKind.Track => SuggestionSourceKind.Track,
            GeoSuggestionKind.Interpolated or GeoSuggestionKind.Neighbor => SuggestionSourceKind.Time,
            _ => null,
        };
        Sources = BuildSources();
    }

    /// <summary>Ganz knapp beieinander (≤ 40 m): Übernehmen ändert kaum etwas.</summary>
    public const double CloseMeters = 40;

    /// <summary>Die drei Quellen in der Reihenfolge laut Foto, laut Spur, laut Zeit (Mockup 25).</summary>
    public IReadOnlyList<SuggestionSourceRow> Sources { get; }

    /// <summary>Gewählte Quelle des Vorschlags (null ohne Vorschlag bzw. von Hand verschoben).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Level), nameof(BadgeKind), nameof(BadgeText))]
    private SuggestionSourceKind? _chosenSource;

    /// <summary>Hervorgehobene Quelle (Klick bzw. Taste 1–3): Punkt auf der Karte größer.</summary>
    [ObservableProperty] private SuggestionSourceKind? _highlightedSource;

    partial void OnChosenSourceChanged(SuggestionSourceKind? value)
    {
        foreach (var source in Sources)
        {
            source.IsChosen = source.Kind == value;
        }
    }

    /// <summary>Quelle wählen: der Vorschlag springt auf ihren Punkt (Knopf „Nehmen“, Taste 2/3).</summary>
    public bool Choose(SuggestionSourceKind kind)
    {
        if (IsApplied || kind == SuggestionSourceKind.Stored
            || Sources.FirstOrDefault(s => s.Kind == kind) is not { IsAvailable: true, Latitude: { } lat, Longitude: { } lon })
        {
            return false;
        }

        Latitude = lat;
        Longitude = lon;
        IsMoved = false;
        ChosenSource = kind;
        HighlightedSource = kind;
        return true;
    }

    /// <summary>Satz unter den Quellen: stimmen sie überein, wo hakt es (Mockup 25).</summary>
    public string Verdict => BuildVerdict().Text;

    public SuggestionVerdictKind VerdictKind => BuildVerdict().Kind;

    /// <summary>Uhr-Hinweis („Geht die Kamerauhr 22 min vor, …“), null ohne nahe Spur.</summary>
    public string? ClockHintText => Item.Sources?.NearTrack is { } hint
        ? $"Geht die Kamerauhr {Minutes(Math.Abs(hint.OffsetSeconds))} min {(hint.OffsetSeconds < 0 ? "vor" : "nach")}, läge das Foto auf der Spur{(Item.Track is { } t ? $" „{t.Title}“" : string.Empty)} (heller Punkt auf der Karte)."
        : null;

    private (string Text, SuggestionVerdictKind Kind) BuildVerdict()
    {
        var c = Item.Sources;
        var track = c?.FromTrack;
        var time = c?.FromTime;
        var trackMeters = Sources[1].Meters;
        var timeMeters = Sources[2].Meters;
        if (c is { TrackToTimeMeters: { } apart } && apart <= CloseMeters && trackMeters <= CloseMeters && timeMeters <= CloseMeters)
        {
            var most = Math.Max(apart, Math.Max(trackMeters!.Value, timeMeters!.Value));
            return ($"Alle Quellen liegen innerhalb von {GeoMath.FormatDistance(most)} — Übernehmen ändert kaum etwas, Ablehnen ist genauso gut.", SuggestionVerdictKind.Ok);
        }

        if (c is { TrackInGap: true } && track is not null)
        {
            var gap = Minutes(track.GapSeconds ?? 0);
            return time is not null
                ? ($"Die Spur hat hier eine Lücke von {gap} min — dort ist sie nur geschätzt. Die GPS-Fotos davor und danach sind näher dran.", SuggestionVerdictKind.Warn)
                : ($"Die Spur hat hier eine Lücke von {gap} min — dort ist sie nur geradlinig geschätzt.", SuggestionVerdictKind.Warn);
        }

        if (c is { TrackToTimeMeters: { } distance })
        {
            var storedText = trackMeters is { } m ? $" Die Position laut Foto liegt {GeoMath.FormatDistance(m)} daneben." : string.Empty;
            return c.Disagree
                ? ($"Spur und Zeit liegen {GeoMath.FormatDistance(distance)} auseinander — eine der Quellen stimmt nicht. Geht die Kamerauhr richtig?", SuggestionVerdictKind.Warn)
                : ($"Spur und Zeit liegen {GeoMath.FormatDistance(distance)} auseinander und stimmen überein.{storedText}", SuggestionVerdictKind.Ok);
        }

        if (track is null && time is null)
        {
            return ClockHintText is { } clock
                ? ($"Keine Spur und keine GPS-Fotos zur Aufnahmezeit. {clock}", SuggestionVerdictKind.Warn)
                : ("Nur die Position laut Foto — keine Spur und keine GPS-Fotos in der Nähe.", SuggestionVerdictKind.Info);
        }

        if (track is null && ClockHintText is { } hint)
        {
            return ($"Nur eine Quelle. {hint}", SuggestionVerdictKind.Warn);
        }

        return (track is not null ? "Nur eine Quelle: die Spur." : "Nur eine Quelle: die GPS-Fotos davor bzw. danach.", SuggestionVerdictKind.Info);
    }

    private static int Minutes(long seconds) => Math.Max(1, (int)Math.Round(seconds / 60.0));

    private string ClockOf(long unix, string format)
        => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.AddMinutes(DisplayOffsetMinutes).ToString(format, CultureInfo.GetCultureInfo("de-DE"));

    private SuggestionSourceRow[] BuildSources()
    {
        var c = Item.Sources;
        double? FromStored(GeoSuggestion s) => Item.StoredLatitude is { } lat && Item.StoredLongitude is { } lon
            ? GeoMath.DistanceMeters(lat, lon, s.Latitude, s.Longitude)
            : null;

        var stored = Item.HasStored
            ? new SuggestionSourceRow(SuggestionSourceKind.Stored, "laut Foto", "gespeicherte Position (geschätzt)",
                GeoAccuracyRules.Describe(Item.Reason), Item.StoredLatitude, Item.StoredLongitude, null)
            : new SuggestionSourceRow(SuggestionSourceKind.Stored, "laut Foto", "ohne Ort", "das Foto hat keine Koordinaten", null, null, null);

        SuggestionSourceRow track;
        if (c?.FromTrack is { } t)
        {
            var name = Item.Track is { } info ? $"Spur „{info.Title}“" + (info.Device is { } d ? $" ({d})" : string.Empty) : "GPS-Spur";
            var at = Item.Utc is { } utc ? " · " + ClockOf(utc, "HH:mm:ss") : string.Empty;
            var gap = t.GapSeconds ?? 0;
            var detail = t.Level == GeoSuggestionLevel.None
                ? $"in einer Lücke von {Minutes(gap)} min · gerade Linie geschätzt"
                : gap > GeoSuggestionEngine.TrackShortGapSeconds ? $"Lücke von {Minutes(gap)} min in der Spur" : $"Spurpunkte {gap} s auseinander";
            track = new SuggestionSourceRow(SuggestionSourceKind.Track, "laut Spur", name + at, detail, t.Latitude, t.Longitude, FromStored(t));
        }
        else
        {
            var detail = c?.NearTrack is { } near && Item.Track is { } info
                ? $"die Spur „{info.Title}“ {(near.OffsetSeconds < 0 ? "endet" : "beginnt")} {Minutes(Math.Abs(near.OffsetSeconds))} min {(near.OffsetSeconds < 0 ? "davor" : "danach")}"
                : "keine GPS-Spur innerhalb von 2 Stunden";
            track = new SuggestionSourceRow(SuggestionSourceKind.Track, "laut Spur", "keine Spur zur Aufnahmezeit", detail, null, null, null);
        }

        SuggestionSourceRow time;
        if (c?.FromTime is { } z && Item.Utc is { } t0)
        {
            string line, detail;
            if (z.Kind == GeoSuggestionKind.Interpolated && z.AnchorBefore is { } b && z.AnchorAfter is { } a)
            {
                line = $"zwischen GPS-Fotos {ClockOf(b.Utc, "HH:mm")} und {ClockOf(a.Utc, "HH:mm")}";
                detail = $"{Minutes(a.Utc - b.Utc)} min Abstand";
            }
            else if ((z.AnchorBefore ?? z.AnchorAfter) is { } n)
            {
                line = $"GPS-Foto {ClockOf(n.Utc, "HH:mm")} ({Minutes(Math.Abs(t0 - n.Utc))} min {(n.Utc <= t0 ? "davor" : "danach")})";
                detail = "nur ein genaues Foto in der Nähe";
            }
            else
            {
                line = "GPS-Foto in der Nähe";
                detail = string.Empty;
            }

            time = new SuggestionSourceRow(SuggestionSourceKind.Time, "laut Zeit", line, detail, z.Latitude, z.Longitude, FromStored(z));
        }
        else
        {
            time = new SuggestionSourceRow(SuggestionSourceKind.Time, "laut Zeit", "keine GPS-Fotos in der Nähe", "innerhalb von 15 min davor oder danach", null, null, null);
        }

        SuggestionSourceRow[] list = [stored, track, time];
        foreach (var s in list)
        {
            s.Owner = this;
            s.IsChosen = s.Kind == ChosenSource;
        }

        return list;
    }

    public GeoSuggestionItem Item { get; }

    public SuggestionGroupRow Group { get; }

    public int DisplayOffsetMinutes { get; }

    public int FileId => Item.FileId;

    /// <summary>Vorschlag (bzw. von Hand verschobener Punkt).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestion), nameof(Detail))]
    private double? _latitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestion), nameof(Detail))]
    private double? _longitude;

    /// <summary>Auf der Karte von Hand verschoben (wird als „manuell“ gespeichert).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText), nameof(Detail))]
    private bool _isMoved;

    /// <summary>Übernommen (Rückgängig bis zum Neuladen in der Zeile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText), nameof(BadgeKind), nameof(Detail), nameof(CanApply), nameof(IsOpen))]
    private bool _isApplied;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private ImageSource? _image;

    /// <summary>Protokoll-Batch des Übernehmens (für „Rückgängig“ in der Zeile).</summary>
    public int? BatchId { get; set; }

    public bool HasSuggestion => Latitude is not null && Longitude is not null;

    public bool CanApply => HasSuggestion && !IsApplied;

    public bool IsOpen => !IsApplied;

    public bool HasStored => Item.HasStored;

    /// <summary>Stufe der gewählten Quelle (nach einem Wechsel die Stufe dieser Quelle).</summary>
    public GeoSuggestionLevel Level => ChosenSource switch
    {
        SuggestionSourceKind.Track when Item.Suggestion?.Kind == GeoSuggestionKind.Track => Item.Level,
        SuggestionSourceKind.Track => Item.Sources?.FromTrack is { Level: not GeoSuggestionLevel.None } t ? t.Level : GeoSuggestionLevel.Likely,
        SuggestionSourceKind.Time when Item.Suggestion?.Kind is GeoSuggestionKind.Interpolated or GeoSuggestionKind.Neighbor => Item.Level,
        SuggestionSourceKind.Time => Item.Sources?.FromTime?.Level ?? GeoSuggestionLevel.Likely,
        _ => Item.Level,
    };

    /// <summary>Herkunft beim Übernehmen: von Hand verschoben, Spur oder Nachbarfotos.</summary>
    public GeoSource SourceOnApply => IsMoved ? GeoSource.Manual
        : ChosenSource == SuggestionSourceKind.Track ? GeoSource.Track
        : ChosenSource == SuggestionSourceKind.Time ? GeoSource.Neighbors
        : Item.SourceOnApply;

    /// <summary>„14:56 · Gipfelkreuz.jpg“.</summary>
    public string Title => Item.LocalTime is { } t ? $"{t.ToString("HH:mm", CultureInfo.GetCultureInfo("de-DE"))} · {Item.Name}" : Item.Name;

    /// <summary>Stil der Plakette: garmin, sure, likely, none, done.</summary>
    public string BadgeKind => IsApplied ? "done" : Level switch
    {
        GeoSuggestionLevel.Track => "garmin",
        GeoSuggestionLevel.Sure => "sure",
        GeoSuggestionLevel.Likely => "likely",
        _ => "none",
    };

    public string BadgeText => IsApplied ? "übernommen" : IsMoved ? "verschoben" : GeoSuggestionText.LevelName(Level);

    public string Detail
    {
        get
        {
            if (IsApplied)
            {
                return "korrigiert";
            }

            if (IsMoved && Latitude is { } lat && Longitude is { } lon)
            {
                var distance = Item.StoredLatitude is { } sl && Item.StoredLongitude is { } so ? $" → {GeoMath.FormatDistance(GeoMath.DistanceMeters(sl, so, lat, lon))} korrigieren" : string.Empty;
                return "von Hand auf der Karte gesetzt" + distance;
            }

            return GeoSuggestionText.Detail(Item, DisplayOffsetMinutes);
        }
    }

    /// <summary>Quelle in Worten (Tooltip, Lightbox).</summary>
    public string SourceText => GeoSuggestionText.Source(Item, DisplayOffsetMinutes);

    public string AutomationName => $"{Title}, {BadgeText}";
}

/// <summary>Texte der Vorschlagsliste.</summary>
public static class PlacesSuggestionText
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string N(int value) => value.ToString("N0", De);

    /// <summary>„Sonntag, 15. März 2020“.</summary>
    public static string Day(DateTime day) => day.ToString("dddd, d. MMMM yyyy", De);

    /// <summary>„Weinebene · 5 Fotos, 4 Vorschläge“.</summary>
    public static string GroupDetail(string? place, int count, int suggestions)
    {
        var text = $"{N(count)} {(count == 1 ? "Foto" : "Fotos")}, {N(suggestions)} {(suggestions == 1 ? "Vorschlag" : "Vorschläge")}";
        return string.IsNullOrWhiteSpace(place) ? text : $"{place} · {text}";
    }

    /// <summary>Zusammenfassung oben in der Seitenleiste.</summary>
    public static string Summary(PlacesSideMode mode, int total, int track, int sure, int likely, int none)
    {
        var who = mode == PlacesSideMode.Inaccurate
            ? $"{N(total)} {(total == 1 ? "Foto" : "Fotos")} mit geschätzter Position (ohne GPS-Höhe/-Zeit, über Mobilfunk/WLAN oder unmöglicher Sprung)."
            : $"{N(total)} {(total == 1 ? "Foto" : "Fotos")} ohne Ort mit Aufnahmezeit.";
        return $"{who} Vorschläge zuerst aus Ihren GPS-Spuren, sonst aus zeitnahen Fotos mit echtem GPS: {N(track)} Garmin-Spur · {N(sure)} sicher · {N(likely)} wahrscheinlich · {N(none)} ohne Vorschlag.";
    }

    public static bool Matches(SuggestionFilter filter, GeoSuggestionLevel level) => filter switch
    {
        SuggestionFilter.Track => level == GeoSuggestionLevel.Track,
        SuggestionFilter.Sure => level == GeoSuggestionLevel.Sure,
        SuggestionFilter.Likely => level == GeoSuggestionLevel.Likely,
        SuggestionFilter.None => level == GeoSuggestionLevel.None,
        SuggestionFilter.Disagree => false,
        _ => true,
    };

    /// <summary>Filter auf ein Foto (Stufe bzw. „Quellen uneinig“).</summary>
    public static bool Matches(SuggestionFilter filter, GeoSuggestionItem item)
        => filter == SuggestionFilter.Disagree ? item.Sources is { Disagree: true } : Matches(filter, item.Level);

    /// <summary>Gruppen nach Tag der Aufnahme (Wanduhrzeit), neueste zuerst; ohne Datum am Ende.</summary>
    public static IReadOnlyList<(string Key, string Title, List<GeoSuggestionItem> Items)> GroupByDay(IEnumerable<GeoSuggestionItem> items)
        => items
            .GroupBy(i => i.LocalTime?.Date)
            .OrderByDescending(g => g.Key ?? DateTime.MinValue)
            .Select(g => (g.Key?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-", g.Key is { } d ? Day(d) : "Ohne Aufnahmedatum", g.OrderBy(i => i.LocalTime).ThenBy(i => i.FileId).ToList()))
            .ToList();
}

/// <summary>Eine Quelle im Vergleich unter dem ausgewählten Foto (Mockup 25).</summary>
public sealed partial class SuggestionSourceRow(
    SuggestionSourceKind kind,
    string title,
    string line,
    string detail,
    double? latitude,
    double? longitude,
    double? meters) : ObservableObject
{
    /// <summary>Zeile des Fotos (gesetzt beim Bauen der Quellen).</summary>
    public SuggestionItemRow Owner { get; internal set; } = null!;

    public SuggestionSourceKind Kind { get; } = kind;

    /// <summary>„laut Foto“, „laut Spur“, „laut Zeit“.</summary>
    public string Title { get; } = title;

    public string Line { get; } = line;

    public string DetailText { get; } = detail;

    public double? Latitude { get; } = latitude;

    public double? Longitude { get; } = longitude;

    /// <summary>Abstand zum Punkt laut Foto (null für „laut Foto“ selbst bzw. ohne Punkt).</summary>
    public double? Meters { get; } = meters;

    public bool IsAvailable => Latitude is not null && Longitude is not null;

    /// <summary>„1,2 km“ bzw. „—“.</summary>
    public string DistanceText => Meters is { } m ? GeoMath.FormatDistance(m) : "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTake))]
    private bool _isChosen;

    /// <summary>Knopf „Nehmen“: verfügbare Quelle, die nicht schon gewählt ist.</summary>
    public bool CanTake => Kind != SuggestionSourceKind.Stored && IsAvailable && !IsChosen;

    /// <summary>Knopf „Behalten“ (= Ablehnen) nur bei „laut Foto“ mit Punkt.</summary>
    public bool CanKeep => Kind == SuggestionSourceKind.Stored && IsAvailable;

    /// <summary>Tastenkürzel 1–3.</summary>
    public int Key => (int)Kind + 1;

    public string AutomationName => $"{Title}: {Line}, {DistanceText}";
}
