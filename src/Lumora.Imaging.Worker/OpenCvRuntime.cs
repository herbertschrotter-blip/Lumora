using OpenCvSharp;
using Lumora.Imaging.Protocol;

namespace Lumora.Imaging.Worker;

/// <summary>Fehler einer Arbeit mit Code für Lumora (der Hilfsprozess läuft weiter).</summary>
internal sealed class WorkerException(WorkerErrorCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public WorkerErrorCode Code { get; } = code;
}

/// <summary>Zahl der Rechen-Threads des Hilfsprozesses (zentral, weil <c>Cv2.SetNumThreads</c> global wirkt).</summary>
internal static class WorkerThreads
{
    private static int _count = Math.Max(1, Environment.ProcessorCount);

    public static int Count => _count;

    public static void Set(int count) => _count = Math.Max(1, count);
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
        Cv2.SetNumThreads(WorkerThreads.Count);
        return Cv2.GetVersionString() ?? string.Empty;
    }
}
