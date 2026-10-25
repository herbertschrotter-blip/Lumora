using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Settings.ModelPackages;

/// <summary>Reiter „Modelle &amp; Daten“ (ADR-0070, Mockup 19). Zustände werden beim Anzeigen gelesen.</summary>
public partial class SettingsModelPackagesSection : UserControl
{
    public SettingsModelPackagesSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is ModelPackagesViewModel vm)
            {
                _ = vm.RefreshAsync();
            }
        };
    }
}

/// <summary>Oberfläche von „Modelle &amp; Daten“; eine Zeile in <c>App.ConfigureServices</c>.</summary>
public static class ModelPackagesUi
{
    public static IServiceCollection AddModelPackagesUi(this IServiceCollection services)
    {
        services.TryAddSingleton<ModelPackagesViewModel>();
        if (Shell.SmokeTestRunner.IsRequested)
        {
            Shell.Smoke.SmokeModelPackages.Register(services); // Testmodell, kein echter Download, keine Netzabfrage
        }

        return services;
    }
}
