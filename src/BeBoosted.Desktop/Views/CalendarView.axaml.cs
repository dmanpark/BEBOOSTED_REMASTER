using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Views;

public partial class CalendarView : UserControl
{
    private readonly DispatcherTimer _nowTimer;
    private readonly DispatcherTimer _undoToastTimer;

    public CalendarView()
    {
        InitializeComponent();
        _nowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _nowTimer.Tick += (_, _) => (DataContext as CalendarViewModel)?.RefreshNow();
        AttachedToVisualTree += (_, _) => _nowTimer.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _nowTimer.Stop();
            _undoToastTimer!.Stop();
        };

        // The approval undo toast stays for 10 seconds; Ctrl+Z keeps working afterwards.
        _undoToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _undoToastTimer.Tick += (_, _) =>
        {
            _undoToastTimer.Stop();
            (DataContext as CalendarViewModel)?.ExpireUndoToast();
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CalendarViewModel viewModel)
            {
                viewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(CalendarViewModel.IsUndoToastVisible)
                        && viewModel.IsUndoToastVisible)
                    {
                        _undoToastTimer.Stop();
                        _undoToastTimer.Start();
                    }
                };
            }
        };
    }

    private void OnSaveCommitmentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CalendarViewModel viewModel && viewModel.TrySaveCommitment())
        {
            NewCommitmentButton.Flyout?.Hide();
        }
    }
}
