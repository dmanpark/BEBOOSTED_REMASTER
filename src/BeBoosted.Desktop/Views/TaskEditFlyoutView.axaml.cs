using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace BeBoosted.Desktop.Views;

public partial class TaskEditFlyoutView : UserControl
{
    public TaskEditFlyoutView()
    {
        InitializeComponent();
    }

    /// <summary>Closes the hosting flyout after Save/Delete — pure view plumbing.</summary>
    private void OnEditActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control
            && control.FindAncestorOfType<FlyoutPresenter>() is { Parent: Avalonia.Controls.Primitives.Popup popup })
        {
            popup.IsOpen = false;
        }
    }
}
