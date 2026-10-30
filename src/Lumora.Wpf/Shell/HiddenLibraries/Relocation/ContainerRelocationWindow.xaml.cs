using System;
using System.IO;
using System.Windows;
using Lumora.Shell.Dialogs;
using Microsoft.Win32;

namespace Lumora.Shell.HiddenLibraries.Relocation;

/// <summary>Assistent „Container verschieben“ (Mockup 26). Code-Behind nur für Dateiauswahl und Schließen.</summary>
public partial class ContainerRelocationWindow : AppFluentWindow
{
    public ContainerRelocationWindow(ContainerRelocationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Finished += (_, _) => DialogResult = true;
    }

    private ContainerRelocationViewModel ViewModel => (ContainerRelocationViewModel)DataContext;

    private void OnPickSource(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Container-Datei wählen",
            Filter = ViewModel.IsVeraCrypt ? "Alle Dateien (*.*)|*.*" : "Virtuelle Festplatte (*.vhdx;*.vhd)|*.vhdx;*.vhd",
            CheckFileExists = true,
        };
        if (ViewModel.SourceFile is { } current && Path.GetDirectoryName(current) is { } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.SetSource(dialog.FileName);
        }
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Zielordner für den Container wählen" };
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.SetTargetFolder(dialog.FolderName);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
