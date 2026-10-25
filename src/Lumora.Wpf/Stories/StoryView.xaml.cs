using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lumora.Stories;

/// <summary>
/// Story-Ansicht (ADR-0060); Logik im <see cref="StoryViewModel"/>. Hier nur: Breite an das ViewModel, Vorschaubilder
/// sichtbarer Zeilen anfordern, die Leiste unter dem Titelbild mitschieben, bis sie oben stehen bleibt, Tages-Sprung
/// scrollen und das „…“-Menü öffnen.
/// </summary>
public partial class StoryView : UserControl
{
    private StoryViewModel? _viewModel;
    private ScrollViewer? _scroller;

    public StoryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Attach(DataContext as StoryViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    /// <summary>Höhe des Titelbilds (erste Zeile) — bis dahin wandert die Leiste mit.</summary>
    public const double CoverHeight = 340;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Attach(e.NewValue as StoryViewModel);
        }
    }

    private void Attach(StoryViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollRequested -= OnScrollRequested;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollRequested += OnScrollRequested;
            UpdateWidth();
        }
    }

    private void OnRowsSizeChanged(object sender, SizeChangedEventArgs e) => UpdateWidth();

    /// <summary>Rechtsklick auf ein Story-Foto › „Als Titelbild für ▸“ (ADR-0068).</summary>
    private async void OnCoverSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: StoryTileViewModel tile } menu || !ReferenceEquals(e.OriginalSource, menu)
            || tile.Owner is not { PhotoActions: { } actions } owner)
        {
            return;
        }

        var host = new Lumora.PhotoMenu.PhotoMenuHost
        {
            Owner = () => Window.GetWindow(this),
            ShowStatus = text => _ = owner.AfterCoverMenuAsync(text),
        };
        await Lumora.PhotoMenu.PhotoContextMenuBuilder.FillCoversAsync(menu, tile.FileId, host, actions).ConfigureAwait(true);
    }

    /// <summary>Breite der Zeilen = Liste minus Bildlaufleiste und rechtem Rand.</summary>
    private void UpdateWidth()
    {
        if (_viewModel is not null && RowsList.ActualWidth > 0)
        {
            _viewModel.SetLayoutWidth(RowsList.ActualWidth - 34);
        }
    }

    private void OnTilesRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StoryTilesRow row })
        {
            _viewModel?.RequestRowImages(row);
        }
    }

    /// <summary>Leiste: unter dem Titelbild, sobald es hinausgescrollt ist oben fixiert.</summary>
    private void OnRowsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _scroller ??= e.OriginalSource as ScrollViewer;
        BarShift.Y = Math.Max(0, CoverHeight - e.VerticalOffset);
    }

    private void OnScrollRequested(int rowIndex)
        => Dispatcher.InvokeAsync(() => ScrollToRow(rowIndex), DispatcherPriority.Loaded);

    /// <summary>Zeile oben unter die Leiste scrollen (Zeile erst realisieren, dann ausrichten).</summary>
    private void ScrollToRow(int rowIndex)
    {
        var panel = FindChild<VirtualizingStackPanel>(RowsList);
        var scroller = _scroller ?? FindChild<ScrollViewer>(RowsList);
        if (panel is null || scroller is null || rowIndex < 0 || rowIndex >= RowsList.Items.Count)
        {
            return;
        }

        panel.BringIndexIntoViewPublic(rowIndex);
        RowsList.UpdateLayout();
        if (RowsList.ItemContainerGenerator.ContainerFromIndex(rowIndex) is FrameworkElement container)
        {
            var top = container.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
            scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + top - StoryBarSpacerRow.Height));
        }
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>Mausrad über den Tages-Chips: waagerecht blättern.</summary>
    private void OnChipsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer chips && chips.ScrollableWidth > 0)
        {
            chips.ScrollToHorizontalOffset(chips.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }

    private static T? FindChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
