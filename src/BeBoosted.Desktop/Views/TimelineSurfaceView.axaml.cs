using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Controls;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;

namespace BeBoosted.Desktop.Views;

/// <summary>
/// Shared timeline surface for Today (one lane) and Week (seven lanes): owns geometry
/// constants, drop-from-Inbox handling, initial scroll, and focus restoration after
/// keyboard moves rebuild the block collections.
/// </summary>
public partial class TimelineSurfaceView : UserControl
{
    // The timeline is one continuous 00:00–24:00 day (BB-QA-001); the initial scroll
    // picks a useful hour, but every minute stays reachable by scrolling.
    public const int VisibleStartHour = 0;
    public const int VisibleEndHour = 24;
    public const double DefaultHourHeight = 56;

    private CalendarBlockId? _refocusBlockId;
    private bool _initialScrollDone;
    private CalendarBlockView? _dragPreview;

    public TimelineSurfaceView()
    {
        InitializeComponent();
        Gutter.StartHour = VisibleStartHour;
        Gutter.EndHour = VisibleEndHour;
        Gutter.HourHeight = DefaultHourHeight;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearPreviews());

        // Empty-slot clicks create a scheduled task. Bubble handlers on the days area
        // fire only for presses no block handled, and never for the header, gutter,
        // or scrollbar (they live outside the days area).
        DaysArea.AddHandler(PointerPressedEvent, OnSlotPointerPressed, RoutingStrategies.Bubble);
        DaysArea.AddHandler(PointerMovedEvent, OnSlotPointerMoved, RoutingStrategies.Bubble);
        DaysArea.AddHandler(PointerReleasedEvent, OnSlotPointerReleased, RoutingStrategies.Bubble);

        DaysArea.LayoutUpdated += OnDaysAreaLayoutUpdated;
    }

    // ---- New task from an empty Week slot ----

    private Point? _slotPressPoint;

    private void OnSlotPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _slotPressPoint = null;
        if (!e.GetCurrentPoint(DaysArea).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || source.FindAncestorOfType<CalendarBlockView>(includeSelf: true) is not null)
        {
            return; // blocks (and their controls) keep their own click semantics
        }

        _slotPressPoint = e.GetPosition(DaysArea);
    }

    private void OnSlotPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_slotPressPoint is { } press)
        {
            var delta = e.GetPosition(DaysArea) - press;
            if (Math.Abs(delta.X) >= 4 || Math.Abs(delta.Y) >= 4)
            {
                _slotPressPoint = null; // past the click threshold — a drag, not a click
            }
        }
    }

    private void OnSlotPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_slotPressPoint is not { } press || Vm is not { } vm)
        {
            _slotPressPoint = null;
            return;
        }

        _slotPressPoint = null;
        var date = DateForColumn(ColumnFromX(press.X));
        var geometry = new CalendarEngine.TimelineGeometry(
            VisibleStartHour * 60, VisibleEndHour * 60, DefaultHourHeight);
        var snapped = geometry
            .TimeFromY(press.Y, CalendarViewModel.SnapMinutes)
            .ToTimeSpan().TotalMinutes;
        // One hour from the snapped slot, kept inside 00:00–23:59 near midnight.
        var startMinutes = Math.Min(snapped, (24 * 60.0) - CalendarViewModel.SnapMinutes);
        var endMinutes = Math.Min(startMinutes + 60, (24 * 60.0) - 1);
        vm.OpenNewTaskEditorAt(
            date,
            TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(startMinutes)),
            TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(endMinutes)));
    }

    public double HourHeight => DefaultHourHeight;

    public int StartHour => VisibleStartHour;

    public int EndHour => VisibleEndHour;

    internal Control DaysHost => DaysArea;

    public double ColumnWidth
        => Days.Count == 0 ? DaysArea.Bounds.Width : DaysArea.Bounds.Width / Days.Count;

    private CalendarViewModel? Vm => DataContext as CalendarViewModel;

    private IReadOnlyList<DayColumnViewModel> Days
        => Vm?.Days is { } days ? days : [];

    public int ColumnFromX(double x)
        => Days.Count == 0 ? 0 : Math.Clamp((int)(x / ColumnWidth), 0, Days.Count - 1);

    public DateOnly DateForColumn(int column)
        => Days.Count == 0 ? default : Days[Math.Clamp(column, 0, Days.Count - 1)].Date;

    /// <summary>Restores focus to the block after the reload triggered by a keyboard move.</summary>
    public void RememberFocus(CalendarBlockId id) => _refocusBlockId = id;

    private void OnDaysAreaLayoutUpdated(object? sender, EventArgs e)
    {
        ApplyGeometry();
        PerformInitialScroll();
        RestoreFocus();
    }

    private void ApplyGeometry()
    {
        foreach (var decorations in DaysArea.GetVisualDescendants().OfType<TimelineDecorations>())
        {
            decorations.StartHour = VisibleStartHour;
            decorations.EndHour = VisibleEndHour;
            decorations.HourHeight = DefaultHourHeight;
        }

        // Block panels must share the exact same geometry as the chrome behind them.
        foreach (var panel in DaysArea.GetVisualDescendants().OfType<TimelinePanel>())
        {
            panel.StartHour = VisibleStartHour;
            panel.EndHour = VisibleEndHour;
            panel.HourHeight = DefaultHourHeight;
        }
    }

    private void PerformInitialScroll()
    {
        if (_initialScrollDone || DaysArea.Bounds.Height <= 0)
        {
            return;
        }

        _initialScrollDone = true;
        var today = Days.FirstOrDefault(d => d.IsToday);
        var targetMinutes = today is { NowMinutes: >= 0 }
            ? Math.Max(today.NowMinutes - 90, VisibleStartHour * 60)
            : 7 * 60;
        var y = (targetMinutes - (VisibleStartHour * 60)) / 60.0 * DefaultHourHeight;
        Dispatcher.UIThread.Post(() => Scroller.Offset = new Vector(0, Math.Max(y, 0)));
    }

    private void RestoreFocus()
    {
        if (_refocusBlockId is not { } id)
        {
            return;
        }

        var target = DaysArea.GetVisualDescendants()
            .OfType<CalendarBlockView>()
            .FirstOrDefault(view => (view.DataContext as CalendarBlockViewModel)?.Id == id);
        if (target is not null)
        {
            _refocusBlockId = null;
            Dispatcher.UIThread.Post(() => target.Focus());
        }
    }

    // ---- Cross-day drag preview (BB-QA-003) ----
    // The per-column block subtrees clip to their own bounds, so a block dragged across a
    // day boundary is rendered here instead: a non-interactive clone of the dragged block
    // on the DragPreviewCanvas overlay spanning every day column.

    /// <summary>
    /// Shows (or repositions) the drag preview for <paramref name="vm"/> in the given
    /// column at the snapped <paramref name="startMinutes"/> — exactly the day and time
    /// a release would persist.
    /// </summary>
    internal void ShowDragPreview(
        CalendarBlockViewModel vm, int column, double startMinutes, double durationMinutes)
    {
        var panels = DaysArea.GetVisualDescendants()
            .OfType<TimelinePanel>()
            .OrderBy(p => p.TranslatePoint(default, DaysArea)?.X ?? 0)
            .ToList();
        if (column < 0 || column >= panels.Count)
        {
            ClearDragPreview();
            return;
        }

        if (_dragPreview is null || !ReferenceEquals(_dragPreview.DataContext, vm))
        {
            ClearDragPreview();
            _dragPreview = new CalendarBlockView { DataContext = vm, IsHitTestVisible = false };
            DragPreviewCanvas.Children.Add(_dragPreview);
        }

        var panel = panels[column];
        var geometry = panel.Geometry;
        var origin = panel.TranslatePoint(default, DragPreviewCanvas) ?? default;
        var start = Math.Max(startMinutes, StartHour * 60.0);
        // Mirror TimelinePanel's single-slot arrangement: 4 px horizontal inset per side.
        _dragPreview.Width = Math.Max(panel.Bounds.Width - 8, 10);
        _dragPreview.Height = Math.Max(
            geometry.HeightForDuration(TimeSpan.FromMinutes(durationMinutes)), 18);
        Canvas.SetLeft(_dragPreview, origin.X + 4);
        Canvas.SetTop(_dragPreview, origin.Y + geometry.YFromMinutes(start));
    }

    /// <summary>Removes the drag preview; safe to call when none is shown.</summary>
    internal void ClearDragPreview()
    {
        if (_dragPreview is not null)
        {
            DragPreviewCanvas.Children.Remove(_dragPreview);
            _dragPreview = null;
        }
    }

    // ---- Drop-from-Inbox ----

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!TryGetDropTarget(e, out var panel, out _, out var start, out var minutes))
        {
            e.DragEffects = DragDropEffects.None;
            ClearPreviews();
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        ClearPreviews(except: panel);
        panel.PreviewStartMinutes = start.ToTimeSpan().TotalMinutes;
        panel.PreviewDurationMinutes = minutes;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (TryGetDropTarget(e, out _, out var date, out var start, out _)
            && TaskDragData.TryParse(e.DataTransfer.TryGetValue(TaskDragData.Format), out var taskId, out _))
        {
            Vm?.ScheduleTask(taskId, date, start);
            e.Handled = true;
        }

        ClearPreviews();
    }

    private bool TryGetDropTarget(
        DragEventArgs e,
        out TimelineDecorations panel,
        out DateOnly date,
        out TimeOnly start,
        out int durationMinutes)
    {
        panel = null!;
        date = default;
        start = default;
        durationMinutes = 0;
        if (!TaskDragData.TryParse(e.DataTransfer.TryGetValue(TaskDragData.Format), out _, out durationMinutes)
            || Days.Count == 0)
        {
            return false;
        }

        var position = e.GetPosition(DaysArea);
        var column = ColumnFromX(position.X);
        var panels = DaysArea.GetVisualDescendants()
            .OfType<TimelineDecorations>()
            .OrderBy(p => p.TranslatePoint(default, DaysArea)?.X ?? 0)
            .ToList();
        if (column >= panels.Count)
        {
            return false;
        }

        panel = panels[column];
        date = DateForColumn(column);
        var y = e.GetPosition(panel).Y;
        var snap = e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            ? CalendarViewModel.FineSnapMinutes
            : CalendarViewModel.SnapMinutes;
        start = panel.Geometry.TimeFromY(y, snap);

        // Keep the block inside the day.
        var latestStart = (VisibleEndHour * 60) - Math.Max(durationMinutes, 5);
        if (start.ToTimeSpan().TotalMinutes > latestStart)
        {
            start = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(latestStart));
        }

        return true;
    }

    private void ClearPreviews(TimelineDecorations? except = null)
    {
        foreach (var panel in DaysArea.GetVisualDescendants().OfType<TimelineDecorations>())
        {
            if (!ReferenceEquals(panel, except))
            {
                panel.PreviewStartMinutes = -1;
                panel.PreviewDurationMinutes = 0;
            }
        }
    }
}
