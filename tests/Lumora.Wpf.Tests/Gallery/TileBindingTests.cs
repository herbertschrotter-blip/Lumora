using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lumora.Gallery.Controls;
using Lumora.Gallery.ListView;
using Lumora.Gallery.ViewModels;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Rauchtest-Wackler „Alben“ (Issue #20, 0.4.41): Beim Öffnen eines intelligenten Albums meldete WPF 4–12 Binding-Fehler
/// an einem <see cref="GalleryListBoxItem"/> (HorizontalContentAlignment/VerticalContentAlignment aus dem System-Stil für
/// ListBoxItem, Herz/Sterne/Marken per <c>FindAncestor ListBox</c>) — immer dann, wenn die Vorlage eines Containers ohne
/// ListBox-Vorfahren aufgebaut wurde. Seitdem: eigener Stilschlüssel für den Container und die geerbte Eigenschaft
/// <see cref="GalleryListBox.TileHostProperty"/> statt FindAncestor. Getestet mit der ECHTEN Collage-Vorlage.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class TileBindingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kachel_Container_ohne_ListBox_Vorfahren_meldet_keine_Binding_Fehler(bool withContainerStyle)
    {
        var messages = TestUiThread.Run(() =>
        {
            using var host = new CollageGridHost(4);
            using var errors = new BindingErrors();
            var item = host.Items[0];
            var lone = new GalleryListBoxItem { ContentTemplate = host.CollageTemplate, Content = item, DataContext = item };
            if (withContainerStyle)
            {
                lone.Style = host.ListBox.ItemContainerStyle;
            }

            var window = OffscreenWindow(lone);
            try
            {
                window.Show();
                window.UpdateLayout();
                GalleryGridHost.Pump(DispatcherPriority.ApplicationIdle);
                if (withContainerStyle)
                {
                    FindByName<Button>(lone, "HeartButton")!.Command.Should().BeNull("ohne Galerie kein Befehl — aber auch kein Fehler");
                }
                else
                {
                    lone.Template.Should().BeNull("ohne Container-Stil gilt kein System-Stil mehr (eigener Stilschlüssel)");
                }
            }
            finally
            {
                window.Close();
            }

            return errors.Messages.ToList();
        }, Timeout);

        messages.Should().BeEmpty();
    }

    [Fact]
    public void Herz_und_Sterne_erreichen_die_Befehle_der_Galerie_ueber_TileHost()
    {
        TestUiThread.Run(() =>
        {
            using var host = new CollageGridHost(12);
            using var errors = new BindingErrors();
            host.ShowAndLayout();
            var container = host.ContainerOf(0)!;
            GalleryListBox.GetTileHost(container).Should().BeSameAs(host.Model, "der DataContext der ListBox fließt an die Kacheln");

            var heart = FindByName<Button>(container, "HeartButton")!;
            heart.Command.Should().BeSameAs(host.Model.ToggleFavoriteCommand);
            heart.Command.Execute(heart.CommandParameter);
            host.Model.FavoriteToggles.Should().Be(1);
            FindAll<RatingStars>(container).Single().Command.Should().BeSameAs(host.Model.SetItemRatingCommand);
            errors.Messages.Should().BeEmpty();
            return 0;
        }, Timeout);
    }

    /// <summary>Folge beim Öffnen eines intelligenten Albums: 20 → (Filter zurückgesetzt) 22 → (Album) 20, dazu Umsortieren.</summary>
    [Theory]
    [InlineData(GalleryViewMode.Grid)]
    [InlineData(GalleryViewMode.Collage)]
    public void Neuladen_mit_Einfuegen_Entfernen_und_Umsortieren_ohne_Binding_Fehler(GalleryViewMode mode)
    {
        var messages = TestUiThread.Run(() =>
        {
            using var host = new CollageGridHost(20, width: 960, height: 520, mode: mode);
            if (mode == GalleryViewMode.Grid)
            {
                host.ListBox.ItemTemplate = host.CollageTemplate; // Vorlage mit Herz/Sterne/Marken
            }

            host.ShowAndLayout();
            using var errors = new BindingErrors();
            var all = host.Items.ToList();
            var with22 = new List<GalleryItem> { all[0], CollageGridHost.CreateItem(100, (4000, 3000)), all[1], CollageGridHost.CreateItem(101, (1920, 1080)) };
            with22.AddRange(all.Skip(2));
            foreach (var target in new[] { with22, all, Enumerable.Reverse(all).ToList(), all })
            {
                Apply(host.Items, target);
                host.Window.UpdateLayout();
            }

            Apply(host.Items, with22);
            Apply(host.Items, all); // ohne Layout dazwischen
            host.Window.UpdateLayout();
            GalleryGridHost.Pump(DispatcherPriority.ApplicationIdle);
            return errors.Messages.ToList();
        }, Timeout);

        messages.Should().BeEmpty();
    }

    [Fact]
    public void Kachel_Vorlagen_binden_nicht_mehr_per_FindAncestor_an_die_ListBox()
    {
        foreach (var file in new[] { "GalleryView.xaml", "GalleryCollageTemplates.xaml" })
        {
            var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Lumora.Wpf", "Gallery", "Views", file));
            xaml.Should().NotContain("AncestorType=ListBox", file);
            xaml.Should().Contain("Path=(gc:GalleryListBox.TileHost).ToggleFavoriteCommand", file);
        }
    }

    // ---- Hilfen --------------------------------------------------------------------------------

    /// <summary>Wie <c>GalleryViewModel.ApplyRows</c>: Verschwundene entfernen, Reihenfolge per Move, Fehlende einfügen.</summary>
    private static void Apply(ObservableCollection<GalleryItem> page, List<GalleryItem> target)
    {
        var ids = target.Select(t => t.Id).ToHashSet();
        for (var i = page.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(page[i].Id))
            {
                page.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < page.Count && ReferenceEquals(page[i], target[i]))
            {
                continue;
            }

            var j = page.IndexOf(target[i]);
            if (j > i)
            {
                page.Move(j, i);
            }
            else
            {
                page.Insert(i, target[i]);
            }
        }

        while (page.Count > target.Count)
        {
            page.RemoveAt(page.Count - 1);
        }
    }

    private static Window OffscreenWindow(object content) => new()
    {
        Width = 300,
        Height = 300,
        Left = -5000,
        Top = -5000,
        ShowActivated = false,
        ShowInTaskbar = false,
        WindowStyle = WindowStyle.None,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Content = content,
    };

    private static T? FindByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
        => FindAll<T>(root).FirstOrDefault(e => e.Name == name);

    private static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindAll<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumora.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo-Wurzel nicht gefunden");
    }

    /// <summary>Sammelt WPF-Binding-Fehler während des Tests.</summary>
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
