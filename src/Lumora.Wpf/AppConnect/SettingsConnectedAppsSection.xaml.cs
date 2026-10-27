using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.AppConnect;

/// <summary>Reiter „Verbundene Apps“ (ADR-0073 Stufe 4, Mockup 11). Liest Apps und Kopplungen bei jedem Anzeigen neu.</summary>
public partial class SettingsConnectedAppsSection : UserControl
{
    public SettingsConnectedAppsSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is ConnectedAppsViewModel vm)
            {
                _ = vm.RefreshAsync();
            }
        };
    }
}

/// <summary>Oberfläche der App-Verbindung; eine Zeile in <c>App.ConfigureServices</c>.</summary>
public static class ConnectedAppsUi
{
    public static IServiceCollection AddConnectedAppsUi(this IServiceCollection services)
    {
        services.TryAddSingleton<AppConnectRuntime>();
        services.TryAddSingleton<ConnectedAppsViewModel>();
        return services;
    }
}
