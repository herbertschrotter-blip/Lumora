using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumora.Shell;

namespace Lumora.AppConnect;

/// <summary>
/// Einstellungen › Verbundene Apps (ADR-0073 Stufe 4, Mockup 11): alle Apps mit Anmeldedatei auf diesem PC und alle
/// Kopplungen — verbundene zuerst. Lädt beim Öffnen des Reiters und nach jeder Änderung der Kopplungen (auch wenn die andere
/// App koppelt oder trennt). Ohne laufende App-Verbindung (Start mit <c>_hidden</c>) bleibt der Reiter bei einem Hinweis.
/// </summary>
public sealed partial class ConnectedAppsViewModel : ObservableObject
{
    private readonly AppConnectRuntime _runtime;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private bool _refreshing;

    public ConnectedAppsViewModel(AppConnectRuntime runtime, IDialogService dialogs, TimeProvider? time = null)
    {
        _runtime = runtime;
        _dialogs = dialogs;
        _time = time ?? TimeProvider.System;
        // Kopplungen ändern sich auch auf Threads der Schnittstelle — Karten nur auf dem UI-Thread anfassen
        _ui = SynchronizationContext.Current
              ?? (System.Windows.Application.Current?.Dispatcher is { } dispatcher ? new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher) : null);
        if (runtime.Connections is not null)
        {
            OnAttached();
        }
        else
        {
            runtime.Attached += (_, _) => Post(OnAttached);
        }
    }

    public ObservableCollection<ConnectedAppItemViewModel> Apps { get; } = [];

    /// <summary>App-Verbindung läuft in diesem Start.</summary>
    [ObservableProperty] private bool _isAvailable;

    [ObservableProperty] private bool _hasApps;

    /// <summary>Statuszeile, z. B. „1 App verbunden“.</summary>
    [ObservableProperty] private string _summary = "Noch keine App verbunden";

    /// <summary>Meldung zu einer App, deren Karte verschwunden ist (getrennt und nicht mehr angemeldet).</summary>
    [ObservableProperty] private string? _message;

    /// <summary>Liste neu aufbauen und bei laufenden, verbundenen Apps nachfragen, was sie Lumora erlauben.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!IsAvailable || _refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            Reload();
            foreach (var item in Apps.ToList())
            {
                await item.RefreshGrantsAsync();
            }

            Reload();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Anmeldedateien und Kopplungen lesen (ohne Netz) und die Karten abgleichen.</summary>
    internal void Reload()
    {
        if (_runtime.Client is not { } client || _runtime.Connections is not { } connections)
        {
            return;
        }

        var registrations = client.Peers().ToDictionary(r => r.AppId, StringComparer.Ordinal);
        var paired = connections.All.ToDictionary(c => c.AppId, StringComparer.Ordinal);
        var existing = Apps.ToDictionary(a => a.AppId, StringComparer.Ordinal);
        var wanted = new List<ConnectedAppItemViewModel>();
        foreach (var appId in paired.Keys.Concat(registrations.Keys).Concat(existing.Values.Where(a => a.IsBusy).Select(a => a.AppId))
                     .Distinct(StringComparer.Ordinal))
        {
            var item = existing.GetValueOrDefault(appId) ?? new ConnectedAppItemViewModel(appId, this, client, connections, _dialogs, _time);
            item.Update(registrations.GetValueOrDefault(appId), paired.GetValueOrDefault(appId));
            wanted.Add(item);
        }

        foreach (var gone in existing.Values.Except(wanted).Where(a => a.IsPaired))
        {
            Message = $"Die Verbindung mit {gone.DisplayName} ist getrennt.";
        }

        wanted = wanted.OrderByDescending(a => a.IsPaired).ThenBy(a => a.DisplayName, StringComparer.CurrentCulture).ToList();
        if (!Apps.SequenceEqual(wanted))
        {
            Apps.Clear();
            foreach (var item in wanted)
            {
                Apps.Add(item);
            }
        }

        HasApps = Apps.Count > 0;
        var count = Apps.Count(a => a.IsPaired);
        Summary = count switch
        {
            0 => "Noch keine App verbunden",
            1 => "1 App verbunden",
            _ => $"{count} Apps verbunden",
        };
    }

    private void OnAttached()
    {
        IsAvailable = true;
        _runtime.Connections!.Changed += (_, _) => Post(Reload);
        Reload();
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
