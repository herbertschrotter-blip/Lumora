using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Lightbox;
using Lumora.Persons;
using Lumora.Persons.Controls;
using Lumora.Persons.ViewModels;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Crops;
using Lumora.Services.Faces.Indexing;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Persons;

/// <summary>
/// Personen-Kacheln nach Mockup 17 (ADR-0068): nichts wird mehr abgeschnitten (Name bis zwei Zeilen, Beziehung, Chips), der
/// Vorschläge-Chip öffnet die Person im Reiter „Vorschläge“, Symbol „automatisch gewählt“, Tooltip; Umschalter „Automatisch /
/// Selbst gewählt“ in der Person.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PersonTileUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static PersonOverview Person(int id, string name, string? relation, int files, int suggestions, bool manualCover)
        => new(new PersonSummary(id, name, files, files, 1000 + id, false)
        {
            Details = new PersonDetails(id, name, string.Empty, null, relation is null ? null : 1, relation, null, false),
            CoverIsManual = manualCover,
        }, suggestions);

    private static (PersonsViewModel Vm, PersonsPage Page, Window Window, FakeRecognitionService Service) Show()
    {
        var service = new FakeRecognitionService();
        service.Persons.Add(Person(1, "Maria Huber-Steinbacher von Hohenberg", "Cousine", 231, 316, manualCover: false));
        service.Persons.Add(Person(2, "Thomas", null, 332, 0, manualCover: true));
        var vm = new PersonsViewModel(
            service,
            NullService.Create<IFaceIndexService>(),
            new FaceImageProvider(NullService.Create<IFaceCropService>()),
            new FakePersonsDialogs(),
            NullService.Create<ILightboxService>(),
            NullService.Create<INavigationService>(),
            NullLogger<PersonsViewModel>.Instance,
            new WeakReferenceMessenger());
        var page = new PersonsPage(vm);
        var window = new Window
        {
            Width = 1100, Height = 760, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000, Content = page,
        };
        window.Show();
        window.UpdateLayout();
        DoEvents();
        return (vm, page, window, service);
    }

    [Fact]
    public void Kachel_ist_hoch_genug_und_zeigt_Name_Beziehung_und_Chips()
    {
        TestUiThread.Run(() =>
        {
            var (vm, page, window, _) = Show();
            try
            {
                vm.Persons.Should().HaveCount(2);
                var list = (TileListBox)page.FindName("PersonsList");
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var presenter = FindVisualChild<ContentPresenter>(container)!;
                var content = (FrameworkElement)VisualTreeHelper.GetChild(presenter, 0);
                content.Measure(new System.Windows.Size(container.ActualWidth, double.PositiveInfinity));
                var needed = content.DesiredSize.Height + 14; // Rahmen der Kachel: 5 Rand + 2 Linie je Seite

                needed.Should().BeLessThanOrEqualTo(container.ActualHeight + 0.5, "nichts wird unten abgeschnitten");
                needed.Should().BeGreaterThan(196, "Ursache bis 0.4.37: feste Zeile 196 px bei mehr Inhalt");
                var texts = FindVisualChildren<TextBlock>(content).ToList();
                var name = texts.First(t => t.Text.StartsWith("Maria", StringComparison.Ordinal));
                name.ActualHeight.Should().BeGreaterThan(30, "langer Name umbricht auf zwei Zeilen");
                name.ActualHeight.Should().BeLessThanOrEqualTo(38.5, "höchstens zwei Zeilen");
                texts.Should().Contain(t => t.Text == "Cousine" && t.IsVisible);
                texts.Should().Contain(t => t.Text == "231" && t.IsVisible, "Chip bestätigt");
                texts.Should().Contain(t => t.Text == "316" && t.IsVisible, "Chip Vorschläge");

                var tile = vm.Persons[0];
                tile.IsCoverAutomatic.Should().BeTrue();
                vm.Persons[1].IsCoverAutomatic.Should().BeFalse("selbst gewählt");
                vm.Persons[1].HasSuggestions.Should().BeFalse();
                tile.TileToolTip.Should().Contain("Cousine").And.Contain("316 Vorschläge").And.Contain("Titelbild: automatisch");
                vm.Persons[1].TileToolTip.Should().Contain("Titelbild: von dir gewählt");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, Timeout);
    }

    [Fact]
    public void Vorschlaege_Chip_oeffnet_die_Person_im_Reiter_Vorschlaege()
    {
        TestUiThread.Run(() =>
        {
            var (vm, page, window, service) = Show();
            try
            {
                var face = new PersonFaceInfo(5001, 77, new RectangleF(0.1f, 0.1f, 0.2f, 0.2f), FaceState.Confirmed, 0.9f, null);
                var suggestion = new PersonFaceInfo(5002, 78, new RectangleF(0.1f, 0.1f, 0.2f, 0.2f), FaceState.Suggested, 0.8f, 0.7f);
                service.PersonFaces[1] = new PersonFaces(vm.Persons[0].Overview.Person, [face], [suggestion]);
                var list = (TileListBox)page.FindName("PersonsList");
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var chip = FindVisualChildren<Button>(container).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Vorschläge");

                chip.Command.Execute(chip.CommandParameter);
                DoEvents();

                vm.Mode.Should().Be(PersonsPageMode.Person);
                vm.DetailTab.Should().Be(0, "Reiter „Vorschläge“");
                vm.IsCoverAutomatic.Should().BeTrue();
                vm.CoverModeText.Should().Contain("automatisch gewählt");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, Timeout);
    }

    [Fact]
    public void Umschalter_Automatisch_loest_das_eigene_Titelbild()
    {
        TestUiThread.Run(() =>
        {
            var (vm, _, window, service) = Show();
            try
            {
                var thomas = vm.Persons[1];
                service.PersonFaces[2] = new PersonFaces(thomas.Overview.Person, [], []);
                vm.OpenPersonCommand.Execute(thomas);
                DoEvents();
                vm.IsCoverManual.Should().BeTrue();

                vm.SetCoverAutomaticCommand.Execute(null);
                DoEvents();

                service.ClearedCovers.Should().Equal(2);

                vm.SetCoverManualCommand.Execute(null);
                DoEvents();
                vm.DetailTab.Should().Be(1, "ohne gewähltes Gesicht: Hinweis und Reiter „Bestätigt“");
                vm.Message.Should().Contain("Als Titelbild");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, Timeout);
    }

    private static void DoEvents()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
        => FindVisualChildren<T>(root).FirstOrDefault();

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}
