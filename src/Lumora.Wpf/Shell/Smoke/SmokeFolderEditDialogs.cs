using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Exclusions;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Rückfragen für „Ordner ausschließen…“ und „Ordner verschieben“ (ADR-0066): Bestätigung automatisch „Ja“, Wahl
/// automatisch der Hauptknopf („Aus der Bibliothek entfernen“), Zielordner vom Schritt vorgegeben; Texte werden gemerkt und
/// geloggt (ohne Pfade).
/// </summary>
internal sealed class SmokeFolderEditDialogs : IFolderEditDialogs
{
    private readonly ILogger<SmokeFolderEditDialogs> _logger;

    public SmokeFolderEditDialogs(ILogger<SmokeFolderEditDialogs> logger) => _logger = logger;

    public List<(string Title, string Message)> Requests { get; } = [];

    /// <summary>Antwort der nächsten Zielauswahl (vom Schritt gesetzt).</summary>
    public string? PickResult { get; set; }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Requests.Add((title, message));
        _logger.LogInformation("Rauchtest-Dialog „{Title}“ → automatisch „{Button}“", title, confirmText);
        return Task.FromResult(true);
    }

    public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText)
    {
        Requests.Add((title, message));
        _logger.LogInformation("Rauchtest-Dialog „{Title}“ → automatisch „{Button}“", title, primaryText);
        return Task.FromResult(DialogChoice.Primary);
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        _logger.LogInformation("Rauchtest-Zielauswahl „{Title}“ → vorgegebenes Ziel", title);
        return PickResult;
    }
}
