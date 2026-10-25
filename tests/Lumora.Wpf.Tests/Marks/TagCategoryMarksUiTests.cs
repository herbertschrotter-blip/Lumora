using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Gallery.ViewModels;
using Lumora.Lightbox;
using Lumora.Marks;
using Lumora.Services.Marks;
using Lumora.Services.Tags;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Marks;

/// <summary>
/// Tag- und Kategorie-Symbole (Mockup 16, 0.4.35) in der Oberfläche: Kachel-Marke nur wenn gesetzt, Anzahl ab 2, höchstens 3
/// Farbpunkte, Schalter aus → unsichtbar, Tooltip erst bei Bedarf; Collage mit der echten Vorlage nur beim Darüberfahren;
/// Lightbox-Leiste mit Namen, Menü „+ Tag / Kategorie“ und Schalter/Vollbild; Ordner-Zahlen; Einstellungs-Karte speichert.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class TagCategoryMarksUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly TagInfo Urlaub = new(1, "Urlaub", "#1E88E5", 0);
    private static readonly TagInfo Berge = new(2, "Berge", "#43A047", 1);
    private static readonly TagInfo Familie = new(3, "Familie", "#E53935", 2);
    private static readonly TagInfo Schnee = new(4, "Schnee", "#8E24AA", 3);

    private static GalleryItemCategories Categories(params string[] labels)
    {
        var values = new Dictionary<int, (int, string)>();
        for (var i = 0; i < labels.Length; i++)
        {
            values[i + 1] = (100 + i, labels[i].Split(": ")[1]);
        }

        return new GalleryItemCategories(values, labels);
    }

    [Fact]
    public void Kachel_Marke_nur_wenn_gesetzt_Anzahl_ab_2_hoechstens_3_Punkte()
    {
        TestUiThread.Run(() =>
        {
            var marks = new TileMarksControl();
            marks.Visibility.Should().Be(Visibility.Collapsed, "ohne Tags und Kategorien keine Marke");
            marks.ToolTip.Should().BeNull();

            marks.Tags = [Urlaub];
            marks.Visibility.Should().Be(Visibility.Visible);
            marks.HasTagMark.Should().BeTrue();
            marks.HasCategoryMark.Should().BeFalse();
            marks.TagCountText.Should().BeEmpty("eine einzelne zeigt nur das Symbol");
            marks.VisibleDots.Should().Be(1);

            marks.Tags = [Urlaub, Berge, Familie, Schnee];
            marks.TagCountText.Should().Be("4");
            marks.VisibleDots.Should().Be(MarkTexts.MaxDots);

            marks.Categories = Categories("Anlass: Feier");
            marks.HasCategoryMark.Should().BeTrue();
            marks.CategoryCountText.Should().BeEmpty();
            marks.Categories = Categories("Anlass: Feier", "Projekt: Nord");
            marks.CategoryCountText.Should().Be("2");

            marks.Tags = [];
            marks.HasTagMark.Should().BeFalse();
            marks.Visibility.Should().Be(Visibility.Visible, "Kategorie-Marke allein bleibt");
            return true;
        }, Timeout);
    }

    [Fact]
    public void Schalter_aus_blendet_die_Kachel_Marke_aus()
    {
        TestUiThread.Run(() =>
        {
            var marks = new TileMarksControl { Tags = [Urlaub], Categories = Categories("Anlass: Feier") };
            marks.Visibility.Should().Be(Visibility.Visible);

            marks.ShowMarks = false;
            marks.Visibility.Should().Be(Visibility.Collapsed);
            marks.ToolTip.Should().BeNull();

            marks.ShowMarks = true;
            marks.Visibility.Should().Be(Visibility.Visible);
            return true;
        }, Timeout);
    }

    [Fact]
    public void Kachel_Tooltip_entsteht_erst_bei_Bedarf_und_nennt_die_Namen()
    {
        TestUiThread.Run(() =>
        {
            var marks = new TileMarksControl { Tags = [Urlaub, Berge], Categories = Categories("Anlass: Feier") };
            marks.ToolTip.Should().Be(MarkToolTips.Placeholder, "kein vorgebauter Tooltip-Baum je Kachel");

            var content = marks.BuildToolTipContent();
            var tip = MarkToolTips.Build(content);
            var texts = MarkToolTips.Texts(tip).ToList();
            texts.Should().ContainInOrder("Tags", "Urlaub", "Berge", "Kategorien", "Anlass: Feier");
            MarkToolTips.DotColors(tip).Should().Equal(
                (Color)ColorConverter.ConvertFromString("#1E88E5"),
                (Color)ColorConverter.ConvertFromString("#43A047"));
            return true;
        }, Timeout);
    }

    [Fact]
    public void Collage_zeigt_die_Marken_erst_beim_Darueberfahren_und_folgt_dem_Schalter()
    {
        TestUiThread.Run(() =>
        {
            using var host = new CollageGridHost(6);
            host.Items[0].Tags = [Urlaub, Berge];
            host.Items[1].Categories = Categories("Anlass: Feier");
            host.ShowAndLayout();

            var withTags = MarksOf(host.ContainerOf(0)!);
            var withCategory = MarksOf(host.ContainerOf(1)!);
            var plain = MarksOf(host.ContainerOf(2)!);
            withTags.Visibility.Should().Be(Visibility.Visible);
            withTags.Opacity.Should().Be(0, "in der Collage nur beim Darüberfahren (wie Herz und Sterne)");
            withTags.TagCountText.Should().Be("2");
            withCategory.HasCategoryMark.Should().BeTrue();
            plain.Visibility.Should().Be(Visibility.Collapsed);

            host.Model.ShowTileMarks = false;
            GalleryGridHost.Pump(DispatcherPriority.DataBind);
            withTags.Visibility.Should().Be(Visibility.Collapsed);
            withCategory.Visibility.Should().Be(Visibility.Collapsed);
            return true;
        }, Timeout);
    }

    [Fact]
    public void Galerie_ViewModel_folgt_dem_Schalter_der_Sitzung()
    {
        TestUiThread.Run(() =>
        {
            var state = new MarkSettingsState();
            var gallery = GalleryForMarks.Create(state);
            var changed = new List<string?>();
            ((INotifyPropertyChanged)gallery).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            gallery.ShowTileMarks.Should().BeTrue("Standard an");
            state.ShowOnTiles = false;
            gallery.ShowTileMarks.Should().BeFalse();
            changed.Should().Contain(nameof(GalleryViewModel.ShowTileMarks));
            return true;
        }, Timeout);
    }

    [Fact]
    public void Lightbox_Leiste_zeigt_Namen_Menue_und_weicht_Schalter_und_Vollbild()
    {
        TestUiThread.Run(() =>
        {
            var state = new MarkSettingsState();
            using var vm = new LightboxViewModel(new LibVlcHost(NullLogger<LibVlcHost>.Instance), contextFactory: null!, NullLogger<LightboxViewModel>.Instance, markSettings: state);
            vm.CurrentTags = [Urlaub, Berge];
            vm.CurrentCategoriesText = "Anlass: Feier";
            vm.CurrentCategoryLabels = ["Anlass: Feier"];

            var bar = new LightboxMarksBar { DataContext = vm };
            var window = new Window
            {
                Width = 800, Height = 120, Left = -5000, Top = -5000, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Content = bar,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                GalleryGridHost.Pump(DispatcherPriority.Loaded);

                bar.TagsPart.Visibility.Should().Be(Visibility.Visible);
                bar.CategoriesPart.Visibility.Should().Be(Visibility.Visible);
                bar.TagChips.Items.Count.Should().Be(2);
                bar.CategoryChips.Items.Count.Should().Be(1);
                Texts(bar).Should().Contain(["Urlaub", "Berge", "Anlass: Feier", "+ Tag / Kategorie"]);
                vm.ShowMarksSeparator.Should().BeTrue();
                // „+ Tag / Kategorie“ öffnet das Menü mit den vorhandenen Dialogen (Strg+T, Strg+K)
                bar.AddButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                GalleryGridHost.Pump(DispatcherPriority.DataBind);
                bar.Menu.IsOpen.Should().BeTrue();
                var commands = bar.Menu.Items.OfType<MenuItem>().Select(i => i.Command).ToList();
                commands.Should().HaveCount(2);
                commands[0].Should().BeSameAs(vm.EditTagsCommand);
                commands[1].Should().BeSameAs(vm.EditCategoriesCommand);
                bar.Menu.IsOpen = false;

                vm.CurrentTags = [];
                vm.ShowMarksSeparator.Should().BeFalse("Trenner nur zwischen Tags und Kategorien");
                GalleryGridHost.Pump(DispatcherPriority.DataBind);
                bar.TagsPart.Visibility.Should().Be(Visibility.Collapsed);
            }
            finally
            {
                window.Close();
            }

            vm.ShowMarksBar.Should().BeTrue();
            vm.ShowTitleMarks.Should().BeFalse("Titelleiste zeigt sie nur bei abgeschalteter Leiste");
            vm.IsFullscreen = true;
            vm.ShowMarksBar.Should().BeFalse("im Vollbild keine Leiste");
            vm.IsFullscreen = false;
            state.ShowInLightbox = false;
            vm.ShowMarksBar.Should().BeFalse();
            vm.ShowTitleMarks.Should().BeTrue();
            return true;
        }, Timeout);
    }

    [Fact]
    public void Lightbox_Fenster_bindet_die_Leiste_an_den_Schalter()
    {
        TestUiThread.Run(() =>
        {
            var state = new MarkSettingsState();
            var vm = new LightboxViewModel(new LibVlcHost(NullLogger<LibVlcHost>.Instance), contextFactory: null!, NullLogger<LightboxViewModel>.Instance, markSettings: state);
            try
            {
                var window = new LightboxWindow(vm, NullLogger<LightboxWindow>.Instance);
                window.MarksBar.Visibility.Should().Be(Visibility.Visible);
                state.ShowInLightbox = false;
                GalleryGridHost.Pump(DispatcherPriority.DataBind);
                window.MarksBar.Visibility.Should().Be(Visibility.Collapsed);
            }
            finally
            {
                vm.Dispose();
            }

            return true;
        }, Timeout);
    }

    [Fact]
    public void Ordner_Zahlen_mit_Tooltip_n_von_m_Fotos()
    {
        TestUiThread.Run(() =>
        {
            var names = new FolderMarkNames(
                new Dictionary<int, TagInfo> { [Urlaub.Id] = Urlaub, [Berge.Id] = Berge },
                new Dictionary<int, string> { [100] = "Anlass: Feier" });
            var summary = new FolderMarkSummary(
                new FolderMarkCount(86, 40, [new MarkUsage(Urlaub.Id, 80), new MarkUsage(Berge.Id, 12), new MarkUsage(999, 1)], [new MarkUsage(100, 40)]),
                212,
                names);
            var control = new FolderMarksControl();
            control.Visibility.Should().Be(Visibility.Collapsed);

            control.Marks = summary;
            control.Visibility.Should().Be(Visibility.Visible);
            control.TagCountText.Should().Be("86");
            control.CategoryCountText.Should().Be("40");
            control.ToolTip.Should().Be(MarkToolTips.Placeholder);
            var texts = MarkToolTips.Texts(MarkToolTips.Build(summary.BuildToolTipContent())).ToList();
            texts.Should().ContainInOrder("Tags · 86 von 212 Fotos", "Urlaub", "Berge", "Kategorien · 40 von 212 Fotos", "Anlass: Feier");

            control.Marks = new FolderMarkSummary(new FolderMarkCount(0, 3, [], []), 3, names);
            control.TagCountText.Should().BeEmpty("ohne Tags nur die Kategorie-Zahl");
            control.CategoryCountText.Should().Be("3");

            control.Marks = null;
            control.Visibility.Should().Be(Visibility.Collapsed);
            return true;
        }, Timeout);
    }

    [Fact]
    public async Task Einstellungs_Karte_schaltet_und_speichert()
    {
        var store = new RecordingStore(new MarkSettings(true, true, true));
        var state = TestUiThread.Run(() => new MarkSettingsState(store), Timeout);
        await state.Loaded.WaitAsync(Timeout);
        TestUiThread.Run(() =>
        {
            var card = new SettingsMarksSection { DataContext = state };
            var window = new Window
            {
                Width = 900, Height = 400, Left = -5000, Top = -5000, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Content = card,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                GalleryGridHost.Pump(DispatcherPriority.Loaded);
                card.TilesSwitch.IsChecked.Should().Be(true);
                card.LightboxSwitch.IsChecked.Should().Be(true);
                card.FolderTreeSwitch.IsChecked = false;
                card.TilesSwitch.IsChecked = false;
                state.ShowInFolderTree.Should().BeFalse();
                state.ShowOnTiles.Should().BeFalse();
                state.ShowInLightbox.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }

            return true;
        }, Timeout);

        await TestUiThread.Run(() => state.LastSave, Timeout).WaitAsync(Timeout);
        store.Saved.Last().Should().Be(new MarkSettings(false, true, false));
    }

    [Fact]
    public async Task Gespeicherte_Schalter_werden_beim_Start_geladen()
    {
        var store = new RecordingStore(new MarkSettings(false, true, false));
        var state = TestUiThread.Run(() => new MarkSettingsState(store), Timeout);
        await state.Loaded.WaitAsync(Timeout);
        TestUiThread.Run(() => state.Current, Timeout).Should().Be(new MarkSettings(false, true, false));
        store.Saved.Should().BeEmpty("Laden speichert nicht zurück");
    }

    [Fact]
    public async Task Umlegen_vor_dem_Ende_des_Ladens_wird_nicht_ueberschrieben()
    {
        var store = new RecordingStore(new MarkSettings(true, true, true)) { Gate = new TaskCompletionSource() };
        var state = TestUiThread.Run(() => new MarkSettingsState(store), Timeout);
        TestUiThread.Run(() => state.ShowInFolderTree = false, Timeout);
        store.Gate.SetResult();
        await state.Loaded.WaitAsync(Timeout);

        TestUiThread.Run(() => state.ShowInFolderTree, Timeout).Should().BeFalse("der Klick des Benutzers gewinnt gegen das späte Laden");
        await TestUiThread.Run(() => state.LastSave, Timeout).WaitAsync(Timeout);
        store.Saved.Last().ShowInFolderTree.Should().BeFalse();
    }

    private static TileMarksControl MarksOf(DependencyObject container)
        => FindVisual<TileMarksControl>(container) ?? throw new InvalidOperationException("Keine Marke in der Vorlage");

    private static T? FindVisual<T>(DependencyObject root)
        where T : DependencyObject
        => GalleryGridHost.FindVisualChild<T>(root);

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Text: { Length: > 0 } text })
            {
                yield return text;
            }

            foreach (var nested in Texts(child))
            {
                yield return nested;
            }
        }
    }

    private sealed class RecordingStore(MarkSettings initial) : IMarkSettingsStore
    {
        public List<MarkSettings> Saved { get; } = new();

        /// <summary>Hält das Laden auf, bis der Test es freigibt (null = sofort).</summary>
        public TaskCompletionSource? Gate { get; init; }

        public async Task<MarkSettings> LoadAsync(CancellationToken ct = default)
        {
            if (Gate is not null)
            {
                await Gate.Task.ConfigureAwait(false);
            }

            return initial;
        }

        public Task SaveAsync(MarkSettings settings, CancellationToken ct = default)
        {
            lock (Saved)
            {
                Saved.Add(settings);
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>Galerie-ViewModel ohne Datenbank — nur für den Schalter (keine Abfrage, kein Laden).</summary>
internal static class GalleryForMarks
{
    public static GalleryViewModel Create(MarkSettingsState state)
        => new(
            null!,
            null!,
            NullService.Create<Lumora.Services.Thumbnails.IThumbnailService>(),
            NullService.Create<Lumora.Services.Catalog.ICatalogUpdateService>(),
            NullService.Create<Lumora.Services.Operations.IFileOperationService>(),
            NullService.Create<Lumora.Services.Operations.IArchiveMoveService>(),
            NullService.Create<Lumora.Lightbox.ILightboxService>(),
            NullService.Create<Lumora.Gallery.Services.IGalleryDialogService>(),
            NullService.Create<Lumora.Gallery.Services.IFolderPickerService>(),
            NullService.Create<Lumora.Gallery.Archives.IArchiveExtractDialogService>(),
            NullService.Create<ITagService>(),
            NullService.Create<Lumora.Gallery.Tags.ITagPickerService>(),
            NullService.Create<Lumora.Services.Categories.ICategoryService>(),
            NullService.Create<Lumora.Gallery.Categories.ICategoryPickerService>(),
            NullService.Create<Lumora.Gallery.ListView.IGalleryViewSettingsStore>(),
            NullLogger<GalleryViewModel>.Instance,
            libraryScope: NullService.Create<Lumora.Services.Library.ILibraryScope>(),
            markSettings: state);
}
