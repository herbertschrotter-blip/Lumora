using System.Linq;
using FluentAssertions;
using Lumora.Gallery.Data;
using Lumora.Gallery.FolderRows;
using Xunit;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>Ansicht „Ordner“: welche Ordner eine Zeile bekommen, in welcher Reihenfolge und wie eingerückt.</summary>
public sealed class FolderRowPlanTests
{
    private static GallerySubfolderDto F(int id, string relative) => new(id, relative, @"X:\Fotos\" + relative, 1);

    [Fact]
    public void Unterordner_eines_Ordners_mit_Zeile_steht_eingerueckt_darunter()
    {
        var plan = FolderRowPlan.Build([F(2, @"Set 2"), F(3, @"Set\Raw"), F(1, "Set")]);

        plan.Select(e => (e.FolderId, e.Name, e.Level)).Should().Equal((1, "Set", 0), (3, "Raw", 1), (2, "Set 2", 0));
    }

    [Fact]
    public void Ordner_ohne_eigene_Fotos_fehlt_seine_Unterordner_stehen_mit_relativem_Pfad()
    {
        // „Urlaub“ selbst hat keine Fotos und kommt deshalb gar nicht in die Liste
        var plan = FolderRowPlan.Build([F(5, @"Urlaub\Tag 2"), F(4, @"Urlaub\Tag 1")]);

        plan.Select(e => (e.FolderId, e.Name, e.Level)).Should().Equal((4, @"Urlaub\Tag 1", 0), (5, @"Urlaub\Tag 2", 0));
    }

    [Fact]
    public void Tiefe_Unterordner_bleiben_eine_Ebene_eingerueckt_mit_Pfad_relativ_zur_Zeile_darueber()
    {
        var plan = FolderRowPlan.Build([F(1, "Set"), F(2, @"Set\Raw\Alt"), F(3, @"Setliste")]);

        plan.Select(e => (e.FolderId, e.Name, e.Level)).Should().Equal((1, "Set", 0), (2, @"Raw\Alt", 1), (3, "Setliste", 0));
    }

    [Fact]
    public void LastSegment_liefert_den_Ordnernamen()
    {
        FolderRowPlan.LastSegment(@"Urlaub\Tag 1").Should().Be("Tag 1");
        FolderRowPlan.LastSegment("Set").Should().Be("Set");
    }
}
