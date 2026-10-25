using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Lumora.Services.Enhance;

namespace Lumora.Enhance;

/// <summary>Fenster „n Fotos verbessern“ (ADR-0071).</summary>
public sealed partial class EnhanceBatchDialogWindow : Shell.Dialogs.AppFluentWindow
{
    private bool _closing;

    public EnhanceBatchDialogWindow(EnhanceBatchDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    public EnhanceBatchDialogViewModel ViewModel { get; }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            DialogResult = ViewModel.Result is not null;
        }
        catch (InvalidOperationException)
        {
            Close(); // nicht modal (Tests)
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ViewModel.CancelCommand.Execute(null);
        }
    }
}

/// <summary>Fragt die Werkzeuge für mehrere Fotos ab (null = abgebrochen).</summary>
public interface IEnhanceBatchDialogService
{
    Task<EnhanceOptions?> ShowAsync(IReadOnlyList<int> fileIds, Window? owner);
}

/// <summary>Produktiv: modales Fenster über dem Hauptfenster.</summary>
public sealed class EnhanceBatchDialogService(IPhotoEnhanceService service, ILogger<EnhanceBatchDialogService>? logger = null) : IEnhanceBatchDialogService
{
    public Task<EnhanceOptions?> ShowAsync(IReadOnlyList<int> fileIds, Window? owner)
    {
        var viewModel = new EnhanceBatchDialogViewModel(service, fileIds, logger);
        var window = new EnhanceBatchDialogWindow(viewModel);
        owner ??= Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
        return Task.FromResult(viewModel.Result);
    }
}
