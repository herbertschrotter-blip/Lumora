using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using Lumora.Gallery.ViewModels;

namespace Lumora.Gallery.Controls;

/// <summary>
/// ListBox für das Kachel-Grid, deren Kacheln im UI-Automation-Baum Blätter sind.
/// Hintergrund (v0.2.0-Hänger): Sobald ein UI-Automation-Client lauscht (Visual Studio Live Visual Tree,
/// Bildschirmleser, Automationsskripte), aktualisiert WPF nach jedem Layoutdurchlauf den gesamten Peer-Baum
/// (<c>AutomationPeer.UpdateSubtree</c>). Bei 500 nicht virtualisierten Kacheln mit je rund zehn Elementen und
/// einem Layoutdurchlauf je eintreffendem Vorschaubild blockierte das den UI-Thread dauerhaft.
/// Mit Blatt-Peers bleibt die Kachel als Listeneintrag sichtbar (Name = Dateiname), ihr Innenleben nicht.
/// <para>
/// Seit 0.2.3 ist das Grid UI-virtualisiert (<see cref="VirtualizingTilePanel"/>): Container existieren nur für
/// sichtbare Kacheln. Die zweiseitige Bindung <c>ListBoxItem.IsSelected ↔ GalleryItem.IsSelected</c> hält deshalb
/// nur realisierte Kacheln synchron; für alle anderen gleicht diese Klasse die Auswahl der ListBox (Umschalt-Bereich,
/// Strg+A) mit den Items ab und umgekehrt (ViewModel hebt Auswahl auf, Seitenwechsel). Maßgeblich ist immer das Item.
/// </para>
/// </summary>
public sealed class GalleryListBox : ListBox
{
    /// <summary>
    /// Geerbte Eigenschaft mit dem ViewModel der Galerie (= DataContext der ListBox) für Bindungen in Kachel-Vorlagen
    /// (Herz, Sterne, Marken). Ersetzt <c>RelativeSource FindAncestor ListBox</c> (0.4.41, Issue #20): Wird die Vorlage
    /// eines Containers ohne ListBox-Vorfahren aufgebaut (z. B. während eines Neuladens kurz außerhalb des Panels),
    /// meldete WPF „Cannot find source“; über die geerbte Eigenschaft ist der Wert dann einfach <c>null</c>.
    /// Bindung in der Vorlage: <c>{Binding Path=(gc:GalleryListBox.TileHost).Befehl, RelativeSource={RelativeSource Self}}</c> —
    /// mit <c>Path=</c>: in der Kurzform ohne <c>Path=</c> löst kompiliertes XAML das Präfix nicht auf (Pfadfehler).
    /// </summary>
    public static readonly DependencyProperty TileHostProperty = DependencyProperty.RegisterAttached(
        "TileHost", typeof(object), typeof(GalleryListBox), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    private readonly HashSet<GalleryItem> _trackedItems = new();
    private bool _selectionSyncScheduled;

    public GalleryListBox()
    {
        // Der DataContext der ListBox (GalleryViewModel) fließt als TileHost an alle Kacheln.
        SetBinding(TileHostProperty, new System.Windows.Data.Binding());
    }

    public static object? GetTileHost(DependencyObject element) => element.GetValue(TileHostProperty);

    public static void SetTileHost(DependencyObject element, object? value) => element.SetValue(TileHostProperty, value);

    protected override DependencyObject GetContainerForItemOverride() => new GalleryListBoxItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is GalleryListBoxItem;

    /// <summary>
    /// Vor dem Realisieren eines Containers einen ausstehenden Abgleich sofort ausführen: Der Selector setzt nach der
    /// Container-Erzeugung (<c>AdjustNewContainers</c> bei GeneratorStatusChanged/LayoutUpdated) <c>IsSelected</c> aus
    /// seiner eigenen Liste — eine vom ViewModel inzwischen aufgehobene Auswahl lebte sonst beim Zurückscrollen wieder auf
    /// und würde über die zweiseitige Bindung ins Item zurückgeschrieben.
    /// </summary>
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        if (_selectionSyncScheduled)
        {
            SyncSelectionFromItems();
        }

        base.PrepareContainerForItemOverride(element, item);
    }

    /// <summary>Auswahl-Gesten der ListBox (Umschalt-Bereich, Strg+A) erreichen auch Items ohne Container.</summary>
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems.OfType<GalleryItem>())
        {
            item.IsSelected = false;
        }

        foreach (var item in e.AddedItems.OfType<GalleryItem>())
        {
            item.IsSelected = true;
        }

        base.OnSelectionChanged(e);
    }

    protected override void OnItemsSourceChanged(IEnumerable oldValue, IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        TrackAll();
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Track(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Remove:
                Untrack(e.OldItems);
                break;
            case NotifyCollectionChangedAction.Replace:
                Untrack(e.OldItems);
                Track(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Reset:
                TrackAll();
                break;
        }
    }

    private void TrackAll()
    {
        foreach (var item in _trackedItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        _trackedItems.Clear();
        Track(Items);
    }

    private void Track(IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items.OfType<GalleryItem>())
        {
            if (_trackedItems.Add(item))
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }
    }

    private void Untrack(IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items.OfType<GalleryItem>())
        {
            if (_trackedItems.Remove(item))
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }
    }

    /// <summary>
    /// Item-Auswahl geändert (ViewModel: Strg+A, Auswahl aufheben, Seitenwechsel). Realisierte Container folgen über die
    /// Bindung sofort; die ListBox-Liste wird gebündelt nachgezogen (ein Durchlauf je Dispatcher-Runde statt je Item).
    /// </summary>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GalleryItem.IsSelected) || _selectionSyncScheduled)
        {
            return;
        }

        _selectionSyncScheduled = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, SyncSelectionFromItems);
    }

    private void SyncSelectionFromItems()
    {
        _selectionSyncScheduled = false;
        if (SelectionMode == SelectionMode.Single)
        {
            return;
        }

        var selected = new List<GalleryItem>();
        foreach (var item in Items.OfType<GalleryItem>())
        {
            if (item.IsSelected)
            {
                selected.Add(item);
            }
        }

        if (selected.Count == SelectedItems.Count && SelectedItems.OfType<GalleryItem>().All(i => i.IsSelected))
        {
            return;
        }

        SetSelectedItems(selected);
    }
}

/// <summary>Container einer Kachel; sein Automation-Peer meldet keine Kinder (siehe <see cref="GalleryListBox"/>).</summary>
public sealed class GalleryListBoxItem : ListBoxItem
{
    /// <summary>
    /// Eigener Stilschlüssel (0.4.41, Issue #20): Der System-Stil für <see cref="ListBoxItem"/> bindet
    /// HorizontalContentAlignment/VerticalContentAlignment per <c>FindAncestor ItemsControl</c> — auch wenn der
    /// Container-Stil der Galerie die Werte setzt. Ein Container, der kurz ohne ListBox-Vorfahren aufgebaut wurde, meldete
    /// dadurch Binding-Fehler. Vorlage, Ränder und Ausrichtung kommen vollständig aus TileContainerStyle bzw.
    /// CollageContainerStyle (GalleryView.xaml), die alles setzen, was der System-Stil sonst lieferte (übrig blieben nur
    /// Rahmenfarbe/-breite, die die eigenen Vorlagen nicht nutzen); ohne Container-Stil bleibt der Container leer.
    /// </summary>
    static GalleryListBoxItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(GalleryListBoxItem), new FrameworkPropertyMetadata(typeof(GalleryListBoxItem)));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LeafItemPeer(this);

    private sealed class LeafItemPeer : ListBoxItemWrapperAutomationPeer
    {
        public LeafItemPeer(ListBoxItem owner)
            : base(owner)
        {
        }

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override string GetNameCore()
        {
            // Dateiname statt des Typnamens aus ToString() des Datenobjekts.
            if ((Owner as ListBoxItem)?.DataContext is ViewModels.GalleryItem item)
            {
                return item.Name;
            }

            return base.GetNameCore();
        }

        /// <summary>Personen und unbenannte Gesichter (0.2.39) als HelpText — die Kachel bleibt ein Blatt.</summary>
        protected override string GetHelpTextCore()
        {
            if ((Owner as ListBoxItem)?.DataContext is ViewModels.GalleryItem item && item.Persons.AutomationText is { Length: > 0 } text)
            {
                return text;
            }

            return base.GetHelpTextCore();
        }
    }
}
