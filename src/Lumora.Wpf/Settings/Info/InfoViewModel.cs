using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Faces.Models;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Shell;
using Lumora.Shell.Branding;

namespace Lumora.Settings.Info;

/// <summary>Eine Zeile einer Info-Karte („Programmierung“ → „Herbert Schrotter“).</summary>
public sealed record InfoLine(string Label, string Value);

/// <summary>
/// Einstellungen › Info (ADR-0042): App-Name, Version (= oberster CHANGELOG-Eintrag), Build-Datum und -Art, Karten
/// „Entwicklung“, „Dieses System“ (.NET, Windows, Grafikkarte/DirectML, Gesichtsmodell, Katalog), „Speicherorte“ (je
/// „öffnen“) und „Verwendete Bausteine“ (Versionen aus den DLLs neben der App), dazu „Alle Lizenzen anzeigen…“,
/// „Versionsinfo kopieren“ und „Änderungsprotokoll“ (nur wenn CHANGELOG.md neben der App liegt). Kein Netzzugriff.
/// </summary>
public sealed partial class InfoViewModel : ObservableObject
{
    private readonly IAppDataPaths _paths;
    private readonly ILibraryContextFactory _factory;
    private readonly ILibraryService _libraries;
    private readonly IInfoActions _actions;
    private readonly ILogger<InfoViewModel> _logger;
    private readonly IFaceIndexService? _faceIndex;
    private readonly IFaceModelProvider? _faceModels;
    private readonly string _appDirectory;

    /// <param name="appDirectory">Ordner mit DLLs und CHANGELOG.md; Standard der App-Ordner (Tests: eigener Ordner).</param>
    public InfoViewModel(
        IAppDataPaths paths,
        ILibraryContextFactory factory,
        ILibraryService libraries,
        IInfoActions actions,
        ILogger<InfoViewModel> logger,
        IFaceIndexService? faceIndex = null,
        IFaceModelProvider? faceModels = null,
        string? appDirectory = null,
        ICatalogContext? catalog = null,
        AppDataLocation? dataLocation = null)
    {
        // ADR-0069: aktiver Datenordner (Standard/portabel/Variable) — Anzeige ohne Pfad
        DataFolderText = catalog?.IsHidden == true
            ? "Versteckte Bibliothek (Katalog im Container)"
            : (dataLocation ?? AppDataRootOverride.ActiveLocation).DisplayText;
        _paths = paths;
        _factory = factory;
        _libraries = libraries;
        _actions = actions;
        _logger = logger;
        _faceIndex = faceIndex;
        _faceModels = faceModels;
        _appDirectory = appDirectory ?? AppInfo.AppDirectory;

        ChangelogPath = AppInfo.FindChangelog(_appDirectory);
        Components = BuildComponents(_appDirectory, sqliteVersion: null);
        DatabasePath = _factory.DatabasePath;
        ThumbnailsPath = _paths.ThumbnailsRoot;
        LogsPath = _paths.LogsRoot;
    }

    public string AppName => AppInfo.AppName;

    public string Version => AppInfo.Version;

    public string HeaderText => AppInfo.HeaderLine(Version, AppInfo.BuildDate, AppInfo.BuildConfiguration);

    public string Description => AppInfo.Description;

    /// <summary>Karte „Entwicklung“ — bewusst ohne E-Mail-Adresse.</summary>
    public IReadOnlyList<InfoLine> Development { get; } =
    [
        new("Programmierung", "Herbert Schrotter"),
        new("Mitarbeit", "Claude (Anthropic), KI-Assistent"),
        new("Vorgänger", $"{LumoraBrand.FormerProductName} (bis {LumoraBrand.LastFormerNameVersion}) · PhotoViewer V2 (PowerShell-Web-App)"),
    ];

    /// <summary>Karte „Dieses System“; gefüllt von <see cref="LoadAsync"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<InfoLine> _systemInfo = [];

    /// <summary>Karte „Verwendete Bausteine“.</summary>
    [ObservableProperty]
    private IReadOnlyList<InfoLine> _components;

    public string DatabasePath { get; }

    /// <summary>Herkunft des Datenordners ohne Pfad: „Standard (%LocalAppData%\Lumora)“, „Portabel …“, „Umgebungsvariable …“ (ADR-0069).</summary>
    public string DataFolderText { get; }

    public string ThumbnailsPath { get; }

    public string LogsPath { get; }

    /// <summary>CHANGELOG.md neben der App, null = Knopf „Änderungsprotokoll“ ausgeblendet.</summary>
    public string? ChangelogPath { get; }

    public bool HasChangelog => ChangelogPath is not null;

    [ObservableProperty]
    private string? _message;

    /// <summary>Systemangaben und Katalogzahlen neu lesen (bei jedem Öffnen des Reiters).</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var gpu = await Task.Run(GraphicsAdapters.GetNames).ConfigureAwait(true);
        var sqlite = await Task.Run(ReadSqliteVersion).ConfigureAwait(true);
        var catalog = await ReadCatalogTextAsync().ConfigureAwait(true);

        SystemInfo =
        [
            new("Laufzeit", $".NET {Environment.Version} · {AppInfo.WindowsText(Environment.OSVersion.Version)} · {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}"),
            new("Grafikkarte", (gpu.Count == 0 ? "unbekannt" : string.Join(", ", gpu)) + FaceExecutionText()),
            new("Gesichtsmodell", FaceModelText()),
            new("Katalog", catalog),
            new("Datenordner", DataFolderText), // ADR-0069
        ];
        Components = BuildComponents(_appDirectory, sqlite);
    }

    [RelayCommand]
    private void OpenDatabase() => Run(() => _actions.RevealFile(DatabasePath), "Katalog-Ordner");

    [RelayCommand]
    private void OpenThumbnails() => Run(() => _actions.OpenFolder(ThumbnailsPath), "Vorschaubild-Ordner");

    [RelayCommand]
    private void OpenLogs() => Run(() => _actions.OpenFolder(LogsPath), "Protokoll-Ordner");

    [RelayCommand(CanExecute = nameof(HasChangelog))]
    private void OpenChangelog() => Run(() => _actions.OpenFile(ChangelogPath!), "Änderungsprotokoll");

    [RelayCommand]
    private void ShowLicenses() => Run(() => _actions.ShowText(LicenseTexts.Title, LicenseTexts.Intro, LicenseTexts.Details), "Lizenzen");

    [RelayCommand]
    private void CopyVersionInfo()
    {
        Run(() => _actions.CopyText(BuildVersionInfo()), "Zwischenablage");
        Message ??= "Versionsinfo in die Zwischenablage kopiert.";
    }

    /// <summary>Text für „Versionsinfo kopieren“: Version, Build, System und Bausteine — ohne Pfade und Namen.</summary>
    internal string BuildVersionInfo()
    {
        var text = new StringBuilder();
        text.AppendLine($"{AppName} {Version}");
        text.AppendLine(HeaderText);
        foreach (var line in SystemInfo.Where(l => l.Label != "Katalog"))
        {
            text.AppendLine($"{line.Label}: {line.Value}");
        }

        foreach (var line in Components)
        {
            text.AppendLine($"{line.Label}: {line.Value}");
        }

        return text.ToString().TrimEnd();
    }

    private void Run(Action action, string what)
    {
        Message = null;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Message = $"{what} konnte nicht geöffnet werden: {ex.Message}";
            _logger.LogWarning(ex, "Info: {What} fehlgeschlagen", what);
        }
    }

    private async Task<string> ReadCatalogTextAsync()
    {
        try
        {
            var all = await _libraries.GetAllAsync().ConfigureAwait(true);
            var files = all.Sum(l => (long)l.FileCount);
            var size = File.Exists(DatabasePath) ? Formatting.Bytes(new FileInfo(DatabasePath).Length) : "noch nicht angelegt";
            var libraries = all.Count == 1 ? "1 Bibliothek" : $"{all.Count} Bibliotheken";
            var fileText = files == 1 ? "1 Datei" : $"{files:N0} Dateien";
            return $"{libraries} · {fileText} · {size}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Info: Katalog nicht lesbar");
            return "nicht lesbar";
        }
    }

    /// <summary>Anhang an die Grafikkarte: womit die Gesichtserkennung rechnet bzw. rechnen würde — „ (DirectML)“, „ (Prozessor)“.</summary>
    internal string FaceExecutionText()
    {
        if (_faceIndex is null)
        {
            return string.Empty;
        }

        var provider = _faceIndex.Status.ExecutionProvider
            ?? (_faceIndex.Settings.UseGpu ? "DirectML" : "Prozessor");
        return $" ({provider})";
    }

    /// <summary>„buffalo_l · geladen“ / „vorhanden, nicht geladen“ / „nicht heruntergeladen“, dazu „Erkennung aus“.</summary>
    internal string FaceModelText()
    {
        if (_faceModels is null)
        {
            return "nicht verfügbar";
        }

        var state = _faceModels.State != FaceModelState.Ready ? "nicht heruntergeladen"
            : _faceIndex?.Status.ExecutionProvider is not null ? "geladen"
            : "vorhanden, nicht geladen";
        var off = _faceIndex is { Settings.Enabled: false } ? " · Erkennung aus" : string.Empty;
        return $"buffalo_l · {state}{off}";
    }

    private string? ReadSqliteVersion()
    {
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            return connection.ServerVersion;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Info: SQLite-Version nicht lesbar");
            return null;
        }
    }

    /// <summary>Versionen der Bausteine aus den DLLs im App-Ordner; fehlende DLLs ohne Versionsangabe.</summary>
    internal static IReadOnlyList<InfoLine> BuildComponents(string directory, string? sqliteVersion)
    {
        string Named(string name, string dll)
        {
            var version = AppInfo.ComponentVersion(directory, dll);
            return version is null ? name : $"{name} {version}";
        }

        var sqlite = sqliteVersion is null ? "SQLite" : $"SQLite {sqliteVersion}";
        return
        [
            new("Oberfläche", $"{Named("WPF-UI", "Wpf.Ui.dll")} · {Named("CommunityToolkit.Mvvm", "CommunityToolkit.Mvvm.dll")}"),
            new("Datenbank", $"{sqlite} · {Named("Entity Framework Core", "Microsoft.EntityFrameworkCore.dll")}"),
            new("Archive", Named("SharpCompress", "SharpCompress.dll")),
            new("Gesichter", $"{Named("ONNX Runtime DirectML", "Microsoft.ML.OnnxRuntime.dll")} · InsightFace buffalo_l (nur nicht-kommerziell)"),
            new("Motive", "SigLIP 2 base von Google (Apache-2.0, Download nach Bestätigung)"), // ADR-0052
            new("Video", $"{Named("LibVLCSharp", "LibVLCSharp.dll")} · FFmpeg (extern)"),
            new("Karte", "OpenStreetMap-Kacheln (ODbL) · CARTO Basemaps und MapTiler Satellite mit eigenem Schlüssel · eigene Kachelkarte"), // ADR-0051/0055
            new("Orte", "Ortsnamen: GeoNames (geonames.org), CC BY 4.0 · offline eingebettet"), // ADR-0050
        ];
    }
}
