namespace Lumora.Services.Edit;

/// <summary>Feste Vorlagen der KI-Bearbeitung (ADR-0076 Punkt 6) — es gibt keinen freien Arbeitsablauf.</summary>
public enum EditTemplate
{
    Background,
    Outfit,
    Remove,
    Expand,
    Enhance,
}

/// <summary>Was der Benutzer bearbeiten will: Vorlage, optional kurzer Text (nur Hintergrund) bzw. Kleidung aus der Liste.</summary>
public sealed record EditRequest(EditTemplate Template, string? FreeText = null, string? OutfitId = null);

/// <summary>
/// Wer auf dem Bild zu sehen ist (buffalo_l): Zahl der Gesichter und das jüngste geschätzte Alter. Gesicht = echte Person
/// (ADR-0076 Punkt 8.3). Alter unbekannt (Modell fehlt, Schätzung gescheitert) gilt wie „unter 21“ — im Zweifel streng.
/// </summary>
public sealed record EditSubject(int FaceCount, double? YoungestAge)
{
    public static EditSubject Nobody { get; } = new(0, null);

    public bool HasPeople => FaceCount > 0;

    /// <summary>Eine Person könnte unter <see cref="EditPolicy.AdultAgeMargin"/> sein (oder das Alter ist unbekannt).</summary>
    public bool MayBeYoung => HasPeople && (YoungestAge is null || YoungestAge < EditPolicy.AdultAgeMargin);

    /// <summary>Personen beider Bilder zusammen (Original und Ergebnis): ein Gesicht im Original zählt auch für das Ergebnis.</summary>
    public static EditSubject Combine(EditSubject a, EditSubject b)
    {
        var faces = Math.Max(a.FaceCount, b.FaceCount);
        if (faces == 0)
        {
            return Nobody;
        }

        if ((a.HasPeople && a.YoungestAge is null) || (b.HasPeople && b.YoungestAge is null))
        {
            return new EditSubject(faces, null);
        }

        return new EditSubject(faces, new[] { a, b }.Where(s => s.HasPeople).Min(s => s.YoungestAge!.Value));
    }
}

/// <summary>Entscheidung der Vorprüfung; <see cref="Reason"/> sachlich für den Benutzer.</summary>
public sealed record EditDecision(bool Allowed, string? Reason)
{
    public static EditDecision Allow { get; } = new(true, null);

    public static EditDecision Deny(string reason) => new(false, reason);
}

/// <summary>Was die Prüfmodelle auf einem Ergebnis sehen: höchste Sicherheit je NudeNet-Klasse und „nsfw“ des ViT (0..1).</summary>
public sealed record EditContentScan(IReadOnlyDictionary<string, float> Parts, float NsfwProbability)
{
    public static EditContentScan Clean { get; } = new(new Dictionary<string, float>(), 0);

    public float Part(string name) => Parts.TryGetValue(name, out var score) ? score : 0;
}

/// <summary>Urteil über ein Ergebnis: behalten oder verwerfen (dann nichts zeigen, nichts speichern, Arbeitsdateien löschen).</summary>
public sealed record EditResultVerdict(bool Keep, string Reason);
