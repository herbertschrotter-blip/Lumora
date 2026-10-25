using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Lumora.Shell;

namespace Lumora.Exclusions;

/// <summary>
/// Rückfragen und Zielauswahl für „Ordner ausschließen…“ und „Ordner verschieben“ (ADR-0066) — austauschbar für Tests und den
/// Rauchtest (der vorher seinen Fake registriert).
/// </summary>
public interface IFolderEditDialogs
{
    /// <summary>Rückfrage mit einem Hauptknopf; true = bestätigt.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Drei Knöpfe (Primär / Sekundär / Abbrechen).</summary>
    Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText);

    /// <summary>Zielordner wählen (beginnt in <paramref name="initialDirectory"/>); null bei Abbruch.</summary>
    string? PickFolder(string title, string? initialDirectory);
}

/// <summary>Produktiv: eigenes Dialogfenster über <see cref="IDialogService"/>, Windows-Ordnerdialog für die Zielauswahl.</summary>
internal sealed class FolderEditDialogs(IDialogService dialogs) : IFolderEditDialogs
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => dialogs.ConfirmAsync(title, message, confirmText);

    public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText)
        => dialogs.ChooseAsync(title, message, primaryText, secondaryText);

    public string? PickFolder(string title, string? initialDirectory)
    {
        // ADR-0049: nichts unter „Zuletzt verwendet“
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false, AddToRecent = false, InitialDirectory = initialDirectory ?? string.Empty };
        var owner = Application.Current?.MainWindow is { IsVisible: true } window ? window : null;
        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FolderName : null;
    }
}
