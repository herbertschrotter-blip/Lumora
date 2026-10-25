using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Motifs;
using Lumora.Services.Motifs.Models;
using Lumora.Shell;

namespace Lumora.Motifs;

/// <summary>Auswahl einer Schwelle (null = „Standard“ = globale Schwelle).</summary>
public sealed record MotifThresholdChoice(double? Value, string Label)
{
    public static string Format(double value) => value < 0.01
        ? (value * 100).ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + " %"
        : (value * 100).ToString("0", CultureInfo.GetCultureInfo("de-DE")) + " %";
}

/// <summary>Zeile der Begriffsliste: Name, Beschreibung, Treffer, eigene Schwelle (Auswahl wirkt sofort).</summary>
public sealed partial class MotifTermRow : ObservableObject
{
    private readonly Action<MotifTermRow, double?> _thresholdChanged;
    private bool _loading;

    public MotifTermRow(MotifTermInfo info, IReadOnlyList<MotifThresholdChoice> choices, Action<MotifTermRow, double?> thresholdChanged)
    {
        Info = info;
        ThresholdChoices = choices;
        _thresholdChanged = thresholdChanged;
        _loading = true;
        SelectedThreshold = choices.FirstOrDefault(c => c.Value == info.Threshold) ?? choices[0];
        _loading = false;
    }

    public MotifTermInfo Info { get; }

    public int Id => Info.Id;

    public string Name => Info.Name;

    public string? Prompt => Info.Prompt;

    public bool HasPrompt => !string.IsNullOrWhiteSpace(Info.Prompt);

    public string FileCountText => Info.FileCount == 1 ? "1 Datei" : $"{Info.FileCount:N0} Dateien";

    /// <summary>„wird berechnet…“, solange der Text-Vektor fehlt.</summary>
    public bool IsPending => !Info.HasVector;

    public IReadOnlyList<MotifThresholdChoice> ThresholdChoices { get; }

    [ObservableProperty] private MotifThresholdChoice? _selectedThreshold;

    partial void OnSelectedThresholdChanged(MotifThresholdChoice? value)
    {
        if (!_loading && value is not null && value.Value != Info.Threshold)
        {
            _thresholdChanged(this, value.Value);
        }
    }
}

/// <summary>
/// Einstellungen › Motive (ADR-0052): Schalter an/aus und Grafikkarte, Modellstatus mit „Motiv-Modell
/// herunterladen…“ (nur nach Bestätigung mit Dateien, Quelle, Größen und Lizenz; Fortschritt, abbrechbar), Stand des
/// Index und „Neu analysieren“, globale Schwelle und die Begriffe der aktiven Bibliothek (anlegen, umbenennen,
/// Beschreibung, Schwelle, löschen). Zustand über <see cref="MotifIndexStatusMessage"/>, Begriffe über
/// <see cref="MotifTermsChangedMessage"/> — beide auf den UI-Thread gebracht.
/// </summary>
public sealed partial class MotifSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IMotifIndexService _index;
    private readonly IMotifTermService _terms;
    private readonly IMotifModelProvider _models;
    private readonly ILibraryService _libraries;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MotifSettingsViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly Lumora.Services.ModelPackages.IModelPackageManager? _packages; // ADR-0070: Laden über den Manager
    private CancellationTokenSource? _downloadCts;
    private bool _loading;
    private int _libraryId;
    private int _termsLoadGeneration;
    private Task? _latestTermsLoad;

    public MotifSettingsViewModel(
        IMotifIndexService index,
        IMotifTermService terms,
        IMotifModelProvider models,
        ILibraryService libraries,
        IDialogService dialogs,
        ILogger<MotifSettingsViewModel> logger,
        IMessenger? messenger = null,
        Lumora.Services.ModelPackages.IModelPackageManager? packages = null)
    {
        _packages = packages;
        _index = index;
        _terms = terms;
        _models = models;
        _libraries = libraries;
        _dialogs = dialogs;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        GlobalThresholdChoices = MotifDefaultsValues.ThresholdChoices
            .Select(v => new MotifThresholdChoice(v, MotifThresholdChoice.Format(v) + (Math.Abs(v - MotifDefaultsValues.Threshold) < 1e-9 ? " (Standard)" : string.Empty)))
            .ToList();
        TermThresholdChoices = new[] { new MotifThresholdChoice(null, "Standard") }
            .Concat(MotifDefaultsValues.ThresholdChoices.Select(v => new MotifThresholdChoice(v, MotifThresholdChoice.Format(v))))
            .ToList();
        _messenger.Register<MotifSettingsViewModel, MotifIndexStatusMessage>(this, static (r, m) => UiThread.Post(() => r.ShowStatus(m.Status)));
        _messenger.Register<MotifSettingsViewModel, MotifModelChangedMessage>(this, static (r, _) => UiThread.Post(r.RefreshModel));
        _messenger.Register<MotifSettingsViewModel, MotifTermsChangedMessage>(this, static (r, m) => UiThread.Post(() =>
        {
            // Begriffe gehören der Bibliotheks-Gruppe (ADR-0048, 0.2.56): die Nachricht nennt irgendeine Bibliothek der Gruppe
            // (den Anker des Begriffs) — neu gelesen wird ohnehin nur der eigene Bereich.
            if (r._libraryId != 0)
            {
                _ = r.LoadTermsAsync();
            }
        }));
        _messenger.Register<MotifSettingsViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.LoadTermsAsync(); }));
    }

    /// <summary>Text des Download-Knopfs.</summary>
    public const string DownloadButtonText = "Motiv-Modell herunterladen… (≈ 690 MB, SigLIP 2 von Google, Apache-2.0)";

    public IReadOnlyList<MotifThresholdChoice> GlobalThresholdChoices { get; }

    public IReadOnlyList<MotifThresholdChoice> TermThresholdChoices { get; }

    public ObservableCollection<MotifTermRow> Terms { get; } = new();

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _useGpu = true;
    [ObservableProperty] private MotifThresholdChoice? _selectedGlobalThreshold;
    [ObservableProperty] private bool _isModelReady;
    [ObservableProperty] private string _modelStatusText = string.Empty;
    [ObservableProperty] private string _modelFolder = string.Empty;
    [ObservableProperty] private string _indexStatusText = string.Empty;
    [ObservableProperty] private string _termsSummary = string.Empty;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTermCommand))]
    private string _newTermName = string.Empty;

    [ObservableProperty] private string _newTermPrompt = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand), nameof(CancelDownloadCommand))]
    private bool _isDownloading;

    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string? _downloadText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private bool _isRebuilding;

    /// <summary>Beim Öffnen des Reiters.</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            IsEnabled = _index.Settings.Enabled;
            UseGpu = _index.Settings.UseGpu;
            SelectedGlobalThreshold = GlobalThresholdChoices.FirstOrDefault(c => Math.Abs(c.Value!.Value - _index.Settings.DefaultThreshold) < 1e-9)
                                      ?? GlobalThresholdChoices.First(c => Math.Abs(c.Value!.Value - MotifDefaultsValues.Threshold) < 1e-9);
        }
        finally
        {
            _loading = false;
        }

        RefreshModel();
        ShowStatus(_index.Status);
        _ = LoadTermsAsync();
    }

    private void RefreshModel()
    {
        // Über die Encoder-Fabrik (produktiv = Dateien des Modellordners; im Rauchtest der Fake ohne Dateien)
        IsModelReady = _terms.IsModelAvailable;
        ModelFolder = _models.ModelFolder;
        ModelStatusText = IsModelReady
            ? "Modell vorhanden (SigLIP 2 base, Bild- und Sprachmodell)."
            : _models.IsOverridden
                ? "Modell nicht vorhanden — der Ordner aus LUMORA_MOTIF_MODEL enthält die Modelldateien nicht."
                : "Modell nicht vorhanden — ohne Modell ist die Motiv-Erkennung aus.";
        DownloadModelCommand.NotifyCanExecuteChanged();
    }

    internal void ShowStatus(MotifIndexStatus status) => IndexStatusText = Describe(status);

    /// <summary>Text des Index-Zustands für die Einstellungen.</summary>
    internal static string Describe(MotifIndexStatus status)
    {
        var counts = status.Total > 0 ? $"{status.Done:N0} von {status.Total:N0} Fotos und Videos analysiert" : null;
        var provider = status.ExecutionProvider is { } p ? $" · {p}" : string.Empty;
        return status.State switch
        {
            MotifIndexState.Disabled => "Ausgeschaltet.",
            MotifIndexState.ModelMissing => "Eingeschaltet, wartet auf das Modell.",
            MotifIndexState.Running => $"Läuft: {counts ?? "Dateien werden gezählt"}{provider}" + (status.MsPerPhoto > 0 ? $" · {status.MsPerPhoto:N0} ms/Foto" : string.Empty),
            MotifIndexState.Paused => $"Angehalten: {counts}{provider}",
            MotifIndexState.Waiting => status.Message is { } w ? $"{w}…" : "Wartet…",
            MotifIndexState.Error => $"Fehler: {status.Message}",
            _ => counts is null ? "Bereit." : $"Bereit: {counts}{provider}" + (status.Message is { } m ? $" ({m})" : string.Empty),
        };
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading)
        {
            _ = ApplyAsync(() => _index.SetEnabledAsync(value), "Motiv-Erkennung");
        }
    }

    partial void OnUseGpuChanged(bool value)
    {
        if (!_loading)
        {
            _ = ApplyAsync(() => _index.SetUseGpuAsync(value), "Grafikkarte verwenden");
        }
    }

    partial void OnSelectedGlobalThresholdChanged(MotifThresholdChoice? value)
    {
        if (!_loading && value?.Value is { } threshold && Math.Abs(threshold - _index.Settings.DefaultThreshold) > 1e-12)
        {
            _ = ApplyAsync(async () =>
            {
                await _index.SetDefaultThresholdAsync(threshold).ConfigureAwait(true);
                ShowMessage($"Schwelle {MotifThresholdChoice.Format(threshold)} — Treffer neu berechnet.", error: false);
            }, "Schwelle");
        }
    }

    private async Task ApplyAsync(Func<Task> apply, string what)
    {
        try
        {
            Message = null;
            await apply().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowMessage($"{what} konnte nicht gespeichert werden: {ex.Message}", error: true);
            _logger.LogError(ex, "Motive: {What} konnte nicht gespeichert werden", what);
        }
    }

    // ---- Begriffe -----------------------------------------------------------------------------

    /// <summary>
    /// Liest die Begriffe des aktiven Bereichs. Mehrere Anstöße (Öffnen des Reiters, Bibliothekswechsel, jede Änderung an
    /// Begriffen) laufen nebeneinander; nur der zuletzt angestoßene schreibt die Liste (0.4.41, Issue #20: ein älterer,
    /// langsamerer Lesevorgang brachte einen eben gelöschten Begriff zurück — Rauchtest „eigener Begriff gelöscht“).
    /// </summary>
    internal async Task LoadTermsAsync()
    {
        var own = LoadTermsCoreAsync(++_termsLoadGeneration);
        _latestTermsLoad = own;
        await own;

        // Überholt: erst zurückkehren, wenn der neueste Lesevorgang die Liste geschrieben hat (Aufrufer lesen danach Terms)
        while (_latestTermsLoad is { } latest && !ReferenceEquals(latest, own))
        {
            own = latest;
            await latest;
        }
    }

    private async Task LoadTermsCoreAsync(int generation)
    {
        try
        {
            var library = await _libraries.GetActiveAsync().ConfigureAwait(true);
            if (generation != _termsLoadGeneration)
            {
                return;
            }

            if (library is null)
            {
                _libraryId = 0;
                Terms.Clear();
                TermsSummary = "Keine Bibliothek — Begriffe gehören zur aktiven Bibliothek.";
                return;
            }

            _libraryId = library.Id;
            var terms = await _terms.GetTermsAsync(library.Id).ConfigureAwait(true);
            if (generation != _termsLoadGeneration)
            {
                return; // ein neuerer Anstoß liest schon
            }

            Terms.Clear();
            foreach (var term in terms)
            {
                Terms.Add(new MotifTermRow(term, TermThresholdChoices, OnRowThresholdChanged));
            }

            var withHits = terms.Count(t => t.FileCount > 0);
            TermsSummary = $"{terms.Count:N0} Begriffe in „{library.Name}“ · {withHits:N0} davon mit Treffern"
                           + (terms.Any(t => !t.HasVector) && IsModelReady ? " · Texte werden berechnet…" : string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Motive: Begriffe konnten nicht geladen werden");
            ShowMessage($"Begriffe konnten nicht geladen werden: {ex.Message}", error: true);
        }
    }

    private bool CanAddTerm() => !string.IsNullOrWhiteSpace(NewTermName);

    [RelayCommand(CanExecute = nameof(CanAddTerm))]
    private Task AddTermAsync() => RunTermActionAsync(async () =>
    {
        var name = NewTermName.Trim();
        await _terms.AddTermAsync(_libraryId, name, string.IsNullOrWhiteSpace(NewTermPrompt) ? null : NewTermPrompt).ConfigureAwait(true);
        NewTermName = string.Empty;
        NewTermPrompt = string.Empty;
        ShowMessage(IsModelReady ? $"Begriff „{name}“ angelegt — Treffer berechnet." : $"Begriff „{name}“ angelegt — Treffer gibt es nach dem Modell-Download.", error: false);
    });

    [RelayCommand]
    private async Task RenameTermAsync(MotifTermRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync("Begriff umbenennen", $"Neuer Name für „{row.Name}“:", row.Name, "Umbenennen").ConfigureAwait(true);
        if (name is null || string.Equals(name.Trim(), row.Name, StringComparison.Ordinal))
        {
            return;
        }

        await RunTermActionAsync(() => _terms.UpdateTermAsync(row.Id, name, row.Prompt)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task EditPromptAsync(MotifTermRow? row)
    {
        if (row is null)
        {
            return;
        }

        var prompt = await _dialogs.PromptTextAsync(
            "Beschreibung für die Erkennung",
            $"Was zeigt ein Foto mit „{row.Name}“? Englisch trennt meist am besten (z. B. „a photo of a dog“). Leer = nur der Begriff selbst.",
            row.Prompt ?? string.Empty,
            "Übernehmen").ConfigureAwait(true);
        if (prompt is null || string.Equals(prompt.Trim(), row.Prompt ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        await RunTermActionAsync(() => _terms.UpdateTermAsync(row.Id, row.Name, prompt)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteTermAsync(MotifTermRow? row)
    {
        if (row is null)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Begriff löschen",
            $"Den Begriff „{row.Name}“ ({row.FileCountText}) löschen? Die Fotos bleiben unverändert; nur die Treffer dieses Begriffs verschwinden.",
            "Löschen",
            isDestructive: true).ConfigureAwait(true);
        if (ok)
        {
            await RunTermActionAsync(() => _terms.DeleteTermAsync(row.Id)).ConfigureAwait(true);
        }
    }

    private void OnRowThresholdChanged(MotifTermRow row, double? threshold)
        => _ = RunTermActionAsync(() => _terms.SetThresholdAsync(row.Id, threshold));

    private async Task RunTermActionAsync(Func<Task> action)
    {
        try
        {
            Message = null;
            await action().ConfigureAwait(true);
        }
        catch (MotifTermException ex)
        {
            ShowMessage(ex.Message, error: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Motive: Aktion an einem Begriff gescheitert");
            ShowMessage($"Die Aktion ist fehlgeschlagen: {ex.Message}", error: true);
        }

        await LoadTermsAsync().ConfigureAwait(true);
    }

    // ---- Modell ---------------------------------------------------------------------------------

    private bool CanDownload() => !IsDownloading && !IsModelReady && !_models.IsOverridden;

    /// <summary>Text des Bestätigungsdialogs: Dateien mit Größen, Quelle, Ziel, Lizenz.</summary>
    internal string DownloadConfirmationText()
    {
        var info = _models.DownloadInfo;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.CurrentCulture, $"Lumora lädt das Bild-Sprach-Modell „SigLIP 2 base“ (Google, als ONNX von Hugging Face) herunter — zusammen {info.TotalSize / 1_000_000.0:N0} MB:\n");
        foreach (var file in info.Files.Where(f => f.Size >= 1_000_000))
        {
            sb.Append(CultureInfo.CurrentCulture, $"  • {file.RelativePath} ({file.Size / 1_000_000.0:N0} MB)\n");
        }

        sb.Append(CultureInfo.CurrentCulture, $"  • {info.Files.Count(f => f.Size < 1_000_000)} kleine Konfigurationsdateien\n");
        sb.Append(CultureInfo.CurrentCulture, $"Quelle: {info.BaseUri}\nZiel: {_models.ModelFolder}\n\n");
        sb.Append("Jede Datei wird per SHA-256 geprüft. Lizenz: Apache-2.0 (auch kommerziell nutzbar).\n\n");
        sb.Append("Die Motiv-Erkennung läuft danach vollständig auf diesem PC — Fotos verlassen den Rechner nicht.");
        return sb.ToString();
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadModelAsync()
    {
        if (_packages is not null)
        {
            // ADR-0070: dieselbe Bestätigung (Name, Quelle, Größe, Lizenz, Ziel) wie in Einstellungen › Modelle & Daten
            Lumora.Services.ModelPackages.ModelDownloadRequest? request;
            try
            {
                request = await Lumora.Settings.ModelPackages.ModelDownloadDialog.AskAsync(
                    _dialogs, _packages, Lumora.Services.ModelPackages.ModelPackageCatalog.MotifsId).ConfigureAwait(true);
            }
            catch (Lumora.Services.ModelPackages.ModelPackageException ex)
            {
                ShowMessage(ex.Message, error: true);
                return;
            }

            if (request is not null)
            {
                await StartDownloadAsync(request).ConfigureAwait(true);
            }

            return;
        }

        var ok = await _dialogs.ConfirmAsync("Motiv-Modell herunterladen?", DownloadConfirmationText(), "Herunterladen").ConfigureAwait(true);
        if (ok)
        {
            await StartDownloadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Download nach der Bestätigung; mit Manager (ADR-0070) über <paramref name="request"/>, sonst (Tests) direkt.</summary>
    internal async Task StartDownloadAsync(Lumora.Services.ModelPackages.ModelDownloadRequest? request = null)
    {
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        IsDownloading = true;
        DownloadPercent = 0;
        DownloadText = "Verbindung wird aufgebaut…";
        Message = null;
        try
        {
            if (_packages is not null && request is not null)
            {
                var progress = new Progress<Lumora.Services.ModelPackages.ModelPackageProgress>(p => ShowProgress(p.Stage, p.Done, p.Total));
                await Task.Run(() => _packages.InstallAsync(request, progress, cts.Token), cts.Token).ConfigureAwait(true);
            }
            else
            {
                var progress = new Progress<MotifModelDownloadProgress>(p => ShowProgress(p.Stage, p.Done, p.Total));
                await _models.DownloadAsync(progress, cts.Token).ConfigureAwait(true);
            }

            ShowMessage("Modell heruntergeladen und geprüft — die Fotos werden jetzt im Hintergrund analysiert.", error: false);
            _index.RequestRun();
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Download abgebrochen — angefangene Dateien wurden verworfen.", error: false);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, error: true);
            _logger.LogWarning(ex, "Motive: Modell-Download gescheitert");
        }
        finally
        {
            _downloadCts = null;
            IsDownloading = false;
            DownloadText = null;
            RefreshModel();
        }
    }

    private void ShowProgress(string stage, long done, long total)
    {
        DownloadPercent = total > 0 ? Math.Clamp(100.0 * done / total, 0, 100) : DownloadPercent;
        DownloadText = total > 0 ? $"{stage}: {done / 1_000_000.0:N0} von {total / 1_000_000.0:N0} MB" : $"{stage}…";
    }

    private bool CanCancelDownload() => IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload() => _downloadCts?.Cancel();

    private bool CanRebuild() => !IsRebuilding;

    [RelayCommand(CanExecute = nameof(CanRebuild))]
    private async Task RebuildAsync()
    {
        var ok = await _dialogs.ConfirmAsync(
            "Motive neu analysieren?",
            "Alle Fotos und Videos der aktiven Bibliothek werden im Hintergrund erneut analysiert. Begriffe und Schwellen bleiben. " +
            "Nötig ist das nur nach Problemen — Begriffe ändern rechnet die Treffer ohne neue Analyse.",
            "Neu analysieren").ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        IsRebuilding = true;
        try
        {
            var count = await _index.RebuildAsync().ConfigureAwait(true);
            ShowMessage(count == 0 ? "Keine Vektoren verworfen." : $"{count:N0} Dateien werden neu analysiert.", error: false);
        }
        catch (Exception ex)
        {
            ShowMessage($"Neu analysieren gescheitert: {ex.Message}", error: true);
            _logger.LogError(ex, "Motive: Neu analysieren gescheitert");
        }
        finally
        {
            IsRebuilding = false;
        }
    }

    private void ShowMessage(string text, bool error)
    {
        MessageIsError = error;
        Message = text;
    }

    public void Dispose()
    {
        _downloadCts?.Cancel();
        _messenger.UnregisterAll(this);
    }
}
