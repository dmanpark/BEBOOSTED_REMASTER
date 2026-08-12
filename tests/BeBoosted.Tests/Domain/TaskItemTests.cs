using BeBoosted.Domain;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Tests.Domain;

public sealed class TaskItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));

    [Fact]
    public void Create_TrimsTitleAndStampsTimestamps()
    {
        var task = TaskItem.Create("  Finish DECA presentation  ", Now);

        Assert.Equal("Finish DECA presentation", task.Title);
        Assert.Equal(Now, task.CreatedAt);
        Assert.Equal(Now, task.ModifiedAt);
        Assert.Equal(TaskOrigin.User, task.Origin);
        Assert.False(task.IsCompleted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsEmptyTitle(string title)
        => Assert.Throws<DomainException>(() => TaskItem.Create(title, Now));

    [Fact]
    public void SetEstimatedDuration_RejectsNonPositive()
    {
        var task = TaskItem.Create("Task", Now);
        Assert.Throws<DomainException>(() => task.SetEstimatedDuration(TimeSpan.Zero, Now));
        Assert.Throws<DomainException>(() => task.SetEstimatedDuration(TimeSpan.FromMinutes(-5), Now));
    }

    [Fact]
    public void Complete_SetsStateAndIsIdempotent()
    {
        var task = TaskItem.Create("Task", Now);
        var later = Now.AddHours(2);

        task.Complete(later);
        Assert.True(task.IsCompleted);
        Assert.Equal(later, task.CompletedAt);

        task.Complete(later.AddHours(1));
        Assert.Equal(later, task.CompletedAt); // unchanged

        task.Reopen(later.AddHours(2));
        Assert.False(task.IsCompleted);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public void RecordNeedsMoreTime_ReplacesEstimateAndKeepsTaskOpen()
    {
        var task = TaskItem.Create("Task", Now, estimatedDuration: TimeSpan.FromMinutes(90));

        task.RecordNeedsMoreTime(TimeSpan.FromMinutes(30), Now.AddHours(3));

        Assert.Equal(TimeSpan.FromMinutes(30), task.EstimatedDuration);
        Assert.False(task.IsCompleted);
        Assert.Equal(Now.AddHours(3), task.ModifiedAt);
    }

    [Fact]
    public void RecordNeedsMoreTime_RejectsCompletedTaskAndNonPositiveRemaining()
    {
        var task = TaskItem.Create("Task", Now);
        Assert.Throws<DomainException>(() => task.RecordNeedsMoreTime(TimeSpan.Zero, Now));

        task.Complete(Now);
        Assert.Throws<DomainException>(() => task.RecordNeedsMoreTime(TimeSpan.FromMinutes(10), Now));
    }

    [Fact]
    public void Rename_ValidatesAndTouches()
    {
        var task = TaskItem.Create("Old", Now);
        task.Rename(" New title ", Now.AddMinutes(5));

        Assert.Equal("New title", task.Title);
        Assert.Equal(Now.AddMinutes(5), task.ModifiedAt);
        Assert.Throws<DomainException>(() => task.Rename(" ", Now));
    }
}
