using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.Containers;
using Lumora.Services.HiddenLibraries.SecurityCheck;

namespace Lumora.Tests.HiddenLibraries;

/// <summary>
/// ADR-0064: Sicherheits-Check — jede Prüfung mit Fake-Lesern (Wert da/fehlt/0/1/Richtlinie überschreibt/Fehler), Wertung je
/// Gefahren-Stufe, Starter-Positivliste, Einstellungen im Katalog, Protokoll ohne Pfade/Laufwerke und ein Quelltext-Wächter:
/// nirgends Schreibzugriffe auf die Registry, kein PowerShell, kein Programmstart außer über den Starter.
/// </summary>
public sealed class SecurityCheckTests
{
    private static readonly SecurityCheckContext Context = new('C', 'X', ExitAfterLock: false);

    private readonly FakeRegistry _registry = new();
    private readonly FakeVolumes _volumes = new();
    private readonly FakeFiles _files = new();
    private readonly FakeVeraCrypt _veraCrypt = new();

    private SecurityCheckSources Sources => new(_registry, _volumes, _files, _veraCrypt);

    // ----- BitLocker -----

    [Theory]
    [InlineData(1, SecurityCheckStatus.Fulfilled)]
    [InlineData(6, SecurityCheckStatus.Fulfilled)]
    [InlineData(2, SecurityCheckStatus.NotFulfilled)]
    [InlineData(3, SecurityCheckStatus.NotFulfilled)]
    [InlineData(4, SecurityCheckStatus.NotFulfilled)]
    [InlineData(5, SecurityCheckStatus.NotFulfilled)]
    [InlineData(8, SecurityCheckStatus.NotFulfilled)]
    [InlineData(0, SecurityCheckStatus.Unknown)]
    [InlineData(99, SecurityCheckStatus.Unknown)]
    public void BitLocker_follows_the_shell_property(int value, SecurityCheckStatus expected)
    {
        _volumes.Value = value;
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.WindowsVersion, "EditionID", "Professional");
        Run(new BitLockerCheck(Sources)).Status.Should().Be(expected);
        _volumes.AskedDrives.Should().Equal('C');
    }

    [Fact]
    public void BitLocker_without_value_is_unknown_and_explains_windows_home()
    {
        _volumes.Value = null;
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.WindowsVersion, "EditionID", "Core");
        var finding = Run(new BitLockerCheck(Sources));
        finding.Status.Should().Be(SecurityCheckStatus.Unknown);
        finding.Detail.Should().Contain("Windows Home");

        _veraCrypt.Path = @"C:\Programme\VeraCrypt\VeraCrypt.exe";
        _volumes.Value = 2;
        Run(new BitLockerCheck(Sources)).Detail.Should().Contain("VeraCrypt-Systemverschlüsselung", "ehrlich: Lumora erkennt sie nicht");
    }

    // ----- Ruhezustand, Schnellstart -----

    [Theory]
    [InlineData(0, null, SecurityCheckStatus.Fulfilled)]
    [InlineData(1, null, SecurityCheckStatus.NotFulfilled)]
    [InlineData(null, 0, SecurityCheckStatus.Fulfilled)]
    [InlineData(null, 1, SecurityCheckStatus.NotFulfilled)]
    [InlineData(null, null, SecurityCheckStatus.Unknown)]
    public void Hibernation_reads_HibernateEnabled_then_the_default(int? enabled, int? fallback, SecurityCheckStatus expected)
    {
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.Power, "HibernateEnabled", enabled);
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.Power, "HibernateEnabledDefault", fallback);
        Run(new HibernationCheck(Sources)).Status.Should().Be(expected);
    }

    [Fact]
    public void Fast_startup_value_policy_and_hibernation()
    {
        var check = new FastStartupCheck(Sources);
        Run(check).Status.Should().Be(SecurityCheckStatus.Unknown, "Wert fehlt");

        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SessionPower, "HiberbootEnabled", 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled);
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SessionPower, "HiberbootEnabled", 0);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled);

        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SystemPolicies, "HiberbootEnabled", 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled, "die Richtlinie erzwingt den Schnellstart");
        Run(check).Detail.Should().Contain("Richtlinie");

        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.Power, "HibernateEnabled", 0);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled, "ohne Ruhezustand kein Schnellstart");
    }

    // ----- Auslagerungsdatei -----

    [Fact]
    public void Page_file_cleared_missing_off_and_no_page_file()
    {
        var check = new PageFileCheck(Sources);
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled, "fehlt = dokumentierter Standard „deaktiviert“");
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.MemoryManagement, "ClearPageFileAtShutdown", 0);
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled);
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.MemoryManagement, "ClearPageFileAtShutdown", 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled);
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.MemoryManagement, "ClearPageFileAtShutdown", "1");
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled, "auch als Text gespeichert");

        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.MemoryManagement, "ClearPageFileAtShutdown", null);
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.MemoryManagement, "PagingFiles", new[] { @"?:\pagefile.sys" });
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled);
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.MemoryManagement, "PagingFiles", new[] { string.Empty });
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled, "keine Auslagerungsdatei");
    }

    // ----- Zuletzt geöffnet, Aktivitätsverlauf, Miniaturbilder -----

    [Fact]
    public void Recent_items_value_default_and_policy_override()
    {
        var check = new RecentItemsCheck(Sources);
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled, "fehlt = Windows-Standard an");
        _registry.Set(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerAdvanced, "Start_TrackDocs", 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled);
        _registry.Set(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerAdvanced, "Start_TrackDocs", 0);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled);

        _registry.Set(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerAdvanced, "Start_TrackDocs", 1);
        _registry.Set(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerPolicies, "NoRecentDocsHistory", 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled, "Richtlinie (Benutzer) überschreibt");
        _registry.Set(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerPolicies, "NoRecentDocsHistory", null);
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.ExplorerPolicies, "NoRecentDocsHistory", 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled, "Richtlinie (Computer) überschreibt");
    }

    [Fact]
    public void Activity_history_only_policies_count_otherwise_unknown()
    {
        var check = new ActivityHistoryCheck(Sources);
        Run(check).Status.Should().Be(SecurityCheckStatus.Unknown, "Schalter der Einstellungen ist nicht dokumentiert — nicht raten");
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SystemPolicies, "PublishUserActivities", 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.Unknown, "Richtlinie erlaubt — der Schalter entscheidet");
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SystemPolicies, "PublishUserActivities", 0);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled);
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SystemPolicies, "PublishUserActivities", null);
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SystemPolicies, "EnableActivityFeed", 0);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled);
    }

    [Fact]
    public void Thumbnails_icons_only_and_policies()
    {
        var check = new ThumbnailCheck(Sources);
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled, "fehlt = Miniaturbilder an");
        Run(check).Detail.Should().Contain("Laufwerk X:");
        _registry.Set(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerAdvanced, "IconsOnly", 0);
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled);
        _registry.Set(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerAdvanced, "IconsOnly", 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled);

        _registry.Set(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerAdvanced, "IconsOnly", 0);
        _registry.Set(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerPolicies, "DisableThumbnails", 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled, "Richtlinie überschreibt");
        _registry.Set(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerPolicies, "DisableThumbnails", null);
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.ExplorerPolicies, "NoThumbnailCache", 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled);
    }

    // ----- Windows-Suche -----

    [Fact]
    public void Search_index_service_off_rules_on_other_drive_and_on_container_drive()
    {
        var check = new SearchIndexCheck(Sources);
        Run(check).Status.Should().Be(SecurityCheckStatus.Unknown, "Regeln nicht lesbar");

        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SearchService, "Start", 4);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled, "Dienst abgeschaltet");
        _registry.Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SearchService, "Start", 2);

        _registry.AddSearchRule("1", @"file:///C:\[59edd155-3418-49dc-b20a-46915077a0e3]\Users\", include: 1);
        _registry.AddSearchRule("2", @"file:///X:\[0000]\Temp\", include: 0);
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled, "nur C: eingeschlossen, auf X: nur ein Ausschluss");

        _registry.AddSearchRule("3", @"file:///x:\[0000]\Fotos\", include: 1);
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled, "ein Ordner auf X: ist eingeschlossen");

        Run(check, Context with { ContainerDrive = null }).Status.Should().Be(SecurityCheckStatus.Unknown);
    }

    [Theory]
    [InlineData(@"file:///X:\[59edd155-3418]\", @"x:\")]
    [InlineData(@"FILE:///x:\Fotos\", @"x:\fotos\")]
    [InlineData(@"file:///C:\[guid]\Users\*\AppData\", @"c:\users\*\appdata\")]
    [InlineData(@"file:///*", "")]
    [InlineData(@"file:///X:\[guid]\Fotos\*", @"x:\fotos\")]
    [InlineData(@"csc://{S-1-5-21}/", null)]
    [InlineData(@"winrt://{S-1-5-21}/", null)]
    [InlineData(null, null)]
    public void Search_rule_urls_are_normalized(string? url, string? expected)
        => SearchIndexCheck.NormalizeRule(url).Should().Be(expected);

    [Fact]
    public void Search_rule_precedence_most_specific_wins()
    {
        SearchIndexCheck.IsDriveIndexed([(@"c:\users\", true)], 'X').Should().BeFalse();
        SearchIndexCheck.IsDriveIndexed([(@"x:\", true)], 'X').Should().BeTrue();
        SearchIndexCheck.IsDriveIndexed([(@"x:\fotos\", true), (@"x:\", false)], 'X').Should().BeTrue("ein Ordner darauf ist eingeschlossen");
        SearchIndexCheck.IsDriveIndexed([("", true)], 'X').Should().BeTrue("Regel für alles");
        SearchIndexCheck.IsDriveIndexed([("", true), (@"x:\", false)], 'X').Should().BeFalse("Laufwerk ausdrücklich ausgeschlossen");
        SearchIndexCheck.IsDriveIndexed([(@"x:\temp\", false)], 'X').Should().BeFalse();
    }

    // ----- VeraCrypt -----

    [Fact]
    public void VeraCrypt_not_installed_is_not_applicable()
    {
        _veraCrypt.Path = null;
        Run(new VeraCryptHistoryCheck(Sources)).Status.Should().Be(SecurityCheckStatus.NotApplicable);
    }

    [Fact]
    public void VeraCrypt_history_setting_default_and_leftover_list()
    {
        _veraCrypt.Path = @"C:\Programme\VeraCrypt\VeraCrypt.exe";
        var check = new VeraCryptHistoryCheck(Sources);
        var config = Path.Combine(_files.ApplicationDataFolder, "VeraCrypt", "Configuration.xml");
        var history = Path.Combine(_files.ApplicationDataFolder, "VeraCrypt", "History.xml");

        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled, "ohne Datei gilt VeraCrypts Standard (kein Verlauf)");
        _files.Texts[config] = Config("1");
        Run(check).Status.Should().Be(SecurityCheckStatus.NotFulfilled);
        _files.Texts[config] = Config("0");
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled);
        _files.Texts[config] = "<VeraCrypt><configuration><config key=\"OpenExplorerWindowAfterMount\">1</config></configuration></VeraCrypt>";
        Run(check).Status.Should().Be(SecurityCheckStatus.Fulfilled, "Wert fehlt = Standard 0");
        _files.Texts[config] = "<VeraCrypt><kaputt";
        Run(check).Status.Should().Be(SecurityCheckStatus.Unknown);
        _files.Texts[config] = Config("7");
        Run(check).Status.Should().Be(SecurityCheckStatus.Unknown);

        _files.Texts[config] = Config("0");
        _files.Texts[history] = "<VeraCrypt><history><volume>Q:\\geheim.hc</volume></history></VeraCrypt>";
        var leftover = Run(check);
        leftover.Status.Should().Be(SecurityCheckStatus.NotFulfilled, "eine alte Liste liegt noch vor");
        leftover.Detail.Should().NotContain("geheim", "Pfade aus der Liste erscheinen nirgends");

        static string Config(string value) => $"<VeraCrypt><configuration><config key=\"SaveVolumeHistory\">{value}</config></configuration></VeraCrypt>";
    }

    [Fact]
    public void Exit_after_lock_follows_the_lumora_setting()
    {
        Run(new ExitAfterLockCheck()).Status.Should().Be(SecurityCheckStatus.NotFulfilled);
        Run(new ExitAfterLockCheck(), Context with { ExitAfterLock = true }).Status.Should().Be(SecurityCheckStatus.Fulfilled);
    }

    // ----- Wertung je Gefahren-Stufe -----

    [Fact]
    public void Rating_per_threat_level()
    {
        var findings = Enum.GetValues<SecurityCheckId>()
            .Select(id => new SecurityCheckFinding(id, id is SecurityCheckId.ActivityHistory or SecurityCheckId.ExitAfterLock ? SecurityCheckStatus.Fulfilled : SecurityCheckStatus.NotFulfilled, "x"))
            .ToList();
        var report = new SecurityCheckReport(findings, ThreatLevel.SharedUser);
        (report.FulfilledCount, report.CountedTotal).Should().Be((1, 9), "Mitbenutzer: alle Windows-Punkte, Lumora-Option grau");
        report.StatusText.Should().Be("Sicherheits-Check: 1 von 9 erfüllt");

        var off = report.WithLevel(ThreatLevel.PoweredOff);
        (off.FulfilledCount, off.CountedTotal).Should().Be((0, 1), "ausgeschalteter PC: nur BitLocker");
        var forensic = report.WithLevel(ThreatLevel.Forensic);
        (forensic.FulfilledCount, forensic.CountedTotal).Should().Be((2, 10), "fachmännisch: alles inkl. Lumora-Option");

        SecurityCheckRating.WeightOf(SecurityCheckId.BitLocker, ThreatLevel.PoweredOff).Should().Be(SecurityCheckWeight.Critical);
        SecurityCheckRating.WeightOf(SecurityCheckId.Thumbnails, ThreatLevel.PoweredOff).Should().Be(SecurityCheckWeight.NotCounted);
        SecurityCheckRating.WeightOf(SecurityCheckId.Thumbnails, ThreatLevel.SharedUser).Should().Be(SecurityCheckWeight.Recommended);
        SecurityCheckRating.WeightOf(SecurityCheckId.ExitAfterLock, ThreatLevel.SharedUser).Should().Be(SecurityCheckWeight.NotCounted);
        SecurityCheckRating.WeightOf(SecurityCheckId.ExitAfterLock, ThreatLevel.Forensic).Should().Be(SecurityCheckWeight.Recommended);

        // nicht zutreffend zählt nie, unbekannt zählt als nicht erfüllt
        var mixed = new SecurityCheckReport(
            [new(SecurityCheckId.VeraCryptHistory, SecurityCheckStatus.NotApplicable, "x"), new(SecurityCheckId.Hibernation, SecurityCheckStatus.Unknown, "x")],
            ThreatLevel.SharedUser);
        (mixed.FulfilledCount, mixed.CountedTotal).Should().Be((0, 1));

        SecurityCheckRating.ParseLevel("1").Should().Be(ThreatLevel.PoweredOff);
        SecurityCheckRating.ParseLevel("3").Should().Be(ThreatLevel.Forensic);
        SecurityCheckRating.ParseLevel(null).Should().Be(ThreatLevel.SharedUser, "Standard Mitbenutzer");
        SecurityCheckRating.ParseLevel("9").Should().Be(ThreatLevel.SharedUser);
        SecurityCheckRating.LevelNote(ThreatLevel.PoweredOff).Should().Contain("genügt BitLocker");
    }

    // ----- Starter-Positivliste -----

    [Fact]
    public void Launcher_opens_only_allowed_targets()
    {
        var shell = new FakeShell();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Windows\System32\secpol.msc", @"C:\Windows\System32\BitLockerWizardElev.exe", @"C:\Programme\VeraCrypt\VeraCrypt.exe" };
        _veraCrypt.Path = @"C:\Programme\VeraCrypt\VeraCrypt.exe";
        var launcher = new SecuritySettingsLauncher(shell, _veraCrypt, existing.Contains, @"C:\Windows\System32");

        foreach (var target in Enum.GetValues<SettingsTarget>().Where(t => t != SettingsTarget.None))
        {
            launcher.Open(target).Should().BeTrue(target.ToString());
        }

        shell.Started.Should().Equal(
            (@"C:\Windows\System32\control.exe", "/name Microsoft.BitLockerDriveEncryption"),
            (@"C:\Windows\System32\control.exe", "/name Microsoft.PowerOptions /page pageGlobalSettings"),
            (@"C:\Windows\System32\secpol.msc", null),
            ("ms-settings:personalization-start", null),
            ("ms-settings:privacy-activityhistory", null),
            (@"C:\Windows\System32\control.exe", "folders"),
            (@"C:\Windows\System32\control.exe", "/name Microsoft.IndexingOptions"),
            (@"C:\Programme\VeraCrypt\VeraCrypt.exe", null));

        launcher.Open(SettingsTarget.None).Should().BeFalse();
        launcher.Open((SettingsTarget)42).Should().BeFalse("unbekannte Ziele gibt es nicht");
        shell.Started.Should().HaveCount(8);
    }

    [Fact]
    public void Launcher_without_secpol_or_bitlocker_shows_the_way()
    {
        var shell = new FakeShell();
        var launcher = new SecuritySettingsLauncher(shell, _veraCrypt, _ => false, @"C:\Windows\System32");
        launcher.IsAvailable(SettingsTarget.SecurityPolicy).Should().BeFalse("Windows Home hat kein secpol.msc — dann gilt die Anleitung");
        launcher.Open(SettingsTarget.SecurityPolicy).Should().BeFalse();
        launcher.IsAvailable(SettingsTarget.VeraCrypt).Should().BeFalse();
        launcher.Open(SettingsTarget.BitLocker).Should().BeTrue();
        shell.Started.Should().Equal(("ms-settings:deviceencryption", null));
    }

    [Theory]
    [InlineData(@"C:\Programme\VeraCrypt\VeraCrypt.exe", true)]
    [InlineData(@"C:\Program Files\VeraCrypt\VeraCrypt.exe", true)]
    [InlineData(@"C:\x\VeraCrypt.exe"" /q /dismount", false)]
    [InlineData(@"C:\x\VeraCrypt.exe & calc.exe", false)]
    [InlineData(@"C:\x\VeraCrypt.exe | calc", false)]
    [InlineData(@"C:\x\cmd.exe", false)]
    [InlineData(@"VeraCrypt.exe", false)]
    [InlineData(@"\\server\share\VeraCrypt.exe", false)]
    [InlineData(@"C:\x /c\VeraCrypt.exe", false)]
    [InlineData(@"C:\%TEMP%\VeraCrypt.exe", false)]
    [InlineData(null, false)]
    public void Launcher_accepts_only_a_plain_veracrypt_path(string? path, bool expected)
    {
        SecuritySettingsLauncher.IsPlainVeraCryptPath(path).Should().Be(expected);
        var shell = new FakeShell();
        _veraCrypt.Path = path;
        new SecuritySettingsLauncher(shell, _veraCrypt, _ => true, @"C:\Windows\System32").Open(SettingsTarget.VeraCrypt).Should().Be(expected);
        shell.Started.Should().HaveCount(expected ? 1 : 0);
        if (expected)
        {
            shell.Started[0].Arguments.Should().BeNull("VeraCrypt wird ohne Befehlszeile geöffnet");
        }
    }

    [Fact]
    public void Launcher_second_guard_rejects_foreign_launches()
    {
        var launcher = new SecuritySettingsLauncher(new FakeShell(), _veraCrypt, _ => true, @"C:\Windows\System32");
        launcher.IsAllowed(new SettingsLaunch("ms-settings:privacy-activityhistory", null)).Should().BeTrue();
        launcher.IsAllowed(new SettingsLaunch("ms-settings:windowsupdate-action", null)).Should().BeFalse();
        launcher.IsAllowed(new SettingsLaunch("ms-settings:privacy-activityhistory", "x")).Should().BeFalse();
        launcher.IsAllowed(new SettingsLaunch(@"C:\Windows\System32\control.exe", "folders")).Should().BeTrue();
        launcher.IsAllowed(new SettingsLaunch(@"C:\Windows\System32\control.exe", "folders & calc")).Should().BeFalse();
        launcher.IsAllowed(new SettingsLaunch(@"C:\Windows\System32\control.exe", "/name Microsoft.UserAccounts")).Should().BeFalse();
        launcher.IsAllowed(new SettingsLaunch(@"C:\Windows\System32\secpol.msc", "/s")).Should().BeFalse();
        launcher.IsAllowed(new SettingsLaunch(@"C:\Windows\System32\cmd.exe", null)).Should().BeFalse();
        launcher.IsAllowed(new SettingsLaunch(@"C:\Windows\System32\regedit.exe", null)).Should().BeFalse();
    }

    // ----- Dienst: Fehler → Unbekannt, Protokoll ohne Pfade, Einstellungen -----

    [Fact]
    public async Task Service_turns_reader_errors_into_unknown_and_logs_no_paths_or_drives()
    {
        _registry.Throw = new UnauthorizedAccessException(@"Zugriff auf X:\Tresor\geheim verweigert");
        _volumes.Throw = new IOException(@"Laufwerk X:\ nicht bereit");
        _veraCrypt.Path = @"X:\Tresor\VeraCrypt.exe";
        var logger = new CapturingLogger<SecurityCheckService>();
        var messenger = new WeakReferenceMessenger();
        var sent = new List<string>();
        messenger.Register<SecurityCheckSummaryMessage>(this, (_, m) => sent.Add(m.Text));
        var store = new MemoryStore { ThrowOnLoad = true };
        var service = new SecurityCheckService(AllChecks(), store, () => 'X', logger, messenger);

        var report = await service.RunAsync();

        report.Findings.Select(f => f.Id).Should().Equal(Enum.GetValues<SecurityCheckId>(), "alle Punkte, in Anzeigereihenfolge");
        report.Findings.Where(f => f.Id is not SecurityCheckId.ExitAfterLock and not SecurityCheckId.VeraCryptHistory)
            .Should().OnlyContain(f => f.Status == SecurityCheckStatus.Unknown, "Leser-Fehler → Unbekannt, nie Absturz");
        report.Findings.Single(f => f.Id == SecurityCheckId.Hibernation).Detail.Should().Contain("nicht geliefert");
        report.Level.Should().Be(ThreatLevel.SharedUser, "Einstellungen nicht lesbar → Standard");
        sent.Should().Equal(report.StatusText);

        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().OnlyContain(l => !Regex.IsMatch(l, @"[A-Za-z]:\\") && !l.Contains("Tresor", StringComparison.Ordinal) && !l.Contains("geheim", StringComparison.Ordinal),
            "nie Pfade oder Laufwerksbuchstaben ins Protokoll");
        logger.Exceptions.Should().BeEmpty("keine Ausnahme-Objekte (Meldungen könnten Pfade enthalten)");
    }

    [Fact]
    public async Task Service_saves_level_and_exit_option_and_rerates_without_reading_again()
    {
        var store = new MemoryStore();
        var service = new SecurityCheckService(AllChecks(), store, () => 'X', NullLogger<SecurityCheckService>.Instance, new WeakReferenceMessenger());
        (await service.SetLevelAsync(ThreatLevel.Forensic)).Should().BeNull("noch nicht geprüft");
        store.Saved.Should().Be(new SecurityCheckSettings(ThreatLevel.Forensic, false));

        var first = await service.RunAsync();
        first.Level.Should().Be(ThreatLevel.Forensic);
        var reads = _registry.Reads;

        var shared = await service.SetLevelAsync(ThreatLevel.SharedUser);
        shared!.Level.Should().Be(ThreatLevel.SharedUser);
        var on = await service.SetExitAfterLockAsync(true);
        on!.Findings.Single(f => f.Id == SecurityCheckId.ExitAfterLock).Status.Should().Be(SecurityCheckStatus.Fulfilled);
        _registry.Reads.Should().Be(reads, "Umstellen liest Windows nicht neu");
        store.Saved.Should().Be(new SecurityCheckSettings(ThreatLevel.SharedUser, true));
        service.LastContext!.ContainerDrive.Should().Be('X');
    }

    [Fact]
    public async Task Changing_one_option_keeps_the_other_and_a_closed_session_sends_nothing()
    {
        var store = new MemoryStore();
        await store.SaveAsync(new SecurityCheckSettings(ThreatLevel.Forensic, ExitAfterLock: true));
        var messenger = new WeakReferenceMessenger();
        var sent = new List<string>();
        messenger.Register<SecurityCheckSummaryMessage>(this, (_, m) => sent.Add(m.Text));
        var service = new SecurityCheckService(AllChecks(), store, () => 'X', NullLogger<SecurityCheckService>.Instance, messenger);

        service.HasSettings.Should().BeFalse();
        await service.SetLevelAsync(ThreatLevel.SharedUser);
        store.Saved.Should().Be(new SecurityCheckSettings(ThreatLevel.SharedUser, ExitAfterLock: true), "die andere Option bleibt erhalten");
        service.HasSettings.Should().BeTrue();
        service.Settings.ExitAfterLock.Should().BeTrue();

        store.ThrowOnSave = true;
        var failing = () => service.SetExitAfterLockAsync(false);
        await failing.Should().ThrowAsync<IOException>();
        service.Settings.ExitAfterLock.Should().BeTrue("Speichern gescheitert → alter Wert gilt");

        service.Dispose();
        var late = () => service.RunAsync();
        await late.Should().ThrowAsync<OperationCanceledException>("Sitzung geschlossen");
        sent.Should().BeEmpty("nie in die Statusleiste der nächsten Sitzung");
    }

    [Fact]
    public async Task Settings_live_in_the_catalog_settings_table()
    {
        var fx = await Fixtures.TempLibraryFixture.CreateAsync();
        using (fx)
        {
            var store = new SecurityCheckSettingsStore(fx.ContextFactory);
            (await store.LoadAsync()).Should().Be(SecurityCheckSettings.Default, "Standard: Mitbenutzer, nicht beenden");
            await store.SaveAsync(new SecurityCheckSettings(ThreatLevel.PoweredOff, true));
            (await store.LoadAsync()).Should().Be(new SecurityCheckSettings(ThreatLevel.PoweredOff, true));
            await store.SaveAsync(new SecurityCheckSettings((ThreatLevel)77, false));
            (await store.LoadAsync()).Level.Should().Be(ThreatLevel.SharedUser, "ungültige Stufe → Standard");
        }
    }

    // ----- Quelltext-Wächter -----

    [Fact]
    public void Source_never_writes_the_registry_or_starts_scripts()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var folders = new[]
        {
            Path.Combine(src, "Lumora.Services", "HiddenLibraries", "SecurityCheck"),
            Path.Combine(src, "Lumora.Wpf", "Shell", "HiddenLibraries", "SecurityCheck"),
        };
        var files = folders.SelectMany(f => Directory.EnumerateFiles(f, "*.cs", SearchOption.AllDirectories))
            .Append(Path.Combine(src, "Lumora.Wpf", "Settings", "Sections", "SettingsSecurityCheckSection.xaml"))
            .Append(Path.Combine(src, "Lumora.Wpf", "Shell", "Smoke", "SmokeSecurityCheckFakes.cs"))
            .ToList();
        files.Should().HaveCountGreaterThan(8);
        string[] forbidden =
        [
            "SetValue(", "CreateSubKey", "DeleteValue", "DeleteSubKey", "writable: true", "ReadWriteSubTree", "RegistryRights",
            "powershell", "pwsh", "reg.exe", "regedit.exe", "\"runas\"", "Verb =", "ProcessStartInfo", "Process.Start", "IElevatedRunner",
            "EncodedCommand", "powercfg.exe", "cmd.exe", "WMI", "ManagementObject",
        ];
        foreach (var file in files)
        {
            var code = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal) && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            code.Should().NotContainEquivalentOf("powershell", $"{Path.GetFileName(file)}: kein Skript-Interpreter");
            foreach (var word in forbidden)
            {
                code.Should().NotContain(word, $"{Path.GetFileName(file)}: Lumora liest nur und ändert keine Windows-Einstellung");
            }
        }

        // OpenSubKey nur mit einem Argument (= nur lesend)
        var reader = File.ReadAllText(Path.Combine(folders[0], "SecurityCheckSources.cs"));
        Regex.Matches(reader, @"OpenSubKey\(([^)]*)\)").Select(m => m.Groups[1].Value).Should().OnlyContain(a => !a.Contains(','), "nur lesend öffnen");
        // „powercfg“ steht nur als Text zum Nachlesen in der Anleitung
        Directory.EnumerateFiles(folders[0], "*.cs").Where(f => File.ReadAllText(f).Contains("powercfg", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).Should().Equal("SecurityCheckCatalog.cs");
    }

    // ----- Hilfen -----

    private static SecurityCheckFinding Run(ISecurityCheck check, SecurityCheckContext? context = null) => check.Run(context ?? Context);

    private IEnumerable<ISecurityCheck> AllChecks() =>
    [
        new ExitAfterLockCheck(), new BitLockerCheck(Sources), new HibernationCheck(Sources), new FastStartupCheck(Sources), new PageFileCheck(Sources),
        new RecentItemsCheck(Sources), new ActivityHistoryCheck(Sources), new ThumbnailCheck(Sources), new SearchIndexCheck(Sources), new VeraCryptHistoryCheck(Sources),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumora.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo nicht gefunden");
    }

    private sealed class FakeRegistry : IRegistryReader
    {
        private readonly Dictionary<(RegistryRoot, string, string), object> _values = new();
        private readonly List<string> _rules = new();

        public Exception? Throw { get; set; }

        public int Reads { get; private set; }

        public void Set(RegistryRoot root, string key, string name, object? value)
        {
            if (value is null)
            {
                _values.Remove((root, key, name));
            }
            else
            {
                _values[(root, key, name)] = value;
            }
        }

        public void AddSearchRule(string name, string url, int include)
        {
            _rules.Add(name);
            Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SearchRules + "\\" + name, "URL", url);
            Set(RegistryRoot.LocalMachine, SecurityCheckKeys.SearchRules + "\\" + name, "Include", include);
        }

        public object? ReadValue(RegistryRoot root, string subKey, string valueName)
        {
            Reads++;
            if (Throw is not null)
            {
                throw Throw;
            }

            return _values.TryGetValue((root, subKey, valueName), out var value) ? value : null;
        }

        public IReadOnlyList<string>? GetSubKeyNames(RegistryRoot root, string subKey)
        {
            Reads++;
            if (Throw is not null)
            {
                throw Throw;
            }

            return subKey == SecurityCheckKeys.SearchRules && _rules.Count > 0 ? _rules.ToList() : null;
        }
    }

    private sealed class FakeVolumes : IVolumePropertyReader
    {
        public int? Value { get; set; }

        public Exception? Throw { get; set; }

        public List<char> AskedDrives { get; } = new();

        public int? ReadBitLockerProtection(char driveLetter)
        {
            AskedDrives.Add(driveLetter);
            return Throw is null ? Value : throw Throw;
        }
    }

    private sealed class FakeFiles : IUserFileReader
    {
        public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string ApplicationDataFolder => @"Q:\Benutzer\AppData\Roaming";

        public string? ReadText(string path) => Texts.TryGetValue(path, out var text) ? text : null;
    }

    private sealed class FakeVeraCrypt : IVeraCryptLocator
    {
        public string? Path { get; set; }

        public string? FindExecutable() => Path;
    }

    private sealed class FakeShell : IShellLauncher
    {
        public List<(string FileName, string? Arguments)> Started { get; } = new();

        public ShellLaunchResult Start(string fileName, string? argumentLine)
        {
            Started.Add((fileName, argumentLine));
            return ShellLaunchResult.Started;
        }

        public bool OpenUrl(string url) => false;
    }

    private sealed class MemoryStore : ISecurityCheckSettingsStore
    {
        public SecurityCheckSettings Saved { get; private set; } = SecurityCheckSettings.Default;

        public bool ThrowOnLoad { get; set; }

        public Task<SecurityCheckSettings> LoadAsync(CancellationToken ct = default)
            => ThrowOnLoad ? throw new IOException(@"X:\Tresor\.lumora\library.db gesperrt") : Task.FromResult(Saved);

        public bool ThrowOnSave { get; set; }

        public Task SaveAsync(SecurityCheckSettings settings, CancellationToken ct = default)
        {
            if (ThrowOnSave)
            {
                throw new IOException("Katalog gesperrt");
            }

            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();

        public List<Exception> Exceptions { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
    }
}
