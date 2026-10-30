using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Lumora.Services.Database;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Lumora.Shell.Dialogs;

/// <summary>
/// Kleines Fenster, solange der Start den Katalog vor einer Migration sichert und aktualisiert (ADR-0069). Bei großen
/// Katalogen dauert das eine Minute und mehr; ohne Fenster sah es aus, als hinge Lumora (2026-10-30, versteckte
/// Bibliothek mit 1,9 GB). Schließen lässt es sich nicht — es geht von selbst zu, sobald der Katalog bereit ist.
/// </summary>
internal sealed class CatalogUpdateWindow : AppFluentWindow
{
    private readonly TextBlock _heading = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _detail = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14) };
    private bool _finished;

    public CatalogUpdateWindow()
    {
        Title = "Lumora";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        AutomationProperties.SetAutomationId(this, "CatalogUpdateWindow");
        _detail.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titleBar = new TitleBar { Title = "Lumora", ShowMaximize = false, ShowMinimize = false, ShowClose = false };
        root.Children.Add(titleBar);
        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 24) };
        body.Children.Add(_heading);
        body.Children.Add(_detail);
        body.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4 });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;
    }

    /// <summary>Text zum Schritt setzen und das Fenster beim ersten Schritt zeigen.</summary>
    public void ShowStep(DatabaseInitStep step)
    {
        (_heading.Text, _detail.Text) = Texts(step);
        if (!IsVisible && !IsClosed)
        {
            Show();
        }
    }

    /// <summary>Katalog bereit (oder Start abgebrochen): Fenster schließen.</summary>
    public void Finish()
    {
        _finished = true;
        if (!IsClosed)
        {
            Close();
        }
    }

    /// <summary>Überschrift und Erklärung je Schritt.</summary>
    internal static (string Heading, string Detail) Texts(DatabaseInitStep step)
    {
        var size = step.DatabaseBytes > 0 ? $" ({FormatSize(step.DatabaseBytes)})" : string.Empty;
        return step.Kind == DatabaseInitStepKind.Backup
            ? ("Katalog wird gesichert …",
               $"Diese Lumora-Version muss den Katalog{size} aktualisieren und sichert ihn vorher. Bei großen Katalogen dauert das eine Minute oder länger — bitte Lumora nicht noch einmal starten.")
            : ("Katalog wird aktualisiert …",
               $"{step.PendingMigrations} {(step.PendingMigrations == 1 ? "Schritt" : "Schritte")} für diese Lumora-Version. Danach öffnet sich Lumora von selbst.");
    }

    private static string FormatSize(long bytes)
        => bytes >= 1L << 30
            ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " GB"
            : Math.Max(1, bytes >> 20).ToString(CultureInfo.GetCultureInfo("de-DE")) + " MB";

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true; // Alt+F4 während der Sicherung: der Start läuft weiter, das Fenster bleibt
        }

        base.OnClosing(e);
    }
}
