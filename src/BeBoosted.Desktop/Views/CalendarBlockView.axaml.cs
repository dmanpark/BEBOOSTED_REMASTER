using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Controls;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Views;

/// <summary>
/// Block interactions: pointer drag to move (snap 15 min, Alt for 5), bottom-grip resize,
/// and keyboard movement (↑/↓ move, Alt for fine, Shift+↑/↓ resize, ←/→ change day,
/// Enter/Space outcome menu or editor, Delete by kind). A click below the drag threshold
/// opens the Task editor for local sessions. External synced events stay locked.
/// </summary>
public partial class CalendarBlockView : UserControl
{
    private TimelineSurfaceView? _surface;
    private Avalonia.Point _pressPoint;
    private double _originStartMinutes;
    private double _originDurationMinutes;
    private int _originColumn;
    private int _currentColumn;
    private bool _moving;
    private bool _resizing;
    private bool _dragConfirmed;

    public CalendarBlockView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressedHandler, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedHandler, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedHandler, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLostHandler);
        AddHandler(KeyDownEvent, OnKeyDownHandler);
    }

    private CalendarBlockViewModel? Vm => DataContext as CalendarBlockViewModel;

    private ContentPresenter? HostPresenter => Parent as ContentPresenter;

    private static int Snap(KeyModifiers modifiers)
        => modifiers.HasFlag(KeyModifiers.Alt) ? CalendarViewModel.FineSnapMinutes : CalendarViewModel.SnapMinutes;

    /// <summary>
    /// Layout minutes run 0–1440 with an exclusive 24:00 day end, which
    /// <see cref="TimeOnly"/> cannot express — clamp to the domain's 23:59 maximum.
    /// </summary>
    private static TimeOnly TimeFromMinutes(double minutes)
        => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Min(minutes, (24 * 60) - 1)));

    private void OnPointerPressedHandler(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || HostPresenter is null
            || (!vm.CanMove && !vm.CanResize && !vm.CanEdit))
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                Focus();
            }

            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Presses on the outcome button (or inside its flyout) never start a drag.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        _surface = this.FindAncestorOfType<TimelineSurfaceView>();
        if (_surface is null)
        {
            return;
        }

        _pressPoint = e.GetPosition(_surface.DaysHost);
        _originStartMinutes = TimelinePanel.GetStartMinutes(HostPresenter);
        _originDurationMinutes = TimelinePanel.GetDurationMinutes(HostPresenter);
        _originColumn = _surface.ColumnFromX(_pressPoint.X);
        _currentColumn = _originColumn;
        _resizing = vm.CanResize && e.Source is Visual gripSource
            && gripSource.FindAncestorOfType<Border>(includeSelf: true) is { Name: "ResizeGrip" };
        _moving = !_resizing && vm.CanMove;
        _dragConfirmed = false;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    private void OnPointerMovedHandler(object? sender, PointerEventArgs e)
    {
        if (_surface is null || HostPresenter is null || (!_moving && !_resizing)
            || !ReferenceEquals(e.Pointer.Captured, this))
        {
            return;
        }

        var position = e.GetPosition(_surface.DaysHost);
        var delta = position - _pressPoint;
        if (!_dragConfirmed && Math.Abs(delta.Y) < 4 && Math.Abs(delta.X) < 4)
        {
            return;
        }

        _dragConfirmed = true;
        var snap = Snap(e.KeyModifiers);
        var minutesDelta = delta.Y / _surface.HourHeight * 60.0;

        if (_resizing)
        {
            var duration = Math.Round((_originDurationMinutes + minutesDelta) / snap) * snap;
            duration = Math.Clamp(duration, snap, (_surface.EndHour * 60.0) - _originStartMinutes);
            TimelinePanel.SetDurationMinutes(HostPresenter, duration);
            return;
        }

        var start = Math.Round((_originStartMinutes + minutesDelta) / snap) * snap;
        start = Math.Clamp(
            start,
            _surface.StartHour * 60.0,
            (_surface.EndHour * 60.0) - _originDurationMinutes);
        TimelinePanel.SetStartMinutes(HostPresenter, start);

        if (Vm is not { } vm)
        {
            return;
        }

        // Repeating tasks move as a whole series — a day change would be rejected
        // on release, so their preview never leaves the origin column.
        _currentColumn = vm.IsRecurring ? _originColumn : _surface.ColumnFromX(position.X);
        if (_currentColumn != _originColumn)
        {
            // The block's own subtree clips to its day column, so the cross-day preview
            // renders on the surface overlay instead; hide the source meanwhile.
            Opacity = 0;
            _surface.ShowDragPreview(vm, _currentColumn, start, _originDurationMinutes);
        }
        else
        {
            Opacity = 1;
            _surface.ClearDragPreview();
        }
    }

    private void OnPointerReleasedHandler(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, this))
        {
            return;
        }

        // Snapshot and reset the drag state before releasing capture: Capture(null)
        // raises PointerCaptureLost synchronously, and its abort cleanup must see an
        // already-idle gesture instead of reverting the values this release persists.
        var resizing = _resizing;
        var confirmed = _dragConfirmed;
        var column = _currentColumn;
        _moving = _resizing = false;
        _dragConfirmed = false;
        e.Pointer.Capture(null);

        if (_surface is null || HostPresenter is null || Vm is null)
        {
            return;
        }

        if (!confirmed)
        {
            // A press that never crossed the drag threshold is a click:
            // local sessions open the Task editor, everything else just keeps focus.
            Vm.Edit();
            return;
        }

        if (resizing)
        {
            var duration = TimelinePanel.GetDurationMinutes(HostPresenter);
            var start = TimelinePanel.GetStartMinutes(HostPresenter);
            Vm.ResizeTo(TimeFromMinutes(start + duration));
        }
        else
        {
            // Persist exactly the day and time the preview showed last.
            Opacity = 1;
            _surface.ClearDragPreview();
            var start = TimelinePanel.GetStartMinutes(HostPresenter);
            Vm.MoveTo(_surface.DateForColumn(column), TimeFromMinutes(start));
        }
    }

    /// <summary>
    /// Losing pointer capture mid-drag (window deactivation, capture theft) aborts the
    /// gesture: every temporary preview value reverts and nothing is persisted.
    /// </summary>
    private void OnPointerCaptureLostHandler(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_moving && !_resizing)
        {
            return;
        }

        if (_dragConfirmed && HostPresenter is not null)
        {
            // Clearing the local values restores the style-bound view-model position.
            HostPresenter.ClearValue(TimelinePanel.StartMinutesProperty);
            HostPresenter.ClearValue(TimelinePanel.DurationMinutesProperty);
        }

        Opacity = 1;
        _surface?.ClearDragPreview();
        _moving = _resizing = false;
        _dragConfirmed = false;
    }

    private void OnKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var step = Snap(e.KeyModifiers);
        var surface = _surface ?? this.FindAncestorOfType<TimelineSurfaceView>();
        switch (e.Key)
        {
            case Key.Up when Vm.CanMove && !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                surface?.RememberFocus(Vm.Id);
                Vm.Nudge(-step);
                e.Handled = true;
                return;
            case Key.Down when Vm.CanMove && !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                surface?.RememberFocus(Vm.Id);
                Vm.Nudge(step);
                e.Handled = true;
                return;
            case Key.Up when Vm.CanResize && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                surface?.RememberFocus(Vm.Id);
                Vm.ResizeBy(-step);
                e.Handled = true;
                return;
            case Key.Down when Vm.CanResize && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                surface?.RememberFocus(Vm.Id);
                Vm.ResizeBy(step);
                e.Handled = true;
                return;
            case Key.Left when Vm.CanMove:
                surface?.RememberFocus(Vm.Id);
                Vm.NudgeDays(-1);
                e.Handled = true;
                return;
            case Key.Right when Vm.CanMove:
                surface?.RememberFocus(Vm.Id);
                Vm.NudgeDays(1);
                e.Handled = true;
                return;
            case Key.Delete when Vm.CanDelete:
                // Dispatches by kind; external synced events never reach here.
                Vm.UnscheduleCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Enter or Key.Space:
                if (CompleteButton.IsVisible && CompleteButton.Flyout is { } outcomeFlyout)
                {
                    outcomeFlyout.ShowAt(CompleteButton);
                    e.Handled = true;
                }
                else if (ProposalButton.IsVisible && ProposalButton.Flyout is { } proposalFlyout)
                {
                    proposalFlyout.ShowAt(ProposalButton);
                    e.Handled = true;
                }
                else if (Vm.CanEdit)
                {
                    Vm.Edit();
                    e.Handled = true;
                }

                return;
        }
    }

    /// <summary>Closes the hosting flyout after an action.</summary>
    private void OnFlyoutActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control
            && control.FindAncestorOfType<FlyoutPresenter>() is { Parent: Popup popup })
        {
            popup.IsOpen = false;
        }
    }
}
