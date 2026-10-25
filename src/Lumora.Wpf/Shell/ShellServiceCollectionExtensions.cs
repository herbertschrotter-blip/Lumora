using Microsoft.Extensions.DependencyInjection;
using Lumora.Folders;
using Lumora.Libraries;
using Lumora.Settings;
using Wpf.Ui.Abstractions;

namespace Lumora.Shell;

/// <summary>
/// Wpf-seitige Registrierungen der Shell: Navigation, Dialoge, Statusleiste, Theme sowie die Seiten
/// Bibliothek / Ordner / Einstellungen mit ihren ViewModels. Wird in App.xaml.cs mit einer Zeile aufgerufen.
/// Seiten sind Singletons — der NavigationPageProvider cached sie zusätzlich je Typ.
/// </summary>
public static class ShellServiceCollectionExtensions
{
    public static IServiceCollection AddShell(this IServiceCollection services)
    {
        // Shell-Infrastruktur
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<INavigationViewPageProvider, NavigationPageProvider>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IAppRestartService, AppRestartService>();
        services.AddSingleton<StatusBarViewModel>();
        Activity.ActivityUiServiceCollectionExtensions.AddActivityUi(services); // Aktivitäts-Anzeige unten rechts (ADR-0067)
        services.AddSingleton<ShellViewModel>();

        // Seiten + ViewModels
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<LibraryPage>();
        services.AddSingleton<FolderTreeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsPage>();

        // Navigation und Einstellungs-Reiter (ADR-0042): Bibliotheks-Umschalter, gewählter Reiter, „Eingang hinzufügen…“
        // auf der Ordner-Seite über die Bibliotheksverwaltung (Info: AddSettingsInfo in App.xaml.cs)
        services.AddSingleton<SettingsSectionState>();
        services.AddSingleton<LibrarySwitcherViewModel>();
        services.AddSingleton<IInboxActions>(sp => sp.GetRequiredService<LibraryViewModel>());

        return services;
    }
}
