using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lumora.Services.AiEdit;

/// <summary>Der laufende ComfyUI-Prozess (Tests: Attrappe).</summary>
public interface IComfyProcess : IDisposable
{
    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>Wartet auf das Ende des Prozesses (nicht der Kinder).</summary>
    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>Beendet den Prozess samt allen Kindprozessen.</summary>
    void KillTree();
}

/// <summary>Startet ComfyUI ohne Fenster; jede Ausgabezeile (stdout und stderr) geht an <c>onOutput</c>.</summary>
public interface IComfyProcessLauncher
{
    IComfyProcess Start(ProcessStartInfo startInfo, Action<string> onOutput);
}

/// <summary>
/// Windows: startet den Prozess und hängt ihn an ein Job-Objekt mit „beim Schließen beenden“ (ADR-0076 Punkt 3) — endet
/// Lumora (auch durch Absturz), schließt Windows das letzte Handle des Jobs und beendet ComfyUI samt Kindern.
/// </summary>
public sealed class ComfyProcessLauncher : IComfyProcessLauncher
{
    public IComfyProcess Start(ProcessStartInfo startInfo, Action<string> onOutput)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = false;
        startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
        startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;

        // Ohne Job-Objekt kein Start: sonst bliebe ComfyUI nach einem Absturz von Lumora lauschend zurück
        var job = KillOnCloseJob.Create()
                  ?? throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Job-Objekt für ComfyUI nicht verfügbar.");
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Forward(e.Data, onOutput);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, onOutput);
        var started = false;
        try
        {
            started = process.Start();
            job.Assign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            if (started)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // schon beendet
                }
            }

            job.Dispose();
            process.Dispose();
            throw;
        }

        return new WindowsComfyProcess(process, job);
    }

    private static void Forward(string? line, Action<string> onOutput)
    {
        if (line is not null)
        {
            onOutput(line);
        }
    }

    private sealed class WindowsComfyProcess(Process process, KillOnCloseJob job) : IComfyProcess
    {
        public bool HasExited => SafeHasExited();

        public int? ExitCode => SafeHasExited() ? process.ExitCode : null;

        public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

        public void KillTree()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Schon beendet bzw. Zugriff verweigert — der Job räumt beim Dispose den Rest weg
            }

            job.Terminate();
        }

        public void Dispose()
        {
            job.Dispose(); // beendet alles, was noch im Job läuft
            process.Dispose();
        }

        private bool SafeHasExited()
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}

/// <summary>Job-Objekt mit <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>; null, wenn Windows es verweigert (dann startet ComfyUI nicht).</summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeFileHandle _handle;

    private KillOnCloseJob(SafeFileHandle handle) => _handle = handle;

    public static KillOnCloseJob? Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, buffer, (uint)size))
            {
                handle.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new KillOnCloseJob(handle);
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "ComfyUI ließ sich nicht an das Job-Objekt hängen.");
        }
    }

    public void Terminate()
    {
        if (!_handle.IsClosed)
        {
            _ = TerminateJobObject(_handle, 1);
        }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte AllowUnsafeBlocks; DllImport reicht hier (wie VirtualDiskNative).
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
#pragma warning restore SYSLIB1054
}
