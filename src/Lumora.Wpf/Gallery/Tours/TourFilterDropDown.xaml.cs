using System;
using System.Windows;
using System.Windows.Controls;
using Lumora.Gallery.ViewModels;

namespace Lumora.Gallery.Tours;

/// <summary>Chip + Auswahl für den Filter „Tour“ (Mockup 24). Logik liegt im GalleryViewModel (GalleryViewModel.Tours.cs).</summary>
public partial class TourFilterDropDown : UserControl
{
    public TourFilterDropDown()
    {
        InitializeComponent();
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        if (DataContext is GalleryViewModel vm)
        {
            vm.LoadTourFilterCommand.Execute(null);
        }

        SearchBox.Focus();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = false;

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (DataContext is GalleryViewModel vm)
        {
            vm.TourFilterSearch = null;
        }
    }
}
