using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Enhance;

namespace Lumora.Enhance;

/// <summary>Eine Wahl im Dialog „Fotos verbessern“ mit Zustand des Modells.</summary>
public sealed partial class EnhanceBatchChoice : ObservableObject
{
    public EnhanceBatchChoice(EnhanceBatchDialogViewModel owner, EnhanceTool tool, string title, string hint)
    {
        Owner = owner;
        Tool = tool;
        Title = title;
        Hint = hint;
    }

    public EnhanceBatchDialogViewModel Owner { get; }

    public EnhanceTool Tool { get; }

    public string Title { get; }

    public string Hint { get; }

    [ObservableProperty] private bool _isChecked;

    [ObservableProperty] private bool _isAvailable = true;

    [ObservableProperty] private string? _stateText;

    partial void OnIsCheckedChanged(bool value) => Owner.OnChoiceChanged(this);
}

/// <summary>
/// Dialog „n Fotos verbessern“ aus der Galerie (ADR-0071, Mockup 13): Werkzeuge (Automatisch, Hochskalieren 2×/4×, Gesichter,
/// Einfärben, Kratzer), Zeitschätzung, Hinweis auf Hintergrund und Grafikkarte. Liefert die Optionen; den Lauf startet die Galerie.
/// </summary>
public sealed partial class EnhanceBatchDialogViewModel : ObservableObject
{
    private readonly IPhotoEnhanceService _service;
    private readonly IReadOnlyList<int> _fileIds;
    private readonly ILogger _logger;
    private bool _syncing;
    private int _estimateVersion;

    public EnhanceBatchDialogViewModel(IPhotoEnhanceService service, IReadOnlyList<int> fileIds, ILogger? logger = null)
    {
        _service = service;
        _fileIds = fileIds;
        _logger = logger ?? NullLogger.Instance;
        Choices =
        [
            new(this, EnhanceTool.Auto, "Automatisch", "Lumora wählt je Foto: Kratzer bei Scans, Gesichter bei kleinen Gesichtern, Hochskalieren bei kleinen Bildern"),
            new(this, EnhanceTool.Upscale, "Hochskalieren und schärfen", "2× oder 4×"),
            new(this, EnhanceTool.Faces, "Gesichter restaurieren", "nur wo Gesichter erkannt sind"),
            new(this, EnhanceTool.Colorize, "Schwarz-Weiß einfärben", "nur bei Fotos ohne Farbe sinnvoll"),
            new(this, EnhanceTool.Erase, "Kratzer und Flecken entfernen", "bei eingescannten Fotos"),
        ];
        _syncing = true;
        Choices[0].IsChecked = true;
        _syncing = false;
    }

    public IReadOnlyList<EnhanceBatchChoice> Choices { get; }

    public string Title => _fileIds.Count == 1 ? "1 Foto verbessern" : $"{_fileIds.Count:N0} Fotos verbessern";

    public int PhotoCount => _fileIds.Count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpscale4))]
    private int _upscaleFactor = 2;

    public bool IsUpscale4 => UpscaleFactor == 4;

    [ObservableProperty] private string _estimateText = "Zeit wird geschätzt …";

    [ObservableProperty] private string? _problem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _canConfirm = true;

    /// <summary>Ergebnis nach „Verbessern“ (null = abgebrochen).</summary>
    public EnhanceOptions? Result { get; private set; }

    public event EventHandler? CloseRequested;

    public async Task LoadAsync()
    {
        try
        {
            var status = await _service.GetToolStatusAsync().ConfigureAwait(true);
            foreach (var choice in Choices.Where(c => c.Tool != EnhanceTool.Auto))
            {
                var s = status.FirstOrDefault(x => x.Tool == choice.Tool);
                choice.IsAvailable = s?.Ready == true;
                choice.StateText = s is { Ready: false } ? s.StateText : null;
            }

            var auto = Choices[0];
            auto.StateText = Choices.Skip(1).Any(c => c.IsAvailable) ? null : "keine Modelle geladen";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foto verbessern: Zustand der Werkzeuge nicht lesbar");
        }

        await UpdateEstimateAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void SetUpscaleFactor(string? factor)
    {
        UpscaleFactor = factor == "4" ? 4 : 2;
        _ = UpdateEstimateAsync();
    }

    /// <summary>„Automatisch“ schließt die übrigen aus und umgekehrt.</summary>
    internal void OnChoiceChanged(EnhanceBatchChoice choice)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (choice.IsChecked && choice.Tool == EnhanceTool.Auto)
            {
                foreach (var other in Choices.Skip(1))
                {
                    other.IsChecked = false;
                }
            }
            else if (choice.IsChecked)
            {
                Choices[0].IsChecked = false;
            }
        }
        finally
        {
            _syncing = false;
        }

        _ = UpdateEstimateAsync();
    }

    public EnhanceOptions BuildOptions() => new()
    {
        Auto = Choices[0].IsChecked,
        Upscale = Choice(EnhanceTool.Upscale),
        UpscaleFactor = UpscaleFactor,
        Faces = Choice(EnhanceTool.Faces),
        FaceStrength = 0.8,
        Colorize = Choice(EnhanceTool.Colorize),
        Erase = Choice(EnhanceTool.Erase),
        Scratches = Choice(EnhanceTool.Erase),
    };

    private bool Choice(EnhanceTool tool) => Choices.First(c => c.Tool == tool).IsChecked;

    public async Task UpdateEstimateAsync()
    {
        var version = Interlocked.Increment(ref _estimateVersion);
        var options = BuildOptions();
        if (options.IsEmpty)
        {
            EstimateText = "Bitte ein Werkzeug wählen.";
            CanConfirm = false;
            return;
        }

        try
        {
            var estimate = await _service.EstimateAsync(_fileIds, options).ConfigureAwait(true);
            if (version != _estimateVersion)
            {
                return;
            }

            Problem = estimate.MissingModels.Count > 0
                ? "Modell fehlt: " + string.Join(", ", estimate.MissingModels.Select(EnhanceTexts.Name)) + " — zuerst in Einstellungen › Modelle & Daten laden."
                : null;
            CanConfirm = estimate.MissingModels.Count == 0 && estimate.Photos > 0;
            EstimateText = estimate.Photos == 0
                ? "Keine Fotos in der Auswahl (Videos und Archive lassen sich nicht verbessern)."
                : $"Geschätzt {estimate.Text} · läuft im Hintergrund, Fortschritt in den Aktivitäten · wechselt sich mit Gesichts-/Motiv-Erkennung ab";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Foto verbessern: Schätzung fehlgeschlagen");
            EstimateText = "Zeit nicht schätzbar.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        Result = BuildOptions();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
