using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Lumora.Services.ModelPackages;
using Lumora.Shell;

namespace Lumora.Settings.ModelPackages;

/// <summary>
/// Bestätigung vor dem Laden eines Modells (Projektregel, ADR-0070): Name, Quelle, Größe, Lizenz, Ziel. Gemeinsam für
/// Einstellungen › Modelle &amp; Daten und die Knöpfe in Gesichter/Motive. Liefert nur nach „Laden“ eine bestätigte Anfrage.
/// </summary>
public static class ModelDownloadDialog
{
    public const string ConfirmButton = "Laden";

    public static string TitleOf(ModelDownloadRequest request) => $"{request.Definition.Title} laden?";

    /// <summary>Text des Dialogs.</summary>
    public static string MessageOf(ModelDownloadRequest request)
    {
        var d = request.Definition;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.CurrentCulture, $"Name: {d.Name} (Version {d.Version})\n");
        sb.Append(CultureInfo.CurrentCulture, $"Quelle: {d.Origin.Name}");
        if (request.DownloadUri is { } uri)
        {
            sb.Append(CultureInfo.CurrentCulture, $" — geladen von {uri.Host}");
        }

        sb.Append('\n');
        sb.Append(CultureInfo.CurrentCulture, $"Größe: {SizeText(d.DownloadSize)}");
        if (d.InstalledSize > 0 && Math.Abs(d.InstalledSize - d.DownloadSize) > 1_048_576)
        {
            sb.Append(CultureInfo.CurrentCulture, $" (danach {SizeText(d.InstalledSize)} auf der Platte)");
        }

        sb.Append('\n');
        sb.Append(CultureInfo.CurrentCulture, $"Lizenz: {d.License}\n");
        if (request.TargetFolder is { } folder)
        {
            sb.Append(CultureInfo.CurrentCulture, $"Ziel: {folder}\n");
        }

        if (!string.IsNullOrWhiteSpace(d.Note))
        {
            sb.Append('\n').Append(d.Note).Append('\n');
        }

        sb.Append("\nJede Datei wird per SHA-256 geprüft; abgebrochene oder falsche Dateien werden verworfen. ");
        sb.Append("Das Modell läuft danach vollständig auf diesem PC — Fotos verlassen den Rechner nicht.");
        return sb.ToString();
    }

    /// <summary>Fragt nach; null = abgelehnt (dann wird nichts geladen).</summary>
    public static async Task<ModelDownloadRequest?> AskAsync(IDialogService dialogs, IModelPackageManager manager, string id)
    {
        var request = manager.DescribeDownload(id);
        var ok = await dialogs.ConfirmAsync(TitleOf(request), MessageOf(request), ConfirmButton).ConfigureAwait(true);
        return ok ? request.Confirm() : null;
    }

    /// <summary>„184 MB“, „1,9 GB“.</summary>
    public static string SizeText(long bytes)
        => bytes >= 1_073_741_824
            ? (bytes / 1_073_741_824.0).ToString("N1", CultureInfo.CurrentCulture) + " GB"
            : Math.Max(1, (long)Math.Round(bytes / 1_048_576.0)).ToString("N0", CultureInfo.CurrentCulture) + " MB";
}
