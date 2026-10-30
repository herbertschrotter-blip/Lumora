namespace Lumora.Services.Edit;

/// <summary>Grenzen der KI-Bearbeitung (ADR-0076 Punkt 8) — gelten ohne Schalter.</summary>
public interface IEditPolicy
{
    /// <summary>Vorprüfung vor dem Senden: Vorlage, Kleidungsliste, Begriffsliste, Altersgrenze für „Outfit“.</summary>
    EditDecision CheckRequest(EditRequest request, EditSubject subject);

    /// <summary>Ergebnisprüfung: freizügig und eine echte Person zu sehen → verwerfen.</summary>
    EditResultVerdict CheckResult(EditContentScan scan, EditSubject subject);

    /// <summary>Feste Negativ-Beschreibung, die jede Vorlage anhängt (8.2).</summary>
    string NegativePrompt { get; }
}

/// <summary>
/// Grenzen der KI-Bearbeitung (ADR-0076 Punkt 8, Stufe 2). Sie schützen vor Versehen und Fehlbedienung, nicht gegen den
/// Besitzer des PCs (8, letzter Absatz). Lumora liefert für sexualisierte Bearbeitungen echter Personen keine Vorlage,
/// und das Ergebnis wird vor dem Anzeigen geprüft.
/// <list type="bullet">
/// <item>Ist eine Person zu sehen (Gesicht im Original oder im Ergebnis), wird ein Ergebnis verworfen, sobald NudeNet eine
/// entblößte Intimzone oder der ViT „nsfw“ meldet (Schwellen unten).</item>
/// <item>Könnte eine Person unter 21 sein (geschätzt, oder Alter unbekannt), gelten niedrigere Schwellen; dann zählen auch
/// bedeckte Intimzonen (Unterwäsche, Bademode), und „Outfit“ ist für dieses Foto gesperrt.</item>
/// <item>Ohne Gesicht wird ein Ergebnis mit deutlicher Nacktheit trotzdem verworfen — ein Ausschnitt ohne Kopf kann
/// ebenso eine echte Person zeigen.</item>
/// </list>
/// Im Zweifel streng: ein Fehler der Prüfmodelle heißt verwerfen (<see cref="EditResultChecker"/>).
/// </summary>
public sealed class EditPolicy : IEditPolicy
{
    /// <summary>Unter diesem geschätzten Alter gilt eine Person als möglicherweise minderjährig (Sicherheitsabstand zu 18).</summary>
    public const double AdultAgeMargin = 21;

    /// <summary>Höchstens so viele Zeichen Freitext (nur Hintergrund).</summary>
    public const int MaxFreeTextLength = 200;

    /// <summary>NudeNet-Klassen für entblößte Intimzonen.</summary>
    public static readonly string[] ExposedParts =
        ["FEMALE_BREAST_EXPOSED", "FEMALE_GENITALIA_EXPOSED", "MALE_GENITALIA_EXPOSED", "BUTTOCKS_EXPOSED", "ANUS_EXPOSED"];

    /// <summary>NudeNet-Klassen für bedeckte Intimzonen (nur bei möglicherweise jungen Personen).</summary>
    public static readonly string[] CoveredParts =
        ["FEMALE_BREAST_COVERED", "FEMALE_GENITALIA_COVERED", "BUTTOCKS_COVERED", "ANUS_COVERED"];

    /// <summary>Erwachsene: entblößt ab dieser Sicherheit.</summary>
    public const float ExposedThreshold = 0.35f;

    /// <summary>Erwachsene: ViT „nsfw“ ab dieser Wahrscheinlichkeit.</summary>
    public const float NsfwThreshold = 0.8f;

    /// <summary>Möglicherweise unter 21: entblößt bzw. „nsfw“ schon ab diesen Werten.</summary>
    public const float YoungExposedThreshold = 0.2f;

    public const float YoungNsfwThreshold = 0.5f;

    /// <summary>Möglicherweise unter 21: bedeckte Intimzonen ab dieser Sicherheit.</summary>
    public const float YoungCoveredThreshold = 0.5f;

    /// <summary>Ohne Gesicht: nur deutliche Nacktheit.</summary>
    public const float NoFaceExposedThreshold = 0.6f;

    public string NegativePrompt => "nudity, naked, nude, topless, cleavage, lingerie, underwear, swimwear, bikini, see-through clothing, sexual content, nsfw, revealing clothing";

    public EditDecision CheckRequest(EditRequest request, EditSubject subject)
    {
        if (!Enum.IsDefined(request.Template))
        {
            return EditDecision.Deny("Unbekannte Vorlage.");
        }

        if (request.Template == EditTemplate.Outfit)
        {
            if (!string.IsNullOrWhiteSpace(request.FreeText))
            {
                return EditDecision.Deny("„Outfit“ hat keinen freien Text — bitte eine Kleidung aus der Liste wählen.");
            }

            if (OutfitCatalog.Find(request.OutfitId) is null)
            {
                return EditDecision.Deny("Bitte eine Kleidung aus der Liste wählen.");
            }

            if (subject.MayBeYoung)
            {
                return EditDecision.Deny(subject.YoungestAge is null
                    ? "„Outfit“ ist für dieses Foto gesperrt: Das Alter der Person ließ sich nicht schätzen."
                    : "„Outfit“ ist für dieses Foto gesperrt: Eine Person ist möglicherweise jünger als 21.");
            }
        }
        else if (request.OutfitId is not null)
        {
            return EditDecision.Deny("Eine Kleidung gibt es nur in der Vorlage „Outfit“.");
        }

        if (request.FreeText is { Length: > 0 } text)
        {
            if (request.Template != EditTemplate.Background)
            {
                return EditDecision.Deny("Freien Text gibt es nur bei „Hintergrund“.");
            }

            if (text.Length > MaxFreeTextLength)
            {
                return EditDecision.Deny($"Der Text ist zu lang (höchstens {MaxFreeTextLength} Zeichen).");
            }

            if (EditTerms.FindBlocked(text) is { Count: > 0 } hits)
            {
                return EditDecision.Deny($"Diese Beschreibung wird nicht gesendet („{hits[0]}“). Die KI-Bearbeitung ändert keine Kleidung zu freizügig und erzeugt keine Nacktheit.");
            }
        }

        return EditDecision.Allow;
    }

    public EditResultVerdict CheckResult(EditContentScan scan, EditSubject subject)
    {
        var exposed = ExposedParts.Max(scan.Part);
        if (!subject.HasPeople)
        {
            return exposed >= NoFaceExposedThreshold
                ? new EditResultVerdict(false, "Verworfen: Das Ergebnis zeigt Nacktheit.")
                : new EditResultVerdict(true, "geprüft");
        }

        if (subject.MayBeYoung)
        {
            var covered = CoveredParts.Max(scan.Part);
            if (exposed >= YoungExposedThreshold || scan.NsfwProbability >= YoungNsfwThreshold || covered >= YoungCoveredThreshold)
            {
                return new EditResultVerdict(false, "Verworfen: Auf dem Foto ist eine Person, die jünger als 21 sein könnte, und das Ergebnis ist freizügig.");
            }

            return new EditResultVerdict(true, "geprüft");
        }

        return exposed >= ExposedThreshold || scan.NsfwProbability >= NsfwThreshold
            ? new EditResultVerdict(false, "Verworfen: Das Ergebnis zeigt eine echte Person freizügig.")
            : new EditResultVerdict(true, "geprüft");
    }
}
