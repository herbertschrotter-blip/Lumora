using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenCvSharp;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Worker.Decoding;

namespace Lumora.Imaging.Worker;

/// <summary>
/// Einstieg des Hilfsprozesses „Zusammenfügen“ (ADR-0078, Bau Stufe 1 Teil A). Liest Anfragen zeilenweise von stdin, rechnet
/// sie nacheinander auf einem eigenen Thread (ein schwerer Lauf zur Zeit), meldet Fortschritt und Ergebnis auf stdout. „Abbrechen“
/// wirkt sofort auf die laufende Arbeit (zwischen Stufen, Bildern und Bändern). Endet bei „quit“ oder wenn Lumora die Leitung
/// schließt. Schreibt keine Protokolldateien und keine Pfade nach außen.
/// </summary>
internal static class Program
{
    private static readonly Lock OutputLock = new();

    [STAThread]
    public static int Main(string[] args)
    {
        CrashGuard.Install(); // vor allem anderen: Abstürze enden sofort, ohne Fehlerbericht-Dialog (0.5.48)
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        var threads = Environment.ProcessorCount;
        var at = Array.IndexOf(args, "--threads");
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0)
        {
            threads = t;
        }

        string version;
        try
        {
            version = OpenCvRuntime.Initialize(threads);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            Send(stdout, new ErrorMessage { Id = 0, Code = WorkerErrorCode.OpenCv, Detail = "OpenCV nicht ladbar: " + ex.GetType().Name });
            return 2;
        }

        CrashGuard.ApplyPriority(WorkerThreads.Count);
        Send(stdout, new ReadyMessage { ProtocolVersion = WorkerProtocol.Version, OpenCvVersion = version, Threads = WorkerThreads.Count });
        var testHooks = Environment.GetEnvironmentVariable(WorkerLimits.TestHooksVariable) == "1";
        var queue = new BlockingCollection<WorkerMessage>();
        var current = new CurrentJob();
        var worker = new Thread(() => RunJobs(queue, current, stdout)) { IsBackground = true, Name = "Rechnen" };
        worker.Start();

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            var message = WorkerProtocol.Decode(line);
            switch (message)
            {
                case null:
                    continue;
                case CancelRequest cancel:
                    current.Cancel(cancel.TargetId);
                    continue;
                case ThreadsRequest threadsRequest:
                    // „Im Hintergrund weiterrechnen“ mitten in einer Arbeit: eigene parallele Abschnitte und Priorität sofort,
                    // OpenCV erst vor der nächsten Arbeit (cv::setNumThreads ist nicht sicher, während gerechnet wird — 0.5.48)
                    WorkerThreads.Set(threadsRequest.Threads);
                    CrashGuard.ApplyPriority(WorkerThreads.Count);
                    continue;
                case QuitRequest:
                    current.Cancel(null);
                    queue.CompleteAdding();
                    worker.Join(TimeSpan.FromSeconds(10));
                    return 0;
                case DebugRequest debug when testHooks:
                    switch (debug.Action)
                    {
                        case "crash":
                            Environment.FailFast("Testabsturz des Hilfsprozesses");
                            break;
                        case "exit":
                            Environment.Exit(3);
                            break;
                        case "unhandled":
                            // verwaltete Ausnahme ohne Fänger auf einem eigenen Thread
                            new Thread(() => throw new InvalidOperationException("Testausnahme ohne Fänger")) { IsBackground = true }.Start();
                            break;
                        default:
                            queue.Add(debug);
                            break;
                    }

                    continue;
                default:
                    queue.Add(message);
                    continue;
            }
        }

        // Lumora hat die Leitung geschlossen (beendet oder abgestürzt): mitgehen
        current.Cancel(null);
        queue.CompleteAdding();
        worker.Join(TimeSpan.FromSeconds(5));
        return 0;
    }

    private static void RunJobs(BlockingCollection<WorkerMessage> queue, CurrentJob current, StreamWriter stdout)
    {
        using var jobs = new PanoramaJobs();
        foreach (var message in queue.GetConsumingEnumerable())
        {
            WorkerThreads.ApplyNative(); // zwischen zwei Arbeiten: vorgemerkte Thread-Zahl an OpenCV
            using var cts = current.Begin(message.Id);
            var ct = cts.Token;
            var throttle = Stopwatch.StartNew();
            var lastStep = (WorkerStep)(-1);
            void Progress(WorkerStep step, int index, int count)
            {
                // höchstens etwa 10 Meldungen je Sekunde, Schrittwechsel immer
                if (step == lastStep && throttle.ElapsedMilliseconds < 100 && index < count)
                {
                    return;
                }

                lastStep = step;
                throttle.Restart();
                Send(stdout, new ProgressMessage { Id = message.Id, Step = step, Index = index, Count = count, Fraction = count > 0 ? Math.Clamp(index / (double)count, 0, 1) : 0 });
            }

            try
            {
                try
                {
                    ct.ThrowIfCancellationRequested(); // schon abgebrochen, während die Anfrage wartete
                    WorkerMessage reply = message switch
                    {
                        EstimateRequest e => jobs.Estimate(e, Progress, ct),
                        ComposeRequest c => jobs.Compose(c, Progress, ct),
                        RenderRequest r => jobs.Render(r, Progress, ct),
                        DebugRequest { Action: "hang" } => Hang(),
                        DebugRequest { Action: "native-threads" } n => new ReadyMessage { Id = n.Id, ProtocolVersion = WorkerProtocol.Version, OpenCvVersion = Cv2.GetVersionString() ?? string.Empty, Threads = WorkerThreads.Native },
                        DebugRequest d => new ReadyMessage { Id = d.Id, ProtocolVersion = WorkerProtocol.Version, OpenCvVersion = Cv2.GetVersionString() ?? string.Empty, Threads = WorkerThreads.Count },
                        _ => new ErrorMessage { Id = message.Id, Code = WorkerErrorCode.BadRequest, Detail = "Unbekannte Anfrage." },
                    };
                    Send(stdout, reply);
                }
                catch (AggregateException agg)
                {
                    // Parallel.For (Lesen, Verzerren, Überblenden) verpackt Fehler: den maßgeblichen auspacken
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(Unwrap(agg));
                }
            }
            catch (OperationCanceledException)
            {
                Send(stdout, new ErrorMessage { Id = message.Id, Code = WorkerErrorCode.Canceled, Detail = "Abgebrochen." });
            }
            catch (WorkerException ex)
            {
                Send(stdout, new ErrorMessage { Id = message.Id, Code = ex.Code, Detail = ex.Message });
            }
            catch (SourceDecodeException ex)
            {
                Send(stdout, new ErrorMessage { Id = message.Id, Code = WorkerErrorCode.SourceUnreadable, Detail = $"Quelle {ex.SourceIndex}: {ex.Message}" });
            }
            catch (OutOfMemoryException)
            {
                Send(stdout, new ErrorMessage { Id = message.Id, Code = WorkerErrorCode.OutOfMemory, Detail = "Zu wenig Arbeitsspeicher." });
                Environment.Exit(4); // Zustand unsicher: Lumora startet bei Bedarf einen neuen Prozess
            }
            catch (OpenCVException ex)
            {
                Send(stdout, new ErrorMessage { Id = message.Id, Code = WorkerErrorCode.OpenCv, Detail = ex.Status + " " + ex.FuncName });
            }
            catch (Exception ex)
            {
                Send(stdout, new ErrorMessage { Id = message.Id, Code = WorkerErrorCode.Unknown, Detail = ex.GetType().Name + ": " + ex.Message });
            }
            finally
            {
                current.End();
            }
        }
    }

    /// <summary>Aus einem Sammelfehler der wichtigste: Speicher vor Abbruch vor unlesbarer Quelle vor dem ersten.</summary>
    internal static Exception Unwrap(AggregateException agg)
    {
        var inner = agg.Flatten().InnerExceptions;
        return inner.FirstOrDefault(e => e is OutOfMemoryException)
            ?? inner.FirstOrDefault(e => e is OperationCanceledException)
            ?? inner.FirstOrDefault(e => e is SourceDecodeException)
            ?? inner.FirstOrDefault(e => e is WorkerException)
            ?? (inner.Count > 0 ? inner[0] : agg);
    }

    private static WorkerMessage Hang()
    {
        Thread.Sleep(Timeout.Infinite); // Test: antwortet nie, auch nicht auf Abbrechen
        return new ErrorMessage();
    }

    private static void Send(StreamWriter stdout, WorkerMessage message)
    {
        var line = WorkerProtocol.Encode(message);
        lock (OutputLock)
        {
            stdout.WriteLine(line);
        }
    }

    /// <summary>
    /// Die laufende Arbeit und ihr Abbruch. Ein Abbruch für eine Anfrage, die noch in der Warteschlange steht, wird gemerkt und
    /// wirkt beim Start sofort (sonst liefe sie ganz durch und Lumora müsste den Prozess nach der Gnadenfrist beenden).
    /// </summary>
    internal sealed class CurrentJob
    {
        private readonly Lock _lock = new();
        private readonly HashSet<int> _canceledEarly = [];
        private CancellationTokenSource? _cts;
        private int _id;

        public CancellationTokenSource Begin(int id)
        {
            lock (_lock)
            {
                _cts = new CancellationTokenSource();
                _id = id;
                if (_canceledEarly.Remove(id))
                {
                    _cts.Cancel();
                }

                return _cts;
            }
        }

        public void End()
        {
            lock (_lock)
            {
                var done = _id;
                _canceledEarly.RemoveWhere(x => x <= done); // Lumora vergibt aufsteigende Nummern: ältere sind erledigt
                _cts = null;
                _id = 0;
            }
        }

        /// <summary>Bricht die laufende Arbeit ab (<paramref name="id"/> null = jede).</summary>
        public void Cancel(int? id)
        {
            lock (_lock)
            {
                if (_cts is not null && (id is null || id == _id))
                {
                    _cts.Cancel();
                }
                else if (id is { } early && early != _id && _canceledEarly.Count < 64)
                {
                    _canceledEarly.Add(early); // wartet noch (oder ist schon fertig — dann bleibt der Eintrag folgenlos)
                }
            }
        }
    }
}
