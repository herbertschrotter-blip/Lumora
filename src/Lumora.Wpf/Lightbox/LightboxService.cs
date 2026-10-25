using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lumora.Lightbox;

/// <summary>
/// Singleton-Implementierung von <see cref="ILightboxService"/>: hält höchstens ein
/// <see cref="LightboxWindow"/>, erzeugt es bei Bedarf (ViewModel transient aus DI), übergibt
/// Sequenz + Startindex und holt es nach vorn. Beim Schließen wird das Fenster zerstört
/// (Player sauber gestoppt und disposed, Spike #03-Lebenszyklus); Größe/Position/Maximiert
/// werden für die Sitzung gemerkt und beim nächsten Öffnen wiederhergestellt.
/// </summary>
public sealed class LightboxService : ILightboxService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LightboxService> _logger;
    private LightboxWindow? _window;
    private Rect? _lastBounds;
    private bool _lastMaximized;

    // LibVlcHost wird bewusst injiziert: so entsteht er vor diesem Service und wird vom Host
    // nach ihm disposed (Player-Besitzer vor LibVLC-Instanz).
    public LightboxService(IServiceProvider services, LibVlcHost vlcHost, ILogger<LightboxService> logger)
    {
        _services = services;
        _ = vlcHost;
        _logger = logger;
    }

    public bool IsOpen => _window is not null;

    public void Open(IReadOnlyList<LightboxEntry> entries, int startIndex, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            _logger.LogWarning("Lightbox.Open ohne Einträge ignoriert");
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Open(entries, startIndex, owner));
            return;
        }

        startIndex = Math.Clamp(startIndex, 0, entries.Count - 1);

        if (_window is null)
        {
            var window = CreateWindow(owner);
            _window = window;
            window.ViewModel.Load(entries, startIndex);
            window.Show();
            if (_lastMaximized)
            {
                window.WindowState = WindowState.Maximized;
            }

            _logger.LogInformation("Lightbox geöffnet: {Count} Einträge, Start {Index}", entries.Count, startIndex);
        }
        else
        {
            _window.ViewModel.Load(entries, startIndex);
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = _lastMaximized ? WindowState.Maximized : WindowState.Normal;
            }

            _logger.LogInformation("Lightbox aktualisiert: {Count} Einträge, Start {Index}", entries.Count, startIndex);
        }

        _window.Activate();
    }

    private LightboxWindow CreateWindow(Window? owner)
    {
        // Bewusst ActivatorUtilities statt GetRequiredService: ein transientes IDisposable, das aus dem
        // Root-Provider aufgelöst wird, hält der DI-Container bis zum App-Ende fest (Leak pro Lightbox-Öffnung).
        // Das Fenster besitzt das ViewModel und disposed es beim Schließen selbst.
        var viewModel = ActivatorUtilities.CreateInstance<LightboxViewModel>(_services);
        var logger = _services.GetRequiredService<ILogger<LightboxWindow>>();
        var window = new LightboxWindow(viewModel, logger)
        {
            PhotoActions = _services.GetService<Lumora.PhotoMenu.PhotoActions>(), // Rechtsklick-Menü (ADR-0068)
            Stories = _services.GetService<Lumora.Stories.StoryGalleryBridge>(),
        };

        // Owner nur, wenn er bereits ein Fenster-Handle hat (sonst wirft WPF).
        if (owner is not null && !ReferenceEquals(owner, window) && new WindowInteropHelper(owner).Handle != IntPtr.Zero)
        {
            window.Owner = owner;
        }

        if (_lastBounds is { } bounds && IsOnScreen(bounds))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = bounds.Left;
            window.Top = bounds.Top;
            window.Width = bounds.Width;
            window.Height = bounds.Height;
        }
        else
        {
            window.WindowStartupLocation = window.Owner is not null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
        }

        window.Closed += OnWindowClosed;
        return window;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not LightboxWindow window)
        {
            return;
        }

        window.Closed -= OnWindowClosed;
        try
        {
            var bounds = window.NormalBounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                _lastBounds = bounds;
            }

            _lastMaximized = window.IsMaximizedOrFullscreenMaximized;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fensterposition konnte nicht gemerkt werden");
        }

        if (ReferenceEquals(_window, window))
        {
            _window = null;
        }

        _logger.LogInformation("Lightbox geschlossen");
    }

    private static bool IsOnScreen(Rect bounds)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var probe = new Rect(bounds.Left, bounds.Top, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        var visible = Rect.Intersect(virtualScreen, probe);
        return !visible.IsEmpty && visible.Width >= 200 && visible.Height >= 120;
    }

    public void Dispose()
    {
        var window = _window;
        _window = null;
        if (window is null)
        {
            return;
        }

        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lightbox-Fenster konnte beim Beenden nicht geschlossen werden");
        }
    }
}
