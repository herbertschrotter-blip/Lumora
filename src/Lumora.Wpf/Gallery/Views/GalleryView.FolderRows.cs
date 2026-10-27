using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumora.Folders;
using Lumora.Gallery.FolderRows;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Lumora.Gallery.Views;

/// <summary>
/// Ansicht „Ordner“ (Mockup 21): Breite der Zeilen an das ViewModel melden und Rechtsklick auf eine Zeile — Aktionen für
/// das ganze Set. Die Ordner-Befehle kommen aus der Ordnerleiste (<see cref="FolderTreeViewModel"/>, gleiche Dialoge und
/// Rückfragen wie dort).
/// </summary>
public partial class GalleryView
{
    private FolderTreeViewModel? _folderTree;

    /// <summary>Zeilenbreite = Liste minus Innenabstand, Zeilen-Rand, Bildlaufleiste und Fuge hinter dem letzten Foto.</summary>
    private void OnFolderRowsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var list = (ItemsControl)sender;
        var width = list.ActualWidth - list.Padding.Left - list.Padding.Right - 12 - SystemParameters.VerticalScrollBarWidth - 4;
        _viewModel.FolderRowsWidth = width;
    }

    private void OnFolderRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderRowViewModel row, ContextMenu: { } menu })
        {
            e.Handled = true;
            return;
        }

        menu.Items.Clear();
        Add(menu, "Ordner öffnen", SymbolRegular.FolderOpen24, () => _viewModel.OpenFolderRowCommand.Execute(row));
        Add(menu, "Andere Fotos zeigen", SymbolRegular.ArrowShuffle24, () => _viewModel.ShuffleFolderRowCommand.Execute(row));
        Add(menu, "Diashow dieses Ordners", SymbolRegular.Play24, () => _viewModel.StartFolderRowSlideshowCommand.Execute(row));

        // Ordner-Aktionen nur für echte Unterordner (nicht „Fotos in …“) und wenn die Ordnerleiste den Ordner kennt
        if (row.IncludeSubfolders && _folderTree?.RevealFolder(row.FolderId) is { } node)
        {
            menu.Items.Add(new Separator());
            Add(menu, "Alle Fotos taggen…", SymbolRegular.Tag24, () => _folderTree.EditFolderTagsCommand.Execute(node));
            Add(menu, "Alle Fotos kategorisieren…", SymbolRegular.Grid24, () => _folderTree.EditFolderCategoriesCommand.Execute(node));
            var rate = new MenuItem { Header = "Alle Fotos bewerten", Icon = new SymbolIcon { Symbol = SymbolRegular.Star24 } };
            for (var stars = 5; stars >= 1; stars--)
            {
                var value = stars.ToString(System.Globalization.CultureInfo.InvariantCulture);
                rate.Items.Add(new MenuItem { Header = new string('★', stars), Command = node.RateAllCommand, CommandParameter = value });
            }

            menu.Items.Add(rate);
            menu.Items.Add(new Separator());
            Add(menu, "Umbenennen…", SymbolRegular.Rename24, () => _folderTree.RenameFolderCommand.Execute(node), node.CanRename);
            Add(menu, "Im Explorer anzeigen", SymbolRegular.FolderArrowRight24, () => _folderTree.ShowFolderInExplorerCommand.Execute(node));
        }
    }

    private static void Add(ContextMenu menu, string header, SymbolRegular symbol, System.Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol }, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
