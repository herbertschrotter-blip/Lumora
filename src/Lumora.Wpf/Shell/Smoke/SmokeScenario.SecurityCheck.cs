using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Services.HiddenLibraries.SecurityCheck;
using Lumora.Settings;
using Lumora.Shell.HiddenLibraries.SecurityCheck;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Teil „Sicherheits-Check“ (ADR-0064) im Schritt „Versteckte Bibliothek“: in der versteckten Sitzung mit
/// Fake-Lesern (<see cref="SmokeSecurityCheckFakes"/>) — Statusleiste, Reiter, Wertung, Gefahren-Stufe, „Erfüllte
/// ausblenden“, „Einstellung öffnen“ (Fake, öffnet nichts), Anleitung, Screenshots hell/dunkel. Danach in der normalen
/// Sitzung: kein Reiter, kein Dienst, kein Statusleisten-Text, keine Einstellung im normalen Katalog. Nichts an Windows.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task SecurityCheckInHiddenSessionAsync(IServiceProvider hidden)
    {
        var navigation = hidden.GetRequiredService<INavigationService>();
        var sections = hidden.GetRequiredService<SettingsSectionState>();
        var page = hidden.GetRequiredService<SettingsPage>();
        var status = hidden.GetRequiredService<StatusBarViewModel>();
        var vm = hidden.GetRequiredService<SecurityCheckViewModel>();
        var launcher = hidden.GetRequiredService<ISecuritySettingsLauncher>() as SmokeSecurityCheckFakes.Launcher
                       ?? throw new SmokeCheckException("Starter-Fake fehlt (Rauchtest darf nichts öffnen)");
        var expected = $"Sicherheits-Check: {SmokeSecurityCheckFakes.ExpectedFulfilledSharedUser} von {SmokeSecurityCheckFakes.ExpectedTotalSharedUser} erfüllt";

        // (a) Erste Prüfung kurz nach dem Öffnen → Statusleiste
        await WaitUntilAsync(() => status.SecurityCheckText is not null, LongWait, "Statusleiste: Sicherheits-Check").ConfigureAwait(true);
        Check(status.SecurityCheckText == expected, $"Statusleiste: {status.SecurityCheckText}");

        // (b) Reiter öffnen (hinter „Versteckte Bibliotheken“)
        navigation.NavigateTo(NavigationTags.SettingsSecurityCheck);
        await WaitUntilAsync(
            () => navigation.CurrentTag == NavigationTags.Settings && sections.SelectedSection == NavigationTags.SettingsSecurityCheck
                  && page.SelectedTabTag == NavigationTags.SettingsSecurityCheck,
            ShortWait,
            "Reiter Sicherheits-Check").ConfigureAwait(true);
        var tags = page.Tabs.Items.OfType<TabItem>().Select(t => t.Tag as string).ToList();
        Check(tags.IndexOf(NavigationTags.SettingsSecurityCheck) == tags.IndexOf(NavigationTags.SettingsHiddenLibraries) + 1, "Reiter hinter „Versteckte Bibliotheken“");
        await WaitUntilAsync(() => vm.Rows.Count == 10 && !vm.IsBusy, ShortWait, "Punkte geladen").ConfigureAwait(true);
        await vm.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(vm.FulfilledCount == SmokeSecurityCheckFakes.ExpectedFulfilledSharedUser && vm.CountedTotal == SmokeSecurityCheckFakes.ExpectedTotalSharedUser && vm.Level == ThreatLevel.SharedUser,
            $"Erneut prüfen: {vm.SummaryText} bei „Mitbenutzer“");
        Check(vm.Rows.Single(r => r.Id == SecurityCheckId.BitLocker).Dot == SecurityCheckDot.Critical, "BitLocker aus = rot, wichtigster Punkt");
        Check(vm.Rows.Single(r => r.Id == SecurityCheckId.ActivityHistory).Dot == SecurityCheckDot.Ok, "Aktivitätsverlauf per Richtlinie aus = grün");

        // (c) Gefahren-Stufe bestimmt die Wertung (Einstellung im versteckten Katalog)
        vm.Level = ThreatLevel.PoweredOff;
        await WaitUntilAsync(() => vm.CountedTotal == 1, ShortWait, "Stufe „Ausgeschalteter PC“: nur BitLocker zählt").ConfigureAwait(true);
        Check(vm.Rows.Single(r => r.Id == SecurityCheckId.Thumbnails).Dot == SecurityCheckDot.Grey, "übrige Punkte grau");
        vm.Level = ThreatLevel.SharedUser;
        await WaitUntilAsync(() => vm.CountedTotal == SmokeSecurityCheckFakes.ExpectedTotalSharedUser, ShortWait, "zurück zu „Mitbenutzer“").ConfigureAwait(true);

        // (d) Erfüllte ausblenden, Einstellung öffnen (Fake), Anleitung
        vm.HideFulfilled = true;
        Check(vm.Rows.Count(r => !r.IsVisible) == SmokeSecurityCheckFakes.ExpectedFulfilledSharedUser, "Erfüllte ausgeblendet");
        vm.HideFulfilled = false;
        vm.Rows.Single(r => r.Id == SecurityCheckId.Thumbnails).OpenCommand.Execute(null);
        Check(launcher.Opened.SequenceEqual([SettingsTarget.FolderOptions]), "„Einstellung öffnen“ geht nur an den Starter (Fake)");
        var bitLocker = vm.Rows.Single(r => r.Id == SecurityCheckId.BitLocker);
        bitLocker.ToggleHowtoCommand.Execute(null);
        Check(bitLocker.IsHowtoOpen && !vm.ExitAfterLock, "Anleitung aufgeklappt; „ganz beenden“ bleibt aus");

        // (e) Screenshots hell/dunkel (Theme der versteckten Sitzung, danach zurück)
        var theme = hidden.GetRequiredService<IThemeService>();
        var before = theme.Mode;
        await theme.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await SnapshotAsync("70-sicherheits-check-hell").ConfigureAwait(true);
        await theme.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("71-sicherheits-check-dunkel").ConfigureAwait(true);
        await theme.SetModeAsync(before).ConfigureAwait(true);
        bitLocker.IsHowtoOpen = false;
        navigation.NavigateTo(NavigationTags.Gallery);
    }

    private async Task NoSecurityCheckInNormalSessionAsync(SmokeScenario next, IServiceProvider services)
    {
        next._navigation.NavigateTo(NavigationTags.Settings);
        await WaitUntilAsync(() => next._navigation.CurrentTag == NavigationTags.Settings, ShortWait, "normale Einstellungen").ConfigureAwait(true);
        var tags = next._settingsPage.Tabs.Items.OfType<TabItem>().Select(t => t.Tag as string).ToList();
        Check(!tags.Contains(NavigationTags.SettingsSecurityCheck) && !tags.Contains(NavigationTags.SettingsHiddenLibraries), "normale Sitzung: kein Reiter Sicherheits-Check");
        Check(services.GetService<ISecurityCheckService>() is null && services.GetService<SecurityCheckViewModel>() is null, "normale Sitzung: kein Sicherheits-Check-Dienst");
        Check(services.GetRequiredService<StatusBarViewModel>().SecurityCheckText is null, "normale Sitzung: nichts in der Statusleiste");
        var rows = await next.DbScalarAsync<int>("SELECT COUNT(*) FROM Settings WHERE Key LIKE 'securitycheck.%'").ConfigureAwait(true);
        Check(rows == 0, "normaler Katalog: keine Einstellung des Sicherheits-Checks");
        next._navigation.NavigateTo(NavigationTags.Gallery);
    }
}
