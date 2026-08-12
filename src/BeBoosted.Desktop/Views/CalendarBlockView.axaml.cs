using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Controls;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Views;

/// <summary>
/// Block interactions: pointer drag to move (snap 15 min, Alt for 5), bottom-grip resize,
/// and keyboard movement (↑/↓ move, Alt for fine, Shift+↑/↓ resize, ←/→ change day,
/// Enter/Space outcome menu, Delete unschedule). Fixed commitments are locked.
/// </summary>
public partial class CalendarBlockView : UserControl
{
    private TimelineSurfaceView? _surface;
    private Avalonia.Point _pressPoint;
    private double _originStartMinutes;
    private double _originDurationMinutes;
    private int _originColumn;
    private bool _moving;
    private bool _resizing;
    private bool _dragConfirmed;

    public CalendarBlockView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressedHandler, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedHandler, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedHandler, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDownHandler);
    }

    private CalendarBlockViewModel? Vm => DataContext as CalendarBlockViewModel;

    private ContentPresenter? HostPresenter => Parent as ContentPresenter;

    private static int Snap(KeyModifiers modifiers)
        => modifiers.HasFlag(KeyModifiers.Alt) ? CalendarViewModel.FineSnapMinutes : CalendarViewModel.SnapMinutes;

    private void OnPointerPressedHandler(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { IsInteractive: true } || HostPresenter is null)
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
        _resizing = e.Source is Visual gripSource
            && gripSource.FindAncestorOfType<Border>(includeSelf: true) is { Name: "ResizeGrip" };
        _moving = !_resizing;
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

        var column = _surface.ColumnFromX(position.X);
        RenderTransform = column != _originColumn
            ? new TranslateTransform((column - _originColumn) * _surface.ColumnWidth, 0)
            : null;
    }

    private void OnPointerReleasedHandler(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, this))
        {
            return;
        }

        e.Pointer.Capture(null);
        if (_surface is null || HostPresenter is null || Vm is null || !_dragConfirmed)
        {
            _moving = _resizing = false;
            return;
        }

        if (_resizing)
        {
            var duration = TimelinePanel.GetDurationMinutes(HostPresenter);
            var start = TimelinePanel.GetStartMinutes(HostPresenter);
            Vm.ResizeTo(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(start + duration)));
        }
        else
        {
            var position = e.GetPosition(_surface.DaysHost);
            var column = _surface.ColumnFromX(position.X);
            RenderTransform = null;
            var start = TimelinePanel.GetStartMinutes(HostPresenter);
            Vm.MoveTo(
                _surface.DateForColumn(column),
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(start)));
        }

        _moving = _resizing = false;
        _dragConfirmed = false;
    }

    private void OnKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsInteractive)
        {
            var step = Snap(e.KeyModifiers);
            var surface = _surface ?? this.FindAncestorOfType<TimelineSurfaceView>();
            switch (e.Key)
            {
                case Key.Up when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    surface?.RememberFocus(Vm.Id);
                    Vm.Nudge(-step);
                    e.Handled = true;
                    return;
                case Key.Down when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    surface?.RememberFocus(Vm.Id);
                    Vm.Nudge(step);
                    e.Handled = true;
                    return;
                case Key.Up when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    surface?.RememberFocus(Vm.Id);
                    Vm.ResizeBy(-step);
                    e.Handled = true;
                    return;
                case Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    surface?.RememberFocus(Vm.Id);
                    Vm.ResizeBy(step);
                    e.Handled = true;
                    return;
                case Key.Left:
                    surface?.RememberFocus(Vm.Id);
                    Vm.NudgeDays(-1);
                    e.Handled = true;
                    return;
                case Key.Right:
                    surface?.RememberFocus(Vm.Id);
                    Vm.NudgeDays(1);
                    e.Handled = true;
                    return;
                case Key.Delete:
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

                    return;
            }
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
