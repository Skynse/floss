using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Floss.App; // Update this if your folder structure uses a different namespace

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        Close(true); // User wants to save
    }

    private void OnDontSaveClick(object? sender, RoutedEventArgs e)
    {
        Close(false); // User wants to discard changes
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null); // User canceled the dialog
    }
}
