using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumora.AppConnect;

/// <summary>Eine Zeile im Kopplungsdialog mit Häkchen.</summary>
public sealed partial class PairingPermissionChoice(PairingPermission permission) : ObservableObject
{
    public PairingPermission Permission { get; } = permission;

    public string Text => Permission.Text;

    [ObservableProperty]
    private bool _isChecked = permission.DefaultOn;
}

/// <summary>
/// Kopplungsdialog (Mockup 11 „Neue App koppeln“): „&lt;App&gt; möchte sich verbinden“, Code zum Vergleichen, Rechte je Zeile.
/// <see cref="Decision"/> ist null bei „Ablehnen“, Schließen oder Esc.
/// </summary>
public sealed partial class PairingDialogWindow : Shell.Dialogs.AppFluentWindow
{
    public PairingDialogWindow(PairingPromptInfo info)
    {
        InitializeComponent();
        Info = info;
        HeadText.Text = $"{info.DisplayName} möchte sich verbinden";
        CheckText.Text = $"Prüfen Sie, ob in {info.DisplayName} derselbe Code steht. Erst danach darf {info.DisplayName} Lumora etwas fragen.";
        CodeText.Text = FormatCode(info.Code);
        Choices = info.Permissions.Select(p => new PairingPermissionChoice(p)).ToList();
        PermissionList.ItemsSource = Choices;
    }

    public PairingPromptInfo Info { get; }

    public IReadOnlyList<PairingPermissionChoice> Choices { get; }

    /// <summary>Gewählte Rechte nach „Verbinden“; null = abgelehnt.</summary>
    public PairingDecision? Decision { get; private set; }

    /// <summary>„482913“ → „482 913“ (wie im Mockup).</summary>
    public static string FormatCode(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    /// <summary>Für Tests und den Rauchtest: „Verbinden“ mit den aktuellen Häkchen.</summary>
    internal void Accept()
    {
        Decision = PairingPermissions.Decide(Choices.Where(c => c.IsChecked).Select(c => c.Permission));
        Close();
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e) => Accept();

    private void OnRejectClick(object sender, RoutedEventArgs e)
    {
        Decision = null;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Decision = null;
            Close();
        }
    }
}
