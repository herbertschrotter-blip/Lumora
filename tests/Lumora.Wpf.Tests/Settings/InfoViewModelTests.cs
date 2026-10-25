using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Settings.Info;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.Settings;

/// <summary>
/// Einstellungen › Info (0.2.44, ADR-0042): App-Version = oberster CHANGELOG-Eintrag (csproj liest ihn beim Build),
/// CHANGELOG.md liegt neben der App, Karten „Entwicklung“ (ohne E-Mail-Adresse), „Dieses System“, „Bausteine“ mit
/// Versionen aus den DLLs, Lizenzen, Versionsinfo kopieren, „Änderungsprotokoll“ nur mit CHANGELOG.
/// </summary>
public sealed class InfoViewModelTests
{
    private sealed class RecordingActions : IInfoActions
    {
        public List<string> Revealed { get; } = [];

        public List<string> Opened { get; } = [];

        public List<string> Copied { get; } = [];

        public List<(string Title, string Details)> Shown { get; } = [];

        public void RevealFile(string path) => Revealed.Add("Datei " + path);

        public void OpenFolder(string path) => Revealed.Add("Ordner " + path);

        public void OpenFile(string path) => Opened.Add(path);

        public void CopyText(string text) => Copied.Add(text);

        public void ShowText(string title, string message, string details) => Shown.Add((title, details));
    }

    private sealed class FakePaths(string root) : IAppDataPaths
    {
        public string AppDataRoot => root;

        public string LibraryDatabasePath => Path.Combine(root, "library.db");

        public string ThumbnailsRoot => Path.Combine(root, "thumbs");

        public string LogsRoot => Path.Combine(root, "logs");

        public string GetLibraryThumbnailsPath(long libraryId) => Path.Combine(ThumbnailsRoot, libraryId.ToString());

        public void EnsureDirectoriesExist()
        {
        }
    }

    private static InfoViewModel Create(RecordingActions actions, InMemoryLibraries libraries, string appDirectory)
    {
        var paths = new FakePaths(@"C:\Daten\Lumora");
        var factory = NullService.Create<ILibraryContextFactory>();
        return new InfoViewModel(paths, factory, libraries, actions, NullLogger<InfoViewModel>.Instance, appDirectory: appDirectory);
    }

    /// <summary>CHANGELOG.md der Repo-Wurzel (vom Testausgabeordner aufwärts gesucht).</summary>
    private static string RepoChangelog()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "CHANGELOG.md");
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "Lumora.slnx")))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("CHANGELOG.md der Repo-Wurzel nicht gefunden");
    }

    [Fact]
    public void App_version_is_the_top_changelog_entry()
    {
        var top = AppInfo.ReadTopChangelogVersion(File.ReadAllText(RepoChangelog()));
        top.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
        AppInfo.Version.Should().Be(top, "Lumora.Wpf.csproj setzt Version aus dem obersten „## [x.y.z]“");
        typeof(AppInfo).Assembly.GetName().Version!.ToString(3).Should().Be(top);
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Should().Be(top, "ohne Commit-Anhang");
    }

    [Fact]
    public void Changelog_is_copied_next_to_the_app()
    {
        var copied = AppInfo.FindChangelog(Path.GetDirectoryName(typeof(AppInfo).Assembly.Location)!);
        copied.Should().NotBeNull("csproj kopiert CHANGELOG.md ins Ausgabeverzeichnis (und ins portable Paket)");
        AppInfo.ReadTopChangelogVersion(File.ReadAllText(copied!)).Should().Be(AppInfo.Version);
    }

    [Theory]
    [InlineData("# Changelog\n\nText\n\n## [0.2.44] — 2026-10-21\n\n## [0.2.43] — 2026-10-21\n", "0.2.44")]
    [InlineData("## [1.10.3]\r\n", "1.10.3")]
    [InlineData("Kein Eintrag ## [0.1.0] mitten im Satz", null)]
    [InlineData("## [Unreleased]\n## [0.3.0] — x", "0.3.0")]
    public void Reads_top_changelog_version(string text, string? expected) =>
        AppInfo.ReadTopChangelogVersion(text).Should().Be(expected);

    [Theory]
    [InlineData("1.24.4+0123abc", "1.24.4")]
    [InlineData(" 4.0.2 ", "4.0.2")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Cleans_versions(string? raw, string? expected) => AppInfo.CleanVersion(raw).Should().Be(expected);

    [Fact]
    public void Describes_windows_and_header()
    {
        AppInfo.WindowsText(new Version(10, 0, 26200)).Should().Be("Windows 11 (Build 26200)");
        AppInfo.WindowsText(new Version(10, 0, 19045)).Should().Be("Windows 10 (Build 19045)");
        AppInfo.HeaderLine("0.2.44", new DateTime(2026, 10, 21, 14, 3, 0), "Debug").Should().Be("Version 0.2.44 · erstellt am 21.10.2026 · Debug-Build");
        AppInfo.HeaderLine("0.2.44", null, "Release").Should().Be("Version 0.2.44 · Release-Build");
#if DEBUG
        AppInfo.BuildConfiguration.Should().Be("Debug");
#endif
        AppInfo.BuildDate.Should().NotBeNull();
    }

    [Fact]
    public async Task Shows_development_system_catalog_components_and_copies_version_info()
    {
        var libraries = new InMemoryLibraries();
        libraries.Add(1, "Fotos", 1200, DateTime.UtcNow);
        libraries.Add(2, "Archiv", 48, DateTime.UtcNow.AddDays(-1));
        var actions = new RecordingActions();
        var vm = Create(actions, libraries, AppContext.BaseDirectory);

        vm.AppName.Should().Be("Lumora");
        vm.HeaderText.Should().StartWith($"Version {AppInfo.Version} · erstellt am ");
        vm.Development.Select(l => l.Label).Should().Equal("Programmierung", "Mitarbeit", "Vorgänger");
        vm.Development.Should().Contain(new InfoLine("Programmierung", "Herbert Schrotter"));
        vm.Development.Should().Contain(new InfoLine("Mitarbeit", "Claude (Anthropic), KI-Assistent"));
        vm.Development.Should().NotContain(l => l.Value.Contains('@'), "keine E-Mail-Adresse");

        await vm.LoadAsync();
        vm.SystemInfo.Select(l => l.Label).Should().Equal("Laufzeit", "Grafikkarte", "Gesichtsmodell", "Katalog", "Datenordner");
        vm.SystemInfo[0].Value.Should().StartWith($".NET {Environment.Version}").And.Contain("Windows");
        vm.SystemInfo[3].Value.Should().Be($"2 Bibliotheken · {1248:N0} Dateien · noch nicht angelegt");

        // Versionen aus den DLLs im Testausgabeordner (dieselben Pakete wie in der App)
        var components = string.Join(" | ", vm.Components.Select(c => $"{c.Label}: {c.Value}"));
        components.Should().MatchRegex(@"WPF-UI \d+\.\d+\.\d+")
            .And.MatchRegex(@"CommunityToolkit\.Mvvm \d+\.\d+")
            .And.MatchRegex(@"SQLite 3\.\d+")
            .And.MatchRegex(@"Entity Framework Core \d+\.\d+")
            .And.MatchRegex(@"SharpCompress \d+\.\d+")
            .And.MatchRegex(@"ONNX Runtime DirectML \d+\.\d+")
            .And.Contain("InsightFace buffalo_l (nur nicht-kommerziell)");

        vm.CopyVersionInfoCommand.Execute(null);
        actions.Copied.Should().ContainSingle();
        var copied = actions.Copied[0];
        copied.Should().StartWith($"Lumora {AppInfo.Version}").And.Contain("Laufzeit: .NET").And.Contain("SharpCompress");
        copied.Should().NotContain("Katalog:", "keine Bibliotheksnamen/-zahlen in der kopierten Versionsinfo").And.NotContain(@"C:\");
        vm.Message.Should().Be("Versionsinfo in die Zwischenablage kopiert.");
    }

    [Theory]
    [InlineData(AppDataLocationMode.Standard, false, @"Standard (%LocalAppData%\Lumora)")]
    [InlineData(AppDataLocationMode.Portable, false, "Portabel (Ordner „Daten“ neben Lumora.exe)")]
    [InlineData(AppDataLocationMode.EnvironmentVariable, false, "Umgebungsvariable LUMORA_APPDATA")]
    [InlineData(AppDataLocationMode.Portable, true, "Versteckte Bibliothek (Katalog im Container)")]
    public async Task Shows_the_active_data_folder_without_a_path(AppDataLocationMode mode, bool hidden, string expected)
    {
        // ADR-0069: Info nennt, welcher Datenordner aktiv ist — ohne Pfad, auch in der kopierten Versionsinfo
        var paths = new FakePaths(@"C:\Portabel\Lumora-0.5.0\Daten");
        var actions = new RecordingActions();
        var vm = new InfoViewModel(
            paths, NullService.Create<ILibraryContextFactory>(), new InMemoryLibraries(), actions, NullLogger<InfoViewModel>.Instance,
            appDirectory: AppContext.BaseDirectory, catalog: new CatalogContext(paths, hidden), dataLocation: new AppDataLocation(mode, paths.AppDataRoot));

        await vm.LoadAsync();

        vm.DataFolderText.Should().Be(expected);
        vm.SystemInfo.Should().Contain(new InfoLine("Datenordner", expected));
        vm.CopyVersionInfoCommand.Execute(null);
        actions.Copied[0].Should().Contain($"Datenordner: {expected}").And.NotContain(paths.AppDataRoot);
    }

    /// <summary>Liefert vorgegebene Eigenschaftswerte (Status, Settings, State) — sonst null.</summary>
    public class PropertyStub : DispatchProxy
    {
        public Dictionary<string, object?> Values { get; } = [];

        public static T Create<T>(params (string Name, object? Value)[] values)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, PropertyStub>();
            foreach (var (name, value) in values)
            {
                ((PropertyStub)(object)proxy).Values[name] = value;
            }

            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name.StartsWith("get_", StringComparison.Ordinal) && Values.TryGetValue(targetMethod.Name[4..], out var value) ? value : null;
    }

    [Theory]
    [InlineData(true, true, "DirectML", true, " (DirectML)", "buffalo_l · geladen")]
    [InlineData(true, true, null, true, " (DirectML)", "buffalo_l · vorhanden, nicht geladen")]
    [InlineData(false, false, null, true, " (Prozessor)", "buffalo_l · vorhanden, nicht geladen · Erkennung aus")]
    [InlineData(false, true, null, false, " (DirectML)", "buffalo_l · nicht heruntergeladen · Erkennung aus")]
    [InlineData(true, false, "CPU", true, " (CPU)", "buffalo_l · geladen")]
    public void Describes_face_recognition_on_the_graphics_card(bool enabled, bool useGpu, string? provider, bool modelReady, string gpuSuffix, string model)
    {
        var index = PropertyStub.Create<Lumora.Services.Faces.Indexing.IFaceIndexService>(
            ("Status", new Lumora.Services.Faces.Indexing.FaceIndexStatus(Lumora.Services.Faces.Indexing.FaceIndexState.Idle, ExecutionProvider: provider)),
            ("Settings", new Lumora.Services.Faces.Indexing.FaceIndexSettings(enabled, useGpu)));
        var models = PropertyStub.Create<Lumora.Services.Faces.Models.IFaceModelProvider>(
            ("State", modelReady ? Lumora.Services.Faces.Models.FaceModelState.Ready : Lumora.Services.Faces.Models.FaceModelState.Missing));
        var vm = new InfoViewModel(
            new FakePaths(@"C:\Daten"), NullService.Create<ILibraryContextFactory>(), new InMemoryLibraries(), new RecordingActions(),
            NullLogger<InfoViewModel>.Instance, index, models, AppContext.BaseDirectory);

        vm.FaceExecutionText().Should().Be(gpuSuffix);
        vm.FaceModelText().Should().Be(model);
    }

    [Fact]
    public void Opens_locations_licenses_and_changelog_only_if_present()
    {
        var actions = new RecordingActions();
        var withChangelog = Create(actions, new InMemoryLibraries(), Path.GetDirectoryName(typeof(AppInfo).Assembly.Location)!);
        withChangelog.HasChangelog.Should().BeTrue();
        withChangelog.OpenChangelogCommand.CanExecute(null).Should().BeTrue();
        withChangelog.OpenChangelogCommand.Execute(null);
        actions.Opened.Should().ContainSingle().Which.Should().EndWith("CHANGELOG.md");

        withChangelog.OpenDatabaseCommand.Execute(null);
        withChangelog.OpenThumbnailsCommand.Execute(null);
        withChangelog.OpenLogsCommand.Execute(null);
        actions.Revealed.Should().Equal("Datei " + withChangelog.DatabasePath, @"Ordner C:\Daten\Lumora\thumbs", @"Ordner C:\Daten\Lumora\logs");

        withChangelog.ShowLicensesCommand.Execute(null);
        actions.Shown.Should().ContainSingle();
        actions.Shown[0].Details.Should().Contain("WPF-UI").And.Contain("SharpCompress").And.Contain("ONNX Runtime")
            .And.Contain("NUR NICHT-KOMMERZIELLE NUTZUNG").And.Contain("LGPL");

        var empty = Directory.CreateTempSubdirectory("lumora-info-");
        try
        {
            var without = Create(actions, new InMemoryLibraries(), empty.FullName);
            without.HasChangelog.Should().BeFalse("ohne CHANGELOG.md neben der App bleibt der Knopf ausgeblendet");
            without.OpenChangelogCommand.CanExecute(null).Should().BeFalse();
            InfoViewModel.BuildComponents(empty.FullName, null).Should().Contain(new InfoLine("Archive", "SharpCompress"), "fehlende DLL: ohne Version");
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }
}
