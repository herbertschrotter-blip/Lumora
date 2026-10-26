namespace Lumora.AppConnect;

/// <summary>Rumpf von <c>POST /av/v1/pairing</c> (Abschnitt 5, Schema <c>PairingRequest</c>).</summary>
public sealed record PairingRequest
{
    public string AppId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Sechsstelliger Code des Abfragers.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Rechte, die der Abfrager bei Lumora haben möchte.</summary>
    public IReadOnlyList<string> RequestedRights { get; init; } = [];

    /// <summary>Rechte, die der Abfrager Lumora anbietet.</summary>
    public IReadOnlyList<string> OfferedRights { get; init; } = [];
}

/// <summary>Antwort 202 auf <c>POST /av/v1/pairing</c>.</summary>
public sealed record PairingCreatedResponse(string PairingId);

/// <summary>Antwort von <c>GET /av/v1/pairing/{id}</c> (Schema <c>PairingStatus</c>).</summary>
public sealed record PairingStatusResponse
{
    /// <summary><c>pending</c> | <c>accepted</c> | <c>rejected</c> | <c>expired</c>.</summary>
    public required string Status { get; init; }

    public string? Secret { get; init; }

    public IReadOnlyList<string>? GrantedRights { get; init; }

    public IReadOnlyList<string>? AcceptedOfferedRights { get; init; }
}

/// <summary>Rumpf von <c>POST</c>/<c>PUT /av/v1/lease</c> (Abschnitt 7).</summary>
public sealed record LeaseRequest
{
    public int? Seconds { get; init; }
}

/// <summary>Halte-Zusage (Schema <c>Lease</c>).</summary>
public sealed record LeaseResponse(string LeaseId, DateTime ExpiresUtc);

/// <summary>Was der Benutzer im Kopplungsdialog sieht (Mockup 11, „Neue App koppeln“).</summary>
public sealed record PairingPromptInfo(string AppId, string DisplayName, string Code, IReadOnlyList<PairingPermission> Permissions);

/// <summary>Entscheidung des Benutzers: erlaubte bzw. angenommene Rechte; null = abgelehnt.</summary>
public sealed record PairingDecision(IReadOnlyList<string> GrantedRights, IReadOnlyList<string> AcceptedOfferedRights);

/// <summary>Fragt den Benutzer, ob eine App sich koppeln darf (Lumora: Dialog nach Mockup 11). Abbruch = Anfrage abgelaufen.</summary>
public interface IPairingPrompt
{
    /// <summary>null = abgelehnt (oder Fenster geschlossen).</summary>
    Task<PairingDecision?> AskAsync(PairingPromptInfo info, CancellationToken ct);
}

/// <summary>
/// Eine Zeile im Kopplungsdialog (Mockup 11): fasst Rechte verständlich zusammen. <see cref="Grants"/> erlaubt Lumora dem
/// Abfrager, <see cref="Accepts"/> nimmt Lumora von ihm an.
/// </summary>
public sealed record PairingPermission(string Key, string Text, bool DefaultOn, IReadOnlyList<string> Grants, IReadOnlyList<string> Accepts);

/// <summary>Rechte des Kopplungsdialogs zu Zeilen bündeln und die Auswahl zurück in Rechte übersetzen.</summary>
public static class PairingPermissions
{
    private static readonly string[] PhotoRights = [Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal];
    private static readonly string[] TourRights = [Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead];

    /// <summary>
    /// Zeilen wie im Mockup: Fotos (an), Personen (aus), Touren/Spuren/Gipfel (an), gegenseitiger Hintergrundstart (aus). Nur
    /// Rechte, die Lumora anbietet bzw. haben möchte; unbekannte Rechte zählen nicht (Abschnitt 3).
    /// </summary>
    public static IReadOnlyList<PairingPermission> Lines(IEnumerable<string> requested, IEnumerable<string> offered)
    {
        var req = requested.Intersect(AppConnectContract.Provides, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var off = offered.Intersect(AppConnectContract.Wants, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var lines = new List<PairingPermission>();
        var photos = PhotoRights.Where(req.Contains).ToList();
        if (photos.Count > 0)
        {
            lines.Add(new("photos", "Fotos suchen, Vorschaubilder abrufen, Originale öffnen", true, photos, []));
        }

        if (req.Contains(Rights.PhotosPeople))
        {
            lines.Add(new("people", "Personen auf den Fotos sehen", false, [Rights.PhotosPeople], []));
        }

        var tours = TourRights.Where(off.Contains).ToList();
        if (tours.Count > 0)
        {
            lines.Add(new("tours", "Touren, GPS-Spuren und Gipfel an Lumora geben", true, [], tours));
        }

        var bgGrant = req.Contains(Rights.BackgroundStart) ? new[] { Rights.BackgroundStart } : [];
        var bgAccept = off.Contains(Rights.BackgroundStart) ? new[] { Rights.BackgroundStart } : [];
        if (bgGrant.Length + bgAccept.Length > 0)
        {
            lines.Add(new("background", bgGrant.Length > 0 && bgAccept.Length > 0 ? "Sich gegenseitig im Hintergrund starten" : "Im Hintergrund starten", false, bgGrant, bgAccept));
        }

        return lines;
    }

    /// <summary>Gewählte Zeilen → erlaubte und angenommene Rechte.</summary>
    public static PairingDecision Decide(IEnumerable<PairingPermission> chosen)
    {
        var list = chosen.ToList();
        return new PairingDecision(
            list.SelectMany(l => l.Grants).Distinct(StringComparer.Ordinal).ToList(),
            list.SelectMany(l => l.Accepts).Distinct(StringComparer.Ordinal).ToList());
    }
}
