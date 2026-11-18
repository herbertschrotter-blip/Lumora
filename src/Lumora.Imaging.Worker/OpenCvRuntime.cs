using OpenCvSharp;
using Lumora.Imaging.Protocol;

namespace Lumora.Imaging.Worker;

/// <summary>Fehler einer Arbeit mit Code für Lumora (der Hilfsprozess läuft weiter).</summary>
internal sealed class WorkerException(WorkerErrorCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public WorkerErrorCode Code { get; } = code;
}

/// <summary>
/// Zahl der Rechen-Threads des Hilfsprozesses (zentral, weil <c>Cv2.SetNumThreads</c> global wirkt). <see cref="Set"/> wirkt
/// sofort auf die eigenen parallelen Abschnitte (jeder liest <see cref="Count"/> beim Start); OpenCV selbst bekommt die Zahl nur
/// zwischen zwei Arbeiten auf dem Rechen-Thread (<see cref="ApplyNative"/>): <c>cv::setNumThreads</c> baut den Thread-Pool ohne
/// Sperre neu und darf nicht laufen, während eine Berechnung ihn benutzt (0.5.48). Bis dahin drosselt die Prozess-Priorität.
/// </summary>
internal static class WorkerThreads
{
    private static int _count = Math.Max(1, Environment.ProcessorCount);
    private static int _native = -1;

    public static int Count => Volatile.Read(ref _count);

    /// <summary>Zuletzt an OpenCV gegebene Zahl (Tests).</summary>
    public static int Native => Volatile.Read(ref _native);

    public static void Set(int count) => Volatile.Write(ref _count, Math.Max(1, count));

    /// <summary>Vorgemerkte Zahl an OpenCV geben — nur auf dem Rechen-Thread und nur, wenn keine Arbeit läuft.</summary>
    public static void ApplyNative()
    {
        var want = Count;
        if (want != Native)
        {
            Cv2.SetNumThreads(want);
            Volatile.Write(ref _native, want);
        }
    }
}

/// <summary>
/// OpenCV-Regeln aus ADR-0078 Punkt 2, jetzt im Hilfsprozess: OpenCL aus (sonst legt OpenCV einen Kernel-Cache außerhalb des
/// Containers an — in der versteckten Sitzung verboten), Threads zentral, Version für die Meldung an Lumora. Die Umgebung
/// (<c>OPENCV_OPENCL_RUNTIME</c>, <c>OPENCV_OPENCL_CACHE_ENABLE</c>, <c>TEMP</c>) setzt schon Lumora beim Start des Prozesses;
/// hier wird sie vor dem ersten Laden der nativen DLL noch einmal festgezogen.
/// </summary>
internal static class OpenCvRuntime
{
    /// <summary>Vor jedem anderen OpenCV-Aufruf.</summary>
    public static string Initialize(int threads)
    {
        // Wirksam ist nur die Umgebung beim Prozessstart (die C-Laufzeit liest sie einmal) — Lumora setzt sie dort; diese
        // Zeilen sind die Absicherung für einen Start von Hand. OpenCvSharp 4.13 bindet cv::ocl::setUseOpenCL nicht an.
        Environment.SetEnvironmentVariable("OPENCV_OPENCL_RUNTIME", "disabled");
        Environment.SetEnvironmentVariable("OPENCV_OPENCL_CACHE_ENABLE", "0");
        Environment.SetEnvironmentVariable("OPENCV_OPENCL_DEVICE", "disabled");
        WorkerThreads.Set(threads);
        WorkerThreads.ApplyNative(); // noch keine Arbeit: sicher
        return Cv2.GetVersionString() ?? string.Empty;
    }
}
