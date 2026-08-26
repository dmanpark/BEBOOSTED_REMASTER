using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;

namespace BeBoosted.Desktop.Views;

public partial class DailyTaskListView : UserControl
{
    private DailyListViewModel? _subscribed;

    public DailyTaskListView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
            {
                _subscribed.RowFocusRequested -= OnRowFocusRequested;
            }

            _subscribed = DataContext as DailyListViewModel;
            if (_subscribed is not null)
            {
                _subscribed.RowFocusRequested += OnRowFocusRequested;
            }
        };
    }

    /// <summary>After a manual schedule, keyboard focus lands on the newly scheduled row.</summary>
    private void OnRowFocusRequested(TaskId taskId)
        => Dispatcher.UIThread.Post(() =>
        {
            var row = this.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("dailyRow")
                    && (border.DataContext as DailyRowViewModel)?.TaskId == taskId);
            row?.Focus();
        });

    /// <summary>Reopening a schedule flyout starts from fresh defaults, not stale edits.</summary>
    private void OnScheduleFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is Flyout { Content: Control { DataContext: ScheduleFlyoutViewModel editor } })
        {
            editor.Reset();
        }
    }

    /// <summary>Closes the hosting flyout after an action button — pure view plumbing.</summary>
    private void OnFlyoutActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control
            && control.FindAncestorOfType<FlyoutPresenter>() is { Parent: Popup popup })
        {
            popup.IsOpen = false;
        }
    }
}
