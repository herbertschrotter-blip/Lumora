using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.Relocation;

namespace Lumora.Shell.HiddenLibraries.Relocation;

/// <summary>Ein Laufwerk zur Auswahl als Ziel (Mockup 26, Schritt 2).</summary>
public sealed partial class RelocationDrive(string root, string label, long freeBytes, long totalBytes, bool removable, string? blockedReason) : ObservableObject
{
    /// <summary>„E:\“.</summary>
    public string Root { get; } = root;

    public string Name => Root.TrimEnd('\\');

    public string Label { get; } = label;

    public long FreeBytes { get; } = freeBytes;

    public bool IsRemovable { get; } = removable;

    /// <summary>Warum es nicht geht („hier liegt der Container jetzt“, „zu wenig Platz“), null = wählbar.</summary>
    public string? BlockedReason { get; } = blockedReason;

    public bool IsUsable => BlockedReason is null;

    /// <summary>Belegt in Prozent (Balken).</summary>
    public double UsedPercent { get; } = totalBytes > 0 ? 100.0 * (totalBytes - freeBytes) / totalBytes : 0;

    public string FreeText => BlockedReason ?? $"{ContainerRelocationRules.FormatBytes(FreeBytes)} frei" + (IsRemovable ? " · wechselbar: langsamer, muss zum Öffnen angesteckt sein" : string.Empty);

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Laufwerke des Rechners (im Test vorgegeben).</summary>
public interface IRelocationDriveSource
{
    IReadOnlyList<(string Root, string Label, long Free, long Total, bool Removable)> GetDrives();
}

/// <summary>Feste und wechselbare, bereite Laufwerke über <see cref="DriveInfo"/>.</summary>
public sealed class SystemRelocationDrives : IRelocationDriveSource
{
    public IReadOnlyList<(string Root, string Label, long Free, long Total, bool Removable)> GetDrives()
    {
        var list = new List<(string, string, long, long, bool)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                {
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? (drive.DriveType == DriveType.Removable ? "Wechseldatenträger" : "Lokaler Datenträger") : drive.VolumeLabel;
                    list.Add((drive.Name, label, drive.AvailableFreeSpace, drive.TotalSize, drive.DriveType == DriveType.Removable));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // nicht bereit — fehlt in der Liste
            }
        }

        return list;
    }
}

/// <summary>
/// Assistent „Container verschieben“ (Mockup 26): Quelle (erkannt bzw. gewählt), Ziel (Laufwerk, Ordner und Name, Prüfung)
/// und Prüfen-und-Starten mit der Wahl für die alte Datei. Ergebnis ist ein <see cref="ContainerRelocationPlan"/>.
/// </summary>
public sealed partial class ContainerRelocationViewModel : ObservableObject
{
    private readonly char _letter;
    private readonly IRelocationDriveSource _drives;
    private readonly Func<string, bool> _exists;
    private readonly IReadOnlyList<string> _syncFolders;

    /// <param name="detectedFile">Datei hinter dem Laufwerk (VHDX über Windows), null = Benutzer wählt sie.</param>
    public ContainerRelocationViewModel(char driveLetter, bool isVeraCrypt, string? detectedFile, IRelocationDriveSource drives, Func<string, bool>? exists = null, IReadOnlyList<string>? syncFolders = null)
    {
        _letter = char.ToUpperInvariant(driveLetter);
        IsVeraCrypt = isVeraCrypt;
        IsDetected = detectedFile is not null;
        _drives = drives;
        _exists = exists ?? File.Exists;
        _syncFolders = syncFolders ?? ContainerRelocationRules.KnownSyncFolders();
        if (detectedFile is not null)
        {
            SetSource(detectedFile);
        }
    }

    public char DriveLetter => _letter;

    public bool IsVeraCrypt { get; }

    /// <summary>Datei kam von Windows (VHDX) statt aus „Datei wählen…“.</summary>
    public bool IsDetected { get; }

    public string SourceTitle => IsVeraCrypt ? $"VeraCrypt-Container als {_letter}:" : $"BitLocker-VHDX hinter {_letter}:";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource), nameof(SourceDetails))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string? _sourceFile;

    private long _sourceBytes;

    public bool HasSource => SourceFile is not null;

    public string PickSourceText => IsDetected ? "Andere Datei…" : "Datei wählen…";

    public string SourceDetails => SourceFile is null
        ? (IsVeraCrypt ? "Bitte die Container-Datei wählen — VeraCrypt verrät den Pfad nicht." : "Windows nannte keine Datei — bitte selbst wählen.")
        : $"Datei {ContainerRelocationRules.FormatBytes(_sourceBytes)} · Laufwerk {Path.GetPathRoot(SourceFile)?.TrimEnd('\\')}";

    /// <summary>Quelle setzen (erkannt oder gewählt) und das Ziel neu vorschlagen.</summary>
    public void SetSource(string file)
    {
        _sourceBytes = new FileInfo(file).Length;
        SourceFile = file;
        LoadDrives();
    }

    // ---- Schritte ----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(NextText), nameof(StepText), nameof(CanGoBack))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    private int _step = 1;

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    public bool IsStep3 => Step == 3;

    public bool CanGoBack => Step > 1;

    public string StepText => $"Schritt {Step} von 3";

    public string NextText => Step == 3 ? "Verschieben starten" : "Weiter";

    /// <summary>Gesetzt bei „Verschieben starten“; das Fenster schließt dann.</summary>
    public ContainerRelocationPlan? Result { get; private set; }

    public event EventHandler? Finished;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        if (Step < 3)
        {
            Step++;
            return;
        }

        Result = new ContainerRelocationPlan(SourceFile!, Path.GetFullPath(TargetFile), _letter, !IsVeraCrypt, KeepOldFile ? OldContainerFile.Keep : OldContainerFile.RecycleBin, Check.IsRename);
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private bool CanNext() => Step switch
    {
        1 => HasSource,
        2 => Check.CanStart && !string.IsNullOrWhiteSpace(TargetFile),
        _ => Check.CanStart,
    };

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step = Math.Max(1, Step - 1);

    // ---- Ziel --------------------------------------------------------------------------------------------------------

    public List<RelocationDrive> Drives { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Check), nameof(ProblemText), nameof(HasProblem), nameof(ModeText), nameof(PlanCopyText), nameof(EstimateText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string _targetFile = string.Empty;

    public ContainerTargetCheck Check => SourceFile is null || string.IsNullOrWhiteSpace(TargetFile)
        ? new ContainerTargetCheck(["Bitte ein Ziel wählen."], false)
        : ContainerRelocationRules.Check(SourceFile, _sourceBytes, TargetFile, _letter, FreeBytesOf, _exists, _syncFolders);

    public bool HasProblem => !Check.CanStart && !string.IsNullOrWhiteSpace(TargetFile);

    public string ProblemText => string.Join("\n", Check.Problems);

    public string ModeText => Check.IsRename
        ? "Gleiches Laufwerk: Lumora benennt die Datei nur um — das geht sofort."
        : "Anderes Laufwerk: Lumora kopiert die Datei, prüft die Kopie und öffnet erst dann den Container am neuen Ort.";

    [RelayCommand]
    private void SelectDrive(RelocationDrive? drive)
    {
        if (drive is not { IsUsable: true } || SourceFile is null)
        {
            return;
        }

        foreach (var d in Drives)
        {
            d.IsSelected = ReferenceEquals(d, drive);
        }

        // gleicher Ordner wie bisher, nur auf dem gewählten Laufwerk
        var relative = Path.GetRelativePath(Path.GetPathRoot(SourceFile)!, SourceFile);
        TargetFile = Path.Combine(drive.Root, relative);
    }

    /// <summary>Ordner aus „Ordner…“: gleicher Dateiname im gewählten Ordner.</summary>
    public void SetTargetFolder(string folder)
    {
        if (SourceFile is not null)
        {
            TargetFile = Path.Combine(folder, Path.GetFileName(SourceFile));
        }
    }

    private void LoadDrives()
    {
        var sourceRoot = Path.GetPathRoot(SourceFile) ?? string.Empty;
        Drives = _drives.GetDrives()
            .Where(d => char.ToUpperInvariant(d.Root[0]) != _letter)
            .Select(d => new RelocationDrive(d.Root, d.Label, d.Free, d.Total, d.Removable,
                string.Equals(d.Root, sourceRoot, StringComparison.OrdinalIgnoreCase) ? null
                : d.Free < _sourceBytes + ContainerRelocationRules.SpareBytes ? $"{ContainerRelocationRules.FormatBytes(d.Free)} frei · zu wenig für {ContainerRelocationRules.FormatBytes(_sourceBytes)}"
                : null))
            .OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase)
            .ToList();
        OnPropertyChanged(nameof(Drives));
        // Vorschlag: das wählbare Laufwerk mit dem meisten Platz außer dem bisherigen
        if (Drives.Where(d => d.IsUsable && !string.Equals(d.Root, sourceRoot, StringComparison.OrdinalIgnoreCase)).MaxBy(d => d.FreeBytes) is { } best)
        {
            SelectDrive(best);
        }
    }

    private long? FreeBytesOf(string root)
    {
        foreach (var d in _drives.GetDrives())
        {
            if (string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                return d.Free;
            }
        }

        return null;
    }

    // ---- Prüfen und starten ------------------------------------------------------------------------------------------

    [ObservableProperty] private bool _keepOldFile = true;

    public bool RecycleOldFile
    {
        get => !KeepOldFile;
        set => KeepOldFile = !value;
    }

    partial void OnKeepOldFileChanged(bool value) => OnPropertyChanged(nameof(RecycleOldFile));

    public string PlanCopyText => Check.IsRename
        ? $"Umbenennen nach {TargetFile}"
        : $"Kopieren nach {TargetFile} · {ContainerRelocationRules.FormatBytes(_sourceBytes)}";

    /// <summary>Grob geschätzte Dauer: Kopieren und Prüfen mit je etwa 150 MB/s.</summary>
    public string EstimateText
    {
        get
        {
            if (Check.IsRename)
            {
                return "So läuft es ab · wenige Sekunden";
            }

            var minutes = Math.Max(1, (int)Math.Round(_sourceBytes * 2 / (150.0 * (1 << 20)) / 60));
            return $"So läuft es ab · geschätzt {(minutes >= 90 ? $"{minutes / 60.0:0.#} h" : $"{minutes} min")}";
        }
    }

    public string OldFolderText => SourceFile is null ? string.Empty : $"Sie liegt weiter unter {Path.GetDirectoryName(SourceFile)} — löschen Sie sie selbst, wenn alles passt.";

    public string ReopenText => IsVeraCrypt
        ? $"Am neuen Ort als {_letter}: öffnen · VeraCrypt fragt das Kennwort des Containers"
        : $"Am neuen Ort als {_letter}: öffnen · das Kennwort fragt Windows ab";

    public string LetterHint => $"Der Laufwerksbuchstabe bleibt {_letter}: — der Katalog kennt die Fotos unter {_letter}:. Erscheint der Container am Ende unter einem anderen Buchstaben, sagt Lumora Bescheid; die alte Datei bleibt dann unberührt.";
}
