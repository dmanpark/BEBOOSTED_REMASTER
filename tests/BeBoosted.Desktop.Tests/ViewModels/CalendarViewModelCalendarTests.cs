using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

public sealed class CalendarViewModelCalendarTests
{
    private sealed record Context(
        CalendarViewModel Calendar,
        InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks,
        FakeClock Clock,
        CalendarService Service,
        PlanningService Planning,
        InMemoryPrioritizationRepository Prioritization);

    private static Context Create(bool seed = false)
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        if (seed)
        {
            TestShell.SeedDesignCalendar(tasks, blocks, clock);
        }

        var service = new CalendarService(blocks, tasks, clock);
        var prioritization = new InMemoryPrioritizationRepository();
        var planning = new PlanningService(
            new InMemoryPlanningProposalRepository(), blocks,
            new InboxQueryService(tasks, blocks), prioritization, service, clock);
        var calendar = new CalendarViewModel(
            new AppSettings(new InMemorySettingsStore()), clock, service, tasks, planning);
        return new Context(calendar, tasks, blocks, clock, service, planning, prioritization);
    }

    [Fact]
    public void TodayView_HasOneDay_WeekViewHasSeven()
    {
        var context = Create(seed: true);

        Assert.Single(context.Calendar.Days);
        Assert.Equal(TestShell.DesignDate, context.Calendar.Days[0].Date);
        Assert.True(context.Calendar.Days[0].IsToday);

        context.Calendar.ViewKind = CalendarViewKind.Week;
        Assert.Equal(7, context.Calendar.Days.Count);
        Assert.Equal(new DateOnly(2026, 8, 10), context.Calendar.Days[0].Date); // Monday
    }

    [Fact]
    public void Reload_PlacesBlocksOnTheRightDays_WithTaskTitles()
    {
        var context = Create(seed: true);
        var today = context.Calendar.Days[0];

        // AP Economics (recurring), Lunch, morning reading (done), practice, statement.
        Assert.Equal(5, today.Blocks.Count);
        Assert.Contains(today.Blocks, b => b.Title == "AP Economics" && b.IsFixed);
        Assert.Contains(today.Blocks, b => b.Title == "Practice DECA role-play" && b.IsTaskBlock);
        Assert.Contains(today.Blocks, b => b.Title == "Morning reading — econ chapter 6" && b.IsDone);

        context.Calendar.ViewKind = CalendarViewKind.Week;
        var saturday = context.Calendar.Days.Single(d => d.Date == new DateOnly(2026, 8, 15));
        Assert.Contains(saturday.Blocks, b => b.Title == "SAT practice test");
        var wednesday = context.Calendar.Days.Single(d => d.Date == new DateOnly(2026, 8, 12));
        Assert.Contains(wednesday.Blocks, b => b.Title == "AP Economics"); // recurrence expanded
    }

    [Fact]
    public void OverlappingBlocks_AreFlaggedAsConflicts()
    {
        var context = Create();
        var task = TaskItem.Create("Overlap", context.Clock.Now, estimatedDuration: TimeSpan.FromMinutes(60));
        context.Tasks.Add(task);
        context.Service.CreateFixedCommitment("Meeting", TestShell.DesignDate, new TimeOnly(15, 0), new TimeOnly(16, 0));
        context.Service.ScheduleTask(task.Id, TestShell.DesignDate, new TimeOnly(15, 30));
        context.Calendar.Reload();

        var blocks = context.Calendar.Days[0].Blocks;
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.True(b.IsConflicted));
    }

    [Fact]
    public void ScheduleTask_AddsBlockAndRaisesDataChanged()
    {
        var context = Create();
        var task = TaskItem.Create("New work", context.Clock.Now, estimatedDuration: TimeSpan.FromMinutes(45));
        context.Tasks.Add(task);
        var changed = 0;
        context.Calendar.DataChanged += () => changed++;

        context.Calendar.ScheduleTask(task.Id, TestShell.DesignDate, new TimeOnly(16, 0));

        Assert.Equal(1, changed);
        Assert.Contains(context.Calendar.Days[0].Blocks, b => b.Title == "New work");
    }

    [Fact]
    public void ReviewNotice_CountsElapsedBlocks_AndClearsAfterOutcome()
    {
        var context = Create();
        var task = TaskItem.Create("Elapsed", context.Clock.Now, estimatedDuration: TimeSpan.FromMinutes(60));
        context.Tasks.Add(task);
        var block = context.Service.ScheduleTask(task.Id, TestShell.DesignDate, new TimeOnly(9, 0));
        context.Calendar.Reload();

        Assert.True(context.Calendar.HasReviewNotice);
        Assert.Equal("1 previous block needs an outcome.", context.Calendar.ReviewNoticeText);

        context.Calendar.RecordOutcome(block.Id, BlockOutcome.Done, null);
        Assert.False(context.Calendar.HasReviewNotice);
        Assert.True(context.Tasks.GetById(task.Id)!.IsCompleted);
    }

    [Fact]
    public void HeaderMeta_SummarizesPlannedTimeForToday()
    {
        var context = Create(seed: true);
        // Practice (90) + statement (60); done blocks excluded.
        Assert.Equal("2 h 30 min planned", context.Calendar.HeaderMeta);
    }

    [Fact]
    public void TrySaveCommitment_ValidatesAndCreates()
    {
        var context = Create();
        context.Calendar.PrepareCommitmentEditorCommand.Execute(null);

        context.Calendar.CommitmentTitle = "";
        Assert.False(context.Calendar.TrySaveCommitment());
        Assert.NotNull(context.Calendar.CommitmentError);

        context.Calendar.CommitmentTitle = "AP Economics";
        context.Calendar.CommitmentStart = new TimeSpan(8, 30, 0);
        context.Calendar.CommitmentEnd = new TimeSpan(9, 45, 0);
        context.Calendar.CommitmentRepeatsWeekly = true;
        foreach (var day in context.Calendar.CommitmentDays.Where(
            d => d.Day is DayOfWeek.Monday or DayOfWeek.Wednesday))
        {
            day.IsSelected = true;
        }

        Assert.True(context.Calendar.TrySaveCommitment());
        var block = context.Blocks.GetAll().Single();
        Assert.Equal("AP Economics", block.Title);
        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Wednesday],
            block.Recurrence!.DaysOfWeek.OrderBy(d => d));
    }

    [Fact]
    public void KeyboardOperations_MoveResizeAndChangeDay()
    {
        var context = Create();
        var task = TaskItem.Create("Keyboard", context.Clock.Now, estimatedDuration: TimeSpan.FromMinutes(60));
        context.Tasks.Add(task);
        var block = context.Service.ScheduleTask(task.Id, TestShell.DesignDate, new TimeOnly(15, 0));
        context.Calendar.Reload();
        var blockVm = context.Calendar.Days[0].Blocks.Single();

        context.Calendar.NudgeBlock(blockVm, 15);
        Assert.Equal(new TimeOnly(15, 15), context.Blocks.GetById(block.Id)!.StartTime);

        blockVm = context.Calendar.Days[0].Blocks.Single();
        context.Calendar.ResizeBlockBy(blockVm, 30);
        Assert.Equal(new TimeOnly(16, 45), context.Blocks.GetById(block.Id)!.EndTime);

        blockVm = context.Calendar.Days[0].Blocks.Single();
        context.Calendar.NudgeBlockDays(blockVm, 1);
        Assert.Equal(TestShell.DesignDate.AddDays(1), context.Blocks.GetById(block.Id)!.Date);
        Assert.Empty(context.Calendar.Days[0].Blocks); // moved off the visible day
    }

    [Fact]
    public void RefreshNow_UpdatesIndicatorWithoutRebuildingCollections()
    {
        var context = Create(seed: true);
        var day = context.Calendar.Days[0];
        var blocksBefore = day.Blocks;

        context.Calendar.RefreshNow();

        Assert.Same(blocksBefore, context.Calendar.Days[0].Blocks);
        Assert.Equal(14 * 60 + 10, context.Calendar.Days[0].NowMinutes, 1);
    }
}
