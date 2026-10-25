using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using Lumora.Gallery.Controls;
using Lumora.Gallery.Data;
using Lumora.Gallery.ListView;
using Lumora.Gallery.ViewModels;
using Lumora.Gallery.Views;

namespace Lumora.Wpf.Tests.Fixtures;

/// <summary>
/// Echtes Fenster mit einer <see cref="GalleryListBox"/>, die wie die GalleryView je <see cref="GalleryViewMode"/> über
/// denselben Helfer (<see cref="GalleryView.ApplyListTemplates"/>) Panel, Vorlage und Container-Stil wechselt: Kacheln
/// (<see cref="VirtualizingTilePanel"/>, schlanke Kachelvorlage) bzw. Collage (<see cref="JustifiedTilePanel"/> mit der
/// ECHTEN Vorlage aus
/// <c>GalleryCollageTemplates.xaml</c>). Items mit gemischten Seitenverhältnissen. Nur auf dem Test-UI-Thread verwenden.
/// </summary>
internal sealed class CollageGridHost : IDisposable
{
    public const double Spacing = 6;

    private const string TileTemplateXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid Background="Transparent">
            <Grid.RowDefinitions>
              <RowDefinition Height="*" />
              <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <Image Source="{Binding Thumbnail}" Stretch="UniformToFill" />
            <TextBlock Grid.Row="1" Text="{Binding Name}" FontSize="11" />
          </Grid>
        </DataTemplate>
        """;

    private const string TileContainerXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
          <Setter Property="IsSelected" Value="{Binding IsSelected, Mode=TwoWay}" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Grid Margin="6"><ContentPresenter /></Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    /// <summary>Wie CollageContainerStyle der GalleryView (ohne EventSetter): Rand 3 = halbe Fuge, Rahmen über dem Bild.</summary>
    private const string CollageContainerXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="ListBoxItem">
          <Setter Property="IsSelected" Value="{Binding IsSelected, Mode=TwoWay}" />
          <Setter Property="Margin" Value="0" />
          <Setter Property="Padding" Value="0" />
          <Setter Property="HorizontalContentAlignment" Value="Stretch" />
          <Setter Property="VerticalContentAlignment" Value="Stretch" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Grid Margin="3">
                  <Border CornerRadius="4" ClipToBounds="True"><ContentPresenter /></Border>
                  <Border x:Name="Frame" BorderThickness="3" BorderBrush="Transparent" IsHitTestVisible="False" />
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="Frame" Property="BorderBrush" Value="DodgerBlue" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    public CollageGridHost(int itemCount, double width = 900, double height = 600, GalleryViewMode mode = GalleryViewMode.Collage)
    {
        var aspects = MixedSizes();
        Items = new ObservableCollection<GalleryItem>(Enumerable.Range(0, itemCount).Select(i => CreateItem(i, aspects[i % aspects.Length])));
        Model = new HostModel { ViewMode = mode };

        var templates = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Lumora;component/Gallery/Views/GalleryCollageTemplates.xaml", UriKind.Absolute),
        };
        CollageTemplate = (DataTemplate)templates["CollageTemplate"];

        var tilePanel = new FrameworkElementFactory(typeof(VirtualizingTilePanel));
        tilePanel.SetBinding(VirtualizingTilePanel.ItemWidthProperty, new Binding(nameof(HostModel.TileWidth)));
        tilePanel.SetBinding(VirtualizingTilePanel.ItemHeightProperty, new Binding(nameof(HostModel.TileHeight)));
        var collagePanel = new FrameworkElementFactory(typeof(JustifiedTilePanel));
        collagePanel.SetBinding(JustifiedTilePanel.TargetRowHeightProperty, new Binding(nameof(HostModel.TileSize)));
        collagePanel.SetBinding(JustifiedTilePanel.LayoutVersionProperty, new Binding(nameof(HostModel.CollageLayoutVersion)));
        collagePanel.SetValue(JustifiedTilePanel.ItemSpacingProperty, Spacing);

        var tile = ((DataTemplate)XamlReader.Parse(TileTemplateXaml), (Style)XamlReader.Parse(TileContainerXaml), new ItemsPanelTemplate(tilePanel));
        var collage = (CollageTemplate, (Style)XamlReader.Parse(CollageContainerXaml), new ItemsPanelTemplate(collagePanel));

        ListBox = new GalleryListBox
        {
            DataContext = Model,
            SelectionMode = SelectionMode.Extended,
            Padding = new Thickness(8),
            BorderThickness = new Thickness(0),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(ListBox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(ListBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(ListBox, true);
        VirtualizingPanel.SetIsVirtualizing(ListBox, true);
        VirtualizingPanel.SetVirtualizationMode(ListBox, VirtualizationMode.Recycling);

        // Wie GalleryView.ApplyGalleryViewMode: lokale Werte je Ansicht über denselben Helfer.
        void Apply()
        {
            var (template, containerStyle, panel) = Model.ViewMode == GalleryViewMode.Collage ? collage : tile;
            GalleryView.ApplyListTemplates(ListBox, template, containerStyle, panel);
        }

        Apply();
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HostModel.ViewMode))
            {
                Apply();
            }
        };

        Window = new Window
        {
            Title = "Lumora.Wpf.Tests – Collage",
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -5000,
            Top = -5000,
            Content = ListBox,
        };
    }

    public ObservableCollection<GalleryItem> Items { get; }

    public HostModel Model { get; }

    public GalleryListBox ListBox { get; }

    public Window Window { get; }

    public DataTemplate CollageTemplate { get; }

    public JustifiedTilePanel Panel => GalleryGridHost.FindVisualChild<JustifiedTilePanel>(ListBox)
        ?? throw new InvalidOperationException("JustifiedTilePanel nicht im Visual-Baum — Ansicht nicht Collage?");

    public void ShowAndLayout()
    {
        Window.Show();
        ListBox.ItemsSource = Items;
        Window.UpdateLayout();
        GalleryGridHost.Pump(DispatcherPriority.Loaded);
    }

    public ListBoxItem? ContainerOf(int index) => ListBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;

    /// <summary>Gemischte Bildmaße (nach EXIF-Drehung) wie in einer Handy-Bibliothek, inklusive Panorama und Bildschirmfoto.</summary>
    public static (int Width, int Height)[] MixedSizes() =>
    [
        (4000, 3000), (3000, 4000), (1920, 1080), (1080, 1080), (2000, 3000), (8000, 2000), (4032, 3024), (1170, 2532),
        (3000, 2000), (3024, 4032), (0, 0), (2400, 1600),
    ];

    public static GalleryItem CreateItem(int index, (int Width, int Height) size)
        => new(new GalleryFileDto(
            Id: index + 1,
            FolderId: 1,
            Name: $"IMG_{index:0000}.jpg",
            Extension: ".jpg",
            Size: 1_234_567 + index,
            ModifiedAtUnix: 1_700_000_000 + index,
            MediaType: 0,
            IsFavorite: false,
            Rating: null,
            Missing: false,
            FolderPath: @"C:\Testbibliothek") { Width = size.Width, Height = size.Height });

    public void Dispose()
    {
        ListBox.ItemsSource = null;
        Window.Close();
    }

    /// <summary>Datenquelle mit den Eigenschaften, an die Panel und Stil binden (wie GalleryViewModel).</summary>
    internal sealed class HostModel : INotifyPropertyChanged
    {
        private GalleryViewMode _viewMode;
        private int _tileSize = 180;
        private int _collageLayoutVersion;
        private bool _showTileMarks = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public GalleryViewMode ViewMode
        {
            get => _viewMode;
            set => Set(ref _viewMode, value);
        }

        public int TileSize
        {
            get => _tileSize;
            set
            {
                Set(ref _tileSize, value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileWidth)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileHeight)));
            }
        }

        public double TileWidth => TileSize;

        public double TileHeight => Math.Round(160 + ((TileSize - 180) * 0.64));

        public int CollageLayoutVersion
        {
            get => _collageLayoutVersion;
            set => Set(ref _collageLayoutVersion, value);
        }

        /// <summary>Wie <c>GalleryViewModel.ShowTileMarks</c> (Schalter „Symbole auf Galerie-Kacheln“, Mockup 16).</summary>
        public bool ShowTileMarks
        {
            get => _showTileMarks;
            set => Set(ref _showTileMarks, value);
        }

        /// <summary>Wie <c>GalleryViewModel.ToggleFavoriteCommand</c> (Herz auf der Kachel); zählt die Aufrufe.</summary>
        public CommunityToolkit.Mvvm.Input.RelayCommand<GalleryItem> ToggleFavoriteCommand => _toggleFavorite ??= new(_ => FavoriteToggles++);

        /// <summary>Wie <c>GalleryViewModel.SetItemRatingCommand</c> (Sterne auf der Kachel).</summary>
        public CommunityToolkit.Mvvm.Input.RelayCommand<object> SetItemRatingCommand => _setRating ??= new(_ => { });

        public int FavoriteToggles { get; private set; }

        private CommunityToolkit.Mvvm.Input.RelayCommand<GalleryItem>? _toggleFavorite;
        private CommunityToolkit.Mvvm.Input.RelayCommand<object>? _setRating;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
