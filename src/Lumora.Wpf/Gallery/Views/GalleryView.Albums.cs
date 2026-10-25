using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Lumora.Albums;
using Lumora.Gallery.ViewModels;
using Lumora.Shell;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Lumora.Gallery.Views;

/// <summary>
/// Alben in der Galerie-Ansicht (ADR-0054): Menü „Album“ der Aktionsleiste, Einträge im Kontextmenü der Kachel und
/// Ziehen der Auswahl auf Ordner (Ordnerbaum) und Alben (Ordner-Seite). Beim Ziehen bleibt eine Mehrfachauswahl erhalten:
/// ein Klick auf eine schon gewählte Kachel wählt sie erst beim Loslassen allein (wie im Explorer).
/// </summary>
public partial class GalleryView
{
    private Point? _dragStart;
    private GalleryItem? _dragItem;
    private GalleryItem? _pendingSingleSelect;

    private void InitAlbumUi()
    {
        var flyout = new ContextMenu();
        flyout.Opened += (_, _) =>
        {
            flyout.Items.Clear();
            AddAlbumItems(flyout.Items, target: null);
        };
        AlbumsDropDown.Flyout = flyout;

        GalleryList.PreviewMouseLeftButtonDown += OnGalleryPreviewMouseDown;
        GalleryList.PreviewMouseMove += OnGalleryPreviewMouseMove;
        GalleryList.PreviewMouseLeftButtonUp += OnGalleryPreviewMouseUp;
    }

    /// <summary>Alben-Einträge (Aktionsleiste „Album“ und Rechtsklick › „Alben ▸“, ADR-0068).</summary>
    internal void AddAlbumItems(ItemCollection items, GalleryItem? target)
    {
        var add = new MenuItem { Header = "Zu Album hinzufügen", Icon = new SymbolIcon { Symbol = SymbolRegular.Album24 } };
        foreach (var entry in _viewModel.ManualAlbumEntries)
        {
            add.Items.Add(new MenuItem
            {
                Header = entry.Name,
                Command = _viewModel.AddSelectionToAlbumCommand,
                CommandParameter = entry,
            });
        }

        if (add.Items.Count > 0)
        {
            add.Items.Add(new Separator());
        }

        add.Items.Add(new MenuItem
        {
            Header = "Album wählen oder neu anlegen…",
            InputGestureText = "Strg+L",
            Command = _viewModel.AddSelectionToNamedAlbumCommand,
        });
        items.Add(add);

        items.Add(new MenuItem
        {
            Header = "Hier verknüpfen…",
            ToolTip = "Auswahl zusätzlich in einem anderen Ordner zeigen — nichts wird kopiert oder verschoben",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Link24 },
            Command = _viewModel.LinkSelectionToFolderCommand,
        });

        if (_viewModel.HasFolderFilter && (target?.IsLinked ?? _viewModel.CurrentPage.Any(i => i.IsSelected && i.IsLinked)))
        {
            items.Add(new MenuItem
            {
                Header = "Verknüpfung entfernen",
                ToolTip = "Nur die Verknüpfung in diesem Ordner — das Foto bleibt, wo es liegt",
                Icon = new SymbolIcon { Symbol = SymbolRegular.LinkDismiss24 },
                Command = _viewModel.UnlinkSelectionFromFolderCommand,
            });
        }

        if (_viewModel.HasAlbumFilter)
        {
            items.Add(new MenuItem
            {
                Header = "Aus Album entfernen",
                ToolTip = "Nur aus dem Album — das Foto bleibt, wo es liegt",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                Command = _viewModel.RemoveSelectionFromAlbumCommand,
            });
            if (target is not null)
            {
                items.Add(new MenuItem
                {
                    Header = "Als Titelbild des Albums",
                    Icon = new SymbolIcon { Symbol = SymbolRegular.ImageMultiple24 },
                    Command = _viewModel.SetAlbumCoverCommand,
                    CommandParameter = target,
                });
            }
        }

        var manage = new MenuItem { Header = "Alben verwalten…" };
        manage.Click += (_, _) => _navigation.NavigateTo(NavigationTags.Albums);
        items.Add(manage);
    }

    // ---- Ziehen ------------------------------------------------------------------------------------------------------

    private void OnGalleryPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _dragItem = null;
        _pendingSingleSelect = null;
        if (e.ClickCount != 1 || !_viewModel.HasAlbumSupport)
        {
            return;
        }

        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not GalleryItem item || IsInsideButton(e.OriginalSource as DependencyObject, container))
        {
            return;
        }

        _dragStart = e.GetPosition(GalleryList);
        _dragItem = item;
        if (item.IsSelected && Keyboard.Modifiers == ModifierKeys.None && _viewModel.SelectedCount > 1)
        {
            // Mehrfachauswahl für das Ziehen behalten: allein wählen erst beim Loslassen ohne Ziehen.
            _pendingSingleSelect = item;
            container.Focus();
            e.Handled = true;
        }
    }

    private void OnGalleryPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingSingleSelect is { } item)
        {
            _viewModel.ClearSelectionCommand.Execute(null);
            item.IsSelected = true;
        }

        _pendingSingleSelect = null;
        _dragStart = null;
        _dragItem = null;
    }

    private void OnGalleryPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragItem is not { } item || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var pos = e.GetPosition(GalleryList);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        _pendingSingleSelect = null;
        var ids = item.IsSelected
            ? _viewModel.CurrentPage.Where(i => i.IsSelected).Select(i => i.Id).ToArray()
            : [item.Id];
        if (ids.Length == 0)
        {
            return;
        }

        // ADR-0068: zusätzlich echter Datei-Drop für andere Programme — dann nur „Kopieren“ erlaubt (ExternalFileTransfer)
        var external = ExternalDragFiles(item);
        var (data, allowed) = Lumora.PhotoMenu.ExternalFileTransfer.CreateDragData(ids, external);
        var effect = DragDrop.DoDragDrop(GalleryList, data, allowed);
        if (allowed == Lumora.PhotoMenu.ExternalFileTransfer.ExternalEffects && effect == DragDropEffects.Copy && external is not null
            && Lumora.Usage.ExternalDropProbe.CursorOverOtherProcess())
        {
            Lumora.Services.Usage.UsageReport.Shared(external.FileIds); // in ein anderes Programm gezogen (Nutzungs-Signale, ADR-0072)
        }
    }
}
