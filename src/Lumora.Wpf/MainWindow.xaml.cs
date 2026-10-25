using System;
using System.Windows;
using Microsoft.Extensions.Logging;
using Lumora.Settings;
using Lumora.Shell;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Lumora;

/// <summary>
/// Hauptfenster: NavigationView (Bibliotheks-Umschalter, Galerie mit Ordnerleiste, Alben, Storys, Personen, Orte, Doppelte,
/// Sortieren, Einstellungen; ADR-0042, ADR-0051, ADR-0068)
/// + Statusleiste.
/// Seiten kommen über den NavigationPageProvider aus DI; Navigation läuft über NavigationView.Navigate(tag),
/// Klicks werden per SelectionChanged an den NavigationService zurückgemeldet.
/// </summary>
public partial class MainWindow : Lumora.Shell.Dialogs.AppFluentWindow
{
    private readonly ShellViewModel _shell;
    private readonly NavigationService _navigation;
    private readonly IThemeService _theme;
    private readonly IServiceProvider _services;
    private readonly ILogger<MainWindow> _logger;
    private bool _watchingSystemTheme;

    public MainWindow(
        ShellViewModel shell,
        NavigationService navigation,
        INavigationViewPageProvider pageProvider,
        IThemeService theme,
        IServiceProvider services,
        ILogger<MainWindow> logger)
    {
        _shell = shell;
        _navigation = navigation;
        _theme = theme;
        _services = services;
        _logger = logger;

        InitializeComponent();
        DataContext = shell;

        MainNav.SetPageProviderService(pageProvider);
        MainNav.SelectionChanged += OnNavSelectionChanged;
        navigation.Attach(tag => MainNav.Navigate(tag));
        theme.ModeChanged += OnThemeModeChanged;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            _logger.LogInformation("MainWindow geladen — Theme-Modus {Mode}", _theme.Mode);
            UpdateSystemThemeWatcher(_theme.Mode);

            await _shell.StatusBar.InitializeAsync();
            _ = _shell.LibrarySwitcher.RefreshAsync(); // Bibliotheks-Umschalter (ADR-0042), ohne den Start aufzuhalten

            // Startseite: Galerie, wenn eine aktive Bibliothek existiert, sonst Einstellungen › Bibliotheken & Eingänge
            var startTag = await _shell.ResolveStartPageAsync();
            _logger.LogInformation("Startseite: {Tag}", startTag);
            _navigation.NavigateTo(startTag);

            await SmokeTestRunner.RunIfRequestedAsync(_services, _navigation, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Initialisieren des Hauptfensters");
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        var item = sender.SelectedItem;
        var tag = item?.TargetPageTag;
        _logger.LogInformation("NavigationView SelectionChanged: Tag={Tag} Seite={Page}", tag, item?.TargetPageType?.Name);
        if (!string.IsNullOrEmpty(tag))
        {
            _navigation.NotifyNavigated(tag);
        }
    }

    private void OnThemeModeChanged(object? sender, AppThemeMode mode) => UpdateSystemThemeWatcher(mode);

    /// <summary>Im Modus „System“ dem Windows-Farbschema live folgen, sonst den Watcher abhängen.</summary>
    private void UpdateSystemThemeWatcher(AppThemeMode mode)
    {
        if (mode == AppThemeMode.System)
        {
            if (!_watchingSystemTheme)
            {
                SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
                _watchingSystemTheme = true;
            }
        }
        else if (_watchingSystemTheme)
        {
            SystemThemeWatcher.UnWatch(this);
            _watchingSystemTheme = false;
        }
    }
}
