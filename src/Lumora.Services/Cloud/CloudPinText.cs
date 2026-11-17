using System.Globalization;

namespace Lumora.Services.Cloud;

/// <summary>Statusmeldungen der Pin-Aktionen (ADR-0077, Mockup 27).</summary>
public static class CloudPinText
{
    public const string KeepHeader = "Immer auf diesem Gerät behalten";
    public const string FreeHeader = "Speicherplatz freigeben";

    public static string Format(CloudPinResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string text;
        if (result.Changed == 0)
        {
            text = result.Action == CloudPinAction.KeepOnDevice
                ? "Nichts zu behalten"
                : "Kein Speicherplatz freizugeben";
        }
        else if (result.Action == CloudPinAction.KeepOnDevice)
        {
            text = result.Changed == 1
                ? "1 Foto wird immer auf diesem Gerät behalten — OneDrive lädt es im Hintergrund herunter"
                : $"{Count(result.Changed)} Fotos werden immer auf diesem Gerät behalten — OneDrive lädt sie im Hintergrund herunter";
        }
        else
        {
            text = result.Changed == 1
                ? "Speicherplatz wird freigegeben — 1 Foto liegt danach nur online"
                : $"Speicherplatz wird freigegeben — {Count(result.Changed)} Fotos liegen danach nur online";
        }

        if (result.NotCloud > 0)
        {
            text += result.NotCloud == 1 ? " · 1 Datei liegt nicht in OneDrive" : $" · {Count(result.NotCloud)} Dateien liegen nicht in OneDrive";
        }

        if (result.Failed > 0)
        {
            text += result.Failed == 1 ? " · 1 Datei: von Windows abgelehnt" : $" · {Count(result.Failed)} Dateien: von Windows abgelehnt";
        }

        return text + ".";
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
