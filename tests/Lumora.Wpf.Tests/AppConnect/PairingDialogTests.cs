using Lumora.AppConnect;
using Lumora.Wpf.Tests.Fixtures;
using Wpf.Ui.Appearance;

namespace Lumora.Wpf.Tests.AppConnect;

/// <summary>
/// Kopplungsdialog der App-Verbindung (ADR-0073, Stufe 2, Mockup 11 „Neue App koppeln“): Kopf, Code, Zeilen mit
/// Voreinstellung, „Verbinden“ gibt die gewählten Rechte, Ablauf schließt ihn ohne Entscheidung; hell/dunkel.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PairingDialogTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static PairingPromptInfo GipfelbuchInfo() => new("gipfelbuch", "Gipfelbuch", "482913",
        PairingPermissions.Lines(
            ["photos.search", "photos.thumbnail", "photos.original", "photos.people", "background.start"],
            ["tours.read", "tracks.read", "summits.read", "background.start"]));

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    [Fact]
    public Task Dialog_zeigt_Code_und_Zeilen_und_Verbinden_gibt_die_Haekchen_zurueck() => OnUiAsync(async () =>
    {
        var prompt = new WindowPairingPrompt(TestUiThread.Dispatcher, () => null);
        var asked = prompt.AskAsync(GipfelbuchInfo(), CancellationToken.None);
        await WaitAsync(() => prompt.Current is { IsVisible: true });
        var dialog = prompt.Current!;

        dialog.HeadText.Text.Should().Be("Gipfelbuch möchte sich verbinden");
        dialog.CodeText.Text.Should().Be("482 913");
        dialog.CheckText.Text.Should().Contain("derselbe Code");
        dialog.Choices.Select(c => (c.Text, c.IsChecked)).Should().Equal(
            ("Fotos suchen, Vorschaubilder abrufen, Originale öffnen", true),
            ("Personen auf den Fotos sehen", false),
            ("Touren, GPS-Spuren und Gipfel an Lumora geben", true),
            ("Sich gegenseitig im Hintergrund starten", false));
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        {
            ApplicationThemeManager.Apply(theme);
            dialog.UpdateLayout();
            dialog.ActualHeight.Should().BeGreaterThan(300);
        }

        dialog.Choices.Single(c => c.Permission.Key == "background").IsChecked = true;
        dialog.Accept();
        var decision = await asked;

        decision!.GrantedRights.Should().Equal("photos.search", "photos.thumbnail", "photos.original", "background.start");
        decision.AcceptedOfferedRights.Should().Equal("tours.read", "tracks.read", "summits.read", "background.start");
        prompt.Current.Should().BeNull();
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
    });

    [Fact]
    public Task Abgelaufene_Anfrage_schliesst_den_Dialog_ohne_Entscheidung() => OnUiAsync(async () =>
    {
        var prompt = new WindowPairingPrompt(TestUiThread.Dispatcher, () => null);
        using var expiry = new CancellationTokenSource();
        var asked = prompt.AskAsync(GipfelbuchInfo(), expiry.Token);
        await WaitAsync(() => prompt.Current is { IsVisible: true });

        expiry.Cancel();
        (await asked.WaitAsync(Timeout)).Should().BeNull("abgelaufen = keine Kopplung");
        await WaitAsync(() => prompt.Current is null); // das Fenster schließt kurz danach auf dem UI-Thread
    });

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue();
    }
}
