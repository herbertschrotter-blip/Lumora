using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumora.Services.HiddenLibraries.SecurityCheck;

/// <summary>Sicherheits-Check der geöffneten versteckten Bibliothek (ADR-0064) — liest nur, ändert keine Windows-Einstellung.</summary>
public interface ISecurityCheckService
{
    /// <summary>Letztes Ergebnis; null, solange noch nicht geprüft wurde.</summary>
    SecurityCheckReport? Last { get; }

    /// <summary>Zuletzt geladene bzw. gerade gesetzte Einstellungen (im Speicher, sofort nach einer Änderung).</summary>
    SecurityCheckSettings Settings { get; }

    /// <summary>true, sobald <see cref="Settings"/> aus dem Katalog gelesen oder gespeichert wurde (sonst Standardwerte).</summary>
    bool HasSettings { get; }

    /// <summary>Kontext des letzten Durchlaufs (Titel mit Laufwerksbuchstaben); null vor dem ersten.</summary>
    SecurityCheckContext? LastContext { get; }

    /// <summary>Einstellungen lesen, alle Prüfungen ausführen, Statusleiste benachrichtigen. Wirft nie wegen einer Prüfung.</summary>
    Task<SecurityCheckReport> RunAsync(CancellationToken ct = default);

    /// <summary>Gefahren-Stufe speichern und die Wertung neu berechnen (ohne neu zu lesen).</summary>
    Task<SecurityCheckReport?> SetLevelAsync(ThreatLevel level, CancellationToken ct = default);

    /// <summary>„Lumora nach dem Zusperren ganz beenden“ speichern und den Punkt neu bewerten.</summary>
    Task<SecurityCheckReport?> SetExitAfterLockAsync(bool exitAfterLock, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class SecurityCheckService : ISecurityCheckService, IDisposable
{
    private readonly IReadOnlyList<ISecurityCheck> _checks;
    private readonly ISecurityCheckSettingsStore _store;
    private readonly Func<char?> _containerDrive;
    private readonly ILogger<SecurityCheckService> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private SecurityCheckContext? _lastContext;
    private volatile bool _disposed;

    /// <param name="containerDrive">Laufwerk der geöffneten versteckten Bibliothek (nur für Anzeige und Suchindex-Prüfung).</param>
    public SecurityCheckService(
        IEnumerable<ISecurityCheck> checks,
        ISecurityCheckSettingsStore store,
        Func<char?> containerDrive,
        ILogger<SecurityCheckService> logger,
        IMessenger? messenger = null)
    {
        _checks = checks.OrderBy(c => c.Id).ToList();
        _store = store;
        _containerDrive = containerDrive;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public SecurityCheckReport? Last { get; private set; }

    public SecurityCheckSettings Settings { get; private set; } = SecurityCheckSettings.Default;

    public bool HasSettings { get; private set; }

    /// <summary>Kontext des letzten Durchlaufs (Titel mit Laufwerksbuchstaben).</summary>
    public SecurityCheckContext? LastContext => _lastContext;

    /// <summary>Windows-Laufwerk aus <see cref="Environment.SystemDirectory"/>; „C“, wenn nicht deutbar.</summary>
    public static char SystemDriveLetter()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        return root is { Length: >= 2 } && root[1] == ':' && char.IsAsciiLetter(root[0]) ? char.ToUpperInvariant(root[0]) : 'C';
    }

    public async Task<SecurityCheckReport> RunAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await LoadSettingsAsync(linked.Token).ConfigureAwait(false);
            var context = new SecurityCheckContext(SystemDriveLetter(), SafeContainerDrive(), Settings.ExitAfterLock);
            _lastContext = context;
            var findings = await Task.Run(() => _checks.Select(c => RunOne(c, context)).ToList(), linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested(); // Sitzung inzwischen geschlossen → kein Ergebnis mehr senden
            return Publish(new SecurityCheckReport(findings, Settings.Level));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SecurityCheckReport?> SetLevelAsync(ThreatLevel level, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(level))
        {
            level = SecurityCheckRating.DefaultLevel;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveAsync(s => s with { Level = level }, ct).ConfigureAwait(false);
            return Last is { } last ? Publish(last.WithLevel(level)) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SecurityCheckReport?> SetExitAfterLockAsync(bool exitAfterLock, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveAsync(s => s with { ExitAfterLock = exitAfterLock }, ct).ConfigureAwait(false);
            if (Last is not { } last)
            {
                return null;
            }

            var context = (_lastContext ?? new SecurityCheckContext(SystemDriveLetter(), null, exitAfterLock)) with { ExitAfterLock = exitAfterLock };
            _lastContext = context;
            var check = _checks.FirstOrDefault(c => c.Id == SecurityCheckId.ExitAfterLock);
            var findings = last.Findings
                .Select(f => f.Id == SecurityCheckId.ExitAfterLock && check is not null ? RunOne(check, context) : f)
                .ToList();
            return Publish(last with { Findings = findings });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ändert eine Einstellung: zuerst die gespeicherten Werte holen (sonst überschriebe der Standard die andere Option),
    /// dann sofort im Speicher übernehmen — das Zusperren liest <see cref="Settings"/>, auch wenn das Speichern noch läuft —
    /// und speichern. Scheitert das Speichern, gilt wieder der alte Wert.
    /// </summary>
    private async Task SaveAsync(Func<SecurityCheckSettings, SecurityCheckSettings> change, CancellationToken ct)
    {
        if (!HasSettings)
        {
            await LoadSettingsAsync(ct).ConfigureAwait(false);
        }

        var previous = Settings;
        var next = change(previous);
        Settings = next;
        try
        {
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            HasSettings = true;
        }
        catch
        {
            Settings = previous;
            throw;
        }
    }

    private async Task LoadSettingsAsync(CancellationToken ct)
    {
        try
        {
            Settings = await _store.LoadAsync(ct).ConfigureAwait(false);
            HasSettings = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Sicherheits-Check: Einstellungen nicht lesbar ({Type}) — bisheriger Stand gilt", ex.GetType().Name);
        }
    }

    /// <summary>Beim Schließen der versteckten Sitzung (Container entsorgt): laufende Prüfung abbrechen, nichts mehr senden.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel(); // nicht entsorgen: ein Nachzügler darf das Token noch lesen
    }

    private char? SafeContainerDrive()
    {
        try
        {
            return _containerDrive() is { } letter && char.IsAsciiLetter(letter) ? char.ToUpperInvariant(letter) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Sicherheits-Check: Laufwerk der Bibliothek unbekannt ({Type})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Eine Prüfung; jeder Fehler wird „Unbekannt“ mit Hinweis. Ins Protokoll nur Punkt und Fehlertyp — nie Pfade oder Laufwerke.</summary>
    private SecurityCheckFinding RunOne(ISecurityCheck check, SecurityCheckContext context)
    {
        try
        {
            return check.Run(context) is { } finding && finding.Id == check.Id
                ? finding
                : SecurityCheckFinding.Unknown(check.Id, "Die Prüfung lieferte kein Ergebnis.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Sicherheits-Check: Punkt {Check} nicht lesbar ({Type})", check.Id, ex.GetType().Name);
            return SecurityCheckFinding.Unknown(check.Id, "Windows hat den Wert nicht geliefert (keine Berechtigung oder nicht vorhanden). Bitte in der Einstellung selbst nachsehen.");
        }
    }

    private SecurityCheckReport Publish(SecurityCheckReport report)
    {
        Last = report;
        if (_disposed)
        {
            return report; // Sitzung geschlossen: nie in die Statusleiste der nächsten (normalen) Sitzung
        }

        try
        {
            _messenger.Send(new SecurityCheckSummaryMessage(report.StatusText));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Sicherheits-Check: Statusleiste nicht benachrichtigt ({Type})", ex.GetType().Name);
        }

        return report;
    }
}

/// <summary>
/// Erste Prüfung kurz nach dem Öffnen der versteckten Sitzung (Statusleiste „Sicherheits-Check: x von y erfüllt“, ohne den
/// Reiter zu öffnen). Nur im Container einer versteckten Sitzung registriert.
/// </summary>
public sealed class SecurityCheckStartup(ISecurityCheckService service, ILogger<SecurityCheckStartup> logger) : BackgroundService
{
    /// <summary>Wartezeit nach dem Start der Sitzung (Migration, Hauptfenster zuerst).</summary>
    internal static TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Delay, stoppingToken).ConfigureAwait(false);
            await service.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Sitzung vorher geschlossen
        }
        catch (Exception ex)
        {
            logger.LogWarning("Sicherheits-Check beim Öffnen fehlgeschlagen ({Type})", ex.GetType().Name);
        }
    }
}
