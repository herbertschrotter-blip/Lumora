using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Lumora.Shell.Branding;

namespace Lumora.AppConnect;

/// <summary>
/// Symbol im Infobereich, solange Lumora im Hintergrund läuft (Abschnitt 9, Mockup 11 „Infobereich-Menü“): wahlweise (Standard
/// aus). Text „Lumora läuft im Hintergrund für Gipfelbuch · seit 14:02“, Menü „Lumora öffnen“, „Verbundene Apps…“, „Beenden“.
/// Windows Forms <see cref="NotifyIcon"/> — WPF hat kein eigenes; nur auf dem UI-Thread benutzen.
/// </summary>
internal sealed class BackgroundTrayIcon : IDisposable
{
    /// <summary>Windows schneidet den Hinweistext eines Infobereich-Symbols nach 127 Zeichen ab.</summary>
    private const int MaxTextLength = 127;

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _title;
    private readonly Icon? _ownedIcon;

    public BackgroundTrayIcon(Action open, Action openConnectedApps, Action exit)
    {
        _ownedIcon = LoadIcon();
        _title = new ToolStripMenuItem { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_title);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Lumora öffnen", null, (_, _) => open());
        menu.Items.Add("Verbundene Apps…", null, (_, _) => openConnectedApps());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => exit());
        _icon = new NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = false,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public bool IsVisible => _icon.Visible;

    /// <summary>Zeigen bzw. Text aktualisieren.</summary>
    public void Show(string text)
    {
        _title.Text = text;
        _icon.Text = text.Length <= MaxTextLength ? text : text[..(MaxTextLength - 1)] + "…";
        _icon.Visible = true;
    }

    public void Hide() => _icon.Visible = false;

    /// <summary>„Lumora läuft im Hintergrund für Gipfelbuch · seit 14:02“ (ohne Abfrager nur „Lumora läuft im Hintergrund“).</summary>
    public static string TextFor(IReadOnlyList<string> requesterNames, DateTimeOffset since)
    {
        var text = requesterNames.Count == 0 ? "Lumora läuft im Hintergrund" : $"Lumora läuft im Hintergrund für {string.Join(", ", requesterNames)}";
        return $"{text} · seit {since.ToLocalTime():HH:mm}";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _ownedIcon?.Dispose();
    }

    private static Icon? LoadIcon()
    {
        try
        {
            using var stream = LumoraAppIcon.OpenIco();
            return stream is null ? null : new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }
}
