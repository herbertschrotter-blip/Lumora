using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lumora.Services.HiddenLibraries;

/// <summary>Art des verschlüsselten Containers hinter einem Laufwerk.</summary>
public enum ContainerKind
{
    /// <summary>Nicht erkannt (normales Laufwerk, BitLocker-VHDX wird erst beim Aushängen über die Storage-Verwaltung geprüft).</summary>
    Unknown = 0,

    /// <summary>VeraCrypt- (oder TrueCrypt-)Volume: Gerätename <c>\Device\VeraCryptVolumeX</c>.</summary>
    VeraCrypt = 1,
}

/// <summary>Ergebnis eines Container-Befehls; <see cref="Message"/> ist für den Benutzer (ohne Pfade der Bibliothek).</summary>
public sealed record ContainerCommandResult(bool Success, string Message);

/// <summary>
/// Wartezeiten beim Einhängen einer VHDX (0.5.4): <paramref name="Poll"/> Abfrage-Takt, <paramref name="Appear"/> bis das neue
/// Laufwerk da ist (schließt eine Windows-Rückfrage nach Administratorrechten ein), <paramref name="UnlockSettle"/> nach der
/// Windows-Abfrage „Laufwerk entsperren“, bis das Laufwerk lesbar ist. Tests verkürzen sie.
/// </summary>
public sealed record VirtualDiskMountTimings(TimeSpan Poll, TimeSpan Appear, TimeSpan UnlockSettle)
{
    public static VirtualDiskMountTimings Default { get; } = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(15));
}

/// <summary>Ergebnis eines Prozesslaufs.</summary>
public sealed record ProcessRunResult(int ExitCode, bool TimedOut);

/// <summary>Startet externe Programme (VeraCrypt, PowerShell); Tests ersetzen ihn — nie echt ein-/aushängen in Tests.</summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Gerätename eines Laufwerksbuchstabens (Windows <c>QueryDosDevice</c>); Tests ersetzen ihn.</summary>
public interface IVolumeDeviceQuery
{
    /// <summary>z. B. <c>\Device\VeraCryptVolumeX</c> oder <c>\Device\HarddiskVolume7</c>; null, wenn es das Laufwerk nicht gibt.</summary>
    string? GetDeviceName(char driveLetter);
}

/// <summary>
/// Eingehängte Image-Dateien (VHD/VHDX) erkennen und wie Explorer › „Auswerfen“ abhängen — ohne Administratorrechte
/// (Nachtrag 0.4.31, ADR-0049/ADR-0061). Tests und Rauchtest ersetzen ihn; nie echt auswerfen in Tests.
/// </summary>
public interface IVirtualDiskEjector
{
    /// <summary>true, wenn das Laufwerk auf einer eingehängten Image-Datei liegt (<c>BusType</c> „File Backed Virtual“).</summary>
    bool IsVirtualDisk(char driveLetter);

    /// <summary>
    /// Wirft das Laufwerk aus und wartet, bis <paramref name="isMounted"/> false liefert oder <paramref name="timeout"/>
    /// um ist. true = ausgeworfen.
    /// </summary>
    Task<bool> EjectAsync(char driveLetter, Func<bool> isMounted, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Image-Datei (VHD/VHDX) einhängen wie Explorer › „Bereitstellen“ und prüfen, ob ein Laufwerk lesbar ist (Nachtrag 0.5.4,
/// ADR-0049/ADR-0061). Tests und Rauchtest ersetzen ihn; nie echt einhängen in Tests.
/// </summary>
public interface IVirtualDiskMounter
{
    /// <summary>Übergibt die Datei an Windows zum Einhängen; true = Windows hat den Auftrag angenommen (der Buchstabe kommt später).</summary>
    Task<bool> MountAsync(string imagePath, CancellationToken ct);

    /// <summary>Laufwerk lesbar — false z. B. für eine gesperrte BitLocker-Partition.</summary>
    bool IsVolumeReady(char driveLetter);
}

/// <summary>Findet <c>VeraCrypt.exe</c> (Registry, Standardpfad); null = nicht installiert. Lumora installiert nichts.</summary>
public interface IVeraCryptLocator
{
    string? FindExecutable();
}

/// <summary>
/// Container ein- und aushängen (ADR-0049). Lumora sieht kein Container-Kennwort: „Container öffnen…“ startet VeraCrypt,
/// das in SEINEM Fenster fragt, bzw. hängt eine BitLocker-VHDX ein und öffnet die Windows-Abfrage „Laufwerk entsperren“
/// (0.5.4). Aushängen nur ohne Gewalt (kein <c>/force</c>) — sind noch Dateien offen, meldet es das.
/// </summary>
public interface IContainerTools
{
    bool IsVeraCryptInstalled { get; }

    ContainerKind Detect(string driveRoot);

    /// <summary>Hängt das Laufwerk aus: VeraCrypt per Befehlszeile, sonst BitLocker-VHDX wie Explorer › „Auswerfen“; sonst Hinweis.</summary>
    Task<ContainerCommandResult> DismountAsync(string driveRoot, CancellationToken ct = default);

    /// <summary>Startet VeraCrypt zum Einhängen; VeraCrypt fragt das Kennwort selbst. Wartet, bis VeraCrypt sich beendet.</summary>
    Task<ContainerCommandResult> MountVeraCryptAsync(string containerFile, char driveLetter, CancellationToken ct = default);

    /// <summary>
    /// Hängt eine VHD/VHDX ein wie Explorer › „Bereitstellen“ (Windows wählt den Buchstaben) und öffnet bei einer gesperrten
    /// BitLocker-Partition die Windows-Abfrage „Laufwerk entsperren“ — das Kennwort sieht Lumora nie. Kein VeraCrypt nötig.
    /// </summary>
    Task<ContainerCommandResult> MountVirtualDiskAsync(string imageFile, CancellationToken ct = default);

    /// <summary>Endung <c>.vhdx</c> bzw. <c>.vhd</c>: Einhängen über Windows statt VeraCrypt.</summary>
    static bool IsVirtualDiskFile(string path)
        => Path.GetExtension(path) is { } ext
           && (ext.Equals(".vhdx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".vhd", StringComparison.OrdinalIgnoreCase));
}

public sealed class ContainerTools : IContainerTools
{
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Auswerfen einer VHDX; beim Beenden wartet <c>DismountAfterExit</c> höchstens 30 s auf alles zusammen.</summary>
    private static readonly TimeSpan EjectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MountTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Die Windows-Abfrage „Laufwerk entsperren“ wartet auf das Kennwort des Benutzers.</summary>
    private static readonly TimeSpan UnlockTimeout = TimeSpan.FromMinutes(10);

    private readonly IProcessRunner _runner;
    private readonly IVolumeDeviceQuery _devices;
    private readonly IVeraCryptLocator _veraCrypt;
    private readonly IVirtualDiskEjector _virtualDisks;
    private readonly IVirtualDiskMounter? _mounter;
    private readonly VirtualDiskMountTimings _timings;

    public ContainerTools(IProcessRunner runner, IVolumeDeviceQuery devices, IVeraCryptLocator veraCrypt, IVirtualDiskEjector virtualDisks,
        IVirtualDiskMounter? mounter = null, VirtualDiskMountTimings? timings = null)
    {
        _runner = runner;
        _devices = devices;
        _veraCrypt = veraCrypt;
        _virtualDisks = virtualDisks;
        _mounter = mounter;
        _timings = timings ?? VirtualDiskMountTimings.Default;
    }

    /// <summary>Die Windows-Abfrage „Laufwerk entsperren“ — dieselbe, die der Explorer bei einem gesperrten Laufwerk öffnet.</summary>
    public static string UnlockDialogPath => Path.Combine(Environment.SystemDirectory, "bdeunlock.exe");

    public bool IsVeraCryptInstalled => _veraCrypt.FindExecutable() is not null;

    public ContainerKind Detect(string driveRoot)
    {
        if (DriveLetterOf(driveRoot) is not { } letter)
        {
            return ContainerKind.Unknown;
        }

        var device = _devices.GetDeviceName(letter);
        return device is not null
               && (device.StartsWith(@"\Device\VeraCryptVolume", StringComparison.OrdinalIgnoreCase)
                   || device.StartsWith(@"\Device\TrueCryptVolume", StringComparison.OrdinalIgnoreCase))
            ? ContainerKind.VeraCrypt
            : ContainerKind.Unknown;
    }

    public async Task<ContainerCommandResult> DismountAsync(string driveRoot, CancellationToken ct = default)
    {
        if (DriveLetterOf(driveRoot) is not { } letter)
        {
            return new ContainerCommandResult(false, "Dieses Laufwerk kann Lumora nicht aushängen.");
        }

        if (string.Equals(Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'), $"{letter}:", StringComparison.OrdinalIgnoreCase))
        {
            return new ContainerCommandResult(false, "Die Bibliothek liegt auf dem Systemlaufwerk — es gibt keinen Container zum Aushängen.");
        }

        if (Detect(driveRoot) == ContainerKind.VeraCrypt)
        {
            return await DismountVeraCryptAsync(letter, ct).ConfigureAwait(false);
        }

        return await DismountVirtualDiskAsync(letter, ct).ConfigureAwait(false);
    }

    public async Task<ContainerCommandResult> MountVeraCryptAsync(string containerFile, char driveLetter, CancellationToken ct = default)
    {
        var exe = _veraCrypt.FindExecutable();
        if (exe is null)
        {
            return new ContainerCommandResult(false, "VeraCrypt ist nicht installiert. Den Container bitte selbst einhängen.");
        }

        if (!File.Exists(containerFile))
        {
            return new ContainerCommandResult(false, "Die Containerdatei wurde nicht gefunden.");
        }

        var letter = char.ToUpperInvariant(driveLetter);
        if (letter is < 'A' or > 'Z' || _devices.GetDeviceName(letter) is not null)
        {
            return new ContainerCommandResult(false, $"Laufwerk {letter}: ist nicht frei.");
        }

        // /volume Datei /letter X /quit: VeraCrypt fragt das Kennwort in seinem eigenen Fenster und beendet sich danach.
        // Bewusst ohne /explore (kein Explorer-Fenster, keine Spur in „Zuletzt verwendet“) und ohne /password.
        var result = await _runner.RunAsync(exe, ["/volume", containerFile, "/letter", letter.ToString(), "/quit"], MountTimeout, ct).ConfigureAwait(false);
        if (_devices.GetDeviceName(letter) is not null)
        {
            return new ContainerCommandResult(true, $"Container ist als {letter}: eingehängt.");
        }

        return new ContainerCommandResult(false, result.TimedOut
            ? "VeraCrypt hat nicht rechtzeitig geantwortet."
            : "Der Container wurde nicht eingehängt (abgebrochen oder falsches Kennwort).");
    }

    public async Task<ContainerCommandResult> MountVirtualDiskAsync(string imageFile, CancellationToken ct = default)
    {
        if (_mounter is null)
        {
            return new ContainerCommandResult(false, "Einhängen über Windows ist hier nicht verfügbar. Bitte die Datei im Explorer doppelklicken.");
        }

        if (!IContainerTools.IsVirtualDiskFile(imageFile) || !File.Exists(imageFile))
        {
            return new ContainerCommandResult(false, "Die Containerdatei (.vhdx) wurde nicht gefunden.");
        }

        // Windows wählt den Laufwerksbuchstaben selbst — das neue Laufwerk ist der Buchstabe, den es vorher nicht gab
        var before = UsedLetters();
        if (!await _mounter.MountAsync(imageFile, ct).ConfigureAwait(false))
        {
            return new ContainerCommandResult(false, "Windows konnte die Containerdatei nicht einhängen.");
        }

        var letter = await WaitAsync(() => UsedLetters().Except(before).Order().Cast<char?>().FirstOrDefault(), _timings.Appear, ct).ConfigureAwait(false);
        if (letter is not { } drive)
        {
            return new ContainerCommandResult(false,
                "Es ist kein neues Laufwerk erschienen — abgebrochen, oder die Datei ist schon eingehängt. Dann das Laufwerk im Explorer entsperren und „Suchen“ wählen.");
        }

        if (_mounter.IsVolumeReady(drive))
        {
            return new ContainerCommandResult(true, $"Container ist als {drive}: eingehängt.");
        }

        // Gesperrte BitLocker-Partition: Windows fragt das Kennwort in SEINEM Fenster (bdeunlock X:), Lumora sieht es nie
        await _runner.RunAsync(UnlockDialogPath, [$"{drive}:"], UnlockTimeout, ct).ConfigureAwait(false);
        var unlocked = await WaitAsync(() => _mounter.IsVolumeReady(drive) ? true : (bool?)null, _timings.UnlockSettle, ct).ConfigureAwait(false);
        return unlocked == true
            ? new ContainerCommandResult(true, $"Container ist als {drive}: eingehängt und entsperrt.")
            : new ContainerCommandResult(false, $"Container ist als {drive}: eingehängt, aber noch gesperrt. Bitte im Explorer entsperren und dann „Suchen“ wählen.");
    }

    private List<char> UsedLetters()
    {
        var used = new List<char>();
        for (var c = 'A'; c <= 'Z'; c++)
        {
            if (_devices.GetDeviceName(c) is not null)
            {
                used.Add(c);
            }
        }

        return used;
    }

    /// <summary>Fragt <paramref name="probe"/> ab, bis es einen Wert liefert oder <paramref name="timeout"/> um ist.</summary>
    private async Task<T?> WaitAsync<T>(Func<T?> probe, TimeSpan timeout, CancellationToken ct)
        where T : struct
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (probe() is { } value)
            {
                return value;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(_timings.Poll, ct).ConfigureAwait(false);
        }
    }

    private async Task<ContainerCommandResult> DismountVeraCryptAsync(char letter, CancellationToken ct)
    {
        var exe = _veraCrypt.FindExecutable();
        if (exe is null)
        {
            return new ContainerCommandResult(false, $"VeraCrypt wurde nicht gefunden — Laufwerk {letter}: bitte in VeraCrypt aushängen.");
        }

        // VeraCrypt bis 1.26.x: /dismount X (Kurzform /d); neuere Fassungen nennen es „unmount“ — beides versuchen.
        foreach (var verb in new[] { "/dismount", "/unmount" })
        {
            await _runner.RunAsync(exe, [verb, letter.ToString(), "/quit", "/silent"], DismountTimeout, ct).ConfigureAwait(false);
            if (_devices.GetDeviceName(letter) is null)
            {
                return new ContainerCommandResult(true, $"Container {letter}: ausgehängt.");
            }
        }

        return new ContainerCommandResult(false, $"VeraCrypt konnte {letter}: nicht aushängen (noch Dateien geöffnet?). Bitte in VeraCrypt aushängen.");
    }

    private async Task<ContainerCommandResult> DismountVirtualDiskAsync(char letter, CancellationToken ct)
    {
        // BitLocker-VHDX: eingehängte Image-Datei (BusType „File Backed Virtual“) → Auswerfen wie im Explorer. Bis 0.4.30
        // per PowerShell Dismount-DiskImage — das braucht Administratorrechte und scheiterte im normalen Lumora-Prozess.
        if (!_virtualDisks.IsVirtualDisk(letter))
        {
            return new ContainerCommandResult(false, $"Laufwerk {letter}: ist kein bekannter Container (VeraCrypt/VHDX) — bitte selbst aushängen.");
        }

        var ejected = await _virtualDisks.EjectAsync(letter, () => _devices.GetDeviceName(letter) is not null, EjectTimeout, ct).ConfigureAwait(false);
        if (ejected || _devices.GetDeviceName(letter) is null)
        {
            return new ContainerCommandResult(true, $"Container {letter}: ausgehängt.");
        }

        return new ContainerCommandResult(false, $"Laufwerk {letter}: konnte nicht ausgehängt werden (noch Dateien geöffnet?) — bitte im Explorer „Auswerfen“ wählen.");
    }

    internal static char? DriveLetterOf(string driveRoot)
    {
        if (string.IsNullOrEmpty(driveRoot) || driveRoot.Length < 2 || driveRoot[1] != ':' || !char.IsAsciiLetter(driveRoot[0]))
        {
            return null;
        }

        return char.ToUpperInvariant(driveRoot[0]);
    }
}

/// <summary>Startet Prozesse ohne Fenster und wartet mit Zeitgrenze (bei Überschreitung wird der Prozess beendet).</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return new ProcessRunResult(-1, TimedOut: false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return new ProcessRunResult(process.ExitCode, TimedOut: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // schon beendet
            }

            return new ProcessRunResult(-1, TimedOut: true);
        }
    }
}

/// <summary><c>QueryDosDevice</c> — liefert für VeraCrypt-Laufwerke <c>\Device\VeraCryptVolumeX</c>.</summary>
public sealed class VolumeDeviceQuery : IVolumeDeviceQuery
{
    public string? GetDeviceName(char driveLetter)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new char[1024];
        var length = QueryDosDevice($"{char.ToUpperInvariant(driveLetter)}:", buffer, (uint)buffer.Length);
        if (length == 0)
        {
            return null;
        }

        var text = new string(buffer, 0, (int)length);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte AllowUnsafeBlocks; DllImport reicht hier.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryDosDeviceW")]
    private static extern uint QueryDosDevice(string lpDeviceName, [Out] char[] lpTargetPath, uint ucchMax);
#pragma warning restore SYSLIB1054
}

/// <summary>
/// Sucht <c>VeraCrypt.exe</c>: Dateityp-Verknüpfung „VeraCryptVolume“ bzw. Deinstallations-Eintrag in der Registry, sonst
/// <c>%ProgramFiles%\VeraCrypt\VeraCrypt.exe</c>.
/// </summary>
public sealed class VeraCryptLocator : IVeraCryptLocator
{
    public string? FindExecutable()
    {
        foreach (var candidate in Candidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string?> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return FromCommand(ReadRegistry(Microsoft.Win32.Registry.ClassesRoot, @"VeraCryptVolume\Shell\open\command", null));
            var icon = ReadRegistry(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VeraCrypt", "DisplayIcon");
            yield return FromCommand(icon)?.Split(',')[0];
        }

        foreach (var programFiles in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var folder = Environment.GetFolderPath(programFiles);
            if (!string.IsNullOrEmpty(folder))
            {
                yield return Path.Combine(folder, "VeraCrypt", "VeraCrypt.exe");
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadRegistry(Microsoft.Win32.RegistryKey hive, string path, string? name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Erster (ggf. gequoteter) Teil einer Befehlszeile.</summary>
    public static string? FromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : text;
    }
}
