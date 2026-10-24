using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Removal;
using Lumora.Services.Operations;
using Lumora.Services.Removal;

namespace Lumora.Wpf.Tests.Removal;

/// <summary>Abläufe mit Bestätigung für „In den Papierkorb…“ und „Fehlende entfernen“ (ADR-0065).</summary>
public sealed class RemovalActionsTests
{
    private readonly FakeRemovalService _service = new();
    private readonly FakeRemovalDialogs _dialogs = new();

    private RemovalActions Create() => new(_service, _dialogs, NullLogger<RemovalActions>.Instance);

    [Fact]
    public async Task Ordner_Bestaetigung_nennt_Zahlen_und_Benutzerdaten_und_bietet_Rueckgaengig()
    {
        _service.Preview = FakeRemovalService.PreviewOf(5);

        var result = await Create().RecycleFolderAsync(5);

        var request = _dialogs.Requests.Should().ContainSingle().Subject;
        request.Title.Should().Be("In den Papierkorb verschieben");
        request.Confirm.Should().Be("In den Papierkorb");
        request.Message.Should().Contain("„Reise“ samt Inhalt").And.Contain("4 Dateien auf der Platte, davon 3 in der Bibliothek")
            .And.Contain("1 Unterordner").And.Contain("2 mit Sternen/Favorit").And.Contain("1 mit benannten Personen").And.Contain("1 in Alben");
        _service.RecycleCalls.Should().Equal((5, false));
        result!.IsWarning.Should().BeFalse();
        result.UndoBatchId.Should().Be(7);
        result.Message.Should().Contain("in den Papierkorb verschoben");
    }

    [Fact]
    public async Task Gesperrter_Ordner_zeigt_den_Grund_ohne_Rueckfrage()
    {
        _service.Preview = FakeRemovalService.PreviewOf(5, blocked: "„Fotos“ ist eine Wurzel der Bibliothek — Wurzeln trägst du unter Einstellungen › Bibliotheken & Eingänge aus.");

        var result = await Create().RecycleFolderAsync(5);

        result!.IsWarning.Should().BeTrue();
        result.Message.Should().Contain("Einstellungen");
        _dialogs.Requests.Should().BeEmpty();
        _service.RecycleCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Ohne_Papierkorb_eigene_Rueckfrage_endgueltig_loeschen_sonst_Abbruch()
    {
        _service.Preview = FakeRemovalService.PreviewOf(5, noBin: "Wechseldatenträger ohne Papierkorb.");
        _dialogs.Answer = false;

        (await Create().RecycleFolderAsync(5)).Should().BeNull("abgebrochen");
        _service.RecycleCalls.Should().BeEmpty("ohne Zustimmung wird nichts gelöscht");
        var request = _dialogs.Requests.Single();
        request.Title.Should().Be("Endgültig löschen?");
        request.Confirm.Should().Be("Endgültig löschen");
        request.Message.Should().Contain("Dieses Laufwerk hat keinen Papierkorb — „Reise“ endgültig löschen?").And.Contain("nicht rückgängig");

        _dialogs.Answer = true;
        var result = await Create().RecycleFolderAsync(5);
        _service.RecycleCalls.Should().Equal((5, true));
        result!.UndoBatchId.Should().BeNull("endgültig gelöscht lässt sich nicht zurücknehmen");
        result.Message.Should().Contain("endgültig gelöscht");
    }

    [Fact]
    public async Task Papierkorb_erst_bei_der_Ausfuehrung_weg_fragt_noch_einmal()
    {
        _service.Preview = FakeRemovalService.PreviewOf(5);
        _service.RecycleResults.Enqueue(new FolderRecycleResult(0, false, false, 0, 0, 0, null, NeedsPermanentConsent: true, NoRecycleBinReason: "Netzlaufwerk"));

        await Create().RecycleFolderAsync(5);

        _dialogs.Requests.Select(r => r.Title).Should().Equal("In den Papierkorb verschieben", "Endgültig löschen?");
        _service.RecycleCalls.Should().Equal((5, false), (5, true));
    }

    [Fact]
    public async Task Fehlende_Bestaetigung_nennt_Zahlen_und_was_verloren_geht()
    {
        _service.Summary = new MissingSummary([1, 2, 3], 0, 2, [@"E:\"], new UserDataCounts(3, 1, 2, 1, 3));
        _service.RemoveResult = new MissingRemovalBatchResult(9, 3, 0, 2, 0, 1, 0, [@"E:\"]);

        var result = await Create().RemoveMissingAsync(new MissingTarget.InFolder(4), "in „2020“ (mit Unterordnern)");

        var request = _dialogs.Requests.Single();
        request.Title.Should().Be("Fehlende aus der Bibliothek entfernen");
        request.Message.Should().Contain("3 Dateien in „2020“ (mit Unterordnern) gibt es auf der Platte nicht mehr")
            .And.Contain("1 mit Sternen/Favorit, 2 mit benannten Personen, 1 in Alben, 3 mit Tags/Kategorien — diese Angaben gehen verloren")
            .And.Contain("2 Dateien unter nicht erreichbaren Laufwerken bleiben");
        result!.UndoBatchId.Should().Be(9);
        result.Message.Should().Contain("3 Dateien aus der Bibliothek entfernt").And.Contain("1 Ordner aus dem Baum").And.Contain("2 offline übersprungen");
    }

    [Fact]
    public async Task Gemischte_Auswahl_wird_nur_mit_Hinweis_beantwortet()
    {
        _service.Summary = new MissingSummary([1], 2, 0, [], UserDataCounts.None);

        var result = await Create().RemoveMissingAsync(new MissingTarget.Selection([1, 2, 3]), "in der Auswahl");

        result!.IsWarning.Should().BeTrue();
        result.Message.Should().Contain("Nur fehlende Dateien").And.Contain("2 Dateien der Auswahl sind vorhanden");
        _dialogs.Requests.Should().BeEmpty();
        _service.RemoveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Nur_offline_meldet_Laufwerk_nicht_erreichbar_und_entfernt_nichts()
    {
        _service.Summary = new MissingSummary([], 0, 4, [@"E:\"], UserDataCounts.None);

        var result = await Create().RemoveMissingAsync(new MissingTarget.InLibraries([1]), "in dieser Bibliothek");

        result!.Message.Should().Be(RemovalActions.OfflineText);
        result.IsWarning.Should().BeTrue();
        _dialogs.Requests.Should().BeEmpty();
        _service.RemoveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Abbruch_der_Bestaetigung_entfernt_nichts()
    {
        _service.Summary = new MissingSummary([1], 0, 0, [], UserDataCounts.None);
        _dialogs.Answer = false;

        (await Create().RemoveMissingAsync(new MissingTarget.InLibraries([1]), "in dieser Bibliothek")).Should().BeNull();
        _service.RemoveCalls.Should().BeEmpty();
    }

    [Fact]
    public void Ergebnis_wieder_da_und_Teilerfolg_in_Worten()
    {
        RemovalActions.DescribeMissingResult(new MissingRemovalBatchResult(0, 0, 2, 0, 0, 0, 0, [])).Message
            .Should().Contain("2 Dateien waren wieder da");
        var partial = RemovalActions.DescribeFolderResult("Reise", new FolderRecycleResult(3, false, false, 2, 1, 0, "gesperrt"));
        partial.IsWarning.Should().BeTrue();
        partial.Message.Should().Contain("nur teilweise").And.Contain("1 Datei geblieben");
        partial.UndoBatchId.Should().Be(3, "was im Papierkorb liegt, kommt per Rückgängig zurück");
    }
}
