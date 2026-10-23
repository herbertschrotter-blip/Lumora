using System.Globalization;

namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>Freier Platz und Dateisystem eines lokalen Laufwerks.</summary>
public sealed record DriveSpace(string Root, long FreeBytes, string FileSystem = "NTFS");

/// <summary>Lokale Laufwerke mit freiem Platz (für Vorschläge); Tests ersetzen sie.</summary>
public interface IDriveSpaceSource
{
    IReadOnlyList<DriveSpace> GetFixedDrives();
}

/// <summary>Laufwerke vom Typ „Fixed“, die bereit sind.</summary>
public sealed class LocalDriveSpaceSource : IDriveSpaceSource
{
    public IReadOnlyList<DriveSpace> GetFixedDrives()
    {
        var drives = new List<DriveSpace>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    drives.Add(new DriveSpace(drive.RootDirectory.FullName, drive.AvailableFreeSpace, drive.DriveFormat));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Laufwerk verschwindet gerade — überspringen
            }
        }

        return drives;
    }
}

/// <summary>Vorschlag für einen neuen BitLocker-Container (alles änderbar).</summary>
public sealed record VhdxSuggestion(string FilePath, int SizeGigabytes, char? DriveLetter, string VolumeLabel);

/// <summary>
/// „Neuen verschlüsselten Container erstellen“ (ADR-0061): BitLocker-VHDX über einen erhöhten Lumora-Lauf mit lesbarem
/// Auftrag (Windows-Funktionen für virtuelle Festplatten und Storage-Verwaltung, Nachtrag 0.4.26) plus den
/// Windows-BitLocker-Assistenten, oder VeraCrypt (Installation über winget nach Bestätigung, Assistent
/// <c>VeraCrypt Format.exe</c>, Einhängen wie „Container öffnen…“). Lumora sieht nie ein Container-Kennwort und
/// protokolliert nichts davon (schon „Container angelegt“ wäre im normalen Protokoll eine Spur, ADR-0049).
/// </summary>
public interface IContainerCreation
{
    /// <summary>null = BitLocker verfügbar; sonst der Grund (die Oberfläche schlägt dann VeraCrypt vor).</summary>
    string? BitLockerUnavailableReason { get; }

    bool IsVeraCryptInstalled { get; }

    bool IsWingetAvailable { get; }

    /// <summary>Freie Laufwerksbuchstaben D–Z.</summary>
    IReadOnlyList<char> GetFreeDriveLetters();

    VhdxSuggestion SuggestVhdx();

    /// <summary>Freier Platz auf dem Laufwerk des Pfads; null = unbekannt bzw. kein lokales Laufwerk.</summary>
    long? GetFreeBytes(string path);

    /// <summary>null = Plan geeignet, sonst die Meldung (Pfad, Cloud-Ordner, NTFS, Größe, Bezeichnung, Buchstabe frei).</summary>
    string? ValidateVhdx(VhdxPlan plan);

    /// <summary>Hinweis (kein Fehler), wenn der Container auf dem Systemlaufwerk liegen soll; sonst null.</summary>
    string? SystemDriveHint(string? path);

    /// <summary>
    /// Startet <c>Lumora.exe --container-create-vhdx …</c> erhöht (eine Windows-Rückfrage, Auftrag lesbar in der
    /// Befehlszeile). Abgelehnt → nichts bleibt.
    /// </summary>
    Task<ContainerCommandResult> CreateVhdxAsync(VhdxPlan plan, CancellationToken ct = default);

    /// <summary>Öffnet den Windows-eigenen BitLocker-Assistenten („BitLocker aktivieren“) für das Laufwerk.</summary>
    ContainerCommandResult OpenBitLockerWizard(char driveLetter);

    Task<BitLockerProtection> GetBitLockerProtectionAsync(char driveLetter, CancellationToken ct = default);

    /// <summary>winget-Installation — nur nach Bestätigung durch den Benutzer aufrufen.</summary>
    Task<ContainerCommandResult> InstallVeraCryptAsync(IProgress<string>? progress, CancellationToken ct = default);

    ContainerCommandResult OpenVeraCryptDownloadPage();

    /// <summary>Startet <c>VeraCrypt Format.exe</c> (Assistent) neben <c>VeraCrypt.exe</c>.</summary>
    ContainerCommandResult StartVeraCryptFormat();

    /// <summary>null = Containerdatei zum Einhängen geeignet (vorhanden, nicht in einem Cloud-Ordner), sonst die Meldung.</summary>
    string? ValidateContainerFile(string containerFile);

    /// <summary>Wie „Container öffnen…“: VeraCrypt fragt das Kennwort in seinem eigenen Fenster.</summary>
    Task<ContainerCommandResult> MountVeraCryptAsync(string containerFile, char driveLetter, CancellationToken ct = default);
}

/// <inheritdoc cref="IContainerCreation" />
public sealed class ContainerCreationService : IContainerCreation
{
    /// <summary>Höchstens so lange auf den erhöhten Lauf warten (wie vor 0.4.26).</summary>
    private static readonly TimeSpan ElevatedTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DriveCacheTime = TimeSpan.FromSeconds(10);
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly IContainerTools _containers;
    private readonly IVeraCryptLocator _veraCrypt;
    private readonly IVolumeDeviceQuery _devices;
    private readonly IDriveSpaceSource _drives;
    private readonly IElevatedRunner _elevated;
    private readonly IShellLauncher _launcher;
    private readonly IBitLockerPlatform _bitLocker;
    private readonly IVeraCryptInstaller _installer;
    private readonly string _systemRoot;
    private readonly Func<IReadOnlyList<string>> _cloudFolders;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string?> _selfExecutable;
    private IReadOnlyList<DriveSpace>? _driveCache;
    private long _driveCacheTicks;

    public ContainerCreationService(
        IContainerTools containers,
        IVeraCryptLocator veraCrypt,
        IVolumeDeviceQuery devices,
        IDriveSpaceSource drives,
        IElevatedRunner elevated,
        IShellLauncher launcher,
        IBitLockerPlatform bitLocker,
        IVeraCryptInstaller installer,
        string? systemRoot = null,
        Func<IReadOnlyList<string>>? cloudFolders = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string?>? selfExecutable = null)
    {
        _containers = containers;
        _veraCrypt = veraCrypt;
        _devices = devices;
        _drives = drives;
        _elevated = elevated;
        _launcher = launcher;
        _bitLocker = bitLocker;
        _installer = installer;
        _systemRoot = systemRoot ?? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        _cloudFolders = cloudFolders ?? HiddenLibraryFolderRules.OneDriveFolders;
        _delay = delay ?? Task.Delay;
        _selfExecutable = selfExecutable ?? (() => Environment.ProcessPath);
    }

    public string? BitLockerUnavailableReason => _bitLocker.UnavailableReason;

    public bool IsVeraCryptInstalled => _veraCrypt.FindExecutable() is not null;

    public bool IsWingetAvailable => _installer.IsWingetAvailable;

    public IReadOnlyList<char> GetFreeDriveLetters()
        => Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => _devices.GetDeviceName(c) is null).ToList();

    public VhdxSuggestion SuggestVhdx()
    {
        var drives = Drives(refresh: true).Where(d => IsNtfs(d.FileSystem)).ToList();
        var best = drives.Where(d => !IsSystemDrive(d.Root)).OrderByDescending(d => d.FreeBytes).FirstOrDefault()
                   ?? drives.OrderByDescending(d => d.FreeBytes).FirstOrDefault()
                   ?? new DriveSpace(_systemRoot, 0);
        var folder = Path.Combine(best.Root, VhdxRules.DefaultFolderName);
        var path = Path.Combine(folder, VhdxRules.DefaultFileName + VhdxRules.Extension);
        for (var n = 2; File.Exists(path) && n < 100; n++)
        {
            path = Path.Combine(folder, $"{VhdxRules.DefaultFileName}-{n}{VhdxRules.Extension}");
        }

        var free = GetFreeDriveLetters();
        return new VhdxSuggestion(path, VhdxRules.SuggestSizeGigabytes(best.FreeBytes), free.Count > 0 ? free[^1] : null, VhdxRules.DefaultLabel);
    }

    public long? GetFreeBytes(string path) => DriveOf(path)?.FreeBytes;

    public string? ValidateVhdx(VhdxPlan plan)
    {
        if (VhdxRules.ValidatePath(plan.FilePath, _cloudFolders()) is { } pathProblem)
        {
            return pathProblem;
        }

        if (DriveOf(plan.FilePath) is not { } drive)
        {
            return "Das Laufwerk für die Containerdatei ist kein lokales Laufwerk (oder nicht bereit).";
        }

        if (!IsNtfs(drive.FileSystem))
        {
            return $"Die Containerdatei muss auf einem NTFS-Laufwerk liegen (dieses ist {drive.FileSystem}). Bitte ein anderes Laufwerk wählen.";
        }

        if (plan.SizeGigabytes < VhdxRules.MinSizeGigabytes || plan.SizeGigabytes > VhdxRules.MaxSizeGigabytes)
        {
            return $"Die Größe muss zwischen {VhdxRules.MinSizeGigabytes} und {VhdxRules.MaxSizeGigabytes} GB liegen.";
        }

        if ((long)plan.SizeGigabytes * Gigabyte > drive.FreeBytes)
        {
            return $"Auf diesem Laufwerk sind nur {drive.FreeBytes / Gigabyte} GB frei. Bitte eine kleinere Größe oder ein anderes Laufwerk wählen.";
        }

        if (VhdxRules.ValidateLabel(plan.VolumeLabel) is { } labelProblem)
        {
            return labelProblem;
        }

        var letter = char.ToUpperInvariant(plan.DriveLetter);
        if (letter is < 'D' or > 'Z')
        {
            return "Bitte einen freien Laufwerksbuchstaben wählen.";
        }

        if (_devices.GetDeviceName(letter) is not null)
        {
            return $"Laufwerk {letter}: ist schon belegt. Bitte einen anderen Buchstaben wählen.";
        }

        return null;
    }

    public string? SystemDriveHint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !IsSystemDrive(Path.GetPathRoot(Path.GetFullPath(path))))
        {
            return null;
        }

        return $"Die Containerdatei liegt auf dem Systemlaufwerk {_systemRoot.TrimEnd('\\')} — das geht, ein anderes Laufwerk hält aber System und Fotos getrennt.";
    }

    public async Task<ContainerCommandResult> CreateVhdxAsync(VhdxPlan plan, CancellationToken ct = default)
    {
        _driveCache = null; // frischer freier Platz für die Prüfung
        if (ValidateVhdx(plan) is { } problem)
        {
            return new ContainerCommandResult(false, problem);
        }

        // Der erhöhte Schritt ist Lumora selbst (ADR-0061, Nachtrag 0.4.26) — nie ein anderes Programm. Unter einem fremden
        // Host (dotnet.exe, Testläufer) gibt es keinen eigenen Einsprung.
        var self = _selfExecutable();
        if (!IsOwnExecutable(self))
        {
            return new ContainerCommandResult(false, "Der Container lässt sich nur aus der Lumora-App (Lumora.exe) heraus anlegen.");
        }

        var file = Path.GetFullPath(plan.FilePath.Trim());
        var letter = char.ToUpperInvariant(plan.DriveLetter);
        var folder = Path.GetDirectoryName(file)!;
        var createdFolder = false;
        try
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                createdFolder = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ContainerCommandResult(false, $"Der Ordner für die Containerdatei kann nicht angelegt werden: {ex.Message}");
        }

        // Lesbarer Auftrag in der Befehlszeile, keine Datei als Übergabe: zwischen Windows-Rückfrage und Lauf kann nichts
        // ausgetauscht werden; der erhöhte Lauf prüft alles noch einmal (VhdxElevatedJob.Validate).
        var run = await _elevated.RunElevatedAsync(
            self!,
            VhdxElevatedCommand.BuildArguments(plan with { FilePath = file, DriveLetter = letter }),
            ElevatedTimeout,
            ct).ConfigureAwait(false);
        _driveCache = null;
        if (run.Declined)
        {
            RemoveFolderIfCreated(folder, createdFolder);
            return new ContainerCommandResult(false, "Abgebrochen: Die Windows-Rückfrage (Administratorrechte) wurde abgelehnt. Es wurde nichts angelegt.");
        }

        if (run.StartFailed)
        {
            RemoveFolderIfCreated(folder, createdFolder);
            return new ContainerCommandResult(false,
                "Windows konnte Lumora nicht mit Administratorrechten starten — vermutlich hat ein Sicherheitsprogramm Lumora.exe "
                + "gesperrt. Es wurde nichts angelegt. " + Alternatives);
        }

        if (run.TimedOut)
        {
            // Der erhöhte Lauf kann noch arbeiten — nichts wegräumen, nichts versprechen
            return new ContainerCommandResult(false,
                "Windows hat nicht rechtzeitig geantwortet; das Anlegen läuft eventuell noch. Bitte gleich im Explorer nachsehen, "
                + $"ob Laufwerk {letter}: erscheint — sonst die Containerdatei (falls vorhanden) löschen und erneut versuchen.");
        }

        if (run.ExitCode == 0 && await WaitForDriveAsync(letter, ct).ConfigureAwait(false))
        {
            return new ContainerCommandResult(true, VhdxElevatedCommand.Describe(VhdxJobExit.Success, letter));
        }

        // Bei einer abgelehnten Prüfung hat der erhöhte Lauf nichts angelegt — eine Datei dort gehört nicht ihm
        var fileLeft = File.Exists(file) && !VhdxElevatedCommand.IsRefusedBeforeCreate(run.ExitCode);
        if (!fileLeft)
        {
            RemoveFolderIfCreated(folder, createdFolder);
        }

        return new ContainerCommandResult(false, DescribeFailure(run.ExitCode, letter, fileLeft));
    }

    /// <summary>Hinweis, wenn die angelegte Datei liegen blieb (ohne Pfad — der steht im Assistenten).</summary>
    private const string EjectAndDelete = "Die angelegte Containerdatei ließ sich nicht entfernen — bitte im Explorer per Rechtsklick „Auswerfen“ (falls eingehängt) und dann löschen.";

    /// <summary>
    /// Exit-Code des erhöhten Laufs → Meldung. Ein Code, der nicht von Lumora stammt (oder 0 ohne Laufwerk und ohne Datei),
    /// heißt: der Lauf wurde von außen beendet bzw. nicht gestartet — meist durch ein Sicherheitsprogramm.
    /// </summary>
    public static string DescribeFailure(int exitCode, char letter, bool fileLeft)
    {
        if (exitCode == 0)
        {
            return fileLeft
                ? $"Der Container wurde angelegt, aber Laufwerk {letter}: ist nicht erschienen. Bitte im Explorer nachsehen; sonst die Containerdatei per Rechtsklick „Auswerfen“ und löschen."
                : BlockedMessage(fileLeft);
        }

        if (!VhdxElevatedCommand.IsOwnExitCode(exitCode))
        {
            return BlockedMessage(fileLeft);
        }

        var text = VhdxElevatedCommand.Describe((VhdxJobExit)exitCode, letter);
        return string.Create(CultureInfo.InvariantCulture, $"{text} (Fehler {exitCode}) ") + (fileLeft ? EjectAndDelete : "Es wurde nichts angelegt.");
    }

    private const string Alternatives =
        "Alternativ „Neu mit VeraCrypt“ wählen — oder den Container selbst anlegen und „Container ist schon eingehängt“ nehmen.";

    private static string BlockedMessage(bool fileLeft)
        => "Ein Sicherheitsprogramm hat den Schritt vermutlich blockiert: Lumora wurde mit Administratorrechten gestartet, hat "
           + "aber nicht zurückgemeldet. "
           + (fileLeft ? EjectAndDelete + " " : "Es wurde nichts angelegt. ")
           + Alternatives;

    /// <summary>Eine eigene Programmdatei (.exe), nicht der .NET-Host — nur sie kennt das Verb <c>--container-create-vhdx</c>.</summary>
    public static bool IsOwnExecutable(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && Path.IsPathFullyQualified(path)
           && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
           && !Path.GetFileName(path).StartsWith("testhost", StringComparison.OrdinalIgnoreCase);

    public ContainerCommandResult OpenBitLockerWizard(char driveLetter)
    {
        if (_bitLocker.UnavailableReason is { } reason)
        {
            return new ContainerCommandResult(false, reason);
        }

        var letter = char.ToUpperInvariant(driveLetter);
        return _launcher.Start(_bitLocker.WizardPath, _bitLocker.WizardArguments(letter)) switch
        {
            ShellLaunchResult.Started => new ContainerCommandResult(true, "Der BitLocker-Assistent von Windows ist geöffnet."),
            ShellLaunchResult.Declined => new ContainerCommandResult(false, "Die Windows-Rückfrage wurde abgelehnt — der BitLocker-Assistent braucht Administratorrechte. Bitte erneut öffnen."),
            _ => new ContainerCommandResult(false, $"Der BitLocker-Assistent ließ sich nicht öffnen. Alternativ im Explorer: Rechtsklick auf Laufwerk {letter}: → „BitLocker aktivieren“."),
        };
    }

    public Task<BitLockerProtection> GetBitLockerProtectionAsync(char driveLetter, CancellationToken ct = default)
        => _bitLocker.GetProtectionAsync(driveLetter, ct);

    public async Task<ContainerCommandResult> InstallVeraCryptAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var result = await _installer.InstallAsync(progress, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        return IsVeraCryptInstalled
            ? result
            : new ContainerCommandResult(false, "winget meldet Erfolg, aber VeraCrypt wurde nicht gefunden. Bitte Lumora neu starten (mit _hidden) oder „Erneut prüfen“.");
    }

    public ContainerCommandResult OpenVeraCryptDownloadPage()
        => _launcher.OpenUrl(VeraCryptPackage.DownloadUrl)
            ? new ContainerCommandResult(true, "Die Download-Seite von VeraCrypt ist im Browser geöffnet.")
            : new ContainerCommandResult(false, $"Der Browser ließ sich nicht öffnen. Adresse: {VeraCryptPackage.DownloadUrl}");

    public ContainerCommandResult StartVeraCryptFormat()
    {
        var exe = _veraCrypt.FindExecutable();
        if (exe is null)
        {
            return new ContainerCommandResult(false, "VeraCrypt ist nicht installiert.");
        }

        var format = Path.Combine(Path.GetDirectoryName(exe) ?? string.Empty, VeraCryptGuide.FormatExecutableName);
        if (!File.Exists(format))
        {
            return new ContainerCommandResult(false, "Der VeraCrypt-Assistent („VeraCrypt Format.exe“) wurde nicht gefunden. Bitte in VeraCrypt „Volume erstellen“ wählen.");
        }

        return _launcher.Start(format, null) switch
        {
            ShellLaunchResult.Started => new ContainerCommandResult(true, "Der VeraCrypt-Assistent ist geöffnet — bitte den Schritten hier folgen."),
            ShellLaunchResult.Declined => new ContainerCommandResult(false, "Die Windows-Rückfrage wurde abgelehnt. Bitte erneut starten."),
            _ => new ContainerCommandResult(false, "Der VeraCrypt-Assistent ließ sich nicht starten. Bitte in VeraCrypt „Volume erstellen“ wählen."),
        };
    }

    public string? ValidateContainerFile(string containerFile)
    {
        if (string.IsNullOrWhiteSpace(containerFile) || !Path.IsPathFullyQualified(containerFile) || !File.Exists(containerFile))
        {
            return "Bitte die eben erstellte Containerdatei wählen.";
        }

        return VhdxRules.IsInCloudFolder(containerFile, _cloudFolders())
            ? "Die Containerdatei liegt in OneDrive oder einem anderen Cloud-Ordner — dort darf sie nicht bleiben. Bitte in einen "
              + "Ordner außerhalb verschieben (VeraCrypt muss dafür ausgehängt sein) und erneut wählen."
            : null;
    }

    public Task<ContainerCommandResult> MountVeraCryptAsync(string containerFile, char driveLetter, CancellationToken ct = default)
        => ValidateContainerFile(containerFile) is { } problem
            ? Task.FromResult(new ContainerCommandResult(false, problem))
            : _containers.MountVeraCryptAsync(containerFile, driveLetter, ct);

    private static bool IsNtfs(string? fileSystem)
        => string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) || string.Equals(fileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);

    private bool IsSystemDrive(string? root)
        => string.Equals(root?.TrimEnd('\\'), _systemRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private DriveSpace? DriveOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return Drives(refresh: false).FirstOrDefault(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Laufwerksliste kurz zwischenspeichern — der Hinweis zum Speicherort fragt bei jedem Tastendruck.</summary>
    private IReadOnlyList<DriveSpace> Drives(bool refresh)
    {
        var now = Environment.TickCount64;
        if (refresh || _driveCache is null || now - _driveCacheTicks > DriveCacheTime.TotalMilliseconds)
        {
            _driveCache = _drives.GetFixedDrives();
            _driveCacheTicks = now;
        }

        return _driveCache;
    }

    /// <summary>Das neue Laufwerk erscheint gleich nach dem erhöhten Lauf; kurz nachsehen (höchstens ≈ 10 s).</summary>
    private async Task<bool> WaitForDriveAsync(char letter, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (_devices.GetDeviceName(letter) is not null)
            {
                return true;
            }

            await _delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        return _devices.GetDeviceName(letter) is not null;
    }

    private static void RemoveFolderIfCreated(string folder, bool created)
    {
        if (!created)
        {
            return;
        }

        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // bleibt leer liegen — kein Inhalt
        }
    }
}