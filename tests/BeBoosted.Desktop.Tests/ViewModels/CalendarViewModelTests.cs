using System.Globalization;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Tests.ViewModels;

public sealed class CalendarViewModelTests
{
    private static CalendarViewModel Create(InMemorySettingsStore? store = null)
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var calendarService = TestShell.CreateCalendarService(blocks, tasks, clock);
        var planning = new PlanningService(
            new InMemoryPlanningProposalRepository(), blocks,
            new InboxQueryService(tasks, blocks), new InMemoryPrioritizationRepository(),
            calendarService, clock);
        return new CalendarViewModel(
            new AppSettings(store ?? new InMemorySettingsStore()),
            clock,
            calendarService,
            tasks,
            planning,
            new InMemoryProjectRepository());
    }

    [Fact]
    public void FirstRun_DefaultsToTodayViewOnCurrentDate()
    {
        var viewModel = Create();

        Assert.Equal(CalendarViewKind.Today, viewModel.ViewKind);
        Assert.Equal(TestShell.DesignDate, viewModel.VisibleDate);
    }

    [Fact]
    public void RestoresPersistedWeekView()
    {
        var store = new InMemorySettingsStore();
        store.Set(SettingKeys.LastCalendarView, "week");

        Assert.Equal(CalendarViewKind.Week, Create(store).ViewKind);
    }

    [Fact]
    public void ChangingView_PersistsImmediately()
    {
        var store = new InMemorySettingsStore();
        var viewModel = Create(store);

        viewModel.ViewKind = CalendarViewKind.Week;

        Assert.Equal("week", store.Get(SettingKeys.LastCalendarView));
    }

    [Fact]
    public void Construction_DoesNotWriteSettings()
    {
        var store = new InMemorySettingsStore();
        _ = Create(store);

        Assert.Null(store.Get(SettingKeys.LastCalendarView));
    }

    [Theory]
    [InlineData(2026, 8, 11, 2026, 8, 10, 2026, 8, 16)] // Tuesday → Mon 10 .. Sun 16
    [InlineData(2026, 8, 10, 2026, 8, 10, 2026, 8, 16)] // Monday is its own week start
    [InlineData(2026, 8, 16, 2026, 8, 10, 2026, 8, 16)] // Sunday belongs to the preceding Monday
    public void WeekRange_StartsOnMonday(
        int y, int m, int d, int my, int mm, int md, int sy, int sm, int sd)
    {
        var (monday, sunday) = CalendarViewModel.WeekRange(new DateOnly(y, m, d));

        Assert.Equal(new DateOnly(my, mm, md), monday);
        Assert.Equal(new DateOnly(sy, sm, sd), sunday);
    }

    [Fact]
    public void Navigation_MovesByDayInTodayViewAndWeekInWeekView()
    {
        var viewModel = Create();

        viewModel.GoNextCommand.Execute(null);
        Assert.Equal(TestShell.DesignDate.AddDays(1), viewModel.VisibleDate);

        viewModel.ViewKind = CalendarViewKind.Week;
        viewModel.GoNextCommand.Execute(null);
        Assert.Equal(TestShell.DesignDate.AddDays(8), viewModel.VisibleDate);

        viewModel.GoPreviousCommand.Execute(null);
        viewModel.GoPreviousCommand.Execute(null);
        Assert.Equal(TestShell.DesignDate.AddDays(-6), viewModel.VisibleDate);

        viewModel.GoToTodayCommand.Execute(null);
        Assert.Equal(TestShell.DesignDate, viewModel.VisibleDate);
    }

    [Fact]
    public void Headers_MatchDesignContent()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var viewModel = Create();

            Assert.Equal("Tuesday, August 11", viewModel.HeaderTitle);
            Assert.Equal(string.Empty, viewModel.HeaderMeta);

            viewModel.ViewKind = CalendarViewKind.Week;
            Assert.Equal("August 10 – 16", viewModel.HeaderTitle);
            Assert.Equal("Week 33", viewModel.HeaderMeta);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
