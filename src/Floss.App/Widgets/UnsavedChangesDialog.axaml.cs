using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Floss.App; // Update this if your folder structure uses a different namespace

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog()
    {
        InitializeComponent();
    }

    public UnsavedChangesDialog(string documentName)
        : this()
    {
        var name = string.IsNullOrWhiteSpace(documentName) ? "Untitled" : documentName.Trim();
        MessageText.Text = $"Save changes to \"{name}\" before closing?";
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
