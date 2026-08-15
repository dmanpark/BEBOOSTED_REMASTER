using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Views;

public partial class ScheduleFlyoutContent : UserControl
{
    public ScheduleFlyoutContent()
    {
        InitializeComponent();
    }

    /// <summary>Success closes the flyout; a domain error keeps it open with the message inline.</summary>
    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScheduleFlyoutViewModel editor && editor.Confirm())
        {
            CloseHostingFlyout(sender);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => CloseHostingFlyout(sender);

    private static void CloseHostingFlyout(object? sender)
    {
        if (sender is Control control
            && control.FindAncestorOfType<FlyoutPresenter>() is { Parent: Avalonia.Controls.Primitives.Popup popup })
        {
            popup.IsOpen = false;
        }
    }
}
