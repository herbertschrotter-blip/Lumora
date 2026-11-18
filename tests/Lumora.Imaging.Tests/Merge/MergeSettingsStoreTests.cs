using Lumora.Imaging.Merge;
using Lumora.Tests.Fixtures;

namespace Lumora.Imaging.Tests.Merge;

/// <summary>
/// Einstellungen › Zusammenfügen: gleichzeitige Speichervorgänge (Fenster merkt Kugel-Form und Farbe, die Einstellungs-Seite
/// speichert alles) dürfen auf einem leeren Katalog nicht mit „UNIQUE constraint failed: Settings.Key“ scheitern (Rauchtest 0.5.48).
/// </summary>
public sealed class MergeSettingsStoreTests : IAsyncLifetime
{
    private TempLibraryFixture _fx = null!;

    public async Task InitializeAsync() => _fx = await TempLibraryFixture.CreateAsync();

    public Task DisposeAsync()
    {
        _fx.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Gleichzeitiges_Speichern_und_Merken_auf_leerem_Katalog_scheitert_nicht()
    {
        var store = new MergeSettingsStore(_fx.ContextFactory);
        var full = MergeSettings.Default with { SphereForm = MergeSphereForm.Full, ColorBoost = 80 };

        var writes = Enumerable.Range(0, 24).Select(i => (i % 3) switch
        {
            0 => Task.Run(() => store.SaveAsync(full)),
            1 => Task.Run(() => store.RememberSphereFormAsync(MergeSphereForm.Full)),
            _ => Task.Run(() => store.RememberColorBoostAsync(80)),
        });

        var act = () => Task.WhenAll(writes);
        await act.Should().NotThrowAsync();

        var loaded = await store.LoadAsync();
        loaded.SphereForm.Should().Be(MergeSphereForm.Full);
        loaded.ColorBoost.Should().Be(80);
    }

    [Fact]
    public async Task Merken_ueberschreibt_nur_den_eigenen_Schluessel()
    {
        var store = new MergeSettingsStore(_fx.ContextFactory);
        await store.SaveAsync(MergeSettings.Default with { SubfolderName = "Ergebnisse", ColorBoost = 120 });

        await store.RememberColorBoostAsync(40);
        await store.RememberSphereFormAsync(MergeSphereForm.Full);

        var loaded = await store.LoadAsync();
        loaded.ColorBoost.Should().Be(40);
        loaded.SphereForm.Should().Be(MergeSphereForm.Full);
        loaded.SubfolderName.Should().Be("Ergebnisse", "Merken schreibt nur seinen eigenen Schlüssel");
    }
}
