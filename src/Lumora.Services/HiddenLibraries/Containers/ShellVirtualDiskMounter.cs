using System.Reflection;
using System.Runtime.Versioning;

namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>
/// Die echte Windows-Seite von <see cref="IVirtualDiskMounter"/> (Nachtrag 0.5.4 zu ADR-0049/ADR-0061): Einhängen über das
/// Shell-Verb „mount“ der Image-Datei — derselbe Weg wie Explorer › „Bereitstellen“ bzw. Doppelklick; braucht Windows dafür
/// Administratorrechte, fragt Windows selbst. Bereit heißt: das Laufwerk ist lesbar (eine gesperrte BitLocker-Partition ist es
/// nicht). Kein Skript, kein Fremdprogramm. Nie in Tests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellVirtualDiskMounter : IVirtualDiskMounter
{
    public Task<bool> MountAsync(string imagePath, CancellationToken ct)
    {
        // Shell-Objekte gehören in einen STA-Thread; das Verb kehrt zurück, sobald Windows den Auftrag angenommen hat
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => done.TrySetResult(MountOnStaThread(imagePath)))
        {
            IsBackground = true,
            Name = "Lumora Einhängen",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(ct);
    }

    public bool IsVolumeReady(char driveLetter)
    {
        try
        {
            return new DriveInfo($"{char.ToUpperInvariant(driveLetter)}:\\").IsReady;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool MountOnStaThread(string imagePath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null || Activator.CreateInstance(shellType) is not { } shell
                || Path.GetDirectoryName(imagePath) is not { } folderPath)
            {
                return false;
            }

            var folder = Invoke(shell, "NameSpace", folderPath);
            var item = folder is null ? null : Invoke(folder, "ParseName", Path.GetFileName(imagePath));
            if (item is null)
            {
                return false;
            }

            Invoke(item, "InvokeVerb", "mount");
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or System.Runtime.InteropServices.COMException or MissingMethodException)
        {
            return false;
        }
    }

    private static object? Invoke(object target, string method, object argument)
        => target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, binder: null, target, [argument], System.Globalization.CultureInfo.InvariantCulture);
}
