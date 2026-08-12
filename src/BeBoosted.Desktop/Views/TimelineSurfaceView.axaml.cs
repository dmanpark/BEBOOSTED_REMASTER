using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    public const int VisibleStartHour = 6;
    public const int VisibleEndHour = 23;
    public const double DefaultHourHeight = 56;

    private CalendarBlockId? _refocusBlockId;
    private bool _initialScrollDone;

    public TimelineSurfaceView()
    {
        InitializeComponent();
        Gutter.StartHour = VisibleStartHour;
        Gutter.EndHour = VisibleEndHour;
        Gutter.HourHeight = DefaultHourHeight;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearPreviews());

        DaysArea.LayoutUpdated += OnDaysAreaLayoutUpdated;
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
        foreach (var panel in DaysArea.GetVisualDescendants().OfType<TimelineDecorations>())
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
            .FirstOrDefault(view => (view.DataContext as CalendarBlockViewModel)?.Block.Id == id);
        if (target is not null)
        {
            _refocusBlockId = null;
            Dispatcher.UIThread.Post(() => target.Focus());
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
