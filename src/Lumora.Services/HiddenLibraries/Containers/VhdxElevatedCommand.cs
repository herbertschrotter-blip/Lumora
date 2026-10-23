using System.Globalization;
using System.Text;

namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>Was der erhöhte Lumora-Lauf anlegen soll — so, wie er es aus der Befehlszeile gelesen hat.</summary>
/// <param name="FilePath">Neue VHDX-Datei (voller, lokaler Pfad).</param>
/// <param name="SizeMegabytes">Höchstgröße in MB (dynamisch erweiterbar).</param>
/// <param name="DriveLetter">Laufwerksbuchstabe D–Z (groß).</param>
/// <param name="VolumeLabel">Bezeichnung des Laufwerks.</param>
public sealed record VhdxElevatedRequest(string FilePath, long SizeMegabytes, char DriveLetter, string VolumeLabel);

/// <summary>
/// Exit-Codes des erhöhten Laufs <c>Lumora.exe --container-create-vhdx …</c> (ADR-0061, Nachtrag 0.4.26). Die Werte liegen
/// absichtlich im Bereich 7100–7199: endet der erhöhte Lauf mit einem anderen Code (z. B. weil ein Sicherheitsprogramm ihn
/// beendet hat), weiß Lumora, dass er nicht von hier stammt.
/// </summary>
public enum VhdxJobExit
{
    Success = 0,

    /// <summary>Befehlszeile unvollständig, doppelt oder mit unbekannten Teilen.</summary>
    InvalidArguments = 7101,

    /// <summary>Speicherort abgelehnt (kein lokaler voller Pfad, kein .vhdx, Cloud-Ordner, Pfad-Tricks, Verknüpfungen im Pfad).</summary>
    InvalidPath = 7102,

    /// <summary>Die Datei gibt es schon.</summary>
    FileExists = 7103,

    /// <summary>Ordner fehlt oder Laufwerk ist kein lokales NTFS/ReFS-Laufwerk.</summary>
    DriveNotSuitable = 7104,

    LetterInUse = 7105,

    InvalidLabel = 7106,

    InvalidSize = 7107,

    /// <summary>Der Lauf hatte keine Administratorrechte.</summary>
    NotElevated = 7108,

    CreateFailed = 7111,

    AttachFailed = 7112,

    DiskNumberFailed = 7113,

    InitializeFailed = 7114,

    PartitionFailed = 7115,

    FormatFailed = 7116,

    LetterFailed = 7117,

    Unexpected = 7120,
}

/// <summary>
/// Der erhöhte Schritt beim Anlegen einer BitLocker-VHDX (ADR-0061, Nachtrag 0.4.26): Lumora startet sich selbst mit
/// Administratorrechten und einem lesbaren Auftrag — <c>Lumora.exe --container-create-vhdx --path "D:\Daten\sicherung.vhdx"
/// --size-mb 51200 --letter X --label "Daten"</c>. Kein Skript, keine Datei als Übergabe, nichts verschlüsselt kodiert:
/// Windows zeigt den Auftrag in der Rückfrage, und zwischen Rückfrage und Lauf kann nichts ausgetauscht werden.
/// </summary>
public static class VhdxElevatedCommand
{
    public const string Verb = "--container-create-vhdx";

    public const string PathOption = "--path";

    public const string SizeOption = "--size-mb";

    public const string LetterOption = "--letter";

    public const string LabelOption = "--label";

    private static readonly string[] Options = [PathOption, SizeOption, LetterOption, LabelOption];

    /// <summary>true, wenn die Befehlszeile (ohne Programmnamen) den erhöhten Schritt verlangt.</summary>
    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Count > 0 && string.Equals(args[0], Verb, StringComparison.Ordinal);

    /// <summary>Befehlszeile für ShellExecute (eine Zeichenkette). Vorher <see cref="VhdxRules.ValidatePath"/>/<see cref="VhdxRules.ValidateLabel"/>.</summary>
    public static string BuildArguments(VhdxPlan plan)
    {
        var path = Path.GetFullPath(plan.FilePath.Trim());
        var label = plan.VolumeLabel.Trim();
        var letter = char.ToUpperInvariant(plan.DriveLetter);
        if (path.Contains('"', StringComparison.Ordinal) || label.Contains('"', StringComparison.Ordinal) || path.EndsWith('\\'))
        {
            throw new ArgumentException("Pfad oder Bezeichnung lassen sich so nicht übergeben.", nameof(plan));
        }

        if (!char.IsAsciiLetterUpper(letter))
        {
            throw new ArgumentException("Ungültiger Laufwerksbuchstabe.", nameof(plan));
        }

        var line = new StringBuilder(Verb);
        line.Append(CultureInfo.InvariantCulture, $" {PathOption} \"{path}\"");
        line.Append(CultureInfo.InvariantCulture, $" {SizeOption} {(long)plan.SizeGigabytes * 1024}");
        line.Append(CultureInfo.InvariantCulture, $" {LetterOption} {letter}");
        line.Append(CultureInfo.InvariantCulture, $" {LabelOption} \"{label}\"");
        return line.ToString();
    }

    /// <summary>
    /// Liest die Befehlszeile streng: genau das Verb und jede Option genau einmal, sonst nichts. Prüft nur die Form
    /// (Zahl, Buchstabe); Pfad, Laufwerk und Bezeichnung prüft <see cref="VhdxElevatedJob"/>.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out VhdxElevatedRequest? request)
    {
        request = null;
        if (!IsRequested(args) || args.Count != 1 + (Options.Length * 2))
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i += 2)
        {
            var option = args[i];
            if (!Options.Contains(option, StringComparer.Ordinal) || !values.TryAdd(option, args[i + 1]))
            {
                return false;
            }
        }

        var sizeText = values[SizeOption];
        if (sizeText.Length is 0 or > 12 || !sizeText.All(char.IsAsciiDigit)
            || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            return false;
        }

        var letterText = values[LetterOption];
        if (letterText.Length != 1 || !char.IsAsciiLetter(letterText[0]))
        {
            return false;
        }

        request = new VhdxElevatedRequest(values[PathOption], size, char.ToUpperInvariant(letterText[0]), values[LabelOption]);
        return true;
    }

    /// <summary>true, wenn der Exit-Code von einem Lumora-Lauf stammt (Erfolg oder einer der eigenen Fehler).</summary>
    public static bool IsOwnExitCode(int exitCode) => Enum.IsDefined(typeof(VhdxJobExit), exitCode);

    /// <summary>true bei den Prüf-Codes 7101–7108: der erhöhte Lauf hat abgelehnt, bevor er etwas angelegt hat.</summary>
    public static bool IsRefusedBeforeCreate(int exitCode)
        => exitCode >= (int)VhdxJobExit.InvalidArguments && exitCode <= (int)VhdxJobExit.NotElevated;

    /// <summary>Was ein eigener Fehler-Code für den Benutzer bedeutet (ohne Pfad, ohne Bezeichnung).</summary>
    public static string Describe(VhdxJobExit exit, char letter) => exit switch
    {
        VhdxJobExit.Success => $"Der Container ist angelegt und als {letter}: eingehängt.",
        VhdxJobExit.InvalidArguments => "Der Administrator-Schritt hat den Auftrag nicht verstanden.",
        VhdxJobExit.InvalidPath => "Der Administrator-Schritt hat den Speicherort abgelehnt (nur ein lokaler Ordner außerhalb von Cloud-Ordnern, ohne Verknüpfungen im Pfad, Endung .vhdx).",
        VhdxJobExit.FileExists => "Diese Containerdatei gibt es schon. Bitte einen anderen Namen wählen.",
        VhdxJobExit.DriveNotSuitable => "Der Ordner bzw. das Laufwerk für die Containerdatei passt nicht (lokales NTFS-Laufwerk nötig).",
        VhdxJobExit.LetterInUse => $"Laufwerk {letter}: ist inzwischen belegt. Bitte einen anderen Buchstaben wählen.",
        VhdxJobExit.InvalidLabel => "Die Bezeichnung wurde abgelehnt (nur Buchstaben A–Z, Ziffern, Leerzeichen und - _ .).",
        VhdxJobExit.InvalidSize => $"Die Größe muss zwischen {VhdxRules.MinSizeGigabytes} und {VhdxRules.MaxSizeGigabytes} GB liegen.",
        VhdxJobExit.NotElevated => "Der Schritt lief ohne Administratorrechte.",
        VhdxJobExit.CreateFailed => "Windows konnte die Containerdatei nicht anlegen.",
        VhdxJobExit.AttachFailed => "Windows konnte die neue Containerdatei nicht einhängen.",
        VhdxJobExit.DiskNumberFailed => "Windows hat den neuen Datenträger nicht gemeldet.",
        VhdxJobExit.InitializeFailed => "Windows konnte den neuen Datenträger nicht initialisieren.",
        VhdxJobExit.PartitionFailed => "Windows konnte auf dem neuen Datenträger keine Partition anlegen.",
        VhdxJobExit.FormatFailed => "Windows konnte das neue Laufwerk nicht formatieren (NTFS).",
        VhdxJobExit.LetterFailed => $"Windows konnte den Laufwerksbuchstaben {letter}: nicht zuweisen.",
        _ => "Im Administrator-Schritt ist ein unerwarteter Fehler aufgetreten.",
    };
}
