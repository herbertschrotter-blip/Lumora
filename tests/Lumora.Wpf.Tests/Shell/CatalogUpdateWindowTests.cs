using Lumora.Services.Database;
using Lumora.Shell.Dialogs;

namespace Lumora.Wpf.Tests.Shell;

/// <summary>Fenster beim Sichern und Aktualisieren des Katalogs (statt eines scheinbaren Hängers beim Start).</summary>
public sealed class CatalogUpdateWindowTests
{
    [Fact]
    public void Texte_nennen_Schritt_Groesse_und_Anzahl()
    {
        var (heading, detail) = CatalogUpdateWindow.Texts(new DatabaseInitStep(DatabaseInitStepKind.Backup, 1_907_621_888, 1));
        heading.Should().Be("Katalog wird gesichert …");
        detail.Should().Contain("Katalog (1,8 GB) aktualisieren").And.Contain("nicht noch einmal starten");

        CatalogUpdateWindow.Texts(new DatabaseInitStep(DatabaseInitStepKind.Backup, 0, 1)).Detail.Should().Contain("muss den Katalog aktualisieren");
        CatalogUpdateWindow.Texts(new DatabaseInitStep(DatabaseInitStepKind.Backup, 5 << 20, 1)).Detail.Should().Contain("(5 MB)");

        var (h2, d2) = CatalogUpdateWindow.Texts(new DatabaseInitStep(DatabaseInitStepKind.Migrate, 0, 2));
        h2.Should().Be("Katalog wird aktualisiert …");
        d2.Should().StartWith("2 Schritte für diese Lumora-Version");
        CatalogUpdateWindow.Texts(new DatabaseInitStep(DatabaseInitStepKind.Migrate, 0, 1)).Detail.Should().StartWith("1 Schritt für");
    }
}
