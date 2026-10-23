using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumora.Services.HiddenLibraries.Containers;

namespace Lumora.Shell.HiddenLibraries;

/// <summary>Seiten des Container-Assistenten (ADR-0061).</summary>
public enum ContainerAssistantStep
{
    /// <summary>Container schon eingehängt / neu mit BitLocker / neu mit VeraCrypt.</summary>
    Choice,

    /// <summary>Speicherort, Größe, Buchstabe, Bezeichnung der VHDX.</summary>
    BitLockerSetup,

    /// <summary>VHDX ist eingehängt; BitLocker-Assistent öffnen und Verschlüsselung prüfen.</summary>
    BitLockerEncrypt,

    /// <summary>VeraCrypt fehlt: Installation über winget bestätigen (oder Hinweis ohne winget).</summary>
    VeraCryptInstall,

    /// <summary>VeraCrypt-Assistent mit Schritt-Anleitung (verstecktes Volume als Standard).</summary>
    VeraCryptGuide,

    /// <summary>Neuen VeraCrypt-Container einhängen (VeraCrypt fragt das Kennwort).</summary>
    VeraCryptMount,

    /// <summary>Verschlüsseltes Laufwerk bereit → Bibliothek anlegen.</summary>
    Ready,
}

/// <summary>Auswahl auf der ersten Seite.</summary>
public enum ContainerChoice
{
    Existing,
    BitLocker,
    VeraCrypt,
}

public enum ContainerAssistantOutcome
{
    Cancelled,

    /// <summary>„Container ist schon eingehängt“ — weiter wie bisher.</summary>
    ExistingContainer,

    /// <summary>Neuer verschlüsselter Container bereit unter <see cref="ContainerAssistantResult.DriveRoot"/>.</summary>
    NewContainer,
}

/// <param name="DriveRoot">Bei <see cref="ContainerAssistantOutcome.NewContainer"/> das neue Laufwerk, z. B. <c>X:\</c>.</param>
/// <param name="Message">Hinweis nach einem Abbruch (z. B. Container angelegt, aber noch nicht verschlüsselt).</param>
public sealed record ContainerAssistantResult(ContainerAssistantOutcome Outcome, string? DriveRoot = null, string? Message = null)
{
    public static ContainerAssistantResult Cancelled(string? message = null) => new(ContainerAssistantOutcome.Cancelled, null, message);
}

/// <summary>Dateiauswahl des Assistenten (ohne Eintrag unter „Zuletzt verwendet“); Tests ersetzen sie.</summary>
public interface IContainerFilePicker
{
    /// <summary>Speicherort für eine neue VHDX; null = abgebrochen.</summary>
    string? PickNewVhdx(string suggestedPath);

    /// <summary>Vorhandene VeraCrypt-Containerdatei; null = abgebrochen.</summary>
    string? PickContainerFile();
}

/// <summary>
/// Zustandsmaschine „Neuen verschlüsselten Container erstellen“ (ADR-0061): Wahl → Erstellen (BitLocker-VHDX bzw.
/// VeraCrypt-Assistent) → verschlüsselt/eingehängt → Bibliothek anlegen. Abbrechen geht auf jeder Seite (außer während
/// ein Schritt läuft) und tut selbst nichts. Kein Kennwort berührt dieses ViewModel; protokolliert wird nichts.
/// </summary>
public sealed partial class ContainerAssistantViewModel : ObservableObject
{
    private const int MaxInstallLines = 6;

    private readonly IContainerCreation _creation;
    private readonly IContainerFilePicker _picker;

    public ContainerAssistantViewModel(IContainerCreation creation, IContainerFilePicker picker)
    {
        _creation = creation;
        _picker = picker;
        BitLockerUnavailableReason = creation.BitLockerUnavailableReason;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoiceStep), nameof(IsBitLockerSetupStep), nameof(IsBitLockerEncryptStep),
        nameof(IsVeraCryptInstallStep), nameof(IsVeraCryptGuideStep), nameof(IsVeraCryptMountStep), nameof(IsReadyStep),
        nameof(PrimaryText), nameof(CanGoBack), nameof(Heading))]
    private ContainerAssistantStep _step = ContainerAssistantStep.Choice;

    public bool IsChoiceStep => Step == ContainerAssistantStep.Choice;

    public bool IsBitLockerSetupStep => Step == ContainerAssistantStep.BitLockerSetup;

    public bool IsBitLockerEncryptStep => Step == ContainerAssistantStep.BitLockerEncrypt;

    public bool IsVeraCryptInstallStep => Step == ContainerAssistantStep.VeraCryptInstall;

    public bool IsVeraCryptGuideStep => Step == ContainerAssistantStep.VeraCryptGuide;

    public bool IsVeraCryptMountStep => Step == ContainerAssistantStep.VeraCryptMount;

    public bool IsReadyStep => Step == ContainerAssistantStep.Ready;

    public string Heading => Step switch
    {
        ContainerAssistantStep.Choice => "Wo sollen die Fotos der versteckten Bibliothek liegen?",
        ContainerAssistantStep.BitLockerSetup => "Neuer BitLocker-Container",
        ContainerAssistantStep.BitLockerEncrypt => "Mit BitLocker verschlüsseln",
        ContainerAssistantStep.VeraCryptInstall => "VeraCrypt installieren",
        ContainerAssistantStep.VeraCryptGuide => "VeraCrypt-Container erstellen",
        ContainerAssistantStep.VeraCryptMount => "VeraCrypt-Container einhängen",
        _ => "Container bereit",
    };

    /// <summary>Text des Hauptknopfs je Seite.</summary>
    public string PrimaryText => Step switch
    {
        ContainerAssistantStep.Choice => "Weiter",
        ContainerAssistantStep.BitLockerSetup => "Container erstellen",
        ContainerAssistantStep.BitLockerEncrypt => "Verschlüsselung prüfen",
        ContainerAssistantStep.VeraCryptInstall => IsWingetAvailable ? "Zustimmen und installieren" : "Erneut prüfen",
        ContainerAssistantStep.VeraCryptGuide => "Weiter: Container einhängen",
        ContainerAssistantStep.VeraCryptMount => "Einhängen",
        _ => ReadyLetter is { } letter ? $"Bibliothek auf {letter}: anlegen" : "Bibliothek anlegen",
    };

    /// <summary>Zurück nur, solange auf dieser Seite noch nichts angelegt wurde.</summary>
    public bool CanGoBack => Step is ContainerAssistantStep.BitLockerSetup or ContainerAssistantStep.VeraCryptInstall
        or ContainerAssistantStep.VeraCryptGuide or ContainerAssistantStep.VeraCryptMount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    private string? _info;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasInfo => !string.IsNullOrEmpty(Info);

    public ContainerAssistantResult? Result { get; private set; }

    /// <summary>Der Assistent ist fertig (Ergebnis in <see cref="Result"/>); das Fenster schließt sich.</summary>
    public event EventHandler? CloseRequested;

    // ---- Wahl ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExistingChoice), nameof(IsBitLockerChoice), nameof(IsVeraCryptChoice))]
    private ContainerChoice _choice = ContainerChoice.Existing;

    public bool IsExistingChoice
    {
        get => Choice == ContainerChoice.Existing;
        set
        {
            if (value)
            {
                Choice = ContainerChoice.Existing;
            }
        }
    }

    public bool IsBitLockerChoice
    {
        get => Choice == ContainerChoice.BitLocker;
        set
        {
            if (value)
            {
                Choice = ContainerChoice.BitLocker;
            }
        }
    }

    public bool IsVeraCryptChoice
    {
        get => Choice == ContainerChoice.VeraCrypt;
        set
        {
            if (value)
            {
                Choice = ContainerChoice.VeraCrypt;
            }
        }
    }

    /// <summary>null = BitLocker verfügbar; sonst Grund (Windows Home …).</summary>
    public string? BitLockerUnavailableReason { get; }

    public bool IsBitLockerAvailable => BitLockerUnavailableReason is null;

    public bool IsBitLockerUnavailable => !IsBitLockerAvailable;

    // ---- BitLocker ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VhdxHint))]
    private string _vhdxPath = string.Empty;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _freeLetters = [];

    [ObservableProperty]
    private string? _selectedLetter;

    [ObservableProperty]
    private string _volumeLabel = VhdxRules.DefaultLabel;

    /// <summary>Freier Platz und ggf. Hinweis „Systemlaufwerk“ zum gewählten Speicherort.</summary>
    public string VhdxHint
    {
        get
        {
            var parts = new List<string>();
            if (_creation.GetFreeBytes(VhdxPath) is { } free)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, "Frei auf diesem Laufwerk: {0:N0} GB. Die Datei wächst erst mit dem Inhalt (dynamisch erweiterbar).", free / (1024L * 1024 * 1024)));
            }

            if (_creation.SystemDriveHint(VhdxPath) is { } system)
            {
                parts.Add(system);
            }

            return string.Join(" ", parts);
        }
    }

    /// <summary>Buchstabe des angelegten, noch nicht als verschlüsselt erkannten Laufwerks.</summary>
    public char? CreatedLetter { get; private set; }

    // ---- VeraCrypt ----

    public bool IsWingetAvailable => _creation.IsWingetAvailable;

    public bool IsWingetMissing => !IsWingetAvailable;

    public string DownloadUrl => VeraCryptPackage.DownloadUrl;

    public string PackageText => $"Paket: {VeraCryptPackage.WingetId} von {VeraCryptPackage.Publisher}";

    public string SourceText => $"Quelle: {VeraCryptPackage.Source}";

    public string SizeInfoText => $"Größe: {VeraCryptPackage.ApproximateSize}";

    public string LicenseText => $"Lizenz: {VeraCryptPackage.License}. Mit „Zustimmen und installieren“ stimmen Sie dieser Lizenz und den Bedingungen der Paketquelle zu.";

    public string WhyWizardText => VeraCryptGuide.WhyWizard;

    /// <summary>Letzte Zeilen der winget-Ausgabe (nur Anzeige, nie im Protokoll).</summary>
    public ObservableCollection<string> InstallLines { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHiddenVolume), nameof(IsNormalVolume), nameof(GuideSteps), nameof(GuideWarnings))]
    private VeraCryptVolumeType _volumeType = VeraCryptGuide.DefaultType;

    public bool IsHiddenVolume
    {
        get => VolumeType == VeraCryptVolumeType.Hidden;
        set
        {
            if (value)
            {
                VolumeType = VeraCryptVolumeType.Hidden;
            }
        }
    }

    public bool IsNormalVolume
    {
        get => VolumeType == VeraCryptVolumeType.Normal;
        set
        {
            if (value)
            {
                VolumeType = VeraCryptVolumeType.Normal;
            }
        }
    }

    public IReadOnlyList<VeraCryptGuideStep> GuideSteps => VeraCryptGuide.Steps(VolumeType);

    public IReadOnlyList<string> GuideWarnings => VeraCryptGuide.Warnings(VolumeType);

    [ObservableProperty]
    private string _containerFile = string.Empty;

    [ObservableProperty]
    private string? _selectedMountLetter;

    // ---- Ergebnis ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText), nameof(ReadyText))]
    private char? _readyLetter;

    public string ReadyText => ReadyLetter is { } letter
        ? $"Der verschlüsselte Container ist als Laufwerk {letter}: eingehängt. Jetzt legt Lumora darauf die versteckte Bibliothek an (Name, Lumora-Kennwort, Ordner)."
        : string.Empty;

    // ---- Befehle ----

    /// <summary>Hauptknopf: je Seite der nächste Schritt.</summary>
    [RelayCommand]
    private Task PrimaryAsync() => Step switch
    {
        ContainerAssistantStep.Choice => NextAsync(),
        ContainerAssistantStep.BitLockerSetup => CreateVhdxAsync(),
        ContainerAssistantStep.BitLockerEncrypt => CheckEncryptionAsync(),
        ContainerAssistantStep.VeraCryptInstall => IsWingetAvailable ? InstallVeraCryptAsync() : RecheckVeraCryptAsync(),
        ContainerAssistantStep.VeraCryptGuide => GoToMountAsync(),
        ContainerAssistantStep.VeraCryptMount => MountAsync(),
        _ => FinishAsync(),
    };

    /// <summary>Erste Seite: Wahl auswerten (ohne Seiteneffekte).</summary>
    public Task NextAsync()
    {
        ClearMessages();
        switch (Choice)
        {
            case ContainerChoice.Existing:
                Complete(new ContainerAssistantResult(ContainerAssistantOutcome.ExistingContainer));
                break;
            case ContainerChoice.BitLocker when BitLockerUnavailableReason is { } reason:
                Error = reason;
                Choice = ContainerChoice.VeraCrypt;
                break;
            case ContainerChoice.BitLocker:
                PrepareBitLocker();
                Step = ContainerAssistantStep.BitLockerSetup;
                break;
            default:
                Step = _creation.IsVeraCryptInstalled ? ContainerAssistantStep.VeraCryptGuide : ContainerAssistantStep.VeraCryptInstall;
                OnPropertyChanged(nameof(PrimaryText));
                break;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Back()
    {
        if (IsBusy || !CanGoBack)
        {
            return;
        }

        ClearMessages();
        Step = Step == ContainerAssistantStep.VeraCryptMount ? ContainerAssistantStep.VeraCryptGuide : ContainerAssistantStep.Choice;
    }

    /// <summary>Abbrechen: schließt nur — angelegte Container bleiben, mit Hinweis.</summary>
    [RelayCommand]
    public void Cancel()
    {
        if (IsBusy)
        {
            return;
        }

        var hint = Step switch
        {
            ContainerAssistantStep.BitLockerEncrypt when CreatedLetter is { } letter =>
                $"Der Container ist als Laufwerk {letter}: angelegt, aber noch nicht verschlüsselt. Bitte im Explorer per Rechtsklick „BitLocker aktivieren“ — oder das Laufwerk „Auswerfen“ und die Containerdatei löschen.",
            ContainerAssistantStep.Ready when ReadyLetter is { } ready =>
                $"Der verschlüsselte Container ist als Laufwerk {ready}: eingehängt. Die Bibliothek können Sie später mit „Versteckte Bibliothek anlegen…“ → „Container ist schon eingehängt“ anlegen.",
            _ => null,
        };
        Complete(ContainerAssistantResult.Cancelled(hint));
    }

    [RelayCommand]
    private void BrowseVhdx()
    {
        if (_picker.PickNewVhdx(VhdxPath) is { } path)
        {
            VhdxPath = path;
        }
    }

    public Task CreateVhdxAsync() => RunAsync(async ct =>
    {
        if (!int.TryParse(SizeText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var size))
        {
            Error = "Bitte die Größe in GB als ganze Zahl angeben.";
            return;
        }

        if (ParseLetter(SelectedLetter) is not { } letter)
        {
            Error = "Bitte einen freien Laufwerksbuchstaben wählen.";
            return;
        }

        var plan = new VhdxPlan(VhdxPath.Trim(), size, letter, VolumeLabel.Trim());
        if (_creation.ValidateVhdx(plan) is { } problem)
        {
            Error = problem;
            return;
        }

        var result = await _creation.CreateVhdxAsync(plan, ct).ConfigureAwait(true);
        if (!result.Success)
        {
            Error = result.Message;
            RefreshLetters(_creation.GetFreeDriveLetters(), SelectedLetter);
            return;
        }

        CreatedLetter = letter;
        Info = result.Message + " Jetzt im BitLocker-Assistenten von Windows verschlüsseln.";
        Step = ContainerAssistantStep.BitLockerEncrypt;
    });

    [RelayCommand]
    private void OpenBitLockerWizard()
    {
        if (CreatedLetter is not { } letter)
        {
            return;
        }

        ClearMessages();
        var result = _creation.OpenBitLockerWizard(letter);
        if (result.Success)
        {
            Info = result.Message + " Danach hier „Verschlüsselung prüfen“.";
        }
        else
        {
            Error = result.Message;
        }
    }

    public Task CheckEncryptionAsync() => RunAsync(async ct =>
    {
        if (CreatedLetter is not { } letter)
        {
            return;
        }

        var state = await _creation.GetBitLockerProtectionAsync(letter, ct).ConfigureAwait(true);
        switch (state)
        {
            case BitLockerProtection.On:
            case BitLockerProtection.Encrypting:
                ReadyLetter = letter;
                Step = ContainerAssistantStep.Ready;
                break;
            case BitLockerProtection.Locked:
                Error = $"Laufwerk {letter}: ist verschlüsselt, aber gesperrt. Bitte im Explorer doppelklicken und mit dem BitLocker-Kennwort entsperren, dann erneut prüfen.";
                break;
            default:
                Error = $"Laufwerk {letter}: ist noch nicht mit BitLocker verschlüsselt. Bitte den BitLocker-Assistenten abschließen (Kennwort, Wiederherstellungsschlüssel, „Verschlüsselung starten“) und dann erneut prüfen.";
                break;
        }
    });

    public Task InstallVeraCryptAsync() => RunAsync(async ct =>
    {
        if (_creation.IsVeraCryptInstalled)
        {
            Step = ContainerAssistantStep.VeraCryptGuide; // inzwischen selbst installiert — kein zweites winget
            return;
        }

        InstallLines.Clear();
        var progress = new Progress<string>(line =>
        {
            InstallLines.Add(line);
            while (InstallLines.Count > MaxInstallLines)
            {
                InstallLines.RemoveAt(0);
            }
        });
        var result = await _creation.InstallVeraCryptAsync(progress, ct).ConfigureAwait(true);
        if (!result.Success)
        {
            Error = result.Message;
            return;
        }

        Info = result.Message;
        Step = ContainerAssistantStep.VeraCryptGuide;
    });

    [RelayCommand]
    public Task RecheckVeraCryptAsync()
    {
        ClearMessages();
        if (_creation.IsVeraCryptInstalled)
        {
            Step = ContainerAssistantStep.VeraCryptGuide;
        }
        else
        {
            Error = "VeraCrypt wurde noch nicht gefunden. Bitte zuerst installieren.";
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenDownloadPage()
    {
        ClearMessages();
        var result = _creation.OpenVeraCryptDownloadPage();
        if (!result.Success)
        {
            Error = result.Message;
        }
    }

    [RelayCommand]
    private void StartVeraCryptFormat()
    {
        ClearMessages();
        var result = _creation.StartVeraCryptFormat();
        if (result.Success)
        {
            Info = result.Message;
        }
        else
        {
            Error = result.Message;
        }
    }

    public Task GoToMountAsync()
    {
        ClearMessages();
        RefreshLetters(_creation.GetFreeDriveLetters(), SelectedMountLetter, mount: true);
        Step = ContainerAssistantStep.VeraCryptMount;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void BrowseContainer()
    {
        if (_picker.PickContainerFile() is { } file)
        {
            ContainerFile = file;
        }
    }

    public Task MountAsync() => RunAsync(async ct =>
    {
        var file = (ContainerFile ?? string.Empty).Trim().Trim('"');
        if (file.Length == 0)
        {
            Error = "Bitte die eben erstellte Containerdatei wählen.";
            return;
        }

        if (ParseLetter(SelectedMountLetter) is not { } letter)
        {
            Error = "Bitte einen freien Laufwerksbuchstaben wählen.";
            return;
        }

        var result = await _creation.MountVeraCryptAsync(file, letter, ct).ConfigureAwait(true);
        if (!result.Success)
        {
            Error = result.Message;
            return;
        }

        ReadyLetter = letter;
        Step = ContainerAssistantStep.Ready;
    });

    public Task FinishAsync()
    {
        if (ReadyLetter is { } letter)
        {
            Complete(new ContainerAssistantResult(ContainerAssistantOutcome.NewContainer, $"{letter}:\\"));
        }

        return Task.CompletedTask;
    }

    private void PrepareBitLocker()
    {
        var suggestion = _creation.SuggestVhdx();
        VhdxPath = suggestion.FilePath;
        SizeText = suggestion.SizeGigabytes.ToString(CultureInfo.CurrentCulture);
        VolumeLabel = suggestion.VolumeLabel;
        RefreshLetters(_creation.GetFreeDriveLetters(), suggestion.DriveLetter is { } l ? $"{l}:" : null);
    }

    /// <summary>Freie Buchstaben als „X:“; die Auswahl bleibt, sonst der letzte freie.</summary>
    private void RefreshLetters(IReadOnlyList<char> letters, string? keep, bool mount = false)
    {
        FreeLetters = letters.Select(l => $"{l}:").ToList();
        var selected = keep is not null && FreeLetters.Contains(keep) ? keep : FreeLetters.LastOrDefault();
        if (mount)
        {
            SelectedMountLetter = selected;
        }
        else
        {
            SelectedLetter = selected;
        }
    }

    private static char? ParseLetter(string? text)
        => text is { Length: >= 1 } && char.IsAsciiLetter(text[0]) ? char.ToUpperInvariant(text[0]) : null;

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        ClearMessages();
        IsBusy = true;
        try
        {
            await work(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearMessages()
    {
        Error = null;
        Info = null;
    }

    private void Complete(ContainerAssistantResult result)
    {
        Result = result;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
