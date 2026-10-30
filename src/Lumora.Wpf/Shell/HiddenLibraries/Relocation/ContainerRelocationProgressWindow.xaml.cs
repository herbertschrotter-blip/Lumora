using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Lumora.Services.HiddenLibraries.Relocation;
using Lumora.Shell.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Lumora.Shell.HiddenLibraries.Relocation;

/// <summary>Fortschritt beim Verschieben des Containers (Mockup 26); schließt erst nach dem Ende per „Schließen“.</summary>
public partial class ContainerRelocationProgressWindow : AppFluentWindow, IContainerRelocationProgress
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private readonly CancellationTokenSource _cancel = new();
    private readonly (ContainerRelocationStep Step, TextBlock Mark, TextBlock Text)[] _rows;
    private bool _finished;

    public ContainerRelocationProgressWindow(ContainerRelocationPlan plan)
    {
        InitializeComponent();
        var copy = plan.IsRename ? $"Umbenennen nach {plan.TargetFile}" : $"Kopieren nach {plan.TargetFile}";
        _rows =
        [
            Row(ContainerRelocationStep.CloseLibrary, "Versteckte Bibliothek geschlossen"),
            Row(ContainerRelocationStep.Dismount, $"Container {plan.DriveLetter}: aushängen"),
            Row(ContainerRelocationStep.Copy, copy),
            Row(ContainerRelocationStep.Verify, plan.IsRename ? "Prüfen entfällt (nur umbenannt)" : "Kopie prüfen"),
            Row(ContainerRelocationStep.Mount, $"Am neuen Ort als {plan.DriveLetter}: öffnen"),
            Row(ContainerRelocationStep.Reopen, "Versteckte Bibliothek wieder öffnen"),
        ];
        SetState(0, done: true);
    }

    public CancellationToken Cancellation => _cancel.Token;

    public void Step(ContainerRelocationStep step)
    {
        for (var i = 0; i < _rows.Length; i++)
        {
            SetState(i, done: _rows[i].Step < step, running: _rows[i].Step == step);
        }

        var copying = step is ContainerRelocationStep.Copy or ContainerRelocationStep.Verify;
        Bar.Visibility = Numbers.Visibility = copying ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = copying;
    }

    public void Copy(ContainerCopyProgress progress)
    {
        Bar.Value = progress.Fraction;
        var speed = progress.BytesPerSecond > 0 ? $"  ·  {progress.BytesPerSecond / (1 << 20):0} MB/s" : string.Empty;
        var rest = progress.Remaining is { } r ? $"  ·  noch etwa {(r.TotalMinutes >= 1 ? $"{Math.Ceiling(r.TotalMinutes):0} min" : $"{Math.Max(1, r.TotalSeconds):0} s")}" : string.Empty;
        Numbers.Text = $"{(progress.Phase == ContainerCopyPhase.Verify ? "Geprüft" : "Kopiert")}: {ContainerRelocationRules.FormatBytes(progress.Done)} von {ContainerRelocationRules.FormatBytes(progress.Total)} ({(progress.Fraction * 100).ToString("0", De)} %){speed}{rest}";
    }

    public void Finish(bool success, string message)
    {
        _finished = true;
        Heading.Text = success ? "Container verschoben" : "Verschieben abgebrochen";
        if (success)
        {
            foreach (var i in new[] { 0, 1, 2, 3, 4 })
            {
                SetState(i, done: true);
            }
        }

        Bar.Visibility = Numbers.Visibility = RunningHint.Visibility = Visibility.Collapsed;
        MessageText.Text = message;
        MessageBorder.SetResourceReference(Border.BackgroundProperty, success ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorCriticalBackgroundBrush");
        MessageBorder.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
    }

    private (ContainerRelocationStep, TextBlock, TextBlock) Row(ContainerRelocationStep step, string text)
    {
        var mark = new TextBlock { Width = 22, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var panel = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(mark, Dock.Left);
        panel.Children.Add(mark);
        panel.Children.Add(label);
        Steps.Children.Add(panel);
        return (step, mark, label);
    }

    private void SetState(int index, bool done = false, bool running = false)
    {
        var (_, mark, text) = _rows[index];
        mark.Text = done ? "" : running ? "" : "";
        mark.SetResourceReference(TextBlock.ForegroundProperty, done ? "SystemFillColorSuccessBrush" : running ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
        text.FontWeight = running ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancel.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true; // während des Ablaufs nur über „Abbrechen“
        }

        base.OnClosing(e);
    }
}
