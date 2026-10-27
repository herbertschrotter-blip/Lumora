using FluentAssertions;
using Lumora.Services.AiEdit;
using Lumora.Services.Infrastructure;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.AiEdit;

public class ComfyInstallationTests
{
    [Fact]
    public void Detect_without_container_is_no_container()
    {
        ComfyInstallation.Detect(null).State.Should().Be(ComfyInstallState.NoContainer);
        ComfyInstallation.Detect(Path.Combine(Path.GetTempPath(), "gibt-es-nicht-" + Guid.NewGuid())).State.Should().Be(ComfyInstallState.NoContainer);
    }

    [Fact]
    public void Detect_empty_container_is_not_installed()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "ki-leer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var installation = ComfyInstallation.Detect(root);
            installation.State.Should().Be(ComfyInstallState.NotInstalled);
            installation.CanStart.Should().BeFalse();
            installation.Problem.Should().NotContain(root, "Hinweise zeigen keine Pfade");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Detect_unverified_version_refuses_start()
    {
        using var container = new FakeContainer(version: "0.44.1");
        var installation = ComfyInstallation.Detect(container.Root);
        installation.State.Should().Be(ComfyInstallState.UnverifiedVersion);
        installation.Version.Should().Be("0.44.1");
        installation.CanStart.Should().BeFalse();
        installation.Problem.Should().Contain("0.44.1").And.Contain("nicht geprüft");
    }

    [Fact]
    public void Detect_verified_version_with_manifest_allows_only_listed_and_present_extensions()
    {
        using var container = new FakeContainer("0.43.0", true, "ComfyUI-GGUF");
        Directory.CreateDirectory(Path.Combine(container.Layout.CustomNodesRoot, "Fremd")); // vorhanden, aber nicht freigegeben
        var installation = ComfyInstallation.Detect(container.Root);
        installation.State.Should().Be(ComfyInstallState.Ready);
        installation.Version.Should().Be("0.43.0");
        installation.AllowedExtensions.Should().Equal("ComfyUI-GGUF");
        installation.Manifest!.Models.Should().ContainSingle(m => m.File == "vae/ae.safetensors");
    }

    [Fact]
    public void Detect_ignores_extension_names_that_are_paths()
    {
        using var container = new FakeContainer("0.43.0", false);
        var manifest = new AiInstallManifest { Extensions = [new AiInstallExtension(@"..\..\models"), new AiInstallExtension("a/b"), new AiInstallExtension("fehlt")] };
        File.WriteAllText(container.Layout.InstallFile, manifest.ToJson());
        ComfyInstallation.Detect(container.Root).AllowedExtensions.Should().BeEmpty();
    }

    [Fact]
    public void Detect_without_manifest_starts_without_extensions()
    {
        using var container = new FakeContainer("0.43.0", false, "ComfyUI-GGUF");
        var installation = ComfyInstallation.Detect(container.Root);
        installation.State.Should().Be(ComfyInstallState.Ready);
        installation.Manifest.Should().BeNull();
        installation.AllowedExtensions.Should().BeEmpty("ohne Installationsliste ist keine Erweiterung freigegeben");
    }

    [Fact]
    public void Detect_broken_manifest_refuses_start()
    {
        using var container = new FakeContainer("0.43.0", false);
        File.WriteAllText(container.Layout.InstallFile, "{ kein json");
        var installation = ComfyInstallation.Detect(container.Root);
        installation.State.Should().Be(ComfyInstallState.BrokenManifest);
        installation.CanStart.Should().BeFalse();
    }

    [Fact]
    public void Manifest_round_trips_and_rejects_foreign_format()
    {
        var manifest = new AiInstallManifest
        {
            ComfyUi = new AiInstallPackage("0.43.0", "ComfyUI_windows_portable_nvidia_cu126.7z", Sha256: "ab"),
            PythonPackages = [new AiInstallPythonPackage("gguf", "0.19.0")],
            Models = [new AiInstallModel("diffusion_models/x.gguf", "Entfernen", Bytes: 42, License: "nicht-kommerziell")],
        };
        var file = Path.Combine(Path.GetTempPath(), "ki-install-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, manifest.ToJson());
            var loaded = AiInstallManifest.TryLoad(file)!;
            loaded.ComfyUi!.Version.Should().Be("0.43.0");
            loaded.PythonPackages.Should().ContainSingle(p => p.Name == "gguf" && p.Version == "0.19.0");
            loaded.Models.Should().ContainSingle(m => m.Bytes == 42 && m.License == "nicht-kommerziell");

            File.WriteAllText(file, """{"format":"anderes","version":1}""");
            var act = () => AiInstallManifest.TryLoad(file);
            act.Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(@"X:\.lumora-ki", true)]
    [InlineData(@"X:\.LUMORA-KI\", true)]
    [InlineData(@"X:\.lumora", true)]
    [InlineData(@"X:\Fotos\.lumora-kiste", false)]
    [InlineData(@"X:\Fotos", false)]
    public void Scanner_skips_ai_edit_folder_by_name(string path, bool expected)
        => CatalogFolders.IsCatalogFolder(path).Should().Be(expected);

    [Fact]
    public void Clear_transient_empties_work_folders_but_keeps_user_folder()
    {
        using var container = new FakeContainer();
        var layout = container.Layout;
        layout.EnsureWorkFolders();
        File.WriteAllText(Path.Combine(layout.InputDir, "a.png"), "x");
        Directory.CreateDirectory(Path.Combine(layout.OutputDir, "sub"));
        File.WriteAllText(Path.Combine(layout.OutputDir, "sub", "b.png"), "x");
        File.WriteAllText(Path.Combine(layout.TempDir, "c.tmp"), "x");
        File.WriteAllText(Path.Combine(layout.WorkRoot, "comfyui_detail.log"), "Prompt …");
        File.WriteAllText(Path.Combine(layout.UserDir, "comfy.settings.json"), "{}");

        layout.ClearTransient().Should().Be(0);

        Directory.EnumerateFileSystemEntries(layout.InputDir).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(layout.OutputDir).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(layout.TempDir).Should().BeEmpty();
        File.Exists(Path.Combine(layout.WorkRoot, "comfyui_detail.log")).Should().BeFalse();
        File.Exists(Path.Combine(layout.UserDir, "comfy.settings.json")).Should().BeTrue();
        new DirectoryInfo(layout.Root).Attributes.Should().HaveFlag(FileAttributes.Hidden).And.HaveFlag(FileAttributes.System);
    }

    [Fact]
    public async Task Settings_store_keeps_idle_minutes_in_catalog_and_clamps()
    {
        using var fixture = await TempLibraryFixture.CreateAsync();
        var store = new AiEditSettingsStore(fixture.ContextFactory);
        (await store.LoadAsync()).IdleMinutes.Should().Be(AiEditSettings.DefaultIdleMinutes);

        await store.SaveAsync(new AiEditSettings(25));
        (await store.LoadAsync()).IdleMinutes.Should().Be(25);

        await store.SaveAsync(new AiEditSettings(9999));
        (await store.LoadAsync()).IdleMinutes.Should().Be(AiEditSettings.MaxIdleMinutes);
    }
}
