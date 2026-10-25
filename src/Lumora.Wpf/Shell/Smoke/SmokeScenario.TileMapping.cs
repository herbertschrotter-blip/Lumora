using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Lumora.Gallery.Controls;
using Lumora.Gallery.ListView;
using Lumora.Gallery.ViewModels;
using Lumora.Gallery.Views;
using Lumora.Lightbox;
using Microsoft.Extensions.Logging;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Block „Doppelklick-Ziel = angezeigte Kachel“ im Rauchtest-Schritt „Lightbox Bild + Video“ (0.2.42): große Kacheln,
/// Kachel weit unten öffnen, schließen, in Mausrad-Rastungen nach oben (Container werden recycelt), dann für jeden
/// sichtbaren Platz den Container per Treffertest holen: angezeigter Name = DataContext = Item des Platzes; eine Kachel
/// per Container-DataContext „doppelklicken“ → die Lightbox zeigt genau diese Datei.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task TileDoubleClickTargetAsync()
    {
        if (_gallery.ViewMode != GalleryViewMode.Grid)
        {
            _gallery.ViewMode = GalleryViewMode.Grid;
        }

        var tileSizeBefore = _gallery.TileSize;
        _gallery.TileSize = GalleryViewModel.MaxTileSize; // wenige Kacheln je Zeile → Scrollen recycelt Container
        var galleryView = FindVisualChild<GalleryView>(Application.Current.MainWindow);
        var list = galleryView is null ? null : FindVisualChild<GalleryListBox>(galleryView);
        VirtualizingTilePanel? panel = null;
        await WaitUntilAsync(
            () => (panel = list is null ? null : FindVisualChild<VirtualizingTilePanel>(list)) is { } p
                  && p.ItemWidth == GalleryViewModel.MaxTileSize && p.ViewportHeight > 0 && !_gallery.IsLoading,
            ShortWait,
            "Kachel-Raster mit großen Kacheln").ConfigureAwait(true);

        try
        {
            // (1) Kachel weit unten öffnen (Fokus wie nach dem Doppelklick), schließen
            var far = _gallery.CurrentPage[^1];
            list!.ScrollIntoView(far);
            list.UpdateLayout();
            var farContainer = list.ItemContainerGenerator.ContainerFromItem(far) as ListBoxItem;
            Check(farContainer is not null && FocusForSmoke(farContainer), $"Kachel {_gallery.CurrentPage.Count} realisiert und fokussiert");
            _gallery.OpenInLightboxCommand.Execute(farContainer!.DataContext);
            await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox für die letzte Kachel").ConfigureAwait(true);
            var window = Application.Current.Windows.OfType<LightboxWindow>().First();
            Check(window.ViewModel.CurrentItem?.FileId == far.Id, $"Lightbox zeigt die letzte Kachel ({window.ViewModel.CounterText})");
            window.ViewModel.CloseCommand.Execute(null);
            await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

            // (2) in Mausrad-Rastungen nach oben
            var steps = 0;
            while (panel!.VerticalOffset > 0 && steps++ < 500)
            {
                panel.MouseWheelUp();
                list.UpdateLayout();
            }

            await Task.Delay(100).ConfigureAwait(true);
            Check(panel.VerticalOffset == 0, $"nach {steps} Rastungen oben ({panel.Columns} Spalten, {panel.ExtentHeight / panel.ItemHeight:N0} Zeilen)");

            // (3) jeder sichtbare Platz: angezeigter Name = DataContext = Item des Platzes
            var slots = VisibleSlots(panel);
            var mismatches = new List<string>();
            foreach (var (index, container) in slots)
            {
                var expected = _gallery.CurrentPage[index];
                var shown = DisplayedName(container);
                if (!ReferenceEquals(container?.DataContext, expected) || shown != expected.Name)
                {
                    mismatches.Add($"Platz {index + 1}: zeigt „{shown}“, DataContext {(container?.DataContext as GalleryItem)?.Id}, erwartet Id {expected.Id}");
                }
            }

            Check(slots.Count > 0 && mismatches.Count == 0, $"{slots.Count} sichtbare Kacheln tragen ihr Item{(mismatches.Count > 0 ? ": " + string.Join("; ", mismatches) : string.Empty)}");

            // (4) eine sichtbare Kachel „doppelklicken“ (DataContext des Containers am Platz, wie OnItemDoubleClick)
            var (slotIndex, target) = slots[slots.Count / 2];
            var targetName = DisplayedName(target);
            _gallery.OpenInLightboxCommand.Execute(target!.DataContext);
            await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox für die sichtbare Kachel").ConfigureAwait(true);
            window = Application.Current.Windows.OfType<LightboxWindow>().First();
            var vm = window.ViewModel;
            Check(vm.CurrentItem?.FileId == _gallery.CurrentPage[slotIndex].Id && vm.CurrentItem?.Name == targetName && vm.CurrentIndex == slotIndex,
                $"Doppelklick auf Platz {slotIndex + 1} („{targetName}“) → Lightbox zeigt „{vm.Title}“ ({vm.CounterText})");
            vm.CloseCommand.Execute(null);
            await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);
        }
        finally
        {
            _gallery.TileSize = tileSizeBefore;
            _gallery.ClearSelectionCommand.Execute(null);
        }
    }

    /// <summary>
    /// Fokus auf ein Element des Hauptfensters. <c>Focus()</c> liefert false, wenn Windows das Fenster nicht in den
    /// Vordergrund lässt (Benutzer arbeitet in einem anderen Programm; Rauchtest-Wackler 0.4.41, Issue #20). Dann bekommt
    /// das Element ausdrücklich den logischen Fokus seines Fensters — der Schritt prüft weiter die Lightbox und die
    /// Kachel-Zuordnung; nur „Fokus-Container bleibt beim Aufräumen“ (braucht Tastaturfokus) ist in diesem Lauf nicht abgedeckt.
    /// </summary>
    private bool FocusForSmoke(UIElement element)
    {
        if (element.Focus())
        {
            return true;
        }

        System.Windows.Input.FocusManager.SetFocusedElement(System.Windows.Input.FocusManager.GetFocusScope(element), element);
        _logger.LogInformation("Rauchtest   Kein Tastaturfokus (Fenster aktiv={Active}) — logischer Fokus={Logical}, Fokus-Container beim Aufräumen nicht geprüft",
            Application.Current.MainWindow?.IsActive, element.IsFocused);
        return element.IsFocused;
    }

    /// <summary>Container per Treffertest in der Mitte jedes vollständig sichtbaren Platzes (Item-Index, Container).</summary>
    private static List<(int Index, ListBoxItem? Container)> VisibleSlots(VirtualizingTilePanel panel)
    {
        var result = new List<(int, ListBoxItem?)>();
        var count = ItemsControl.GetItemsOwner(panel)?.Items.Count ?? 0;
        var firstRow = (int)System.Math.Floor(panel.VerticalOffset / panel.ItemHeight);
        var lastRow = (int)System.Math.Ceiling((panel.VerticalOffset + panel.ViewportHeight) / panel.ItemHeight) - 1;
        for (var row = firstRow; row <= lastRow; row++)
        {
            // Mitte des sichtbaren Teils der Zeile; kaum sichtbare Zeilen auslassen.
            var top = System.Math.Max(row * panel.ItemHeight - panel.VerticalOffset, 0);
            var bottom = System.Math.Min((row + 1) * panel.ItemHeight - panel.VerticalOffset, panel.ViewportHeight);
            if (bottom - top < 40)
            {
                continue;
            }

            for (var column = 0; column < panel.Columns && row * panel.Columns + column < count; column++)
            {
                var center = new Point((column + 0.5) * panel.ItemWidth, (top + bottom) / 2);
                DependencyObject? hit = panel.InputHitTest(center) as DependencyObject;
                while (hit is not null and not ListBoxItem)
                {
                    hit = VisualTreeHelper.GetParent(hit);
                }

                result.Add((row * panel.Columns + column, hit as ListBoxItem));
            }
        }

        return result;
    }

    /// <summary>Text des an „Name“ gebundenen TextBlocks der Kachel — das, was der Benutzer sieht.</summary>
    private static string? DisplayedName(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        if (root is TextBlock text && BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(GalleryItem.Name))
        {
            return text.Text;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (DisplayedName(VisualTreeHelper.GetChild(root, i)) is { } name)
            {
                return name;
            }
        }

        return null;
    }
}
