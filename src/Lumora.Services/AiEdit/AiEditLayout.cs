using Lumora.Services.Infrastructure;

namespace Lumora.Services.AiEdit;

/// <summary>
/// Ordner der KI-Bearbeitung im Container (ADR-0076 Punkt 2): <c>&lt;Container-Wurzel&gt;\.lumora-ki\</c> mit dem portablen
/// ComfyUI-Paket, den Modellen, den Arbeitsordnern, den Caches und <c>ki-install.json</c>. Eine Installation je Container,
/// gemeinsam für alle versteckten Bibliotheken darin. Außerhalb dieses Ordners legt die KI-Bearbeitung nichts ab.
/// </summary>
public sealed class AiEditLayout
{
    public const string InstallFileName = "ki-install.json";

    public AiEditLayout(string containerRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRoot);
        ContainerRoot = containerRoot;
        Root = Path.Combine(containerRoot, CatalogFolders.AiEditFolderName);
        PackageRoot = Path.Combine(Root, "ComfyUI");
        PythonExe = Path.Combine(PackageRoot, "python_embeded", "python.exe");
        ComfyRoot = Path.Combine(PackageRoot, "ComfyUI");
        MainPy = Path.Combine(ComfyRoot, "main.py");
        VersionFile = Path.Combine(ComfyRoot, "comfyui_version.py");
        CustomNodesRoot = Path.Combine(ComfyRoot, "custom_nodes");
        ModelsRoot = Path.Combine(Root, "models");
        WorkRoot = Path.Combine(Root, "work");
        InputDir = Path.Combine(WorkRoot, "input");
        OutputDir = Path.Combine(WorkRoot, "output");
        TempDir = Path.Combine(WorkRoot, "temp");
        UserDir = Path.Combine(WorkRoot, "user");
        PreviewsDir = Path.Combine(WorkRoot, "previews");
        CacheRoot = Path.Combine(Root, "cache");
        InstallFile = Path.Combine(Root, InstallFileName);
    }

    /// <summary>Wurzel des geöffneten Containers (Laufwerk der versteckten Bibliothek, „X:\“).</summary>
    public string ContainerRoot { get; }

    /// <summary><c>.lumora-ki</c> (versteckt + System + nicht indizieren).</summary>
    public string Root { get; }

    /// <summary>Das portable ComfyUI-Paket, unverändert.</summary>
    public string PackageRoot { get; }

    public string PythonExe { get; }

    public string ComfyRoot { get; }

    public string MainPy { get; }

    public string VersionFile { get; }

    public string CustomNodesRoot { get; }

    public string ModelsRoot { get; }

    /// <summary>Arbeitsordner; zugleich Arbeitsverzeichnis des Prozesses (ComfyUI legt dort sein Detail-Protokoll an).</summary>
    public string WorkRoot { get; }

    public string InputDir { get; }

    public string OutputDir { get; }

    public string TempDir { get; }

    /// <summary>ComfyUI-Benutzerordner (Einstellungen der Oberfläche, eigene Datenbank) — bleibt beim Beenden erhalten.</summary>
    public string UserDir { get; }

    public string PreviewsDir { get; }

    /// <summary>Python-, Torch-, Hugging-Face-, CUDA- und pip-Caches.</summary>
    public string CacheRoot { get; }

    public string InstallFile { get; }

    /// <summary>Ordner, die beim Beenden geleert werden (ADR-0076 Punkt 4); <see cref="UserDir"/> gehört nicht dazu.</summary>
    public IReadOnlyList<string> TransientDirs => [InputDir, OutputDir, TempDir, PreviewsDir];

    /// <summary>Legt Arbeits- und Cache-Ordner an und setzt die Attribute von <c>.lumora-ki</c> (falls verloren).</summary>
    public void EnsureWorkFolders()
    {
        foreach (var dir in new[] { InputDir, OutputDir, TempDir, UserDir, PreviewsDir, CacheRoot, Path.Combine(CacheRoot, "tmp") })
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            var info = new DirectoryInfo(Root);
            info.Attributes |= FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nur Komfort: Scan, Flatten und Zielbaum überspringen .lumora-ki auch am Namen.
        }
    }

    /// <summary>
    /// Leert Ein-/Ausgabe, Zwischenstände und das Detail-Protokoll von ComfyUI. Gibt die Zahl der Einträge zurück, die
    /// sich nicht löschen ließen (noch offen) — 0 = leer.
    /// </summary>
    public int ClearTransient()
    {
        var failed = 0;
        foreach (var dir in TransientDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                failed += TryDelete(entry);
            }
        }

        if (Directory.Exists(WorkRoot))
        {
            foreach (var log in new DirectoryInfo(WorkRoot).EnumerateFiles("*.log"))
            {
                failed += TryDelete(log);
            }
        }

        return failed;
    }

    private static int TryDelete(FileSystemInfo entry)
    {
        try
        {
            if (entry is DirectoryInfo dir)
            {
                dir.Delete(recursive: true);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }
}
