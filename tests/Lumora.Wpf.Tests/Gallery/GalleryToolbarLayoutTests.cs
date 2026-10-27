using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumora.Gallery.Controls;
using Lumora.Gallery.Data;
using Lumora.Gallery.ListView;
using Lumora.Gallery.ViewModels;
using Lumora.Wpf.Tests.Fixtures;
using Wpf.Ui.Controls;
using Xunit.Abstractions;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Galerie-Kopfzeile (0.2.13, neu gebaut 0.2.43 nach Mockup 06): Die Tests rendern das echte <see cref="GalleryToolbar"/>
/// in einem Fenster bei −5000/−5000 mit der Inhaltsbreite der Galerie-Seite (Fensterbreite − 220 px Navigationsbereich −
/// 2 × 16 px Rand) und prüfen: nichts ragt über den Rand, die Suche hat ihre Mindestbreite und Platz für den Platzhalter,
/// bei 1280 DIP bleiben Titelzeile, Zeile „Datei“ und Sortierleiste einzeilig — auch mit allen Datei-Filtern aktiv.
/// Die PNGs (hell und dunkel) landen unter <c>bin/…/toolbar-shots</c>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GalleryToolbarLayoutTests(ITestOutputHelper output)
{
    private const double NavPaneWidth = 220;
    private const double PageMargin = 16;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(1280)]
    [InlineData(1920)]
    [InlineData(2560)]
    public void Kopfzeile_bleibt_ab_1280_einzeilig(int windowWidth)
    {
        var result = Render(windowWidth, new ToolbarStub(), $"galerie-kopfzeile-{windowWidth}");

        result.Overflow.Should().BeEmpty("kein Element darf über den rechten Rand der Kopfzeile ragen");
        result.SearchAreaWidth.Should().BeGreaterThanOrEqualTo(result.SearchAreaMinWidth - 0.5);
        result.SearchBoxWidth.Should().BeGreaterThanOrEqualTo(result.PlaceholderNeeded, "der Platzhalter muss vollständig sichtbar sein");
        result.HeaderLines.Should().Be(1, "Titel und Suche stehen in einer Zeile");
        result.FileLines.Should().Be(1, "die Zeile „Datei“ bricht bei 1280 DIP nicht um");
        result.ContentLines.Should().Be(1);
        result.ViewLines.Should().Be(1, "die Sortierleiste bricht bei 1280 DIP nicht um");
        result.ActiveRowVisible.Should().BeFalse("ohne Filter keine Zeile „Es gilt“");
    }

    [Fact]
    public void Mit_Touren_bricht_die_Zeile_Inhalt_bei_1280_sauber_um()
    {
        // Mit Tags, Personen, Motiven, Alben, Kategorien, Ort, Doppelte und Lieblinge passt „Tour“ nicht mehr dazu (Mockup 24)
        var result = Render(1280, new ToolbarStub { ShowTourFilter = true }, "galerie-kopfzeile-1280-mit-touren");

        result.Overflow.Should().BeEmpty();
        result.HeaderLines.Should().Be(1);
        result.FileLines.Should().Be(1);
        result.ContentLines.Should().Be(2);
        result.ViewLines.Should().Be(1);
    }

    [Fact]
    public void Kopfzeile_1280_mit_allen_Filtern_und_Ordner_Chip_bleibt_einzeilig()
    {
        var stub = ToolbarStub.AllActive();

        var result = Render(1280, stub, "galerie-kopfzeile-1280-filter-aktiv");

        result.Overflow.Should().BeEmpty();
        result.HeaderLines.Should().Be(1);
        result.FileLines.Should().Be(1, "auch mit „Bewertung: ab 3 ★“, „Ablage: Eingang“ und Zähler");
        result.ViewLines.Should().Be(1);
        result.ActiveRowVisible.Should().BeTrue();
    }

    [Fact]
    public void Kopfzeile_1280_dunkel_rendert_mit_aktiven_Filtern()
    {
        var result = Render(1280, ToolbarStub.AllActive(), "galerie-kopfzeile-1280-filter-aktiv-dunkel", dark: true);

        result.Overflow.Should().BeEmpty();
        result.FileLines.Should().Be(1);
    }

    [Fact]
    public void Kopfzeile_1024_bricht_sauber_um()
    {
        var result = Render(1024, ToolbarStub.AllActive(), "galerie-kopfzeile-1024");

        result.Overflow.Should().BeEmpty();
        result.SearchAreaWidth.Should().BeGreaterThanOrEqualTo(result.SearchAreaMinWidth - 0.5);
    }

    [Fact]
    public void Kopfzeile_rechte_Haelfte_der_Ordner_Seite_bleibt_bedienbar()
    {
        // Die GalleryView steckt auch rechts in der Ordner-Seite: dort ist sie deutlich schmaler.
        var result = Render(1024, ToolbarStub.AllActive(), "galerie-kopfzeile-ordnerseite", contentWidthOverride: 560);

        result.Overflow.Should().BeEmpty();
        result.SearchAreaWidth.Should().BeGreaterThanOrEqualTo(result.SearchAreaMinWidth - 0.5);
        result.HeaderLines.Should().Be(2, "die Suche rutscht unter den Titel");
        result.FileLines.Should().BeGreaterThan(1);
    }

    // ----- ToolbarFlowPanel direkt -----

    [Fact]
    public void FlowPanel_breit_eine_Zeile_Fuellelement_bis_MaxWidth_rechtsbuendig()
    {
        TestUiThread.Run(
            () =>
            {
                var (panel, a, b, fill, c) = CreatePanel();
                Layout(panel, 1000);

                panel.LineCount.Should().Be(1);
                Slot(a).X.Should().Be(0);
                Slot(b).X.Should().Be(108);
                Slot(fill).Width.Should().Be(300);
                Slot(c).Right.Should().BeApproximately(1000, 0.5, "Füll-Element an MaxWidth schiebt den Rest nach rechts");
                Slot(fill).X.Should().BeApproximately(1000 - 50 - 8 - 300, 0.5);
                return 0;
            },
            Timeout);
    }

    [Fact]
    public void FlowPanel_schmal_Fuellelement_bricht_um_und_beginnt_linksbuendig()
    {
        TestUiThread.Run(
            () =>
            {
                var (panel, a, b, fill, c) = CreatePanel();
                Layout(panel, 400);

                panel.LineCount.Should().Be(2);
                Slot(a).Y.Should().Be(Slot(b).Y);
                Slot(fill).X.Should().Be(0, "das umgebrochene Füll-Element beginnt die Zeile");
                Slot(fill).Y.Should().BeGreaterThan(Slot(a).Y);
                Slot(fill).Width.Should().Be(300, "es wächst bis MaxWidth");
                Slot(c).X.Should().Be(308);
                return 0;
            },
            Timeout);
    }

    [Fact]
    public void FlowPanel_Abstandhalter_ohne_MaxWidth_schiebt_rechte_Gruppe_an_den_Rand_und_ignoriert_Collapsed()
    {
        TestUiThread.Run(
            () =>
            {
                var panel = new ToolbarFlowPanel { Spacing = 8 };
                var left = Box(120, 30);
                var hidden = Box(500, 30);
                hidden.Visibility = Visibility.Collapsed;
                var spacer = new Border { MinWidth = 0, Height = 1 };
                ToolbarFlowPanel.SetFill(spacer, true);
                var right = Box(200, 32);
                panel.Children.Add(left);
                panel.Children.Add(hidden);
                panel.Children.Add(spacer);
                panel.Children.Add(right);

                Layout(panel, 800);

                panel.LineCount.Should().Be(1);
                Slot(right).Right.Should().BeApproximately(800, 0.5);
                Slot(left).Y.Should().Be(1, "Elemente werden in der Zeile (Höhe 32) vertikal zentriert");

                Layout(panel, 300);
                panel.LineCount.Should().Be(2, "die rechte Gruppe passt nicht mehr und bricht um");
                Slot(right).X.Should().Be(0);
                return 0;
            },
            Timeout);
    }

    [Fact]
    public void FlowPanel_Titel_links_Suche_als_letztes_Fuellelement_rechtsbuendig()
    {
        TestUiThread.Run(
            () =>
            {
                var panel = new ToolbarFlowPanel { Spacing = 16 };
                var title = Box(300, 50);
                var search = new Border { MinWidth = 300, MaxWidth = 380, Height = 34 };
                ToolbarFlowPanel.SetFill(search, true);
                panel.Children.Add(title);
                panel.Children.Add(search);

                Layout(panel, 1000);
                panel.LineCount.Should().Be(1);
                Slot(search).Width.Should().Be(380);
                Slot(search).Right.Should().BeApproximately(1000, 0.5, "die Suche steht rechtsbündig");

                Layout(panel, 560);
                panel.LineCount.Should().Be(2);
                Slot(search).X.Should().Be(0);
                return 0;
            },
            Timeout);
    }

    // ----- Hilfen -----

    private static (ToolbarFlowPanel Panel, Border A, Border B, Border Fill, Border C) CreatePanel()
    {
        var panel = new ToolbarFlowPanel { Spacing = 8, LineSpacing = 6 };
        var a = Box(100, 30);
        var b = Box(100, 30);
        var fill = new Border { MinWidth = 200, MaxWidth = 300, Height = 30 };
        ToolbarFlowPanel.SetFill(fill, true);
        var c = Box(50, 30);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(fill);
        panel.Children.Add(c);
        return (panel, a, b, fill, c);
    }

    private static Border Box(double width, double height) => new() { Width = width, Height = height };

    private static void Layout(ToolbarFlowPanel panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
    }

    private static Rect Slot(FrameworkElement element) => LayoutInformation.GetLayoutSlot(element);

    private RenderResult Render(int windowWidth, ToolbarStub stub, string shotName, double? contentWidthOverride = null, bool dark = false)
    {
        var result = TestUiThread.Run(
            () =>
            {
                var previousTheme = global::Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
                global::Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                    dark ? global::Wpf.Ui.Appearance.ApplicationTheme.Dark : global::Wpf.Ui.Appearance.ApplicationTheme.Light);

                try
                {
                    var contentWidth = contentWidthOverride ?? windowWidth - NavPaneWidth - (2 * PageMargin);
                    var toolbar = new GalleryToolbar { DataContext = stub, PageTitle = "Galerie" };
                    var page = new Border { Width = contentWidth, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(PageMargin, 8, PageMargin, 12), Child = toolbar };

                    var pane = new Border { Width = NavPaneWidth, Background = Brush("ControlFillColorDefaultBrush") };
                    pane.Child = new System.Windows.Controls.TextBlock
                    {
                        Text = "Navigation (220 px)",
                        Margin = new Thickness(16, 12, 0, 0),
                        Foreground = Brush("TextFillColorTertiaryBrush"),
                    };

                    var root = new DockPanel { Background = Brush("ApplicationBackgroundBrush"), LastChildFill = false };
                    if (contentWidthOverride is null)
                    {
                        DockPanel.SetDock(pane, Dock.Left);
                        root.Children.Add(pane);
                    }

                    DockPanel.SetDock(page, Dock.Left);
                    root.Children.Add(page);

                    var window = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -5000,
                        Top = -5000,
                        SizeToContent = SizeToContent.WidthAndHeight,
                        Content = root,
                    };
                    window.Show();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    try
                    {
                        var measured = Measure(toolbar);
                        SaveShot(root, toolbar.ActualHeight + page.Margin.Top + page.Margin.Bottom, shotName);
                        return measured;
                    }
                    finally
                    {
                        window.Close();
                    }
                }
                finally
                {
                    if (previousTheme != global::Wpf.Ui.Appearance.ApplicationTheme.Unknown)
                    {
                        global::Wpf.Ui.Appearance.ApplicationThemeManager.Apply(previousTheme);
                    }
                }
            },
            Timeout);

        output.WriteLine(
            $"{shotName}: Inhalt {result.ToolbarWidth:N0} px, Zeilen Titel/Datei/Inhalt/Leiste {result.HeaderLines}/{result.FileLines}/{result.ContentLines}/{result.ViewLines}, " +
            $"Suche {result.SearchAreaWidth:N0} px (min {result.SearchAreaMinWidth:N0}), Eingabefeld {result.SearchBoxWidth:N0} px (Platzhalter braucht {result.PlaceholderNeeded:N0}), " +
            $"Datei-Zeile belegt {result.FileRowUsed:N0} px");
        return result;
    }

    private static RenderResult Measure(GalleryToolbar toolbar)
    {
        // Filterkarte und Suche sind seit 0.4.1 eigene Steuerelemente (ADR-0055) — ihre Namen liegen in deren Namensraum
        var card = (GalleryFilterCard)toolbar.FindName("FilterCard");
        var headerRow = (ToolbarFlowPanel)toolbar.FindName("HeaderRow");
        var fileRow = (ToolbarFlowPanel)card.FindName("FileRow");
        var contentRow = (ToolbarFlowPanel)card.FindName("ContentRow");
        var viewRow = (ToolbarFlowPanel)toolbar.FindName("ViewRow");
        var activeRow = (FrameworkElement)card.FindName("ActiveRow");
        var searchArea = (GallerySearchBox)toolbar.FindName("SearchArea");
        var searchBox = searchArea.Box;

        var overflow = new List<string>();
        var fileRowUsed = 0.0;
        foreach (var panel in new[] { headerRow, fileRow, contentRow, viewRow })
        {
            foreach (FrameworkElement child in panel.Children)
            {
                if (child.Visibility != Visibility.Visible)
                {
                    continue;
                }

                var bounds = child.TransformToAncestor(toolbar).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
                if (bounds.Right > toolbar.ActualWidth + 0.5)
                {
                    overflow.Add($"{child.GetType().Name} {child.Name}: rechts {bounds.Right:N1} > {toolbar.ActualWidth:N1}");
                }

                if (ReferenceEquals(panel, fileRow) && !ToolbarFlowPanel.GetFill(child))
                {
                    fileRowUsed += child.ActualWidth;
                }
            }
        }

        // Platzhalter + Suchsymbol links (ca. 34 px) + Innenabstände der TextBox (links und rechts, rechts mit Tastenhinweis)
        var placeholder = new FormattedText(
            searchBox.PlaceholderText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(searchBox.FontFamily, searchBox.FontStyle, searchBox.FontWeight, searchBox.FontStretch),
            searchBox.FontSize,
            Brushes.White,
            1.0);

        return new RenderResult(
            toolbar.ActualWidth,
            headerRow.LineCount,
            fileRow.LineCount,
            contentRow.LineCount,
            viewRow.LineCount,
            activeRow.IsVisible,
            searchArea.ActualWidth,
            searchArea.MinWidth,
            searchBox.ActualWidth,
            placeholder.WidthIncludingTrailingWhitespace + 34 + searchBox.Padding.Left + searchBox.Padding.Right,
            fileRowUsed,
            overflow);
    }

    private static void SaveShot(FrameworkElement root, double visibleHeight, string name)
    {
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(Math.Min(root.ActualHeight, visibleHeight)); // nur die Kopfzeile, nicht die Fenster-Mindesthöhe
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var dir = Path.Combine(AppContext.BaseDirectory, "toolbar-shots");
        Directory.CreateDirectory(dir);
        using var stream = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(stream);
    }

    private static Brush Brush(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;

    private sealed record RenderResult(
        double ToolbarWidth,
        int HeaderLines,
        int FileLines,
        int ContentLines,
        int ViewLines,
        bool ActiveRowVisible,
        double SearchAreaWidth,
        double SearchAreaMinWidth,
        double SearchBoxWidth,
        double PlaceholderNeeded,
        double FileRowUsed,
        List<string> Overflow);

    /// <summary>Datenquelle mit den Eigenschaften, an die die Kopfzeile bindet (wie GalleryViewModel).</summary>
    private sealed class ToolbarStub
    {
        private static readonly ICommand Noop = new NoopCommand();

        public static ToolbarStub AllActive()
        {
            var stub = new ToolbarStub
            {
                MediaFilter = GalleryMediaKind.Images,
                FavoritesOnly = true,
                MinRating = 3,
                LocationFilter = GalleryLocation.Inbox,
                HasFolderFilter = true,
                FolderChipText = @"Ordner: 2026-07 Urlaub Italien (mit Unterordnern)",
                TagFilterValueText = "Urlaub",
                HasTagFilter = true,
                PersonFilterValueText = "ohne Person",
                HasPersonFilter = true,
                SearchTerm = "strand",
                TotalCount = 342,
                ResultCountText = " von 1.248 Dateien",
            };
            stub.ActiveConditions.Add(new GalleryFilterCondition(GalleryFilterConditionKind.Search, "Suche „strand“", IsFirst: true));
            stub.ActiveConditions.Add(new GalleryFilterCondition(GalleryFilterConditionKind.Media, "Bilder"));
            stub.ActiveConditions.Add(new GalleryFilterCondition(GalleryFilterConditionKind.Favorites, "Favoriten"));
            stub.ActiveConditions.Add(new GalleryFilterCondition(GalleryFilterConditionKind.Rating, "ab 3 Sternen"));
            stub.ActiveConditions.Add(new GalleryFilterCondition(GalleryFilterConditionKind.Location, "noch im Eingang"));
            stub.ActiveConditions.Add(new GalleryFilterCondition(GalleryFilterConditionKind.Tags, "Tag „Urlaub“"));
            stub.ActiveConditions.Add(new GalleryFilterCondition(GalleryFilterConditionKind.Persons, "ohne benannte Person"));
            return stub;
        }

        // Titel, Trefferzeile, Suche
        public string LibraryNameDisplay => "Test  ·  C:\\Fotos";

        public int TotalCount { get; set; } = 1248;

        public string ResultCountText { get; set; } = " Dateien";

        public bool HasFolderFilter { get; set; }

        public string FolderChipText { get; set; } = string.Empty;

        public string? SearchTerm { get; set; }

        // Datei-Filter
        public GalleryMediaKind MediaFilter { get; set; } = GalleryMediaKind.All;

        public bool FavoritesOnly { get; set; }

        public int MinRating { get; set; }

        public bool HasRatingFilter => MinRating > 0;

        public string RatingFilterText => MinRating == 0 ? "egal" : $"ab {MinRating} Sternen";

        public string RatingChipText => MinRating == 0 ? "egal" : $"ab {MinRating} ★";

        public IReadOnlyList<GalleryRatingOption> RatingOptions { get; } = [new(0, "egal"), new(3, "ab 3 Sternen")];

        public GalleryLocation LocationFilter { get; set; } = GalleryLocation.Anywhere;

        public bool HasLocationFilter => LocationFilter != GalleryLocation.Anywhere;

        public string LocationFilterText => LocationFilter switch
        {
            GalleryLocation.Inbox => "Noch im Eingang",
            GalleryLocation.Library => "In der Bibliothek",
            _ => "Überall",
        };

        public string LocationChipText => LocationFilter switch
        {
            GalleryLocation.Inbox => "Eingang",
            GalleryLocation.Library => "Bibliothek",
            _ => "Überall",
        };

        public IReadOnlyList<GalleryLocationOption> LocationOptions { get; } = [new(GalleryLocation.Anywhere, "Überall", "-")];

        public ObservableCollection<GalleryFilterCondition> ActiveConditions { get; } = [];

        public int ActiveFilterCount => ActiveConditions.Count;

        public bool HasActiveFilters => ActiveConditions.Count > 0;

        // Inhalt
        public string TagFilterValueText { get; set; } = "alle";

        public string TagFilterText => "Tags";

        public bool HasTagFilter { get; set; }

        public IReadOnlyList<object> TagFilterEntries { get; } = [];

        public GalleryTagMode TagFilterMode { get; set; } = GalleryTagMode.Any;

        public bool HasNoTags => true;

        public bool ShowPersonFilter => true;

        /// <summary>Chip „Tour“ (ADR-0073 Stufe 7) — erst sichtbar, wenn es Touren gibt.</summary>
        public bool ShowTourFilter { get; set; }

        public string TourFilterChipText => "Tour";

        public bool HasTourFilter => false;

        public string PersonFilterValueText { get; set; } = "alle";

        public string PersonFilterText => "Personen";

        public bool HasPersonFilter { get; set; }

        public bool HasPersons => false;

        public IReadOnlyList<object> PersonFilterEntries { get; } = [];

        public GalleryPersonMode PersonFilterMode { get; set; } = GalleryPersonMode.Any;

        public bool PersonFilterIncludeSuggestions { get; set; }

        public bool HasCategories => true;

        public string CategoryFilterValueText => "alle";

        public string CategoryFilterText => "Kategorien";

        public bool HasCategoryFilter => false;

        public IReadOnlyList<object> CategoryFilterGroups { get; } = [];

        // Sortier- und Ansichtsleiste
        public IReadOnlyList<GallerySortFieldOption> SortFields { get; } =
        [
            new(GallerySortField.Modified, "Änderungsdatum"),
            new(GallerySortField.Name, "Name"),
            new(GallerySortField.Size, "Größe"),
        ];

        public GallerySortFieldOption? SelectedSortField { get; set; }

        public string SortDirectionText => "Neueste zuerst";

        public SymbolRegular SortDirectionSymbol => SymbolRegular.ArrowSortDown24;

        public IReadOnlyList<GalleryViewModeOption> ViewModeOptions { get; } =
        [
            new(GalleryViewMode.Grid, "Kacheln", SymbolRegular.Grid24, "Kacheln"),
            new(GalleryViewMode.Collage, "Collage", SymbolRegular.Board24, "Collage"),
            new(GalleryViewMode.List, "Liste", SymbolRegular.AppsListDetail24, "Liste"),
        ];

        public GalleryViewMode ViewMode { get; set; } = GalleryViewMode.Grid;

        public bool IsGridView => ViewMode == GalleryViewMode.Grid;

        public bool IsTileSizeAdjustable => ViewMode is GalleryViewMode.Grid or GalleryViewMode.Collage;

        public int TileSize { get; set; } = GalleryViewModel.DefaultTileSize;

        public int MinTileSizeValue => GalleryViewModel.MinTileSize;

        public int MaxTileSizeValue => GalleryViewModel.MaxTileSize;

        public int TileSizeStepValue => GalleryViewModel.TileSizeStep;

        public bool HasLibrary => true;

        public ICommand SearchCommand => Noop;

        public ICommand SelectAllCommand => Noop;

        public ICommand ClearSearchCommand => Noop;

        public ICommand ClearFolderFilterCommand => Noop;

        public ICommand UndoCommand => Noop;

        public ICommand ReloadCommand => Noop;

        public ICommand ResetFiltersCommand => Noop;

        public ICommand RemoveFilterConditionCommand => Noop;

        public ICommand ToggleSortDirectionCommand => Noop;

        public ICommand ApplyTagFilterCommand => Noop;

        public ICommand ClearTagFilterCommand => Noop;

        public ICommand ApplyPersonFilterCommand => Noop;

        public ICommand ClearPersonFilterCommand => Noop;

        public ICommand ApplyCategoryFilterCommand => Noop;

        public ICommand ClearCategoryFilterCommand => Noop;

        public ToolbarStub()
        {
            SelectedSortField = SortFields[0];
        }

        private sealed class NoopCommand : ICommand
        {
            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter)
            {
            }
        }
    }
}
