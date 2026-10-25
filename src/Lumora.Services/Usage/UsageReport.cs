using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Messaging;

namespace Lumora.Services.Usage;

/// <summary>
/// Einzeiler für Aktionen, die Verwendung bzw. Weitergabe melden (ADR-0072), ohne <see cref="IUsageSignals"/> zu kennen:
/// sendet <see cref="UsageSignalMessage"/> über den Messenger (Standard: <see cref="WeakReferenceMessenger.Default"/>, den der
/// Dienst der aktuellen Katalog-Sitzung empfängt). Leere Listen werden nicht gesendet.
/// </summary>
public static class UsageReport
{
    /// <summary>Vom Benutzer verwendet: Album, Story, Titelbild, Collage aus einer Auswahl, „Verbessern“.</summary>
    public static void Used(IReadOnlyCollection<int> fileIds, IMessenger? messenger = null) => Send(fileIds, UsageKind.Used, messenger);

    /// <summary>Weitergegeben: Drucken, Teilen, Exportieren, Kopieren nach außen (Ziehen, Strg+C, „Kopieren nach…“ außerhalb).</summary>
    public static void Shared(IReadOnlyCollection<int> fileIds, IMessenger? messenger = null) => Send(fileIds, UsageKind.Shared, messenger);

    private static void Send(IReadOnlyCollection<int>? fileIds, UsageKind kind, IMessenger? messenger)
    {
        if (fileIds is null || fileIds.Count == 0)
        {
            return;
        }

        (messenger ?? WeakReferenceMessenger.Default).Send(new UsageSignalMessage(fileIds.ToList(), kind));
    }
}
