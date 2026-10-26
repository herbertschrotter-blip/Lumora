using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Enhance;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Enhance;
using Lumora.Services.ModelPackages;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;
using Wpf.Ui.Appearance;

namespace Lumora.Wpf.Tests.Enhance;

/// <summary>
/// Oberfläche von „Foto verbessern“ (ADR-0071, Mockup 13): Lightbox-Leiste (Werkzeuge mit Modell-Zustand, Vorschau, Vorher/
/// Nachher, Speichern, Verwerfen, Pinsel), Stapel-Dialog, Einstellungen › Verbessern, Fassungs-Marke; Darstellung ohne
/// Binding-Fehler. Fake-Dienst — kein Modell, kein Download.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class EnhanceUiTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumora-wpf-enhance-" + Guid.NewGuid().ToString("N"));

    public EnhanceUiTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Task OnUiAsync(Func<Task> scenario) => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    [Fact]
    public Task Leiste_zeigt_Werkzeuge_mit_Modell_Zustand_und_Laden_nur_nach_Bestaetigung() => OnUiAsync(async () =>
    {
        var service = new FakeEnhanceService(_root);
        var dialogs = new ScriptedDialogs { ConfirmAnswer = false };
        var manager = new ModelPackageManager(
            [new FileSetModelPackageSource(EnhanceModelCatalog.Faces, EnhanceModelCatalog.FaceFusionBase, new ModelStorage(_root), new NoDownload(), messenger: new WeakReferenceMessenger())],
            new NoDownload());
        using var vm = new EnhancePanelViewModel(service, manager, dialogs);
        await vm.SetOpenAsync(true);

        vm.Tools.Select(t => t.Title).Should().Equal("Automatisch", "Hochskalieren und schärfen", "Gesichter restaurieren", "Einfärben", "Entfernen", "Zuschneiden, drehen, Licht");
        vm.Tool(EnhanceTool.Upscale).Should().Match<EnhanceToolItem>(t => t.IsReady && t.StateText == "bereit" && !t.CanLoad);
        var faces = vm.Tool(EnhanceTool.Faces);
        faces.Should().Match<EnhanceToolItem>(t => !t.IsReady && t.StateText == "Modell fehlt" && t.CanLoad);
        vm.Tool(EnhanceTool.Colorize).Should().Match<EnhanceToolItem>(t => t.StateText == "Quelle folgt" && !t.CanLoad);

        await vm.LoadModelCommand.ExecuteAsync(faces);

        dialogs.Confirmations.Should().Equal("Gesichter restaurieren laden?");
        File.Exists(Path.Combine(_root, "enhance-faces", EnhanceModelCatalog.FacesFile)).Should().BeFalse("abgelehnt → nichts geladen");
    });

    [Fact]
    public Task Natuerlichkeit_kommt_aus_den_Einstellungen_und_wird_beim_Berechnen_gemerkt() => OnUiAsync(async () =>
    {
        // 0.5.6: Herbert fand die Haut nach dem Hochskalieren „zu glatt und künstlich“
        var store = new SeededSettingsStore { Current = EnhanceSettings.Default with { Naturalness = 0.5, Grain = 0.1 } };
        var service = new FakeEnhanceService(_root);
        using var vm = new EnhancePanelViewModel(service, settingsStore: store);
        await vm.SetOpenAsync(true);
        await vm.SetFileAsync(42, isImage: true);

        (vm.Naturalness, vm.Grain).Should().Be((0.5, 0.1));
        vm.ShowNaturalness.Should().BeTrue("„Automatisch“ kann hochskalieren");
        vm.ToggleToolCommand.Execute(vm.Tool(EnhanceTool.Faces));
        vm.ShowNaturalness.Should().BeFalse("nur Gesichter: kein Hochskalieren");
        vm.ToggleToolCommand.Execute(vm.Tool(EnhanceTool.Upscale));
        vm.ShowNaturalness.Should().BeTrue();

        vm.Naturalness = 0.6;
        vm.Grain = 0.3;
        vm.SetUpscaleModelCommand.Execute("natural");
        vm.IsNaturalModel.Should().BeTrue();
        vm.NaturalModel.StateText.Should().NotBeNullOrEmpty("Zustand des zweiten Modells aus der Statusabfrage");
        vm.BuildOptions().Should().Match<EnhanceOptions>(o => o.Naturalness == 0.6 && o.Grain == 0.3 && o.UpscaleModel == EnhanceUpscaleModel.Natural);
        await vm.CalculateCommand.ExecuteAsync(null);

        service.Rendered.Last().Naturalness.Should().Be(0.6);
        for (var i = 0; i < 50 && store.Saved.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        store.Saved.Should().ContainSingle().Which.Should().Match<EnhanceSettings>(s => s.Naturalness == 0.6 && s.Grain == 0.3 && s.UpscaleModel == EnhanceUpscaleModel.Natural);
        store.Current.Location.Should().Be(EnhanceSettings.Default.Location, "die übrigen Einstellungen bleiben");
    });

    private sealed class SeededSettingsStore : IEnhanceSettingsStore
    {
        public EnhanceSettings Current { get; set; } = EnhanceSettings.Default;

        public List<EnhanceSettings> Saved { get; } = [];

        public Task<EnhanceSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public Task SaveAsync(EnhanceSettings settings, CancellationToken ct = default)
        {
            Saved.Add(settings);
            Current = settings;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public Task Automatisch_und_Modell_Werkzeuge_schliessen_sich_aus_Zuschneiden_geht_immer_dazu() => OnUiAsync(async () =>
    {
        using var vm = new EnhancePanelViewModel(new FakeEnhanceService(_root));
        await vm.SetOpenAsync(true);
        vm.Tool(EnhanceTool.Auto).IsSelected.Should().BeTrue("Standard");

        vm.ToggleToolCommand.Execute(vm.Tool(EnhanceTool.Upscale));
        vm.ToggleToolCommand.Execute(vm.Tool(EnhanceTool.Adjust));
        vm.Tool(EnhanceTool.Auto).IsSelected.Should().BeFalse();
        vm.SetUpscaleFactorCommand.Execute("4");
        vm.RotateRightCommand.Execute(null);
        vm.CropPreset = "Quadrat";
        vm.Brightness = 0.3;
        var options = vm.BuildOptions();
        options.Should().Match<EnhanceOptions>(o => !o.Auto && o.Upscale && o.UpscaleFactor == 4 && o.Rotation == 90 && o.CropAspect == 1.0 && o.Brightness == 0.3);

        vm.ToggleToolCommand.Execute(vm.Tool(EnhanceTool.Auto));
        vm.Tool(EnhanceTool.Upscale).IsSelected.Should().BeFalse();
        vm.Tool(EnhanceTool.Adjust).IsSelected.Should().BeTrue();
    });

    [Fact]
    public Task Vorschau_dann_Speichern_als_neue_Datei_mit_Meldung() => OnUiAsync(async () =>
    {
        var service = new FakeEnhanceService(_root);
        using var vm = new EnhancePanelViewModel(service);
        EnhanceSaveResult? saved = null;
        vm.Saved += (_, r) => saved = r;
        await vm.SetOpenAsync(true);
        await vm.SetFileAsync(42, isImage: true);
        vm.TargetName.Should().Be("Foto_verbessert.jpg");
        vm.ToggleToolCommand.Execute(vm.Tool(EnhanceTool.Upscale));

        await vm.CalculateCommand.ExecuteAsync(null);

        vm.HasPreview.Should().BeTrue();
        vm.AfterImage.Should().NotBeNull();
        vm.ShowsOverlay.Should().BeTrue("Vorher/Nachher auf der Bühne");
        vm.ComparePosition.Should().Be(0.5);
        vm.PreviewInfo.Should().Contain("Hochskalieren");
        service.Rendered.Should().ContainSingle().Which.Upscale.Should().BeTrue();
        vm.SaveCommand.CanExecute(null).Should().BeTrue();

        await vm.SaveCommand.ExecuteAsync(null);

        service.Saved.Should().ContainSingle();
        saved!.Path.Should().EndWith("Foto_verbessert.jpg");
        vm.HasPreview.Should().BeFalse();
        vm.Message.Should().Contain("Gespeichert als „Foto_verbessert.jpg“").And.Contain("Original bleibt unverändert");
        vm.MessageIsError.Should().BeFalse();
    });

    [Fact]
    public Task Verwerfen_und_geaenderte_Optionen_loeschen_die_Vorschau() => OnUiAsync(async () =>
    {
        var service = new FakeEnhanceService(_root);
        using var vm = new EnhancePanelViewModel(service);
        await vm.SetOpenAsync(true);
        await vm.SetFileAsync(42, isImage: true);
        vm.ToggleToolCommand.Execute(vm.Tool(EnhanceTool.Upscale));
        await vm.CalculateCommand.ExecuteAsync(null);
        var first = service.LastPreview!;

        vm.DiscardCommand.Execute(null);
        vm.HasPreview.Should().BeFalse();
        service.Discarded.Should().Equal(first);
        vm.Message.Should().Contain("nichts gespeichert");

        await vm.CalculateCommand.ExecuteAsync(null);
        vm.SetUpscaleFactorCommand.Execute("4"); // Option geändert → Vorschau passt nicht mehr
        vm.HasPreview.Should().BeFalse();
        service.Discarded.Should().HaveCount(2);

        await vm.CalculateCommand.ExecuteAsync(null);
        await vm.SetFileAsync(43, isImage: true); // geblättert
        vm.HasPreview.Should().BeFalse();
        service.Discarded.Should().HaveCount(3);
        await vm.SetFileAsync(44, isImage: false); // Video
        vm.HasPhoto.Should().BeFalse();
        vm.CalculateCommand.CanExecute(null).Should().BeFalse();
    });

    [Fact]
    public Task Optionen_waehrend_der_Berechnung_geaendert_verwerfen_das_Ergebnis() => OnUiAsync(async () =>
    {
        var service = new FakeEnhanceService(_root) { Gate = new TaskCompletionSource() };
        using var vm = new EnhancePanelViewModel(service);
        await vm.SetOpenAsync(true);
        await vm.SetFileAsync(42, isImage: true);
        vm.ToggleToolCommand.Execute(vm.Tool(EnhanceTool.Upscale));

        var running = vm.CalculateCommand.ExecuteAsync(null);
        vm.IsBusy.Should().BeTrue();
        vm.SetUpscaleFactorCommand.Execute("4");
        service.Gate.SetResult();
        await running;

        vm.HasPreview.Should().BeFalse("das Ergebnis passt nicht mehr zu den Optionen");
        service.Discarded.Should().ContainSingle();
        vm.Message.Should().Contain("neu berechnen");
    });

    [Fact]
    public Task Fehlendes_Modell_meldet_den_Grund_ohne_Vorschau() => OnUiAsync(async () =>
    {
        var service = new FakeEnhanceService(_root) { MissingOnRender = true };
        using var vm = new EnhancePanelViewModel(service);
        await vm.SetOpenAsync(true);
        await vm.SetFileAsync(42, isImage: true);
        vm.ToggleToolCommand.Execute(vm.Tool(EnhanceTool.Faces));

        await vm.CalculateCommand.ExecuteAsync(null);

        vm.HasPreview.Should().BeFalse();
        vm.MessageIsError.Should().BeTrue();
        vm.Message.Should().Contain("Modell fehlt");
    });

    [Fact]
    public Task Pinsel_markiert_nur_im_Entfernen_Modus_mit_Strich_zurueck() => OnUiAsync(async () =>
    {
        using var vm = new EnhancePanelViewModel(new FakeEnhanceService(_root));
        await vm.SetOpenAsync(true);
        await vm.SetFileAsync(42, isImage: true);
        vm.AddDot(0.5, 0.5);
        vm.BrushDots.Should().BeEmpty("ohne „Entfernen“ kein Pinsel");

        vm.ToggleToolCommand.Execute(vm.Tool(EnhanceTool.Erase));
        vm.IsBrushMode.Should().BeTrue();
        vm.ShowsOverlay.Should().BeTrue();
        vm.BeginStroke();
        vm.AddDot(0.2, 0.2);
        vm.AddDot(0.25, 0.2);
        vm.BeginStroke();
        vm.AddDot(0.8, 0.8);
        vm.BrushSummary.Should().Be("2 Striche markiert.");
        vm.UndoStrokeCommand.Execute(null);
        vm.BrushDots.Should().HaveCount(2);
        vm.BuildOptions().Should().Match<EnhanceOptions>(o => o.Erase && o.BrushDots.Count == 2 && o.HasErase);
        vm.ClearStrokesCommand.Execute(null);
        vm.BrushDots.Should().BeEmpty();
        vm.BuildOptions().HasErase.Should().BeFalse();

        await vm.CalculateCommand.ExecuteAsync(null);
        vm.Message.Should().Contain("Pinsel", "ohne Markierung nichts zu tun");
    });

    [Fact]
    public Task Buehne_setzt_den_Regler_und_reicht_Pinselpunkte_relativ_weiter() => OnUiAsync(() =>
    {
        var target = new RecordingBrush();
        var image = BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgr24, null, new byte[200 * 100 * 3], 600);
        var overlay = new EnhanceStageOverlay { BeforeSource = image, AfterSource = image, Width = 416, Height = 216, BrushTarget = target };
        var window = new Window { Content = overlay, Width = 500, Height = 300, ShowActivated = false, Left = -10000 };
        window.Show();
        try
        {
            overlay.UpdateLayout();
            var rect = overlay.FitRect(image);
            rect.Should().Be(new Rect(8, 8, 400, 200));
            overlay.SetPositionFrom(new Point(108, 50));
            overlay.Position.Should().BeApproximately(0.25, 0.0001);
            overlay.SetPositionFrom(new Point(-50, 50));
            overlay.Position.Should().Be(0);

            var rtb = new RenderTargetBitmap(416, 216, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(overlay); // Vorher/Nachher zeichnen ohne Fehler
            overlay.IsBrushMode = true;
            rtb.Render(overlay);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Klick_auf_die_ganze_Werkzeugkarte_trifft_den_Umschalter() => OnUiAsync(async () =>
    {
        // 0.5.2: Der unsichtbare Knopf über der Karte bekam vom WPF-UI-Stil HorizontalAlignment=Left und war leer — er saß winzig
        // beim Symbol, ein Klick auf Titel oder Unterzeile tat nichts. Echter Treffertest statt Command.Execute.
        using var vm = new EnhancePanelViewModel(new FakeEnhanceService(_root));
        await vm.SetOpenAsync(true);
        await vm.SetFileAsync(42, isImage: true);
        var panel = new EnhancePanel { DataContext = vm };
        var window = new Window { Content = panel, Width = 360, Height = 900, ShowActivated = false, Left = -10000 };
        window.Show();
        try
        {
            window.UpdateLayout();
            foreach (var tool in vm.Tools)
            {
                var title = Descendants(panel).OfType<System.Windows.Controls.TextBlock>().First(t => t.Text == tool.Title);
                var card = Descendants(panel).OfType<System.Windows.Controls.Border>().First(b => b.Name == "Card" && b.DataContext == tool);
                foreach (var point in new[]
                         {
                             title.TranslatePoint(new Point(title.ActualWidth / 2, title.ActualHeight / 2), panel),
                             card.TranslatePoint(new Point(card.ActualWidth * 0.6, card.ActualHeight - 4), panel),
                         })
                {
                    var hit = panel.InputHitTest(point) as DependencyObject;
                    var button = Ancestors(hit).OfType<System.Windows.Controls.Button>().FirstOrDefault();
                    button.Should().NotBeNull($"Klick auf „{tool.Title}“ bei {point} muss den Umschalter treffen");
                    button!.Command.Should().BeSameAs(vm.ToggleToolCommand);
                    button.CommandParameter.Should().BeSameAs(tool);
                }
            }
        }
        finally
        {
            window.Close();
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? node)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            yield return node;
        }
    }

    [Fact]
    public Task Leiste_hell_und_dunkel_ohne_Binding_Fehler() => OnUiAsync(async () =>
    {
        using var vm = new EnhancePanelViewModel(new FakeEnhanceService(_root));
        await vm.SetOpenAsync(true);
        await vm.SetFileAsync(42, isImage: true);
        using var errors = new BindingErrors();
        var panel = new EnhancePanel { DataContext = vm };
        var window = new Window { Content = panel, Width = 360, Height = 900, ShowActivated = false, Left = -10000 };
        window.Show();
        try
        {
            foreach (var tool in new[] { EnhanceTool.Upscale, EnhanceTool.Faces, EnhanceTool.Erase, EnhanceTool.Adjust })
            {
                vm.ToggleToolCommand.Execute(vm.Tool(tool));
            }

            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                ApplicationThemeManager.Apply(theme);
                window.UpdateLayout();
                panel.ActualHeight.Should().BeGreaterThan(400);
            }

            await vm.CalculateCommand.ExecuteAsync(null);
            window.UpdateLayout();
            errors.Messages.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }
    });

    [Fact]
    public Task Stapel_Dialog_mit_Zeitschaetzung_und_gesperrten_Werkzeugen_ohne_Modell() => OnUiAsync(async () =>
    {
        var service = new FakeEnhanceService(_root);
        var vm = new EnhanceBatchDialogViewModel(service, [1, 2, 3, 4, 5, 6]);
        await vm.LoadAsync();

        vm.Title.Should().Be("6 Fotos verbessern");
        vm.Choices.Single(c => c.Tool == EnhanceTool.Upscale).IsAvailable.Should().BeTrue();
        vm.Choices.Single(c => c.Tool == EnhanceTool.Faces).Should().Match<EnhanceBatchChoice>(c => !c.IsAvailable && c.StateText == "Modell fehlt");
        vm.EstimateText.Should().Contain("etwa 2 Minuten auf der Grafikkarte").And.Contain("Hintergrund");
        vm.CanConfirm.Should().BeTrue();

        vm.Choices.Single(c => c.Tool == EnhanceTool.Upscale).IsChecked = true;
        vm.Choices[0].IsChecked.Should().BeFalse("Automatisch schließt die übrigen aus");
        vm.SetUpscaleFactorCommand.Execute("4");
        await vm.UpdateEstimateAsync();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.ConfirmCommand.Execute(null);

        closed.Should().BeTrue();
        vm.Result.Should().Match<EnhanceOptions>(o => o.Upscale && o.UpscaleFactor == 4 && !o.Auto);

        service.EstimateMissing = [EnhanceTool.Faces];
        await vm.UpdateEstimateAsync();
        vm.CanConfirm.Should().BeFalse();
        vm.Problem.Should().Contain("Modell fehlt: Gesichter");
    });

    [Fact]
    public Task Einstellungen_Verbessern_speichern_sofort_und_zeigen_die_Modelle() => OnUiAsync(async () =>
    {
        var store = new MemorySettingsStore();
        var sections = new SettingsSectionState();
        var manager = new ModelPackageManager(
            EnhanceModelCatalog.All.Select(e => (IModelPackageSource)new FileSetModelPackageSource(e.Definition, e.BaseUri, new ModelStorage(_root), new NoDownload(), messenger: new WeakReferenceMessenger())).ToList(),
            new NoDownload());
        using var vm = new EnhanceSettingsViewModel(store, manager, new ScriptedDialogs(), sections);
        await vm.RefreshAsync();

        vm.Models.Select(m => m.ModelName).Should().Equal("Real-ESRGAN x4plus", "Real-ESRGAN general x4v3", "GFPGAN v1.4", "DeOldify (artistic)", "LaMa (big-lama)");
        vm.Models.Should().OnlyContain(m => m.StateText == "nicht geladen" && m.CanLoad && m.SizeText.StartsWith("≈ ", StringComparison.Ordinal));
        vm.FileNameExample.Should().Be("Foto_verbessert.jpg");

        vm.ShowBestVariant = true;
        await WaitUntilAsync(() => store.Saved.Count > 0);
        store.Saved[^1].ShowBestVariant.Should().BeTrue();
        vm.FileNamePattern = "{name}-hd";
        vm.UseSubfolder = true;
        vm.FileNameExample.Should().Be("Foto-hd.jpg");
        await vm.SaveNowAsync();
        store.Saved[^1].Should().Match<EnhanceSettings>(s => s.Location == EnhanceSaveLocation.Subfolder && s.FileNamePattern == "{name}-hd");

        vm.OpenModelPackagesCommand.Execute(null);
        sections.SelectedSection.Should().Be(NavigationTags.SettingsModels);
    });

    [Fact]
    public Task Einstellungen_hell_und_dunkel_ohne_Binding_Fehler() => OnUiAsync(async () =>
    {
        using var vm = new EnhanceSettingsViewModel(new MemorySettingsStore());
        using var errors = new BindingErrors();
        var section = new SettingsEnhanceSection { DataContext = vm };
        var window = new Window { Content = section, Width = 1000, Height = 900, ShowActivated = false, Left = -10000 };
        window.Show();
        try
        {
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                ApplicationThemeManager.Apply(theme);
                window.UpdateLayout();
            }

            await WaitUntilAsync(() => vm.IsLoaded);
            vm.Models.Should().OnlyContain(m => m.StateText == "Quelle folgt", "ohne Manager nicht ladbar");
            errors.Messages.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }
    });

    [Fact]
    public void Fassungs_Marke_nennt_die_Zahl_und_unterscheidet_Original_und_Fassung()
    {
        var item = new GalleryItem(new GalleryFileDto(5, 1, "a_verbessert.jpg", ".jpg", 100, 0, 0, false, null, false, @"C:\Fotos"));
        item.HasVariants.Should().BeFalse();
        item.VariantText.Should().BeEmpty();
        item.Variants = new PhotoVariantGroup(4, [5]);
        item.HasVariants.Should().BeTrue();
        item.VariantText.Should().Be("2 Fassungen");
        item.IsVariant.Should().BeTrue();
        new PhotoVariantGroup(4, [5, 6]).AllFileIds.Should().Equal(4, 5, 6);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Bedingung nicht erreicht");
            }

            await Task.Delay(20);
        }
    }

    // ---- Fakes ---------------------------------------------------------------------------------------------------

    /// <summary>Dienst-Attrappe: Hochskalieren bereit, Gesichter „Modell fehlt“, Einfärben „Quelle folgt“; Vorschau als echtes JPEG.</summary>
    private sealed class FakeEnhanceService(string root) : IPhotoEnhanceService
    {
        public List<EnhanceOptions> Rendered { get; } = [];

        public List<EnhancePreview> Saved { get; } = [];

        public List<EnhancePreview> Discarded { get; } = [];

        public EnhancePreview? LastPreview { get; private set; }

        public bool MissingOnRender { get; set; }

        /// <summary>Wenn gesetzt, wartet die Berechnung darauf (Optionen während der Berechnung ändern).</summary>
        public TaskCompletionSource? Gate { get; set; }

        public IReadOnlyList<EnhanceTool> EstimateMissing { get; set; } = [];

        public Task<IReadOnlyList<EnhanceToolStatus>> GetToolStatusAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EnhanceToolStatus>>(
            [
                new(EnhanceTool.Auto, null, true, false, "bereit", null),
                new(EnhanceTool.Upscale, EnhanceModelCatalog.UpscaleId, true, false, "bereit", "Real-ESRGAN x4plus"),
                new(EnhanceTool.Faces, EnhanceModelCatalog.FacesId, false, true, "Modell fehlt", "GFPGAN v1.4"),
                new(EnhanceTool.Colorize, EnhanceModelCatalog.ColorizeId, false, false, "Quelle folgt", "DeOldify"),
                new(EnhanceTool.Erase, EnhanceModelCatalog.InpaintId, true, false, "bereit", "LaMa"),
                new(EnhanceTool.Adjust, null, true, false, "bereit", null),
            ]);

        public async Task<EnhancePreview> RenderAsync(int fileId, EnhanceOptions options, IProgress<EnhanceProgress>? progress = null, CancellationToken ct = default)
        {
            Rendered.Add(options);
            await Task.Yield();
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            if (MissingOnRender)
            {
                throw new EnhanceModelMissingException(EnhanceTool.Faces, "Gesichter: Modell fehlt. Das Modell zuerst laden.");
            }

            progress?.Report(new EnhanceProgress("Foto wird hochskaliert", 0.5));
            var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".jpg");
            new WicEnhanceImageCodec().SaveJpeg(EnhanceImage.Blank(32, 16), path, 90);
            LastPreview = new EnhancePreview(fileId, path, 32, 16, [EnhanceTool.Upscale], "upscale", TimeSpan.FromSeconds(1.5), []);
            return LastPreview;
        }

        public Task<EnhanceSaveResult> SaveAsync(EnhancePreview preview, CancellationToken ct = default)
        {
            Saved.Add(preview);
            return Task.FromResult(new EnhanceSaveResult(9, 77, Path.Combine(root, "Foto_verbessert.jpg"), null));
        }

        public void Discard(EnhancePreview preview) => Discarded.Add(preview);

        public Task<string?> SuggestFileNameAsync(int fileId, CancellationToken ct = default) => Task.FromResult<string?>("Foto_verbessert.jpg");

        public Task<EnhanceEstimate> EstimateAsync(IReadOnlyList<int> fileIds, EnhanceOptions options, CancellationToken ct = default)
            => Task.FromResult(new EnhanceEstimate(fileIds.Count, TimeSpan.FromSeconds(100), true, EstimateMissing));

        public Task<EnhanceBatchResult> RunBatchAsync(IReadOnlyList<int> fileIds, EnhanceOptions options, CancellationToken ct = default)
            => Task.FromResult(new EnhanceBatchResult(0, 0, 0, 0, false, []));

        public void ReleaseModels()
        {
        }
    }

    private sealed class MemorySettingsStore : IEnhanceSettingsStore
    {
        public List<EnhanceSettings> Saved { get; } = [];

        public Task<EnhanceSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved.LastOrDefault() ?? EnhanceSettings.Default);

        public Task SaveAsync(EnhanceSettings settings, CancellationToken ct = default)
        {
            Saved.Add(settings);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBrush : IEnhanceBrushTarget
    {
        public List<(double X, double Y)> Dots { get; } = [];

        public void BeginStroke()
        {
        }

        public void AddDot(double x, double y) => Dots.Add((x, y));
    }

    private sealed class NoDownload : IModelDownloader, IModelMetadataClient
    {
        public Task DownloadAsync(Uri source, string target, long expectedSize, IProgress<long>? progress, CancellationToken ct)
            => throw new InvalidOperationException("kein Download im Test");

        public Task<string> GetStringAsync(Uri uri, CancellationToken ct) => throw new InvalidOperationException("kein Netz im Test");
    }

    private sealed class BindingErrors : TraceListener
    {
        public BindingErrors()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }

        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
