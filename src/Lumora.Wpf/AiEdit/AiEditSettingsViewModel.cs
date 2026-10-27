using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Services.AiEdit;
using Lumora.Shell;
using Lumora.Shell.HiddenLibraries;

namespace Lumora.AiEdit;

/// <summary>Punkt für den Status-Kreis der Laufzeit.</summary>
public enum AiEditStateKind
{
    Off,
    Busy,
    Ok,
    Error,
}

/// <summary>Eine Zeile der Modell-Liste aus <c>ki-install.json</c>.</summary>
public sealed record AiEditModelRow(string Purpose, string Name, string SizeText, string License, string StateText, bool IsPresent);

/// <summary>
/// Einstellungen › KI-Bearbeitung (ADR-0076, Mockup 23) — nur in der geöffneten versteckten Sitzung. Stufe 1: Installation im
/// Container erkennen, Version prüfen, ComfyUI zur Probe starten und beenden, Zustand, Grafikkarte, Leerlauf-Zeit, Platz
/// und die freigegebenen Modelle. Vorlagen, Grenzen und das ComfyUI-Fenster folgen in späteren Stufen.
/// </summary>
public sealed partial class AiEditSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IComfyRuntime _runtime;
    private readonly IAiEditSettingsStore _store;
    private readonly ICatalogSessionManager _sessions;
    private readonly ILogger<AiEditSettingsViewModel> _logger;
    private readonly SynchronizationContext? _ui;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _loading;
    private int _pendingMinutes = AiEditSettings.DefaultIdleMinutes;

    public AiEditSettingsViewModel(
        IComfyRuntime runtime,
        IAiEditSettingsStore store,
        ICatalogSessionManager sessions,
        ILogger<AiEditSettingsViewModel> logger,
        TimeProvider? time = null)
    {
        _runtime = runtime;
        _store = store;
        _sessions = sessions;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        // Der Zustand ändert sich auf Threads der Laufzeit — Anzeige nur auf dem UI-Thread anfassen
        _ui = SynchronizationContext.Current
              ?? (System.Windows.Application.Current?.Dispatcher is { } dispatcher ? new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher) : null);
        _runtime.StatusChanged += OnStatusChanged;
        Apply(_runtime.Status);
    }

    public bool IsHiddenModeEnabled => _sessions.IsHiddenModeEnabled;

    public bool IsHiddenSessionActive => _sessions.IsHiddenSessionActive;

    public ObservableCollection<AiEditModelRow> Models { get; } = new();

    [ObservableProperty]
    private string _installText = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private AiEditStateKind _stateKind;

    [ObservableProperty]
    private string? _deviceText;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _canStart;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _canStop;

    [ObservableProperty]
    private double? _idleMinutes = AiEditSettings.DefaultIdleMinutes;

    [ObservableProperty]
    private string _spaceText = "wird berechnet …";

    [ObservableProperty]
    private bool _hasModels;

    public int MaxIdleMinutes => AiEditSettings.MaxIdleMinutes;

    /// <summary>Beim Anzeigen des Reiters: Installation neu erkennen, Einstellungen, Platz und Modelle laden.</summary>
    public async Task LoadAsync()
    {
        var installation = _runtime.Refresh();
        Apply(_runtime.Status);
        try
        {
            _loading = true;
            var settings = await _store.LoadAsync().ConfigureAwait(true);
            IdleMinutes = settings.IdleMinutes;
            _runtime.IdleTimeout = TimeSpan.FromMinutes(settings.IdleMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("KI-Bearbeitung: Einstellungen nicht lesbar ({Type})", ex.GetType().Name);
        }
        finally
        {
            _loading = false;
        }

        try
        {
            var (space, rows) = await Task.Run(() => (Space(installation), ModelRows(installation))).ConfigureAwait(true);
            SpaceText = space;
            Models.Clear();
            foreach (var row in rows)
            {
                Models.Add(row);
            }

            HasModels = Models.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SpaceText = "nicht ermittelbar";
            _logger.LogWarning("KI-Bearbeitung: Platz nicht ermittelbar ({Type})", ex.GetType().Name);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        try
        {
            await _runtime.EnsureStartedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("KI-Bearbeitung: Start aus den Einstellungen fehlgeschlagen ({Type})", ex.GetType().Name);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => _runtime.StopAsync("von Hand");

    partial void OnIdleMinutesChanged(double? value)
    {
        if (_loading || value is not { } minutes)
        {
            return;
        }

        var clamped = AiEditSettings.ClampIdleMinutes((int)Math.Round(minutes));
        _runtime.IdleTimeout = TimeSpan.FromMinutes(clamped);
        Volatile.Write(ref _pendingMinutes, clamped);
        _ = SaveAsync();
    }

    /// <summary>Speichert immer den neuesten Wert, eins nach dem anderen — schnelles Hochzählen endet nicht bei einem älteren.</summary>
    private async Task SaveAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(new AiEditSettings(Volatile.Read(ref _pendingMinutes))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("KI-Bearbeitung: Einstellungen nicht gespeichert ({Type})", ex.GetType().Name);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void OnStatusChanged()
    {
        var status = _runtime.Status;
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            Apply(status);
        }
        else
        {
            _ui.Post(_ => Apply(_runtime.Status), null);
        }
    }

    private void Apply(ComfyRuntimeStatus status)
    {
        InstallText = DescribeInstallation(status.Installation);
        (StateText, StateKind) = status.State switch
        {
            ComfyRuntimeState.Unavailable => ("nicht startbar", AiEditStateKind.Off),
            ComfyRuntimeState.Stopped => ("aus", AiEditStateKind.Off),
            ComfyRuntimeState.Starting => ("startet …", AiEditStateKind.Busy),
            ComfyRuntimeState.Ready => (RunningText(status.ReadySince), AiEditStateKind.Ok),
            ComfyRuntimeState.Stopping => ("wird beendet …", AiEditStateKind.Busy),
            _ => ("Fehler", AiEditStateKind.Error),
        };
        DeviceText = status.Device is { } device
            ? $"{device.Name} · {Math.Round(device.VramTotalBytes / 1024d / 1024 / 1024):0} GB Grafikspeicher"
            : null;
        // Bei „nicht startbar“ steht der Grund schon in der Zeile „Laufzeit“ — nicht doppelt zeigen
        Message = status.Message == InstallText ? null : status.Message;
        CanStart = status.Installation.CanStart && status.State is ComfyRuntimeState.Stopped or ComfyRuntimeState.Failed;
        CanStop = status.State is ComfyRuntimeState.Starting or ComfyRuntimeState.Ready;
    }

    private string RunningText(DateTimeOffset? since)
    {
        if (since is not { } start)
        {
            return "läuft";
        }

        var minutes = (int)Math.Max(0, (_time.GetUtcNow() - start).TotalMinutes);
        return minutes < 1 ? "läuft · gerade gestartet" : $"läuft · seit {minutes} min";
    }

    internal static string DescribeInstallation(ComfyInstallation installation) => installation.State switch
    {
        ComfyInstallState.Ready => $"ComfyUI {installation.Version} (geprüfte Version)"
                                   + (installation.AllowedExtensions.Count switch
                                   {
                                       0 => " · keine Erweiterung",
                                       1 => " · 1 Erweiterung freigegeben",
                                       var n => $" · {n} Erweiterungen freigegeben",
                                   })
                                   + (installation.Manifest is null ? " · Installationsliste fehlt" : string.Empty),
        _ => installation.Problem ?? "nicht eingerichtet",
    };

    private static string Space(ComfyInstallation installation)
    {
        if (installation.Layout is not { } layout || !Directory.Exists(layout.Root))
        {
            return "—";
        }

        var program = FolderSize(layout.PackageRoot);
        var models = FolderSize(layout.ModelsRoot);
        var work = FolderSize(layout.WorkRoot) + FolderSize(layout.CacheRoot);
        var free = new DriveInfo(layout.ContainerRoot).AvailableFreeSpace;
        return $"Programm {Formatting.Bytes(program)} · Modelle {Formatting.Bytes(models)} · Arbeitsordner und Caches {Formatting.Bytes(work)} · frei: {Formatting.Bytes(free)}";
    }

    private static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(f => f.Length);
    }

    private static IReadOnlyList<AiEditModelRow> ModelRows(ComfyInstallation installation)
    {
        if (installation is not { Layout: { } layout, Manifest: { } manifest })
        {
            return [];
        }

        var rows = new List<AiEditModelRow>();
        foreach (var model in manifest.Models)
        {
            var relative = model.File.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(layout.ModelsRoot, relative));
            // Nur Dateien unter models\ — ein Eintrag mit „..“ zeigt nichts außerhalb an
            var inside = full.StartsWith(Path.GetFullPath(layout.ModelsRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var info = inside ? new FileInfo(full) : null;
            var present = info is { Exists: true } && (model.Bytes <= 0 || info.Length == model.Bytes);
            var state = info is not { Exists: true } ? "fehlt" : present ? "geladen" : "Größe weicht ab";
            rows.Add(new AiEditModelRow(
                model.Purpose ?? "—",
                Path.GetFileName(relative),
                model.Bytes > 0 ? Formatting.Bytes(model.Bytes) : "—",
                model.License ?? "—",
                state,
                present));
        }

        foreach (var extension in manifest.Extensions)
        {
            var present = installation.AllowedExtensions.Contains(extension.Name, StringComparer.OrdinalIgnoreCase);
            var commit = extension.Commit is { Length: > 7 } c ? c[..7] : extension.Commit;
            rows.Add(new AiEditModelRow("Erweiterung", commit is null ? extension.Name : $"{extension.Name} ({commit})", "—",
                extension.License ?? "—", present ? "freigegeben" : "fehlt", present));
        }

        return rows;
    }

    public void Dispose() => _runtime.StatusChanged -= OnStatusChanged;
}
