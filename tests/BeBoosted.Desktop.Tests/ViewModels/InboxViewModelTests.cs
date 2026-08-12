using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Tests.ViewModels;

public sealed class InboxViewModelTests
{
    private static (InboxViewModel Inbox, InMemoryTaskRepository Repository) Create(
        InMemoryTaskRepository? repository = null)
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var repo = repository ?? new InMemoryTaskRepository();
        return (new InboxViewModel(new TaskService(repo, clock), repo, clock), repo);
    }

    [Fact]
    public void LoadsExistingOpenTasks()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var (inbox, _) = Create(TestShell.SeededTasks(clock));

        Assert.Equal(4, inbox.OpenCount);
        Assert.True(inbox.HasTasks);
        Assert.Equal("Finish DECA presentation", inbox.Tasks[0].Title);
    }

    [Fact]
    public void Capture_AddsTaskAndClearsText()
    {
        var (inbox, repository) = Create();

        inbox.CaptureText = "  Email recommendation request  ";
        inbox.CaptureCommand.Execute(null);

        Assert.Single(inbox.Tasks);
        Assert.Equal("Email recommendation request", inbox.Tasks[0].Title);
        Assert.Equal(string.Empty, inbox.CaptureText);
        Assert.Single(repository.GetInbox());
    }

    [Fact]
    public void Capture_IgnoresBlankInput()
    {
        var (inbox, repository) = Create();

        inbox.CaptureText = "   ";
        inbox.CaptureCommand.Execute(null);

        Assert.Empty(inbox.Tasks);
        Assert.Empty(repository.GetInbox());
    }

    [Fact]
    public void Complete_RemovesRowAndPersists()
    {
        var (inbox, repository) = Create();
        inbox.CaptureText = "Review economics chapter";
        inbox.CaptureCommand.Execute(null);
        var row = inbox.Tasks[0];

        row.CompleteCommand.Execute(null);

        Assert.Empty(inbox.Tasks);
        Assert.False(inbox.HasTasks);
        Assert.True(repository.GetAll().Single().IsCompleted);
    }

    [Fact]
    public void Delete_RemovesRowAndTask()
    {
        var (inbox, repository) = Create();
        inbox.CaptureText = "Mistake";
        inbox.CaptureCommand.Execute(null);

        inbox.Tasks[0].DeleteCommand.Execute(null);

        Assert.Empty(inbox.Tasks);
        Assert.Empty(repository.GetAll());
    }

    [Fact]
    public void CommitEdit_UpdatesTitleDeadlineAndDuration()
    {
        var (inbox, repository) = Create();
        inbox.CaptureText = "Draft essay outline";
        inbox.CaptureCommand.Execute(null);
        var row = inbox.Tasks[0];

        row.EditTitle = "Draft college essay outline";
        row.EditDeadline = new DateTimeOffset(new DateTime(2026, 8, 16));
        row.EditDurationMinutes = 60;
        row.CommitEditCommand.Execute(null);

        Assert.Equal("Draft college essay outline", row.Title);
        Assert.Equal("Sun · 1 h", row.MetaText);
        var persisted = repository.GetAll().Single();
        Assert.Equal(new DateOnly(2026, 8, 16), persisted.Deadline);
        Assert.Equal(TimeSpan.FromMinutes(60), persisted.EstimatedDuration);
    }

    [Fact]
    public void CommitEdit_WithBlankTitleRevertsInsteadOfSaving()
    {
        var (inbox, repository) = Create();
        inbox.CaptureText = "Keep me";
        inbox.CaptureCommand.Execute(null);
        var row = inbox.Tasks[0];

        row.EditTitle = "  ";
        row.CommitEditCommand.Execute(null);

        Assert.Equal("Keep me", row.Title);
        Assert.Equal("Keep me", row.EditTitle);
        Assert.Equal("Keep me", repository.GetAll().Single().Title);
    }

    [Fact]
    public void MetaText_UsesRelativeDeadlinesAndDurations()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var (inbox, _) = Create(TestShell.SeededTasks(clock));

        Assert.Equal("Fri · 1 h 30 min", inbox.Tasks[0].MetaText); // deadline Aug 14
        Assert.Equal("Sun · 1 h", inbox.Tasks[1].MetaText);        // deadline Aug 16
        Assert.Equal("45 min", inbox.Tasks[2].MetaText);           // no deadline
    }
}
