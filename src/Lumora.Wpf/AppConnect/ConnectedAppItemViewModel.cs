using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumora.Shell;

namespace Lumora.AppConnect;

/// <summary>Farbe der Zustands-Marke einer App (Mockup 11: läuft, im Hintergrund, aus, nicht verbunden).</summary>
public enum ConnectedAppTone
{
    Neutral = 0,
    Success = 1,
    Accent = 2,
    Caution = 3,
}

/// <summary>Was die Karte gerade tut.</summary>
public enum ConnectedAppPhase
{
    Idle = 0,

    /// <summary>Lumora startet die App normal und wartet auf ihre Anmeldedatei (höchstens 20 s).</summary>
    Starting = 1,

    /// <summary>Kopplung angefragt; die andere App zeigt den Code und wartet auf den Benutzer (höchstens 5 Minuten).</summary>
    Waiting = 2,

    /// <summary>Trennen läuft.</summary>
    Disconnecting = 3,
}

/// <summary>Ein Recht in der Karte: Lumora erlaubt es der App (umschaltbar) oder bekommt es von ihr (nur Anzeige).</summary>
public sealed partial class AppRightOption : ObservableObject
{
    private readonly Action? _changed;
    private bool _quiet;

    internal AppRightOption(string right, (string Text, string? Hint) text, bool isOn, bool isEditable, Action? changed)
    {
        Right = right;
        Text = text.Text;
        Hint = text.Hint;
        _isOn = isOn;
        IsEditable = isEditable;
        _changed = changed;
    }

    public string Right { get; }

    public string Text { get; }

    public string? Hint { get; }

    public bool IsEditable { get; }

    [ObservableProperty] private bool _isOn;

    /// <summary>Stand aus dem Speicher übernehmen, ohne erneut zu speichern.</summary>
    internal void Sync(bool isOn)
    {
        _quiet = true;
        IsOn = isOn;
        _quiet = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_quiet)
        {
            _changed?.Invoke();
        }
    }
}

/// <summary>
/// Karte einer App in Einstellungen › Verbundene Apps (ADR-0073 Stufe 4, Mockup 11): Zustand, Rechte in beide Richtungen,
/// Verbinden mit Code, Starten, Trennen. Nur was Vertrag 1.1 hergibt; das Geheimnis sieht die Karte nie.
/// </summary>
public sealed partial class ConnectedAppItemViewModel : ObservableObject
{
    private readonly ConnectedAppsViewModel _owner;
    private readonly AppConnectClient _client;
    private readonly ConnectionStore _connections;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private bool _updated;

    internal ConnectedAppItemViewModel(string appId, ConnectedAppsViewModel owner, AppConnectClient client, ConnectionStore connections,
        IDialogService dialogs, TimeProvider time)
    {
        AppId = appId;
        _owner = owner;
        _client = client;
        _connections = connections;
        _dialogs = dialogs;
        _time = time;
    }

    public string AppId { get; }

    /// <summary>Anmeldedatei der App (fehlt, wenn sie auf diesem PC nicht mehr angemeldet ist).</summary>
    public AppRegistration? Registration { get; private set; }

    public AppConnection? Connection { get; private set; }

    public ObservableCollection<AppRightOption> GrantedToPeer { get; } = [];

    public ObservableCollection<AppRightOption> ReceivedFromPeer { get; } = [];

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _initial = "?";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string? _interfaceText;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private ConnectedAppTone _tone;
    [ObservableProperty] private bool _isPaired;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _grantedHeader = string.Empty;
    [ObservableProperty] private string _receivedHeader = string.Empty;
    [ObservableProperty] private string _startLabel = string.Empty;
    [ObservableProperty] private ConnectedAppPhase _phase;
    [ObservableProperty] private bool _isPairing;
    [ObservableProperty] private string? _pairingCode;
    [ObservableProperty] private string? _pairingText;
    [ObservableProperty] private bool _canPair;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canDisconnect;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _noticeIsError;

    /// <summary>Während eines Ablaufs bleibt die Karte stehen, auch wenn die Anmeldedatei kurz fehlt.</summary>
    internal bool IsBusy => Phase != ConnectedAppPhase.Idle;

    /// <summary>Anmeldedatei und Kopplung übernehmen; Übergänge verbunden ↔ getrennt melden.</summary>
    internal void Update(AppRegistration? registration, AppConnection? connection)
    {
        var wasPaired = Connection is not null;
        Registration = registration ?? (IsBusy ? Registration : null);
        Connection = connection;
        if (_updated && wasPaired != (connection is not null))
        {
            SetNotice(connection is null ? $"Die Verbindung mit {Name} ist getrennt." : $"Mit {Name} verbunden.", isError: false);
        }

        _updated = true;
        DisplayName = Name;
        Initial = DisplayName.Length > 0 ? DisplayName[..1].ToUpper(CultureInfo.CurrentCulture) : "?";
        GrantedHeader = $"{DisplayName} darf von Lumora";
        ReceivedHeader = $"Lumora bekommt von {DisplayName}";
        StartLabel = $"{DisplayName} starten";
        SyncRights();
        Apply();
    }

    private string Name => Registration?.DisplayName ?? Connection?.DisplayName ?? AppId;

    // --- Abläufe ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Kopplung anfragen (Abschnitt 5): App bei Bedarf normal starten, Code zeigen, auf die Bestätigung in der anderen App
    /// warten. Abbrechen beendet nur das Warten hier; der Dialog drüben schließt sich beim Ablauf selbst.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PairAsync(CancellationToken ct)
    {
        SetNotice(null, false);
        try
        {
            var peer = await EnsureRunningAsync(ct);
            var code = AppConnectClient.NewCode();
            PairingCode = code[..3] + " " + code[3..];
            var until = _time.GetLocalNow() + _client.PairingTimeout;
            PairingText = $"Prüfen Sie, ob in {DisplayName} derselbe Code steht, und bestätigen Sie dort. Die Anfrage läuft um {until:HH:mm} Uhr ab.";
            Phase = ConnectedAppPhase.Waiting;
            var outcome = await _client.PairAsync(peer, code, ct);
            _owner.Reload(); // Übergang „verbunden“ meldet Update
            switch (outcome)
            {
                case PairingOutcome.Accepted:
                    SetNotice($"Mit {DisplayName} verbunden.", false);
                    break;
                case PairingOutcome.Rejected:
                    SetNotice($"{DisplayName} hat die Verbindung abgelehnt.", false);
                    break;
                default:
                    SetNotice($"Keine Bestätigung in {DisplayName} — die Anfrage ist abgelaufen.", false);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetNotice($"Verbinden abgebrochen. Der Dialog in {DisplayName} schließt sich beim Ablauf von selbst.", false);
        }
        catch (Exception ex) when (ex is AppConnectException or HttpRequestException or TaskCanceledException)
        {
            SetNotice(Describe(ex), true);
        }
        finally
        {
            PairingCode = null;
            PairingText = null;
            Phase = ConnectedAppPhase.Idle;
            _owner.Reload();
        }
    }

    /// <summary>App normal starten (mit Fenster, ohne Hintergrund-Schalter — Abschnitt 5, Ränder).</summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        SetNotice(null, false);
        try
        {
            await EnsureRunningAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is AppConnectException or HttpRequestException or TaskCanceledException)
        {
            SetNotice(Describe(ex), true);
        }
        finally
        {
            Phase = ConnectedAppPhase.Idle;
        }

        await _owner.RefreshAsync();
    }

    /// <summary>Trennen nach Rückfrage (Abschnitt 5, Schritt 6): die andere App erfährt es, wenn sie läuft; hier immer gelöscht.</summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        var name = DisplayName;
        var confirmed = await _dialogs.ConfirmAsync(
            "Verbindung trennen?",
            $"{name} kann Lumora danach nichts mehr fragen, und Lumora bekommt nichts mehr von {name}. Für eine neue Verbindung muss sie wieder in einer der beiden Apps bestätigt werden.",
            "Trennen",
            isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        Phase = ConnectedAppPhase.Disconnecting;
        try
        {
            await _client.UnpairAsync(AppId);
        }
        finally
        {
            Phase = ConnectedAppPhase.Idle;
            _owner.Reload();
        }
    }

    /// <summary>Was die andere App Lumora erlaubt, über <c>/info</c> nachführen; kennt sie die Kopplung nicht mehr, ist sie getrennt.</summary>
    internal async Task RefreshGrantsAsync()
    {
        if (Registration is not { } peer || !IsPaired || !IsRunning || IsBusy)
        {
            return;
        }

        try
        {
            await _client.RefreshGrantsAsync(peer);
        }
        catch (Exception ex) when (ex is AppConnectException or HttpRequestException or TaskCanceledException)
        {
            // not_paired: der Client hat die Kopplung schon gelöscht, Update meldet „getrennt“; sonst bleibt der letzte Stand
        }
    }

    private async Task<AppRegistration> EnsureRunningAsync(CancellationToken ct)
    {
        if (Registration is { } known && RegistrationFiles.IsAlive(known))
        {
            return known;
        }

        Phase = ConnectedAppPhase.Starting;
        var running = await _client.EnsureRunningAsync(AppId, ct);
        Registration = running;
        Apply();
        return running;
    }

    // --- Anzeige ----------------------------------------------------------------------------------------------------

    partial void OnPhaseChanged(ConnectedAppPhase value) => Apply();

    private void Apply()
    {
        var reg = Registration;
        IsPaired = Connection is not null;
        IsRunning = reg is not null && RegistrationFiles.IsAlive(reg);
        IsPairing = Phase is ConnectedAppPhase.Waiting || (Phase == ConnectedAppPhase.Starting && PairCommand.IsRunning);
        (StateText, Tone) = Phase switch
        {
            ConnectedAppPhase.Starting => ("startet…", ConnectedAppTone.Accent),
            ConnectedAppPhase.Waiting => ("wartet auf Bestätigung", ConnectedAppTone.Caution),
            ConnectedAppPhase.Disconnecting => ("wird getrennt…", ConnectedAppTone.Neutral),
            _ when !IsPaired => ("nicht verbunden", ConnectedAppTone.Caution),
            _ when IsRunning && reg?.Mode == "background" => ("läuft im Hintergrund", ConnectedAppTone.Accent),
            _ when IsRunning => ("läuft", ConnectedAppTone.Success),
            _ => ("nicht gestartet", ConnectedAppTone.Neutral),
        };

        var parts = new List<string> { reg is null ? "auf diesem PC nicht mehr angemeldet" : $"Version {reg.AppVersion}" };
        parts.Add(Connection is { } c ? $"verbunden seit {c.PairedAtUtc.ToLocalTime():dd.MM.yyyy}" : "noch nicht verbunden");
        DetailText = string.Join(" · ", parts);
        InterfaceText = reg is null ? null : $"App-Verbindung {reg.ContractVersion} · nur auf diesem PC (127.0.0.1)";

        var idle = Phase == ConnectedAppPhase.Idle;
        CanPair = idle && !IsPaired && reg is not null;
        CanStart = idle && reg is not null && !IsRunning;
        CanDisconnect = idle && IsPaired;
    }

    /// <summary>
    /// Rechte-Listen wie im Mockup: links, was die App von Lumora möchte (umschaltbar, Abschnitt 5 Schritt 7), rechts, was
    /// Lumora von ihr möchte und sie anbietet (Anzeige aus <c>grantedToYou</c>). Erlaubtes steht immer drin.
    /// </summary>
    private void SyncRights()
    {
        if (Connection is not { } connection)
        {
            GrantedToPeer.Clear();
            ReceivedFromPeer.Clear();
            return;
        }

        var reg = Registration;
        var toPeer = AppConnectContract.Provides
            .Where(r => reg is null || reg.Wants.Count == 0 || reg.Wants.Contains(r) || connection.GrantedToPeer.Contains(r))
            .ToList();
        var fromPeer = AppConnectContract.Wants
            .Where(r => (reg?.Provides.Contains(r) ?? false) || connection.GrantedByPeer.Contains(r))
            .ToList();
        Sync(GrantedToPeer, toPeer, connection.GrantedToPeer, editable: true);
        Sync(ReceivedFromPeer, fromPeer, connection.GrantedByPeer, editable: false);
    }

    private void Sync(ObservableCollection<AppRightOption> list, List<string> rights, IReadOnlyList<string> granted, bool editable)
    {
        if (!list.Select(o => o.Right).SequenceEqual(rights, StringComparer.Ordinal))
        {
            list.Clear();
            foreach (var right in rights)
            {
                var text = editable ? AppRightTexts.Granted(right, Name) : AppRightTexts.Received(right, Name);
                list.Add(new AppRightOption(right, text, granted.Contains(right), editable, editable ? SaveGrants : null));
            }

            return;
        }

        foreach (var option in list)
        {
            option.Sync(granted.Contains(option.Right));
        }
    }

    private void SaveGrants()
        => _connections.SetGrantedToPeer(AppId, GrantedToPeer.Where(o => o.IsOn).Select(o => o.Right));

    private void SetNotice(string? text, bool isError)
    {
        Notice = text;
        NoticeIsError = isError;
    }

    /// <summary>Fehler nach Abschnitt 11 in Worte fassen — nur der Code zählt, Texte der Gegenseite zeigt Lumora nicht.</summary>
    private string Describe(Exception ex) => ex switch
    {
        AppConnectException { Code: ErrorCodes.ContractMismatch } => $"{DisplayName} nutzt eine andere Fassung der App-Verbindung. Bitte beide Apps aktualisieren.",
        AppConnectException { Code: ErrorCodes.NotFound } when Phase == ConnectedAppPhase.Starting || Registration is null => $"{DisplayName} ist auf diesem PC nicht mehr angemeldet.",
        AppConnectException { Code: ErrorCodes.Unavailable } when Phase == ConnectedAppPhase.Starting => $"{DisplayName} ließ sich nicht starten oder meldete sich nicht innerhalb von 20 Sekunden.",
        AppConnectException { Code: ErrorCodes.Unavailable } => $"{DisplayName} läuft nicht.",
        AppConnectException { Code: ErrorCodes.TooManyRequests } => $"{DisplayName} nimmt gerade keine weiteren Anfragen an. Bitte gleich noch einmal versuchen.",
        AppConnectException { Code: ErrorCodes.Busy } => $"{DisplayName} ist gerade beschäftigt. Bitte gleich noch einmal versuchen.",
        AppConnectException e => $"{DisplayName} hat die Anfrage nicht angenommen ({e.Code}).",
        _ => $"{DisplayName} ist nicht erreichbar.",
    };
}
