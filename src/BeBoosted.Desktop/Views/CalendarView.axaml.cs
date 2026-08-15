using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Views;

public partial class CalendarView : UserControl
{
    private readonly DispatcherTimer _nowTimer;
    private readonly DispatcherTimer _undoToastTimer;
    private IInputElement? _commitmentEditorReturnFocus;

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

                    if (e.PropertyName == nameof(CalendarViewModel.CommitmentEditor))
                    {
                        OnCommitmentEditorChanged(viewModel.CommitmentEditor is not null);
                    }
                };
            }
        };
    }

    /// <summary>Escape closes the modal without mutating anything, even from a field.</summary>
    private void OnCommitmentModalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is CalendarViewModel viewModel)
        {
            viewModel.CloseCommitmentEditor();
            e.Handled = true;
        }
    }

    /// <summary>Initial focus goes to Title; closing returns focus to the invoker.</summary>
    private void OnCommitmentEditorChanged(bool isOpen)
    {
        if (isOpen)
        {
            _commitmentEditorReturnFocus =
                TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(() =>
                this.GetVisualDescendants()
                    .OfType<TextBox>()
                    .FirstOrDefault(box => box.Name == "CommitmentTitleBox")
                    ?.Focus());
            return;
        }

        if (_commitmentEditorReturnFocus is Control { IsLoaded: true } control)
        {
            Dispatcher.UIThread.Post(() => control.Focus());
        }

        _commitmentEditorReturnFocus = null;
    }
}
