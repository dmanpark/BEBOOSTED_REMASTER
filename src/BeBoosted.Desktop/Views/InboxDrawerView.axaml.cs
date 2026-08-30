using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Controls;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Views;

public partial class InboxDrawerView : UserControl
{
    private Point _rowPressPoint;
    private TaskRowViewModel? _pressedRow;
    private PointerPressedEventArgs? _rowPressArgs;

    public InboxDrawerView()
    {
        InitializeComponent();
    }

    public void FocusCapture() => CaptureBox.Focus();

    // ---- Drag a task onto the calendar to schedule it ----

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: TaskRowViewModel row }
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && e.Source is Visual source
            && source.FindAncestorOfType<Button>(includeSelf: true) is null)
        {
            _pressedRow = row;
            _rowPressPoint = e.GetPosition(this);
            _rowPressArgs = e;
        }
    }

    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedRow is not { } row || _rowPressArgs is not { } pressArgs)
        {
            return;
        }

        var delta = e.GetPosition(this) - _rowPressPoint;
        if (Math.Abs(delta.X) < 5 && Math.Abs(delta.Y) < 5)
        {
            return;
        }

        _pressedRow = null;
        _rowPressArgs = null;
        var minutes = (int)(row.Task.EstimatedDuration?.TotalMinutes ?? 30);
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(TaskDragData.Format, TaskDragData.Serialize(row.Task.Id, minutes)));
        await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedRow = null;
        _rowPressArgs = null;
    }
}
