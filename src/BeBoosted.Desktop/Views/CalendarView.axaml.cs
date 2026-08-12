using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Views;

public partial class CalendarView : UserControl
{
    private readonly DispatcherTimer _nowTimer;

    public CalendarView()
    {
        InitializeComponent();
        _nowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _nowTimer.Tick += (_, _) => (DataContext as CalendarViewModel)?.RefreshNow();
        AttachedToVisualTree += (_, _) => _nowTimer.Start();
        DetachedFromVisualTree += (_, _) => _nowTimer.Stop();
    }

    private void OnSaveCommitmentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CalendarViewModel viewModel && viewModel.TrySaveCommitment())
        {
            NewCommitmentButton.Flyout?.Hide();
        }
    }
}
