namespace Lumora.Services.Messaging;

/// <summary>
/// Ausschlüsse beim Einlesen geändert (ADR-0066): <paramref name="LibraryId"/> = Ordner dieser Bibliothek, null = allgemeine Liste
/// (alle Bibliotheken des Katalogs). Ordnerüberwachung und Ordnerbaum laden ihre Regeln neu.
/// </summary>
public sealed record ScanExclusionsChangedMessage(int? LibraryId);
