using System.Diagnostics;
using System.Security.Cryptography;

namespace Lumora.Services.HiddenLibraries.Relocation;

/// <summary>Abschnitt beim Kopieren: erst kopieren, dann die Kopie lesen und vergleichen.</summary>
public enum ContainerCopyPhase
{
    Copy,
    Verify,
}

/// <summary>Fortschritt beim Kopieren bzw. Prüfen (Bytes dieses Abschnitts).</summary>
/// <param name="BytesPerSecond">Mittel seit Beginn des Abschnitts, 0 am Anfang.</param>
public readonly record struct ContainerCopyProgress(ContainerCopyPhase Phase, long Done, long Total, double BytesPerSecond)
{
    public double Fraction => Total <= 0 ? 1 : Math.Clamp((double)Done / Total, 0, 1);

    /// <summary>Restzeit des Abschnitts, null solange die Geschwindigkeit unbekannt ist.</summary>
    public TimeSpan? Remaining => BytesPerSecond > 0 ? TimeSpan.FromSeconds((Total - Done) / BytesPerSecond) : null;
}

/// <summary>Die Kopie weicht vom Original ab (Prüfsumme) — sie wurde gelöscht, das Original ist unberührt.</summary>
public sealed class ContainerCopyMismatchException() : IOException("Die Kopie stimmt nicht mit dem Original überein (Prüfsumme). Sie wurde gelöscht, das Original ist unverändert.");

/// <summary>
/// Kopiert eine Container-Datei (Mockup 26): zuerst unter einem Zwischennamen, dabei SHA-256 des Originals; danach die
/// Kopie noch einmal lesen und vergleichen; erst bei Gleichheit auf den Zielnamen umbenennen. Bei Fehler oder Abbruch
/// wird die halbe Kopie gelöscht — das Original wird nie angefasst.
/// </summary>
public static class ContainerFileCopier
{
    /// <summary>Endung der halbfertigen Kopie.</summary>
    public const string PartSuffix = ".lumora-teilkopie";

    private const int BufferSize = 4 << 20;

    /// <summary>Kopieren und prüfen; liefert die Prüfsumme (Hex).</summary>
    public static async Task<string> CopyAndVerifyAsync(string source, string target, IProgress<ContainerCopyProgress>? progress, CancellationToken ct)
    {
        var part = target + PartSuffix;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            var total = new FileInfo(source).Length;
            byte[] sourceHash;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                output.SetLength(total); // Platz vorab belegen: ein volles Laufwerk fällt gleich auf, nicht nach Stunden
                sourceHash = await PumpAsync(input, output, total, ContainerCopyPhase.Copy, progress, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            byte[] copyHash;
            await using (var check = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                copyHash = await PumpAsync(check, null, total, ContainerCopyPhase.Verify, progress, ct).ConfigureAwait(false);
            }

            if (!sourceHash.AsSpan().SequenceEqual(copyHash))
            {
                throw new ContainerCopyMismatchException();
            }

            File.Move(part, target);
            return Convert.ToHexString(sourceHash);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static async Task<byte[]> PumpAsync(Stream input, Stream? output, long total, ContainerCopyPhase phase, IProgress<ContainerCopyProgress>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        var watch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long done = 0;
        progress?.Report(new ContainerCopyProgress(phase, 0, total, 0));
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            if (output is not null)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            done += read;
            if (watch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
            {
                lastReport = watch.Elapsed;
                progress?.Report(new ContainerCopyProgress(phase, done, total, done / Math.Max(0.001, watch.Elapsed.TotalSeconds)));
            }
        }

        progress?.Report(new ContainerCopyProgress(phase, done, total, done / Math.Max(0.001, watch.Elapsed.TotalSeconds)));
        return hash.GetHashAndReset();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // bleibt liegen; der nächste Versuch überschreibt den Zwischennamen
        }
    }
}
