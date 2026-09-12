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
/// Enter/Space done, outcome menu or editor, Delete by kind). A click below the drag threshold
/// opens the Task editor for local sessions. External synced events stay locked.
/// </summary>
public partial class CalendarBlockView : UserControl
{
    /// <summary>
    /// The narrowest block that holds the checkbox and the overflow at once, summed
    /// straight off this view's own AXAML rather than guessed: 1px of border a side (2),
    /// the project-accent edge (3), the inner grid's 8+8 margin (16), the checkbox's
    /// 20 wide plus its 8 right margin (28) and the overflow's 20 plus its 6 left margin
    /// (26) — 75px, with nothing left over for the title.
    /// </summary>
    private const double BothControlsFitWidth = 2 + 3 + 16 + 28 + 26;

    /// <summary>
    /// The narrowest title the overflow is willing to share a block with. Measured, not
    /// picked: the word "Practice" at the title's own 12px SemiBold is 48px exactly, so
    /// this is about eight characters — enough to tell two sessions apart at a glance.
    /// </summary>
    private const double MinimumSharedTitleWidth = 48;

    /// <summary>
    /// What the overflow actually costs a block: room for both controls AND for a title
    /// worth reading beside them. <see cref="BothControlsFitWidth"/> alone answers the
    /// narrower question "what fits both controls", and using it as the threshold kept
    /// both controls on an 89px block (1440x960, two overlapping) beside a 9px title that
    /// was an ellipsis and nothing else — which is the option that was weighed against
    /// this one and turned down. Two derived constants, not one tuned number.
    ///
    /// How many sessions share an hour decides whether a block clears 123, not the
    /// window: two overlapping give 65px at 1100x720, 89px at 1440x960 and 123px at
    /// 1920x1080; three give 43, 59 and 82. The checkbox alone needs 33px, so it survives
    /// all of them; the title is what gets cut. Below 123 the overflow hides — see the
    /// styles in the AXAML for exactly what that costs and why it is acceptable.
    ///
    /// This reads the arranged width, which is the true one only because
    /// <see cref="Controls.TimelinePanel"/> now measures each block at the width it will
    /// be arranged into. While those disagreed, no width predicate could have worked: the
    /// inner grid was laid out for a width the block never got.
    /// </summary>
    private const double OverflowFitWidth = BothControlsFitWidth + MinimumSharedTitleWidth;

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
        SizeChanged += OnSizeChangedHandler;
    }

    /// <summary>
    /// A block learns how narrow it is only when it is laid out, because its width comes
    /// from how many sessions share its hour rather than from anything it knows about
    /// itself. The class does the rest — see <see cref="OverflowFitWidth"/>.
    /// </summary>
    private void OnSizeChangedHandler(object? sender, SizeChangedEventArgs e)
        => BlockBorder.Classes.Set("narrow", e.NewSize.Width < OverflowFitWidth);

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
                // Done belongs to the checkbox, so the keyboard presses the checkbox:
                // pointing Enter at the outcome flyout instead led with "Needs more
                // time", and Enter-Enter on a session recorded that rather than
                // finishing it. Keyboard and mouse now do the same thing.
                //
                // An already-done session is the exception the comment below has always
                // described: its checkbox would UNDO, which is not what a stray Enter on
                // a finished block should mean, so it keeps falling through to the
                // editor - reopening stays a deliberate click or a Tab into the block.
                if (Vm.ShowCompletionControl && !Vm.IsDone)
                {
                    surface?.RememberFocus(Vm.Id);
                    Vm.ToggleSessionDoneCommand.Execute(null);
                    e.Handled = true;
                }

                // A repeating occurrence carries its own circle instead of a checkbox,
                // so ShowCompletionControl is false for it and the branch above never
                // fires - Enter used to open the editor while the equivalent one-off
                // finished. The keyboard presses whichever control the block offers, and
                // the already-done exception above applies here word for word.
                else if (Vm.ShowOccurrenceCompletionControl && !Vm.IsDone)
                {
                    surface?.RememberFocus(Vm.Id);
                    Vm.ToggleOccurrenceDoneCommand.Execute(null);
                    e.Handled = true;
                }

                // The overflow, for a session that has no checkbox to press. As the
                // predicates stand ShowOutcomeAction implies ShowCompletionControl, so
                // an open session always takes the branch above and this one is only
                // reached if those two ever come apart. The overflow stays reachable by
                // Tab either way, which is how Today reaches its own. A done session
                // has no flyout left and falls through to the editor.
                else if (OutcomeButton.IsVisible && OutcomeButton.Flyout is { } outcomeFlyout)
                {
                    outcomeFlyout.ShowAt(OutcomeButton);
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
