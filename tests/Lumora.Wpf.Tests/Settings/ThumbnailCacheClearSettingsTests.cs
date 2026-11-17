using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Thumbnails;
using Lumora.Settings;
using Lumora.Shell;

namespace Lumora.Wpf.Tests.Settings;

/// <summary>
/// Einstellungen › Speicher &amp; Wartung › „Cache leeren“ (Nachbesserung 0.5.40): nach der Bestätigung genau ein Aufruf von
/// <see cref="IThumbnailCacheCleaner"/> (hält den Vorschaubild-Dienst an, stößt den Vorrat an); Ergebnis bzw. Fehler als Hinweis.
/// </summary>
public sealed class ThumbnailCacheClearSettingsTests
{
    [Fact]
    public async Task Leeren_laeuft_ueber_den_Dienst_und_meldet_das_Ergebnis()
    {
        var cleaner = new FakeCleaner { Result = new ThumbnailCacheClearResult(5, 1, 3) };
        var vm = Create(cleaner, confirm: true);

        await vm.ClearThumbnailCacheCommand.ExecuteAsync(null);

        cleaner.Calls.Should().Be(1);
        vm.InfoMessage.Should().Be("5 Dateien und 3 Datenbankeinträge entfernt (1 Dateien in Benutzung übersprungen).");
        vm.IsCacheBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Ohne_Bestaetigung_nichts_und_Fehler_als_Hinweis()
    {
        var cleaner = new FakeCleaner();
        var declined = Create(cleaner, confirm: false);
        await declined.ClearThumbnailCacheCommand.ExecuteAsync(null);
        cleaner.Calls.Should().Be(0);

        cleaner.Fail = true;
        var vm = Create(cleaner, confirm: true);
        await vm.ClearThumbnailCacheCommand.ExecuteAsync(null);
        vm.ErrorMessage.Should().StartWith("Cache konnte nicht geleert werden:");
        vm.IsCacheBusy.Should().BeFalse();
    }

    // Nur „Cache leeren“ wird gebraucht — die übrigen Dienste bleiben leer (Belegung zeigt dann „unbekannt“).
    private static SettingsViewModel Create(FakeCleaner cleaner, bool confirm)
        => new(null!, null!, null!, null!, null!, null!, new FakeDialogs(confirm), null!, null!, null!, NullLogger<SettingsViewModel>.Instance,
            cacheCleaner: cleaner);

    private sealed class FakeCleaner : IThumbnailCacheCleaner
    {
        public ThumbnailCacheClearResult Result { get; set; } = new(0, 0, 0);

        public bool Fail { get; set; }

        public int Calls { get; private set; }

        public Task<ThumbnailCacheClearResult> ClearAsync(CancellationToken ct = default)
        {
            Calls++;
            return Fail ? Task.FromException<ThumbnailCacheClearResult>(new IOException("Katalog gesperrt")) : Task.FromResult(Result);
        }
    }

    private sealed class FakeDialogs(bool confirm) : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
            => Task.FromResult(confirm);

        public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
            => Task.FromResult(DialogChoice.None);

        public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK")
            => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public string? PickFolder(string title) => null;
    }
}
