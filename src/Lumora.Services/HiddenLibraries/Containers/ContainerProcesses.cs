using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>Ergebnis eines erhöht gestarteten Programms (ADR-0061).</summary>
/// <param name="Declined">Die Windows-Rückfrage (UAC) wurde abgelehnt — das Programm lief gar nicht.</param>
/// <param name="ExitCode">Exit-Code; -1, wenn das Programm nicht startete oder nicht rechtzeitig endete.</param>
/// <param name="StartFailed">Windows hat das Programm nicht gestartet (nicht wegen abgelehnter Rückfrage, z. B. gesperrt).</param>
public sealed record ElevatedRunResult(bool Declined, int ExitCode, bool TimedOut, bool StartFailed = false);

/// <summary>
/// Startet ein Programm mit Administratorrechten (<c>Verb=runas</c>, genau eine Windows-Rückfrage) und wartet auf sein
/// Ende. Tests ersetzen ihn — nie echt erhöht ausführen in Tests. Die Befehlszeile enthält nie Kennwörter.
/// </summary>
public interface IElevatedRunner
{
    /// <param name="argumentLine">Ganze Befehlszeile (ShellExecute nimmt nur eine Zeichenkette); Pfade selbst quoten.</param>
    Task<ElevatedRunResult> RunElevatedAsync(string fileName, string argumentLine, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Ergebnis eines Programmlaufs mit mitgelesener Ausgabe.</summary>
/// <param name="Output">Standard- und Fehlerausgabe (gekürzt); nur für die Anzeige, nie ins Protokoll.</param>
/// <param name="StartFailed">Das Programm wurde nicht gefunden bzw. konnte nicht starten.</param>
public sealed record ProcessOutputResult(int ExitCode, bool TimedOut, string Output, bool StartFailed = false);

/// <summary>Startet ein Programm ohne Fenster und liest seine Ausgabe mit (winget, Statusabfragen); Tests ersetzen ihn.</summary>
public interface IProcessOutputRunner
{
    /// <param name="lines">Erhält jede nicht leere Ausgabezeile (für einen Fortschritt in der Oberfläche).</param>
    Task<ProcessOutputResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IProgress<string>? lines,
        CancellationToken ct);
}

/// <summary>Wie ein Programm per Shell gestartet wurde.</summary>
public enum ShellLaunchResult
{
    Started = 0,

    /// <summary>Die Windows-Rückfrage (UAC) wurde abgelehnt.</summary>
    Declined = 1,

    Failed = 2,
}

/// <summary>Startet Programme bzw. öffnet Webseiten über die Windows-Shell, ohne zu warten; Tests ersetzen ihn.</summary>
public interface IShellLauncher
{
    ShellLaunchResult Start(string fileName, string? argumentLine);

    /// <summary>Öffnet eine https-Adresse im Standardbrowser (andere Adressen werden abgelehnt).</summary>
    bool OpenUrl(string url);
}

/// <summary>ShellExecute mit <c>runas</c>; ein abgelehnte Rückfrage meldet Windows als Fehler 1223 (ERROR_CANCELLED).</summary>
public sealed class ElevatedProcessRunner : IElevatedRunner
{
    internal const int ErrorCancelled = 1223;

    public async Task<ElevatedRunResult> RunElevatedAsync(string fileName, string argumentLine, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(fileName, argumentLine)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ElevatedRunResult(Declined: true, -1, TimedOut: false);
        }
        catch (Win32Exception)
        {
            return new ElevatedRunResult(Declined: false, -1, TimedOut: false, StartFailed: true);
        }

        if (process is null)
        {
            return new ElevatedRunResult(Declined: false, -1, TimedOut: false, StartFailed: true);
        }

        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return new ElevatedRunResult(Declined: false, process.ExitCode, TimedOut: false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Ein erhöhter Prozess lässt sich von hier aus nicht beenden — nur melden.
                return new ElevatedRunResult(Declined: false, -1, TimedOut: true);
            }
        }
    }
}

/// <summary>Programmlauf ohne Fenster mit umgeleiteter Ausgabe (UTF-8), Zeitgrenze und gekürztem Mitschnitt.</summary>
public sealed class ProcessOutputRunner : IProcessOutputRunner
{
    private const int MaxOutputChars = 64 * 1024;

    public async Task<ProcessOutputResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IProgress<string>? lines,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        void OnData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            // Fortschrittsbalken überschreiben die Zeile mit \r — nur der letzte Stand zählt
            var line = e.Data.Split('\r').LastOrDefault(s => s.Trim().Length > 0)?.Trim();
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            lock (output)
            {
                if (output.Length < MaxOutputChars)
                {
                    output.AppendLine(line);
                }
            }

            lines?.Report(line);
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        try
        {
            if (!process.Start())
            {
                return new ProcessOutputResult(-1, TimedOut: false, string.Empty, StartFailed: true);
            }
        }
        catch (Win32Exception)
        {
            return new ProcessOutputResult(-1, TimedOut: false, string.Empty, StartFailed: true);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessOutputResult(-1, TimedOut: true, Snapshot(output));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        // Rest der Ausgabe abholen (WaitForExit ohne Zeitgrenze wartet auf das Ende der Lese-Streams)
        process.WaitForExit();
        return new ProcessOutputResult(process.ExitCode, TimedOut: false, Snapshot(output));
    }

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // schon beendet bzw. erhöht gestarteter Kindprozess
        }
    }
}

/// <summary>ShellExecute ohne Warten; erhöhte Programme (Manifest) lösen dabei selbst die Windows-Rückfrage aus.</summary>
public sealed class ShellLauncher : IShellLauncher
{
    public ShellLaunchResult Start(string fileName, string? argumentLine)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, argumentLine ?? string.Empty) { UseShellExecute = true });
            return ShellLaunchResult.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ElevatedProcessRunner.ErrorCancelled)
        {
            return ShellLaunchResult.Declined;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return ShellLaunchResult.Failed;
        }
    }

    public bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return Start(uri.AbsoluteUri, null) == ShellLaunchResult.Started;
    }
}
