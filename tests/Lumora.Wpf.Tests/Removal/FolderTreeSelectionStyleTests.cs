using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Folders;
using Lumora.Gallery.Services;
using Lumora.Services.Database;
using Lumora.Services.Library;
using Lumora.Services.Operations;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.Removal;

/// <summary>
/// Reiter „Ordner“ (0.4.30): der gewählte Ordner ist im dunklen Design deutlich erkennbar — Akzent-Balken, Akzent-Tönung,
/// Symbol in Akzentfarbe, Name halbfett und hell genug. Die Vorlage liegt in <c>Folders/FolderTreeStyles.xaml</c>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class FolderTreeSelectionStyleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Gewaehlter_Ordner_im_dunklen_Design_deutlich_hervorgehoben() => TestUiThread.Dispatcher.InvokeAsync(() =>
    {
        var styles = new ResourceDictionary { Source = new Uri("pack://application:,,,/Lumora;component/Folders/FolderTreeStyles.xaml") };
        var template = (HierarchicalDataTemplate)styles["FolderNodeTemplate"];
        var owner = new FolderTreeViewModel(
            NullService.Create<ILibraryContextFactory>(),
            NullService.Create<ILibraryService>(),
            NullService.Create<IFileOperationService>(),
            new ScriptedDialogs(),
            new FolderActions(null!, null!, null!, null!, null!, null!, null!, NullService.Create<IGalleryDialogService>(), NullLogger<FolderActions>.Instance),
            NullLogger<FolderTreeViewModel>.Instance);
        var node = new FolderNode(owner, 1, "Reise", @"X:\Fotos\Reise", 2, 2, hasChildren: false);
        var other = new FolderNode(owner, 2, "Arbeit", @"X:\Fotos\Arbeit", 1, 1, hasChildren: false);
        var tree = new TreeView { ItemsSource = new[] { node, other }, ItemTemplate = template };
        var window = new Window { Content = tree, Width = 400, Height = 300, ShowActivated = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            T Part<T>(FolderNode n, string name)
                where T : DependencyObject
            {
                var item = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(n);
                item.ApplyTemplate();
                var presenter = FindChild<ContentPresenter>(item, p => p.Content == n)!;
                presenter.ApplyTemplate();
                return (T)template.FindName(name, presenter);
            }

            Part<Border>(node, "SelectionBar").Visibility.Should().Be(Visibility.Hidden, "ungewählt kein Balken, Breite bleibt (kein Springen)");
            Part<TextBlock>(node, "FolderName").FontWeight.Should().Be(FontWeights.Normal);

            node.IsSelected = true;
            window.UpdateLayout();

            var bar = Part<Border>(node, "SelectionBar");
            bar.Visibility.Should().Be(Visibility.Visible);
            bar.Width.Should().Be(3);
            var accent = ((SolidColorBrush)bar.Background).Color;
            var fill = Part<Border>(node, "SelectionFill");
            fill.Visibility.Should().Be(Visibility.Visible);
            ((SolidColorBrush)fill.Background).Color.Should().Be(accent, "Tönung in Akzentfarbe");
            fill.Opacity.Should().BeInRange(0.1, 0.4, "deutlich sichtbar, aber dezent");
            var name = Part<TextBlock>(node, "FolderName");
            name.FontWeight.Should().Be(FontWeights.SemiBold);
            Luminance(((SolidColorBrush)name.Foreground).Color).Should().BeGreaterThan(160, "heller Name im dunklen Design");
            var icon = Part<global::Wpf.Ui.Controls.SymbolIcon>(node, "FolderIcon");
            var iconColor = ((SolidColorBrush)icon.Foreground).Color;
            var otherIcon = ((SolidColorBrush)Part<global::Wpf.Ui.Controls.SymbolIcon>(other, "FolderIcon").Foreground).Color;
            iconColor.Should().NotBe(otherIcon, "Symbol des gewählten Ordners in Akzentfarbe");
            Part<Border>(other, "SelectionBar").Visibility.Should().Be(Visibility.Hidden, "nur der gewählte Ordner");
        }
        finally
        {
            window.Close();
        }
    }).Task.WaitAsync(Timeout);

    private static double Luminance(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static T? FindChild<T>(DependencyObject parent, Func<T, bool> match)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && match(t))
            {
                return t;
            }

            if (FindChild(child, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
